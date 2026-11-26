using System.Buffers.Binary;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Write;

/// <summary>
/// Dosya verisi (Faz 2.2). Kurallar:
/// <list type="bullet">
/// <item>Kalıcı veri asla yerinde değiştirilmez: yazılan aralık yeni bloklara yazılır, eski bloklar başvuru sayısı düşürülerek
/// bırakılır (klonlarda kardeşin verisi korunur). Yalnızca bu işlemde ayrılmış bloklar yerinde güncellenir.</item>
/// <item>Extent'ler 0'dan boşluksuz sürer; delik = fiziksel adresi 0 olan extent. Ayrılan boyut = extent'lerin toplamı
/// (delikler dahil); delik baytları SPARSE_BYTES alanında ve INODE_IS_SPARSE bayrağındadır.</item>
/// <item>Her veri bloğunun extentref ağacında bir fiziksel extent kaydı vardır (sahip = veri akışı kimliği, başvuru sayısı).</item>
/// <item>Son bloğun dosya sonundan sonraki baytları her zaman sıfırdır (kısaltmada sıfırlanır). Dosya büyürken eski sondan yeni
/// sona dek sıfır okunur: dosya sonunun ötesindeki ayrılmış bloklar (macOS'un ön ayırması) deliğe çevrilir, kirli son blok yeniden
/// yazılır (<see cref="PlanStaleZeroing"/>).</item>
/// <item>Şifreli birimde (ONEKEY) her veri extent'i crypto_id taşır: yeni yazılan blokta crypto_id = ilk yazıldığı fiziksel blok
/// (<see cref="NewCryptoId"/>, macOS kuralı: fixtures/crypto-survey.txt); bölünen extent'in sağ parçası +k alır, birleştirme yalnız
/// crypto_id'ler de ardışıksa yapılır. Veri tweak = crypto_id × (blok/512) + birim ile şifrelenir (<see cref="WriteSession.WriteData"/>).</item>
/// </list>
/// </summary>
public sealed partial class VolumeWriter
{
    /// <summary>Blok cinsinden extent; Phys 0 = delik. Flags = len_and_flags'in üst 8 biti (şifreli birimde veri extent'inde
    /// FEXT_CRYPTO_ID_IS_TWEAK); bölünmede korunur.</summary>
    private readonly record struct Ext(ulong Start, ulong Blocks, ulong Phys, ulong Crypto = 0, byte Flags = 0)
    {
        public ulong End => Start + Blocks;

        /// <summary>Extent içindeki k. bloğun crypto_id'si (şifresizde 0).</summary>
        public ulong CryptoAt(ulong k) => Crypto == 0 ? 0 : Crypto + k;
    }

    /// <summary>Yeni yazılan veri bloğunun crypto_id'si: şifreli birimde bloğun fiziksel adresi, şifresizde 0.</summary>
    private ulong NewCryptoId(ulong phys) => Crypto is null ? 0 : phys;

    /// <summary>FEXT_CRYPTO_ID_IS_TWEAK: crypto_id alanı bir tweak'tir (macOS ONEKEY birimde her veri extent'ine koyar; USBTEST 74/74).</summary>
    internal const byte FextCryptoIdIsTweak = 0x1;

    /// <summary>Yeni yazılan veri extent'inin bayrakları.</summary>
    private byte NewExtentFlags => Crypto is null ? (byte)0 : FextCryptoIdIsTweak;

    /// <summary>Şifreli birimde yeni dosyaların varsayılan crypto_id'si: CRYPTO_SW_ID (macOS ONEKEY birimdeki dosyalarda 4; USBTEST 83/83).</summary>
    private ulong NewDefaultCryptoId => Crypto is null ? 0UL : 4UL;

    private uint Bs => _s.Reader.BlockSize;

