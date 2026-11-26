using Apfs.Container;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Diagnostics;

public static partial class FullCheck
{
    /// <summary>
    /// Tam denetimin dosya sistemi nesnesi (dosya, klasör, bağ, öbür) başına tuttuğu bellek, bayt (1.9.1; ölçüm: 100 000
    /// dosyalı 1 GB görüntüde CLI'nin geçtiği en küçük GC yığın sınırı, <c>PerfProbeTests.MeasureFullCheck</c>, bkz. plan devir notu).
    /// </summary>
    internal const long BytesPerObject = 1024;

    private static readonly AsyncLocal<long?> BudgetForTest = new();

    /// <summary>Yalnız testler: bu akışta bütçe (null = gerçek).</summary>
    internal static void SetMemoryBudgetForTest(long? bytes) => BudgetForTest.Value = bytes;

    /// <summary>
    /// Tam denetimin bellek tahmini ve bütçesi: en büyük birimin süper bloğu sayaçlarından (dosya + klasör + sembolik bağ +
    /// öbür nesne) × <see cref="BytesPerObject"/>; bütçe min(2 GiB, kullanılabilir fiziksel bellek / 4). Kapı tahmin bütçeyi aşarsa
    /// tam denetimi hiç başlatmaz (milyonlarca dosyalı diskte hizmet belleği tüketip donmasın); CLI uyarıp yine dener.
    /// </summary>
    public static (long Needed, long Budget) MemoryEstimate(ApfsContainer c)
    {
        ulong objects = 0;
        foreach (ulong oid in c.Superblock.FsOids)
        {
            try
            {
                var v = c.Reader.ReadObject(c.Omap.Resolve(oid, c.Xid).Paddr, ApfsConstants.TypeFs, oid);
                // Birimler sırayla denetlenir; bir birimin kayıtları sonrakine geçince bırakılır → tepe en büyük birim
                ulong volume = 0;
                for (int off = 184; off <= 208; off += 8) volume = SatAdd(volume, Bin.U64(v, off));
                objects = Math.Max(objects, volume);
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { } // okunamayan birimi tam denetim bildirir
        }
        long needed = objects > (ulong)(long.MaxValue / BytesPerObject) ? long.MaxValue : (long)objects * BytesPerObject;
        long budget = BudgetForTest.Value ?? Math.Min(2L << 30, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);
        return (needed, budget);
    }

    private static ulong SatAdd(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;

    /// <summary>
    /// Dosya sistemi kayıtlarının apfsck'ten alınan biçim kuralları (1.9.1; apfsck inode.c parse_inode_xfields,
    /// check_xfield_flags, parse_inode_record; xattr.c parse_decmpfs; compress.c; extents.c crypto_state başvuruları). Yazıcı bu
    /// kayıtları çözüp yeniden yazar ya da onlara göre karar verir: biçimi bozuk xfield blob'u yanlış adı/veri akışını okutur,
    /// sıkıştırma bayrağı ile decmpfs uyuşmazsa dosya "sıkıştırılmış" sanılır ya da sanılmaz, şifresiz birimde crypto_id'li extent
    /// şifre çözülmeye kalkışılan veridir. Kural başına ilk 5 kayıt adıyla, fazlası toplamla yazılır.
    /// </summary>
    private sealed class RecordRules(string label, bool encrypted, CheckReport r)
    {
        private const ulong CryptoUnassigned = ulong.MaxValue;
        private const ulong InodeWasCloned = 0x10, InodeIsSparse = 0x200, InodeHasPurgeableFlags = 0x02000000;
        private const uint BsdCompressed = 0x20;
        private const ushort XattrEmbedded = 0x2;
        private const byte XfReserved = 0x04 | 0x40 | 0x80, XfUser = 0x10, XfSystem = 0x20;
        private const byte XfDoNotCopy = 0x02, XfChildrenInherit = 0x08;

        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly HashSet<ulong> _decmpfs = [], _compressed = [];

        private void Report(string key, string text)
        {
            int n = _counts.GetValueOrDefault(key) + 1;
            _counts[key] = n;
            if (n <= 5) r.Error(text);
        }

        /// <summary>Kural başına toplamlar (5'i aşanlar).</summary>
        public void Finish()
        {
            foreach (ulong id in _compressed)
                if (!_decmpfs.Contains(id)) Report("Check.CompressedNoDecmpfs", CoreText.T("Check.CompressedNoDecmpfs", label, id));
            foreach (ulong id in _decmpfs)
                if (!_compressed.Contains(id)) Report("Check.DecmpfsNotCompressed", CoreText.T("Check.DecmpfsNotCompressed", label, id));
            foreach (var (key, n) in _counts)
                if (n > 5) r.Error(CoreText.T("Check.RuleTotal", label, key, n));
        }

        /// <summary>Inode kaydı: xfield düzeni, bayrak ↔ xfield, sıkıştırılmamış boy alanı, veri akışının crypto_id'si.</summary>
        public void Inode(ulong id, Inode ino, byte[] v)
        {
            if ((ino.BsdFlags & BsdCompressed) != 0) _compressed.Add(id);
            if (!ino.HasUncompressedSize && ino.UncompressedSize != 0)
                Report("Check.UncompressedSizeWithoutFlag", CoreText.T("Check.UncompressedSizeWithoutFlag", label, id));
            if (XFieldProblem(v, out var types, out ulong cryptoId) is { } why)
            {
                Report("Check.InodeXFields", CoreText.T("Check.InodeXFields", label, id, why));
                return;
            }
            if (types.Contains(InodeXField.SparseBytes) != ((ino.InternalFlags & InodeIsSparse) != 0)
                || types.Contains(15) != ((ino.InternalFlags & InodeHasPurgeableFlags) != 0))
                Report("Check.InodeXFieldFlags", CoreText.T("Check.InodeXFieldFlags", label, id));
            if (!encrypted && cryptoId is not (0 or CryptoSwId)
                && !(cryptoId == CryptoUnassigned && (ino.InternalFlags & InodeWasCloned) != 0))
                Report("Check.CryptoIdOnPlain", CoreText.T("Check.CryptoIdOnPlain", label, id, cryptoId));
        }

        /// <summary>Öznitelik kaydı: decmpfs'in sahibi; şifresiz birimde öznitelik veri akışının crypto_id'si.</summary>
        public void Xattr(ulong owner, byte[] key, byte[] val)
        {
            if (key.Length >= 10 && Bin.Utf8Z(Bin.Slice(key, 10, key.Length - 10)) == "com.apple.decmpfs") _decmpfs.Add(owner);
            // Ya gömülü ya veri akışı (apfsck xattr.c): ikisi de ya da hiçbiri olursa yazıcı silerken başka nesnenin akışını sanabilir
            if (val.Length >= 2 && (Bin.U16(val, 0) & (ApfsVolume.XattrDataStream | XattrEmbedded)) is not (ApfsVolume.XattrDataStream or XattrEmbedded))
                Report("Check.XattrKind", CoreText.T("Check.XattrKind", label, owner));
            if (!encrypted && val.Length >= 4 + 32 && (Bin.U16(val, 0) & ApfsVolume.XattrDataStream) != 0
                && Bin.U64(val, 4 + 24) is not (0 or CryptoSwId) and var c)
                Report("Check.CryptoIdOnPlain", CoreText.T("Check.CryptoIdOnPlain", label, owner, c));
        }

        /// <summary>Dosya extent'i: şifresiz birimde crypto_id 0 (ya da CRYPTO_SW_ID) olmalı.</summary>
        public void Extent(ulong streamId, byte[] val)
        {
            if (!encrypted && val.Length >= 24 && Bin.U64(val, 16) is not (0 or CryptoSwId) and var c)
                Report("Check.CryptoIdOnPlain", CoreText.T("Check.CryptoIdOnPlain", label, streamId, c));
        }

        /// <summary>crypto_state kaydı: şifresiz birimde olamaz.</summary>
        public void CryptoState(ulong id)
        {
            if (!encrypted) Report("Check.CryptoStateOnPlain", CoreText.T("Check.CryptoStateOnPlain", label, id));
        }

        /// <summary>
        /// xf_blob düzeni (apfsck parse_inode_xfields): en az bir alan; kullanılan veri boyu kalanla aynı; ayrılmış bayrak yok, hem
        /// kullanıcı hem çekirdek bayrağı yok; tür tanınıyor (ayrılmış 6, 9, 12 değil), her tür bir kez; boy türünden (ad NUL'a dek),
        /// bilinen türlerde bayraklar sabit; dolgu sıfır ve alanlar değeri tam doldurur. Sorun yoksa null.
        /// </summary>
        private static string? XFieldProblem(byte[] v, out HashSet<byte> types, out ulong cryptoId)
        {
            types = [];
            cryptoId = 0;
            const int Base = 92;
            if (v.Length <= Base) return null;
            int len = v.Length - Base - 4;
            if (len < 0) return "blob";
            int count = Bin.U16(v, Base), used = Bin.U16(v, Base + 2);
            if (count == 0) return "count 0";
            len -= count * 4;
            if (len < 0) return "count";
            if (used != len) return "used";
            int val = Base + 4 + count * 4;
            for (int i = 0; i < count; i++)
            {
                int h = Base + 4 + i * 4;
                byte type = v[h], flags = v[h + 1];
                int size = Bin.U16(v, h + 2);
                if ((flags & XfReserved) != 0 || ((flags & XfUser) != 0 && (flags & XfSystem) != 0)) return $"flags {type}";
                int expect = type switch
                {
                    InodeXField.SnapXid or InodeXField.DeltaTreeOid or InodeXField.PrevFsize or InodeXField.DirStatsKey
                        or InodeXField.SparseBytes or 15 or 16 => 8,
                    InodeXField.DocumentId or InodeXField.Rdev => 4,
                    InodeXField.FinderInfo => 32,
                    InodeXField.FsUuid => 16,
                    InodeXField.Dstream => 40,
                    InodeXField.Name => NameLength(v, val, v.Length - val),
                    _ => -1,
                };
                if (expect < 0) return type == InodeXField.Name ? "name" : $"type {type}";
                if (!types.Add(type)) return $"duplicate {type}";
                if (size != expect) return $"size {type}";
                byte? fixedFlags = type switch
                {
                    InodeXField.Name => XfDoNotCopy,
                    InodeXField.Dstream => XfSystem,
                    InodeXField.SparseBytes => XfSystem | XfChildrenInherit,
                    InodeXField.DirStatsKey => XfSystem | XfDoNotCopy,
                    _ => null,
                };
                if (fixedFlags is { } ff && flags != ff) return $"flags {type}";
                if (type == InodeXField.Dstream && val + 24 <= v.Length) cryptoId = Bin.U64(v, val + 16);
                int pad = ((size + 7) & ~7) - size;
                len -= size + pad;
                if (len < 0) return "overflow";
                val += size;
                for (int k = 0; k < pad; k++) if (v[val + k] != 0) return "padding";
                val += pad;
            }
            return len != 0 ? "length" : null;
        }

        /// <summary>Ad alanının NUL dahil boyu (apfsck read_name_xfield: kalan değer içinde NUL'a dek); NUL yoksa -1.</summary>
        private static int NameLength(byte[] v, int at, int max)
        {
            if (max <= 0 || at >= v.Length) return -1;
            int nul = v.AsSpan(at, Math.Min(max, v.Length - at)).IndexOf((byte)0);
            return nul < 0 ? -1 : nul + 1;
        }
    }
}