    private List<Ext> LoadExtents(ulong streamId)
    {
        var list = new List<Ext>();
        foreach (var e in _fs.Scan(k => JKey.ComparePrefix(k, streamId, JType.FileExtent)))
        {
            ulong logical = Bin.U64(e.Key, 8), len = Bin.U64(e.Value, 0) & 0x00FFFFFFFFFFFFFF;
            if (logical % Bs != 0 || len % Bs != 0 || len == 0) throw ApfsException.Corrupt(CoreText.T("VolWriter.UnalignedExtent", streamId));
            ulong phys = Bin.U64(e.Value, 8), crypto = Bin.U64(e.Value, 16);
            if (Crypto is null && crypto != 0) throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("VolWriter.EncryptedExtentOnPlain"));
            if (Crypto is not null && phys != 0 && crypto == 0) throw ApfsException.Corrupt(CoreText.T("VolWriter.ExtentWithoutCryptoId", streamId));
            list.Add(new Ext(logical / Bs, len / Bs, phys, phys == 0 ? 0 : crypto, (byte)(Bin.U64(e.Value, 0) >> 56)));
        }
        return list;
    }

    private void PutExtent(ulong streamId, Ext x) =>
        FsPut(Records.FileExtentKey(streamId, x.Start * Bs), Records.FileExtentVal(x.Blocks * Bs, x.Phys, x.Crypto, x.Flags));

    private void DeleteExtent(ulong streamId, Ext x) => FsDelete(Records.FileExtentKey(streamId, x.Start * Bs));

    // ---------------------------------------------------------------------------------------------------------------
    // Fiziksel extent kayıtları (extentref ağacı)

    private readonly record struct Phys(ulong Start, ulong Blocks, ulong Owner, int Refcnt, byte Kind)
    {
        public ulong End => Start + Blocks;
    }

    /// <summary>[start, start+n) ile kesişen fiziksel extent kayıtları, adres sırasıyla.</summary>
    private List<Phys> PhysOverlapping(ulong start, ulong n)
    {
        var list = new List<Phys>();
        var floor = _extRef.Floor(Records.PhysExtKey(start));
        ulong from = floor is { } f ? JKey.ObjId(f.Key) : start;
        foreach (var e in _extRef.Scan(k => JKey.ObjId(k) < from ? -1 : JKey.ObjId(k) >= start + n ? 1 : 0))
        {
            ulong lk = Bin.U64(e.Value, 0);
            var p = new Phys(JKey.ObjId(e.Key), lk & 0x0FFFFFFFFFFFFFFF, Bin.U64(e.Value, 8), Bin.I32(e.Value, 16), (byte)(lk >> 60));
            if (p.End > start) list.Add(p);
        }
        return list;
    }

    private void PutPhys(Phys p) => _extRef.Put(Records.PhysExtKey(p.Start), Records.PhysExtVal(p.Blocks, p.Owner, p.Refcnt, p.Kind));

    /// <summary>Yeni ayrılan veri blokları için başvuru sayısı 1 olan kayıt (bitişik ve aynı sahipli kayıtla birleştirilir).</summary>
    private void AddPhys(ulong start, ulong n, ulong owner)
    {
        var p = new Phys(start, n, owner, 1, Records.PeKindNew);
        if (start > 0 && PhysOverlapping(start - 1, 1) is [var left] && left.End == start && left.Owner == owner && left.Refcnt == 1 && left.Kind == p.Kind)
        {
            _extRef.Delete(Records.PhysExtKey(left.Start));
            p = p with { Start = left.Start, Blocks = left.Blocks + n };
        }
        PutPhys(p);
    }

    /// <summary>
    /// [start, start+n) bloklarının başvuru sayısını bir azaltır; kayıtlar gerektiğinde bölünür. Sayısı 0'a inen bloklar bırakılır.
    /// </summary>
    private void DecRef(ulong start, ulong n)
    {
        ulong end = start + n;
        var overlapping = PhysOverlapping(start, n);
        ulong covered = 0;
        foreach (var p in overlapping)
        {
            _extRef.Delete(Records.PhysExtKey(p.Start));
            ulong a = Math.Max(p.Start, start), b = Math.Min(p.End, end);
            covered += b - a;
            if (p.Start < a) PutPhys(p with { Blocks = a - p.Start });
            if (b < p.End) PutPhys(p with { Start = b, Blocks = p.End - b });
            if (p.Refcnt > 1) PutPhys(p with { Start = a, Blocks = b - a, Refcnt = p.Refcnt - 1 });
            else FreeData(a, b - a);
        }
        if (covered != n) throw ApfsException.Corrupt(CoreText.T("VolWriter.PhysExtentMissing", start, n));
    }

    private (ulong Start, ulong Count) AllocData(ulong maxBlocks)
    {
        var r = _s.AllocateData(this, maxBlocks, ReserveBlocks);
        AddAlloc((long)r.Count);
        return r;
    }

    private void FreeData(ulong start, ulong n)
    {
        _s.Spaceman.Free(start, n);
        AddAlloc(-(long)n);
    }

    /// <summary>
    /// [<paramref name="blk"/>, <paramref name="last"/>) yazımında yeni bloğa gidecek kesintisiz aralığın sonu: bu işlemde ayrılmış
    /// (yerinde yazılan) ilk blok ya da <paramref name="last"/>. Ayırma aralık aralık yapılır: ayrılan blok sayısı denetimde sayılanla
    /// (<see cref="NewBlocksPlan"/>) birebir (önceden bir parça yerinde yazılacak blokların üstünü de kaplayabiliyordu).
    /// </summary>
    private ulong SegmentEnd(List<Ext> exts, ulong blk, ulong last)
    {
        foreach (var x in exts) // başlangıca göre sıralı, örtüşmeyen: bulunan ilk blok en küçüğüdür
        {
            if (x.Phys == 0 || x.End <= blk + 1 || x.Start >= last) continue;
            for (ulong b = Math.Max(x.Start, blk + 1), e = Math.Min(x.End, last); b < e; b++)
                if (_s.Spaceman.IsAllocatedThisTx(x.Phys + (b - x.Start))) return b;
        }
        return last;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Extent listesinde aralık değiştirme

    /// <summary>
    /// Mantıksal [start, start+n) aralığını <paramref name="phys"/> (0 = delik) ile değiştirir. Aralığın eski fiziksel blokları
    /// bırakılır (başvuru düşürülür). Aralık dosyanın ayrılan sonundan ötedeyse araya delik eklenir. Komşu extent'ler birleştirilir.
    /// </summary>
    private void ReplaceRange(ulong streamId, List<Ext> exts, ulong start, ulong n, ulong phys, ulong crypto = 0)
    {
        ulong end = start + n;
        ulong allocEnd = exts.Count == 0 ? 0 : exts[^1].End;
        var result = new List<Ext>(exts.Count + 3);
        foreach (var x in exts)
        {
            if (x.End <= start || x.Start >= end) { result.Add(x); continue; }
            DeleteExtent(streamId, x);
            if (x.Start < start) { var left = x with { Blocks = start - x.Start }; result.Add(left); PutExtent(streamId, left); }
            ulong a = Math.Max(x.Start, start), b = Math.Min(x.End, end);
            if (x.Phys != 0) DecRef(x.Phys + (a - x.Start), b - a);
            if (end < x.End)
            {
                var right = new Ext(end, x.End - end, x.Phys == 0 ? 0 : x.Phys + (end - x.Start), x.CryptoAt(end - x.Start), x.Flags);
                result.Add(right);
                PutExtent(streamId, right);
            }
        }
        if (start > allocEnd)
        {
            var gap = new Ext(allocEnd, start - allocEnd, 0);
            result.Add(gap);
            PutExtent(streamId, gap);
        }
        var nw = new Ext(start, n, phys, phys == 0 ? 0 : crypto, phys == 0 ? (byte)0 : NewExtentFlags);
        result.Add(nw);
        result.Sort((p, q) => p.Start.CompareTo(q.Start));
        PutExtent(streamId, nw);
        if (phys != 0) AddPhys(phys, n, streamId);

        // Komşularla birleştir (her ikisi delik ya da fiziksel olarak ardışık; şifreli birimde crypto_id'ler de ardışık)
        for (int i = result.Count - 1; i > 0; i--)
        {
            var l = result[i - 1];
            var r = result[i];
            bool mergeable = l.End == r.Start && ((l.Phys == 0 && r.Phys == 0) ||
                (l.Phys != 0 && r.Phys == l.Phys + l.Blocks && r.Crypto == l.CryptoAt(l.Blocks) && r.Flags == l.Flags));
            if (!mergeable) continue;
            DeleteExtent(streamId, l);
            DeleteExtent(streamId, r);
            var m = new Ext(l.Start, l.Blocks + r.Blocks, l.Phys, l.Crypto, l.Flags);
            PutExtent(streamId, m);
            result[i - 1] = m;
            result.RemoveAt(i);
        }
        exts.Clear();
        exts.AddRange(result);
    }

    /// <summary>Mantıksal blok aralığını sonundan keser (newEnd'den sonrası bırakılır).</summary>
    private void TruncateExtents(ulong streamId, List<Ext> exts, ulong newEnd)
    {
        for (int i = exts.Count - 1; i >= 0; i--)
        {
            var x = exts[i];
            if (x.End <= newEnd) break;
            DeleteExtent(streamId, x);
            ulong keep = x.Start < newEnd ? newEnd - x.Start : 0;
            if (x.Phys != 0) DecRef(x.Phys + keep, x.Blocks - keep);
            if (keep > 0) { exts[i] = x with { Blocks = keep }; PutExtent(streamId, exts[i]); }
            else exts.RemoveAt(i);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Inode'un veri akışı alanları

    private void UpdateStream(ulong id, InodeRecord ino, ulong size, List<Ext> exts)
    {
        ulong alloced = (exts.Count == 0 ? 0 : exts[^1].End) * Bs;
        ulong sparse = 0;
        foreach (var x in exts) if (x.Phys == 0) sparse += x.Blocks * Bs;
        var old = ino.Find(Records.XtDstream);
        ulong written = old is null ? 0 : Bin.U64(old.Data, 24), read = old is null ? 0 : Bin.U64(old.Data, 32);
        ulong defaultCrypto = old is null ? NewDefaultCryptoId : Bin.U64(old.Data, 16); // var olan değer korunur
        ino.Set(Records.XtDstream, Records.XfSystemField, Records.Dstream(size, alloced, defaultCrypto, written, read));
        if (sparse > 0)
        {
            var sb = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(sb, sparse);
            ino.Set(Records.XtSparseBytes, Records.XfSystemField | Records.XfChildrenInherit, sb);
            ino.InternalFlags |= Records.InodeIsSparse;
        }
        else
        {
            ino.Remove(Records.XtSparseBytes);
            ino.InternalFlags &= ~Records.InodeIsSparse;
        }
        // Dosya veri akışının başvuru kaydı (ilk kez veri akışı oluşurken)
        if (!_fs.TryGet(Records.Key(ino.PrivateId, JType.DstreamId), out _))
            FsPut(Records.Key(ino.PrivateId, JType.DstreamId), Records.DstreamIdVal(1));
        long now = NowNs();
        if (!_keepModTime) ino.ModNs = now;
        ino.ChangeNs = now;
        PutInode(id, ino);
        Touch();
    }

    // İçerik değişikliği değiştirme zamanına dokunmasın (yalnız Write/SetSize'ın değişiklik evresinde; bkz. keepModTime)
    private bool _keepModTime;

    private void MutateContent(bool keepModTime, Action change) => Mutate(() =>
    {
        _keepModTime = keepModTime;
        try { change(); }
        finally { _keepModTime = false; }
    });

    // ---------------------------------------------------------------------------------------------------------------
    // Uzatmada eski dosya sonunun ötesi

    /// <summary>
    /// Uzatmanın sıfırlaması. macOS dosya sonunun ötesinde blok ayırabilir (ön ayırma); o blokların içeriği sıfırlanmamıştır
    /// (silinmiş başka bir dosyanın verisi). Dosya büyürken o bölge görünür olur: eski sondan sonraki tam bloklar deliğe çevrilir
    /// (bloklar bırakılır, yazım yok); eski son blok kısmen doluysa ve sondan sonrası sıfır değilse o blok, sonu sıfırlanmış olarak
    /// yeni bloğa yazılır (Windows'ta yazılmış dosyada zaten sıfırdır: yazım yok).
    /// </summary>
    private sealed record StalePlan(ulong HoleFrom, ulong HoleTo, ulong TailBlock, byte[]? Tail, OpCost Cost, ulong DataBlocks);

    /// <summary>
    /// Denetim evresinde (hiçbir şey değişmez): <paramref name="size"/> boylu dosya büyürken görünür olacak eski bölgenin planı;
    /// sıfırlanacak bir şey yoksa null. <paramref name="upTo"/>: işlemin kendisinin baştan yazmadığı bölgenin sonu (blok; boy
    /// büyütmede yeni blok sayısı, yazmada yazımın ilk bloğu — o blok tampona dosya sonundan sonrası sıfır okunarak yazılır).
    /// Bedel: delik bir parçalık yazım gibi (kesişen extent'ler ve fiziksel kayıtları), son blok bir yazım ve bir parça daha
    /// (ayırması işlemin kendi ayırmasından önce yapılır).
    /// </summary>
    private StalePlan? PlanStaleZeroing(List<Ext> exts, ulong size, ulong upTo)
    {
        ulong allocEnd = exts.Count == 0 ? 0 : exts[^1].End;
        ulong holeFrom = (size + Bs - 1) / Bs, holeTo = Math.Min(upTo, allocEnd);
        bool punch = holeTo > holeFrom && exts.Any(x => x.Phys != 0 && x.End > holeFrom && x.Start < holeTo);
        ulong tailBlock = size / Bs;
        byte[]? tail = null;
        if (size % Bs != 0 && tailBlock < upTo && exts.FirstOrDefault(x => x.Phys != 0 && x.Start <= tailBlock && tailBlock < x.End) is { Phys: not 0 } t)
        {
            var block = new byte[Bs];
            _s.Reader.ReadBlocks(t.Phys + (tailBlock - t.Start), 0, block);
            Crypto?.DecryptData(block, t.CryptoAt(tailBlock - t.Start), 0);
            var after = block.AsSpan((int)(size % Bs));
            if (after.IndexOfAnyExcept((byte)0) >= 0)
            {
                after.Clear();
                tail = block;
            }
            else System.Security.Cryptography.CryptographicOperations.ZeroMemory(block);
        }
        if (!punch && tail is null) return null;
        var cost = default(OpCost);
        ulong data = 0;
        if (punch) cost += WriteCost(1, Overlap(exts, holeFrom, holeTo), MergeablePairs(exts));
        if (tail is not null)
        {
            var (blocks, runs) = NewBlocksPlan(exts, tailBlock, tailBlock + 1);
            // Yeniden yazımın ReplaceRange'i bütün listenin birleşebilen çiftlerini birleştirir; delik bunu zaten yaptıysa sayılmaz
            cost += WriteCost(runs + 1, Overlap(exts, tailBlock, tailBlock + 1), punch ? 0 : MergeablePairs(exts));
            data = blocks;
        }
        return new StalePlan(holeFrom, punch ? holeTo : holeFrom, tailBlock, tail, cost, data);
    }

    /// <summary>Değişiklik evresinde <see cref="PlanStaleZeroing"/>'in planı: delik, sonra son blok (boy henüz değişmez).</summary>
    private void ApplyStaleZeroing(ulong id, ulong streamId, List<Ext> exts, StalePlan p, ulong size)
    {
        if (p.HoleTo > p.HoleFrom) ReplaceRange(streamId, exts, p.HoleFrom, p.HoleTo - p.HoleFrom, 0);
        if (p.Tail is not null)
        {
            ReplaceBlocks(id, streamId, exts, p.TailBlock, p.Tail, size);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(p.Tail);
        }
    }

    private InodeRecord RequireWritableFile(ulong id)
    {
        var ino = GetInode(id) ?? throw new ApfsException(ApfsErrorCode.NotFound, $"inode {id}");
        if ((ino.Mode & Inode.ModeTypeMask) != Inode.ModeRegular) throw new ApfsException(ApfsErrorCode.InvalidArgument, CoreText.T("VolWriter.NotAFile"));
        if ((ino.BsdFlags & Inode.BsdFlagCompressed) != 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("VolWriter.CompressedReadOnly"));
        if (IsCloned(ino))
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("VolWriter.ClonedReadOnly"));
        return ino;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dışa açık işlemler

    /// <summary>
    /// İçeriği bu sürümde değiştirilemeyen dosya mı (macOS'un sıkıştırdığı ya da klon; <see cref="RequireWritableFile"/> ile aynı
    /// kural, salt sorgu). Ad değiştirme, silme, zaman ve gizli bayrağı bu dosyalarda da serbesttir.
    /// </summary>
    public bool IsContentReadOnly(ulong id) =>
        GetInode(id) is { } ino && (ino.Mode & Inode.ModeTypeMask) == Inode.ModeRegular &&
        ((ino.BsdFlags & Inode.BsdFlagCompressed) != 0 || IsCloned(ino));

    /// <summary>Dosyanın görünen boyutu (oturumun güncel görüntüsü).</summary>
    public ulong GetSize(ulong id) => GetInode(id)?.Dstream?.Size ?? 0;

    /// <summary>
    /// Testler: macOS'un ön ayırmasını (F_PREALLOCATE) taklit eder — dosyanın boyu <paramref name="size"/> olur, blokları ve
    /// içerikleri dosyada kalır (ayrılan boyut değişmez; dosya sonunun ötesindeki baytlar sıfırlanmaz). Ürün kodu kullanmaz.
    /// </summary>
    internal void SetSizeKeepingBlocks(ulong id, ulong size)
    {
        var ino = RequireWritableFile(id);
        var exts = LoadExtents(ino.PrivateId);
        if (size > (exts.Count == 0 ? 0 : exts[^1].End) * Bs) throw new ArgumentOutOfRangeException(nameof(size));
        EnsureSpace(Room.Metadata, ResizeCost);
        Mutate(() => UpdateStream(id, GetInode(id)!, size, exts));
    }

    /// <summary>
    /// Testler: macOS'un satır içi (gömülü) xattr'ını taklit eder — <paramref name="id"/>'ye <paramref name="length"/> baytlık
    /// XATTR_DATA_EMBEDDED değerli <paramref name="name"/> kaydı (macOS 3 804 bayta dek gömer; büyük kayıt B-ağacı bölünmesini
    /// sınar). Ürün kodu xattr yazmaz.
    /// </summary>
    internal void SetEmbeddedXattrForTest(ulong id, string name, int length)
    {
        if (GetInode(id) is null) throw new ApfsException(ApfsErrorCode.NotFound, $"inode {id}");
        var key = Records.XattrKey(id, name);
        var val = new byte[4 + length];
        BinaryPrimitives.WriteUInt16LittleEndian(val, Records.XattrDataEmbedded);
        BinaryPrimitives.WriteUInt16LittleEndian(val.AsSpan(2), (ushort)length);
        for (int i = 0; i < length; i++) val[4 + i] = (byte)('a' + i % 26);
        EnsureSpace(Room.Metadata, new OpCost(1, 1, 1, (ulong)(key.Length + val.Length + 8)));
        Mutate(() => { FsPut(key, val); Touch(); });
    }

    /// <summary>Dosyadan okur (delikler ve dosya sonu ötesi sıfır); okunan bayt sayısını döndürür.</summary>
    public int Read(ulong id, ulong offset, Span<byte> buffer)
    {
        var ino = GetInode(id) ?? throw new ApfsException(ApfsErrorCode.NotFound, $"inode {id}");
        ulong size = ino.Dstream?.Size ?? 0;
        if (offset >= size) return 0;
        int n = (int)Math.Min((ulong)buffer.Length, size - offset);
        buffer[..n].Clear();
        foreach (var x in LoadExtents(ino.PrivateId))
        {
            ulong xs = x.Start * Bs, xe = x.End * Bs;
            ulong a = Math.Max(xs, offset), b = Math.Min(xe, offset + (ulong)n);
            if (a >= b || x.Phys == 0) continue;
            if (Crypto is null)
            {
                _s.Reader.ReadBlocks(x.Phys, (long)(a - xs), buffer.Slice((int)(a - offset), (int)(b - a)));
                continue;
            }
            // Şifreli: kapsayan bloklar okunur, bloğun crypto_id'siyle çözülür, istenen kısım kopyalanır
            ulong k0 = (a - xs) / Bs, k1 = (b - xs + Bs - 1) / Bs;
            var blocks = new byte[(k1 - k0) * Bs];
            _s.Reader.ReadBlocks(x.Phys + k0, 0, blocks);
            Crypto.DecryptData(blocks, x.CryptoAt(k0), 0);
            blocks.AsSpan((int)(a - xs - k0 * Bs), (int)(b - a)).CopyTo(buffer[(int)(a - offset)..]);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(blocks);
        }
        return n;
    }

    /// <summary>
    /// Dosyaya yazar (gerekirse uzatır). Kısmi ilk/son blok mevcut içerikle tamamlanır; bloklar <see cref="ReplaceBlocks"/> ile
    /// yeni bloklara yazılır. Gereken yeni bloklar önce boş alanla karşılaştırılır (sığmazsa hiçbir şey değişmeden DiskFull).
    /// <paramref name="keepModTime"/>: değiştirme zamanı değişmez (değişiklik zamanı yine "şimdi") — Windows'ta tanıtıcıda yazma
    /// zamanı açıkça ayarlandıysa o tanıtıcının sonraki yazımları zamanı güncellemez (NTFS; 1.9.1).
    /// </summary>
    public void Write(ulong id, ulong offset, ReadOnlySpan<byte> data, bool keepModTime = false)
    {
        if (data.IsEmpty) return;
        // Denetim (hiçbir şey değişmez; bkz. Mutate)
        var ino = RequireWritableFile(id);
        if (offset > MaxFileSize || (ulong)data.Length > MaxFileSize - offset) RequireWithinMaxSize(ulong.MaxValue);
        ulong size = ino.Dstream?.Size ?? 0;
        ulong end = offset + (ulong)data.Length;
        ulong first = offset / Bs, last = (end + Bs - 1) / Bs;
        var buf = new byte[(last - first) * Bs];
        bool readFirst = offset % Bs != 0 && first * Bs < size;
        if (readFirst) Read(id, first * Bs, buf.AsSpan(0, (int)Bs));
        if (end % Bs != 0 && (last - 1) * Bs < size && !(last - 1 == first && readFirst))
            Read(id, (last - 1) * Bs, buf.AsSpan((int)((last - 1 - first) * Bs), (int)Bs));
        data.CopyTo(buf.AsSpan((int)(offset - first * Bs)));
        var exts = LoadExtents(ino.PrivateId);
        var (newBlocks, runs) = NewBlocksPlan(exts, first, last);
        var cost = WriteCost(runs, Overlap(exts, first, last), MergeablePairs(exts));
        // Dosya sonunun ötesine yazım: aradaki eski bölge sıfır okunmalı
        var stale = end > size ? PlanStaleZeroing(exts, size, first) : null;
        if (stale is not null) { cost += stale.Cost; newBlocks += stale.DataBlocks; }
        EnsureSpace(newBlocks > 0 ? Room.Data : Room.Metadata, cost, newBlocks);
        MutateContent(keepModTime, () =>
        {
            if (stale is not null) ApplyStaleZeroing(id, ino.PrivateId, exts, stale, size);
            ReplaceBlocks(id, ino.PrivateId, exts, first, buf, Math.Max(size, end));
        });
    }

    /// <summary>
    /// [first, first + buf/blok) mantıksal bloklarının içeriğini <paramref name="buf"/> ile değiştirir (tam bloklar) ve boyutu
    /// <paramref name="newSize"/> yapar. Bu işlemde ayrılmış bloklar yerinde, diğerleri yeni bloklara yazılır. Değişiklik evresidir:
    /// çağıran yeni blok gereksinimini önceden denetlemiş olmalı (<see cref="EnsureSpace"/>, <see cref="NewBlocksPlan"/>); <paramref name="exts"/> akışın güncel
    /// extent listesi (yerinde güncellenir).
    /// </summary>
    private void ReplaceBlocks(ulong id, ulong streamId, List<Ext> exts, ulong first, byte[] buf, ulong newSize)
    {
        ulong last = first + (ulong)buf.Length / Bs;
        ulong blk = first;
        int pos = 0;
        while (blk < last)
        {
            var inPlace = exts.FirstOrDefault(x => x.Phys != 0 && x.Start <= blk && blk < x.End && _s.Spaceman.IsAllocatedThisTx(x.Phys + (blk - x.Start)));
            if (inPlace.Phys != 0)
            {
                ulong run = 1;
                while (blk + run < Math.Min(last, inPlace.End) && _s.Spaceman.IsAllocatedThisTx(inPlace.Phys + (blk + run - inPlace.Start))) run++;
                _s.WriteData(this, inPlace.Phys + (blk - inPlace.Start), buf.AsSpan(pos, (int)(run * Bs)), inPlace.CryptoAt(blk - inPlace.Start));
                blk += run; pos += (int)(run * Bs);
                continue;
            }
            var (p, cnt) = AllocData(SegmentEnd(exts, blk, last) - blk);
            _s.WriteData(this, p, buf.AsSpan(pos, (int)(cnt * Bs)), NewCryptoId(p));
            ReplaceRange(streamId, exts, blk, cnt, p, NewCryptoId(p));
            blk += cnt; pos += (int)(cnt * Bs);
        }
        UpdateStream(id, GetInode(id)!, newSize, exts);
    }

    /// <summary>
    /// Dosya boyutunu değiştirir: kısaltmada sondaki bloklar bırakılır ve son bloğun kalan kısmı sıfırlanır; uzatmada delik eklenir.
    /// Kısaltmada sıfırlanan son kısmi blok yeni bloğa yazılır: o blok önceden denetlenir (disk tam doluysa hiçbir şey değişmeden
    /// DiskFull; önceden kısaltma yapılıp sıfırlama yarıda kalıyordu). Kesilen bloklardan bu işlemde ayrılmış olanlar hemen boşa
    /// çıktığı için o bloğa sayılır (önceden paydaki diskte böyle kısaltma "disk dolu" alıyordu). Yer açan kısaltma payı
    /// kullanabilir, seyrek uzatma yalnız üst veri büyütür (bkz. <see cref="Room"/>). <paramref name="keepModTime"/>: bkz.
    /// <see cref="Write"/>.
    /// </summary>
    public void SetSize(ulong id, ulong newSize, bool keepModTime = false)
    {
        // Denetim (hiçbir şey değişmez; bkz. Mutate)
        var ino = RequireWritableFile(id);
        RequireWithinMaxSize(newSize);
        ulong size = ino.Dstream?.Size ?? 0;
        if (newSize == size && ino.Dstream is not null) return;
        var exts = LoadExtents(ino.PrivateId);
        ulong allocEnd = exts.Count == 0 ? 0 : exts[^1].End;
        ulong newBlocks = (newSize + Bs - 1) / Bs;

        var room = newSize < size ? Room.Release : Room.Metadata;
        var cost = ResizeCost;
        ulong credit = 0;
        if (newBlocks < allocEnd)
        {
            cost = TruncateCost(id, ino.PrivateId, exts, newBlocks);
            room = Room.Release;
            foreach (var x in exts)
            {
                if (x.Phys == 0 || x.End <= newBlocks) continue;
                ulong from = Math.Max(x.Start, newBlocks);
                credit += _s.Spaceman.CountAllocatedThisTx(x.Phys + (from - x.Start), x.End - from); // blok blok değil
            }
        }
        else if (newBlocks > allocEnd) cost = ExtendCost(MergeablePairs(exts));

        // Kısaltmada son kısmi bloğun dosya sonundan sonrası sıfırlanmalı (sonradan uzatılırsa eski bayt görünmesin).
        // Blok bir delikse zaten sıfırdır. İçeriği (yeni sona dek) değişiklikten önce okunur.
        ulong lastBlock = newSize / Bs;
        byte[]? tail = null;
        ulong data = 0;
        if (newSize < size && newSize % Bs != 0 && exts.Any(x => x.Phys != 0 && x.Start <= lastBlock && lastBlock < x.End))
        {
            tail = new byte[Bs];
            Read(id, lastBlock * Bs, tail.AsSpan(0, (int)(newSize - lastBlock * Bs)));
            var (tb, truns) = NewBlocksPlan(exts, lastBlock, lastBlock + 1);
            cost += WriteCost(truns, Overlap(exts, lastBlock, lastBlock + 1), MergeablePairs(exts));
            if (tb > 0) { data = tb; room = Room.Data; }
        }
        // Uzatmada eski sondan yeni sona dek sıfır okunmalı: ön ayrılmış bloklar deliğe, kirli son blok yeniden
        var stale = newSize > size ? PlanStaleZeroing(exts, size, newBlocks) : null;
        if (stale is not null)
        {
            cost += stale.Cost;
            if (stale.DataBlocks > 0) { data += stale.DataBlocks; room = Room.Data; }
        }
        EnsureSpace(room, cost, data, credit);

        MutateContent(keepModTime, () =>
        {
            if (newBlocks < allocEnd) TruncateExtents(ino.PrivateId, exts, newBlocks);
            if (stale is not null) ApplyStaleZeroing(id, ino.PrivateId, exts, stale, size);
            if (newBlocks > allocEnd) ReplaceRange(ino.PrivateId, exts, allocEnd, newBlocks - allocEnd, 0);
            UpdateStream(id, GetInode(id)!, newSize, exts);
            if (tail is not null) ReplaceBlocks(id, ino.PrivateId, exts, lastBlock, tail, newSize);
        });
    }
}
