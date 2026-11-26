using Apfs.Container;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Diagnostics;

/// <summary>Tam denetimin sonucu. Hatalar diskin tutarsız olduğunu, uyarılar olağan dışı ama zararsız durumları gösterir.</summary>
public sealed class CheckReport
{
    private const int MaxErrors = 500;

    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Info { get; } = [];
    public int SuppressedErrors { get; private set; }

    /// <summary>Yazıcının bu sürümde güncel tutamadığı özellikler (ör. klasör boyut istatistikleri); yazma kapısı bunlarda kapanır.</summary>
    public List<string> WriteBlockers { get; } = [];

    /// <summary>Bir birimde çözümlenmeyen yapılar var (bitmemiş şifreleme işlemi, anlık görüntü, mühür…); blok sahipliği tam denetlenemedi.</summary>
    public bool OwnershipIncomplete { get; internal set; }
    public bool IsClean => Errors.Count == 0;

    /// <summary>Tırnak içindeki adları (dosya/klasör adları) "…" ile değiştirir; kişisel adlar günlüğe ve rapora girmesin.</summary>
    public static string HideNames(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\"[^\"]*\"", "\"…\"");

    internal void Error(string s)
    {
        if (Errors.Count < MaxErrors) Errors.Add(s);
        else SuppressedErrors++;
    }
}

/// <summary>
/// `apfs check --full`: kapsayıcının yapısal tutarlılığını denetler; yazma kapısı da bunu kullanır. Salt okunurdur.
/// Kapsam (apfsck'nin bir alt kümesi; geliştirmede asıl hakem apfsck'dir):
/// <list type="bullet">
/// <item>checkpoint tamamlığı (bkz. <see cref="Checkpoint"/>) ve blok 0 kopyası;</item>
/// <item>alan yöneticisi sayaçları, boş kuyruklar;</item>
/// <item><b>blok sahipliği</b>: her blok ya tam bir kez kullanılıyor ya da boş; sonuç diskteki bitmap'lerle (ana aygıt ve iç havuz)
/// bit bit karşılaştırılır;</item>
/// <item>nesne haritası kayıtlarının hepsi kullanılıyor; birim blok sayacı;</item>
/// <item>dosya sistemi: dizin girdisi → inode, tür, ad karması, klasör çocuk sayısı, bağlantı sayısı, birim sayaçları,
/// veri akışı extent'leri (0'dan başlar, boşluksuz, ayrılan boyutla eşit), dstream ve fiziksel extent başvuru sayıları.</item>
/// </list>
/// Kilidi açılmamış şifreli birimlerin dosya sistemi ağacı okunamaz; bloklarının sahipliği nesne haritasından ve extentref
/// ağacından (ikisi de şifresiz) yine denetlenir.
/// Şifreli birim kuralları (apfsck btree.c: omap kaydının ENCRYPTED bayrağı iki yönde; içerik denetimi apfsck'de yok, macOS'un
/// gerçek birimlerinden çıkarıldı — fixtures/crypto-survey.txt): her fs ağacı düğümünün omap kaydı ENCRYPTED taşır, şifresiz
/// birimde hiçbiri taşımaz; düğümün kendi o_type'ında OBJ_ENCRYPTED yalnız şifreli birimin fs ağacı düğümlerinde vardır (apfsck
/// object.c; iki yönlü; birimin omap, extentref ve snap_meta düğümlerinde asla; fs ağacı ancak anahtarla denetlenir);
/// anahtarla açılmış birimde (şifrelemesi sonradan değiştirilmemişse — ENC_ROLLED birimde şifresiz kalmış
/// eski veri crypto_id 0 taşır, Seagate'te 10 extent) delik olmayan her dosya/öznitelik extent'inin crypto_id'si 0 ya da 4 değildir
/// ve FEXT_CRYPTO_ID_IS_TWEAK bayrağını taşır; tek anahtarlı (ONEKEY) birimde yalnız CRYPTO_SW_ID'nin (4) boş crypto_state kaydı
/// olabilir (macOS onu koyar; başkası dosya başına anahtar demektir).
/// </summary>
public static partial class FullCheck
{
    /// <param name="keys">Kilidi açılmış birim anahtarları (parola yeniden sorulmaz); şifreli birimin dosya sistemi ancak anahtarla denetlenir.</param>
    public static CheckReport Run(ApfsContainer c, Func<VolumeLockInfo, string?>? passwords = null, IEnumerable<Crypto.VolumeKey>? keys = null)
    {
        var r = new CheckReport();
        try
        {
            RunCore(c, passwords, keys, r);
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
        {
            r.Error(CoreText.T("Check.Incomplete", e.Detail ?? e.Message));
        }
        return r;
    }

    private static void RunCore(ApfsContainer c, Func<VolumeLockInfo, string?>? passwords, IEnumerable<Crypto.VolumeKey>? keys, CheckReport r)
    {
        var sb = c.Superblock;
        var reader = c.Reader;
        foreach (var w in c.Warnings) r.Error(CoreText.T("Check.ContainerWarning", w));
        if (c.PartitionOverlapsOther) r.Error(CoreText.T("Check.PartitionOverlapsOther"));

        // ---- checkpoint ----
        var cp = Checkpoint.Read(c);
        foreach (var p in cp.Problems) r.Error($"checkpoint: {p}");
        if (!cp.IsValid) return;

        // Sonraki işlem ve nesne kimlikleri (1.9.1; apfsck super.c, object.c): yazıcı yeni checkpoint'i next_xid'le, yeni
        // sanal/geçici nesneleri next_oid'den yazar. next_xid ≤ xid eski xid'li checkpoint yazdırır (açılışta eskisi seçilebilir);
        // kullanılan bir oid'e eşit/küçük next_oid var olan nesneyle çakışan nesne yazdırır (veri kaybı). apfsck next_xid = xid + 1
        // ister; Mac'in boşluklu olası değeri reddedilmesin diye yalnız güvenlik koşulu (>) aranır.
        if (sb.NextXid <= sb.Xid) r.Error(CoreText.T("Check.NextXid", sb.NextXid, sb.Xid));
        OidsBelowNext(r, CoreText.T("Check.LabelCheckpointMap"), cp.Mappings.Select(m => m.Oid), sb.NextOid);

        var block0 = reader.ReadBlock(0);
        if (!Fletcher64.Verify(block0)) r.Warnings.Add(CoreText.T("Check.Block0Corrupt"));
        else if (Bin.U64(block0, 16) != sb.Xid) r.Warnings.Add(CoreText.T("Check.Block0Stale", Bin.U64(block0, 16)));

        // ---- alan yöneticisi ----
        var sm = Spaceman.Read(c, cp);
        var chunks = sm.MainChunks();
        if (sm.Main.BlockCount != sb.BlockCount) r.Error(CoreText.T("Check.SpacemanBlockCount"));
        if ((ulong)chunks.Count != sm.Main.ChunkCount) r.Error(CoreText.T("Check.SpacemanChunkCount"));
        if (sm.Tier2.BlockCount != 0) r.Error(CoreText.T("Check.SpacemanTier2"));
        CheckChunkGeometry(sm, chunks, r);
        CheckSpacemanLayout(sm, r);

        var used = new BlockSet(sb.BlockCount);
        var ipUsed = new BlockSet(sm.IpBlockCount);
        // Sağlam diskte her blok bir kez işaretlenir: toplam işaret kümenin boyunu aşarsa üst üste binme kesindir; ondan sonra
        // işaretlenmez (çakışan dev aralıklar denetimi saatlerce sürdürmesin) — tek hata yazılır
        bool usedFull = false, ipFull = false;
        void Mark(ulong start, ulong count, string what)
        {
            if (count == 0) return;
            if (start >= sb.BlockCount || count > sb.BlockCount - start) { r.Error(CoreText.T("Check.RangeOutside", what, start, count)); return; }
            if (!used.TryCharge(count)) { if (!usedFull) r.Error(CoreText.T("Check.MarkedTooMuch", what)); usedFull = true; return; }
            if (used.Mark(start, count) is ulong dup) r.Error(CoreText.T("Check.BlockUsedTwice", what, dup));
        }
        void MarkIp(ulong paddr, ulong count, string what)
        {
            if (paddr < sm.IpBase || count > sm.IpBlockCount || paddr - sm.IpBase > sm.IpBlockCount - count)
            {
                r.Error(CoreText.T("Check.BlockOutsideIp", what, paddr));
                return;
            }
            if (!ipUsed.TryCharge(count)) { if (!ipFull) r.Error(CoreText.T("Check.MarkedTooMuch", what)); ipFull = true; return; }
            if (ipUsed.Mark(paddr - sm.IpBase, count) is ulong dup) r.Error(CoreText.T("Check.IpBlockUsedTwice", what, sm.IpBase + dup));
        }

        Mark(0, 1, CoreText.T("Check.LabelBlock0"));
        Mark(sb.XpDescBase, sb.XpDescBlocks, CoreText.T("Check.LabelXpDesc"));
        Mark(sb.XpDataBase, sb.XpDataBlocks, CoreText.T("Check.LabelXpData"));
        Mark(sm.IpBmBase, sm.IpBmBlockCount, CoreText.T("Check.LabelIpBitmapRing"));
        Mark(sm.IpBase, sm.IpBlockCount, CoreText.T("Check.LabelInternalPool"));
        Mark(sb.KeylockerStart, sb.KeylockerBlockCount, CoreText.T("Check.LabelContainerKeybag"));
        Mark(sb.MkbLockerStart, sb.MkbLockerBlockCount, CoreText.T("Check.LabelMediaKeybag"));
        if (sb.KeylockerStart != 0)
        {
            // Birim anahtar depoları ('recs') da kapsayıcının bloklarıdır; yerleri kapsayıcı anahtar deposundaki etiket-3 kayıtlarında
            foreach (var e in Crypto.VolumeKeys.LoadContainerKeybag(c))
                if (e.Tag == Crypto.VolumeKeys.TagVolumeUnlockRecords)
                {
                    if (e.Data.Length < 16) { r.Error(CoreText.T("Check.VolumeKeybagRecordShort")); continue; }
                    Mark(Bin.U64(e.Data, 0), Bin.U64(e.Data, 8), CoreText.T("Check.LabelVolumeKeybag"));
                }
        }
        if (sb.EfiJumpstart != 0) MarkEfi(reader, sb.EfiJumpstart, Mark, r);
        if (sb.BlockedOutCount != 0) r.Error(CoreText.T("Check.BlockedOut"));
        if (sb.EvictMappingTreeOid != 0) r.Error(CoreText.T("Check.EvictMapping"));

        foreach (var ch in chunks) if (ch.BitmapAddr != 0) MarkIp(ch.BitmapAddr, 1, CoreText.T("Check.LabelChunkBitmap"));
        foreach (var cib in sm.MainCibAddresses()) MarkIp(cib, 1, "CIB");
        for (uint i = 0; i < sm.Main.CabCount; i++) MarkIp(sm.ArrayU64(sm.Main.AddrOffset, i), 1, "CAB");

        // Geçici nesnelerin kimlikleri tekil (1.9.1; apfsck object.c "an ephemeral object id was reused"): iki boş kuyruk aynı
        // ağacı gösterirse yazıcı ikisini yazınca checkpoint'te aynı oid'li iki eşleme olurdu
        var ephemeral = new HashSet<ulong>();
        foreach (ulong oid in new[] { sb.SpacemanOid, sb.ReaperOid, sm.FreeQueues[Spaceman.SfqIp].TreeOid, sm.FreeQueues[Spaceman.SfqMain].TreeOid })
            if (oid != 0 && !ephemeral.Add(oid)) r.Error(CoreText.T("Check.EphemeralOidReused", oid));
        CheckFreeQueues(sb, sm, Mark, MarkIp, r);

        if (cp.Find(sb.ReaperOid) is null) r.Error(CoreText.T("Check.ReaperMissing"));
        else
        {
            var reaper = ReaperState.Read(cp, sb);
            if (reaper.Problem is { } p) r.Error(p);
            foreach (var m in reaper.NotRewrittenByWriter(cp, sb))
                r.WriteBlockers.Add(CoreText.T("Check.UnknownEphemeral", m.Oid, m.Type, m.Subtype));
        }

        // ---- kapsayıcı nesne haritası ----
        var omapRecords = MarkObjectMap(c.Omap, sb.OmapOid, Mark, CoreText.T("Check.LabelContainerOmap"), out _, r);
        var fsLatest = new Dictionary<ulong, ObjectMap.Record>();
        foreach (var rec in omapRecords)
        {
            if (rec.Xid > sb.Xid) { r.Error(CoreText.T("Check.OmapFutureXid", rec.Oid, rec.Xid)); continue; }
            if (rec.Size != sb.BlockSize) r.Error(CoreText.T("Check.OmapRecordSize", rec.Oid, rec.Size));
            // Bayraklar bilinenler; kapsayıcının kayıtları şifreli olamaz (1.9.1; apfsck btree.c parse_omap_record)
            if ((rec.Flags & ~OmapValValidMask) != 0 || (rec.Flags & ApfsConstants.OmapValEncrypted) != 0)
                r.Error(CoreText.T("Check.OmapRecordFlags", CoreText.T("Check.LabelContainerOmap"), rec.Oid, rec.Flags));
            if (!fsLatest.TryAdd(rec.Oid, rec)) r.Error(CoreText.T("Check.OmapMultipleRecords", rec.Oid));
            Mark(rec.Paddr, 1, CoreText.T("Check.LabelVolumeSuperblock", rec.Oid));
        }
        OidsBelowNext(r, CoreText.T("Check.LabelContainerOmap"), omapRecords.Select(x => x.Oid), sb.NextOid);
        foreach (ulong oid in sb.FsOids)
            if (!fsLatest.ContainsKey(oid)) r.Error(CoreText.T("Check.VolumeNotInOmap", oid));
        if (fsLatest.Count != sb.FsOids.Count) r.Error(CoreText.T("Check.OmapNonVolumeRecords"));
        // Birim UUID'leri tek (1.9.1): anahtar ve parola UUID ile eşlenir
        var byUuid = new Dictionary<Guid, ulong>();
        foreach (var (oid, loc) in fsLatest)
        {
            try
            {
                var u = Bin.Uuid(reader.ReadObject(loc.Paddr, ApfsConstants.TypeFs, oid), 240);
                if (!byUuid.TryAdd(u, oid)) r.Error(CoreText.T("Check.VolumeUuidRepeated", u, byUuid[u], oid));
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { } // okunamayan birim süper bloğu aşağıda bildirilir
        }

        // ---- birimler ----
        var opened = c.OpenVolumes(passwords, keys);
        try
        {
            for (int i = 0; i < sb.FsOids.Count; i++)
            {
                ulong oid = sb.FsOids[i];
                if (!fsLatest.TryGetValue(oid, out var loc)) continue;
                var apsb = reader.ReadObject(loc.Paddr, ApfsConstants.TypeFs, oid);
                var res = opened.FirstOrDefault(x => x.Index == i);
                string label = CoreText.T("Check.LabelVolume", Bin.Utf8Z(Bin.Slice(apsb, 704, 256)));
                CheckVolume(c, apsb, res?.Volume, res?.Error, label, Mark, r);
            }
        }
        finally
        {
            foreach (var o in opened) o.Volume?.Dispose(); // şifreleyiciler bellekte kalmasın (1.9.1)
        }

        // ---- bitmap karşılaştırması ----
        CompareMain(sm, chunks, used, r);
        CompareIp(sm, ipUsed, r);
    }

    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Parça geometrisi (1.9.1; apfsck spaceman.c parse_spaceman_chunk_counts, parse_spaceman_device, parse_chunk_info,
    /// parse_chunk_info_block): CIB başına parça ve CAB başına CIB sayısı blok boyundan; parça, CIB ve CAB sayıları blok
    /// sayısından; parçalar 0'dan ardışık, sonuncusu dışında tam boy, xid'leri 0 değil; son CIB dışında her CIB dolu. Yazıcı
    /// parçayı adresten bölerek bulur: ChunksPerCib = 0 commit'i DivideByZero ile düşürüyordu, hizasız parça yanlış biti işaretlerdi.
    /// </summary>
    private static void CheckChunkGeometry(Spaceman sm, IReadOnlyList<ChunkInfo> chunks, CheckReport r)
    {
        uint bs = sm.BlockSize, bpc = sm.BlocksPerChunk;
        if (sm.ChunksPerCib != (bs - 40) / 32) r.Error(CoreText.T("Check.SmChunksPerCib", sm.ChunksPerCib, (bs - 40) / 32));
        if (sm.CibsPerCab != (bs - 40) / 8) r.Error(CoreText.T("Check.SmCibsPerCab", sm.CibsPerCab, (bs - 40) / 8));
        var dev = sm.Main;
        if (dev.ChunkCount != (dev.BlockCount + bpc - 1) / bpc) r.Error(CoreText.T("Check.SmDeviceCount", "chunk", dev.ChunkCount));
        if (sm.ChunksPerCib != 0 && dev.CibCount != (dev.ChunkCount + sm.ChunksPerCib - 1) / sm.ChunksPerCib)
            r.Error(CoreText.T("Check.SmDeviceCount", "CIB", dev.CibCount));
        if (sm.CibsPerCab != 0)
        {
            ulong cabs = (dev.CibCount + sm.CibsPerCab - 1) / sm.CibsPerCab;
            if (cabs == 1) cabs = 0; // CAB ancak biri dolabiliyorsa kullanılır
            if (dev.CabCount != cabs) r.Error(CoreText.T("Check.SmDeviceCount", "CAB", dev.CabCount));
        }
        var perCib = sm.CibChunkCounts;
        for (int i = 0; i < perCib.Count; i++)
            if (perCib[i] == 0 || perCib[i] > sm.ChunksPerCib || (i < perCib.Count - 1 && perCib[i] != sm.ChunksPerCib))
            { r.Error(CoreText.T("Check.SmCibChunks", i, perCib[i])); break; }
        ulong start = 0, blocks = 0;
        bool bad = false;
        for (int i = 0; i < chunks.Count; i++)
        {
            var ch = chunks[i];
            bool last = i == chunks.Count - 1;
            if (ch.Addr != start || ch.BlockCount == 0 || (!last && ch.BlockCount != bpc) || ch.Xid == 0)
            { r.Error(CoreText.T("Check.SmChunkGeometry", i, ch.Addr, ch.BlockCount)); bad = true; break; }
            start += ch.BlockCount;
            blocks += ch.BlockCount;
        }
        if (!bad && blocks != dev.BlockCount) r.Error(CoreText.T("Check.SmChunkBlocks", blocks, dev.BlockCount));
    }

    /// <summary>
    /// Alan yöneticisi nesnesinin dizileri ve iç havuz bitmap halkasının boş listesi (1.9.1; apfsck spaceman.c
    /// spaceman_val_from_off, spaceman_16_array_from_off, check_ip_free_next). Yazıcı bu dizilere doğrudan yazar (bitmap yuvası
    /// döndürme): yapının başlığına binen ya da nesnenin dışına taşan ofset, sınırın dışındaki baş/kuyruk ve döngülü liste
    /// başlığı ya da başka bir diziyi bozardı. Dizi ofsetleri yapının ardında (sürümlü yapı 2 520, eski yapı 336 bayt), hizalı,
    /// nesnenin içinde ve birbirine binmez; boş liste kuyruktan başlayıp başa, oradan kuyruğa sınır içinde ve döngüsüz varır,
    /// kuyruğun "sonraki"si 0xFFFF.
    /// </summary>
    private static void CheckSpacemanLayout(Spaceman sm, CheckReport r)
    {
        const uint Versioned = 0x1, FullSize = 2520, OldSize = 336;
        uint structSize = (sm.Flags & Versioned) != 0 ? FullSize : OldSize;
        if ((sm.Flags & Versioned) != 0 && sm.StructSize != FullSize) r.Error(CoreText.T("Check.SmStructSize", sm.StructSize));
        var dev = sm.Main;
        var arrays = new (string Name, uint Off, ulong Len, uint Align)[]
        {
            ("sm_addr_offset", dev.AddrOffset, 8UL * (dev.CabCount != 0 ? dev.CabCount : dev.CibCount), 8),
            ("sm_ip_bm_xid_offset", sm.IpBmXidOffset, 8UL * sm.IpBmSizeInBlocks, 8),
            ("sm_ip_bitmap_offset", sm.IpBitmapOffset, 2UL * sm.IpBmSizeInBlocks, 2),
            ("sm_ip_bm_free_next_offset", sm.IpBmFreeNextOffset, 2UL * sm.IpBmBlockCount, 2),
        };
        bool ok = true;
        foreach (var (name, off, len, align) in arrays)
            if (len != 0 && (off < structSize || off % align != 0 || off + len > (ulong)sm.Raw.Length))
            { r.Error(CoreText.T("Check.SmArrayOffset", name, off)); ok = false; }
        for (int i = 0; i < arrays.Length; i++)
            for (int j = i + 1; j < arrays.Length; j++)
            {
                var (a, b) = (arrays[i], arrays[j]);
                if (a.Len != 0 && b.Len != 0 && a.Off < b.Off + b.Len && b.Off < a.Off + a.Len)
                { r.Error(CoreText.T("Check.SmArraysOverlap", a.Name, b.Name)); ok = false; }
            }
        if (!ok || sm.IpBmBlockCount == 0) return;
        uint n = sm.IpBmBlockCount, free = 0;
        ushort curr = sm.IpBmFreeTail, next = sm.IpBmFreeHead;
        do
        {
            if (curr >= n || next >= n) { r.Error(CoreText.T("Check.IpFreeListOutside", curr, next)); return; }
            if (++free >= n) { r.Error(CoreText.T("Check.IpFreeListLoops")); return; }
            curr = next;
            next = sm.ArrayU16(sm.IpBmFreeNextOffset, curr);
        } while (curr != sm.IpBmFreeTail);
        if (sm.ArrayU16(sm.IpBmFreeNextOffset, sm.IpBmFreeTail) != 0xFFFF) r.Error(CoreText.T("Check.IpFreeListTail"));
        // Halka 16 kopya; kullanılan yuvalar (sm_ip_bitmap_offset) boş listede değil, kuyruk değil, ikişer kez değil; sayıları
        // halka − boş (apfsck parse_ip_bitmap_list). Yazıcı yeni kopyayı boş listenin başına yazar: canlı yuva listede olsaydı
        // geçerli bitmap yerinde (kopyalamadan) ezilir, liste kesilirdi.
        if (n != 16 * sm.IpBmSizeInBlocks || n - free != sm.IpBmSizeInBlocks) r.Error(CoreText.T("Check.IpRingCounts", n, sm.IpBmSizeInBlocks, n - free));
        var used = new HashSet<ushort> { sm.IpBmFreeTail };
        for (uint i = 0; i < sm.IpBmSizeInBlocks; i++)
        {
            ushort slot = sm.ArrayU16(sm.IpBitmapOffset, i);
            if (slot >= n || sm.ArrayU16(sm.IpBmFreeNextOffset, slot) != 0xFFFF || !used.Add(slot))
            { r.Error(CoreText.T("Check.IpUsedSlotFree", slot)); return; }
        }
    }

    private static void MarkEfi(ObjectReader reader, ulong paddr, Action<ulong, ulong, string> mark, CheckReport r)
    {
        mark(paddr, 1, CoreText.T("Check.LabelEfiDriver"));
        var efi = reader.ReadObject(paddr, 0x14);
        uint n = Bin.U32(efi, 44);
        if (176 + (long)n * 16 > efi.Length) { r.Error(CoreText.T("Check.EfiExtentCount")); return; }
        for (uint i = 0; i < n; i++)
            mark(Bin.U64(efi, (int)(176 + i * 16)), Bin.U64(efi, (int)(184 + i * 16)), CoreText.T("Check.LabelEfiDriverData"));
    }

    private static void CheckFreeQueues(NxSuperblock sb, Spaceman sm, Action<ulong, ulong, string> mark,
        Action<ulong, ulong, string> markIp, CheckReport r)
    {
        string[] names = [CoreText.T("Check.LabelInternalPool"), CoreText.T("Check.LabelMainDevice"), CoreText.T("Check.LabelTier2")];
        for (int q = 0; q < 3; q++)
        {
            var info = sm.FreeQueues[q];
            if (q == Spaceman.SfqTier2)
            {
                if (info.TreeOid != 0 || info.Count != 0) r.Error(CoreText.T("Check.Tier2QueueNotEmpty"));
                continue;
            }
            ulong total = 0, oldest = 0;
            foreach (var e in sm.ReadFreeQueue(q))
            {
                if (e.Xid > sb.Xid) r.Error(CoreText.T("Check.FreeQueueFutureXid", names[q], e.Xid));
                if (e.Length == 0) r.Error(CoreText.T("Check.FreeQueueZeroLength", names[q]));
                if (oldest == 0 || e.Xid < oldest) oldest = e.Xid;
                total += e.Length;
                bool inIp = e.Paddr >= sm.IpBase && e.Paddr < sm.IpBase + sm.IpBlockCount;
                if (q == Spaceman.SfqIp)
                {
                    if (!inIp) r.Error(CoreText.T("Check.FreeQueueIpOutside", e.Paddr));
                    else markIp(e.Paddr, e.Length, CoreText.T("Check.LabelFreeQueueIp"));
                }
                else
                {
                    if (inIp) r.Error(CoreText.T("Check.FreeQueueMainInIp", e.Paddr));
                    else mark(e.Paddr, e.Length, CoreText.T("Check.LabelFreeQueueMain"));
                }
            }
            if (total != info.Count) r.Error(CoreText.T("Check.FreeQueueCount", names[q], info.Count, total));
            if (oldest != info.OldestXid) r.Error(CoreText.T("Check.FreeQueueOldest", names[q], info.OldestXid, oldest));
        }
    }

    /// <summary>omap nesnesini ve ağacının bütün düğümlerini işaretler; kayıtları döndürür. OBJ_ENCRYPTED taşıyan düğümler <paramref name="flagged"/>'e eklenir.</summary>
    private static List<ObjectMap.Record> MarkObjectMap(ObjectMap omap, ulong omapPaddr, Action<ulong, ulong, string> mark, string what, out ulong nodeCount,
        CheckReport r, List<ulong>? flagged = null)
    {
        mark(omapPaddr, 1, what);
        nodeCount = 0;
        var order = new BTree.BTree.KeyOrderCheck(Write.KeyOrder.Omap);
        foreach (var n in omap.Tree.EnumerateNodes(order))
        {
            mark(n.Paddr, 1, CoreText.T("Check.LabelNode", what));
            if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged?.Add(n.Paddr);
            nodeCount++;
        }
        OrderErrors(r, what, order);
        if (Bin.U32(omap.Raw, 36) != 0 || Bin.U64(omap.Raw, 56) != 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Check.OmapHasSnapshots", what));
        // Bayraklar bilinenler (apfsck check_omap_flags); şifreleme/anahtar değiştirme sürüyor bitleri yazmayı engeller (şifreli
        // birimde kapı zaten reddeder; şifresiz birimde de macOS işlemi sürdürmeye kalkardı) (1.9.1)
        uint omFlags = Bin.U32(omap.Raw, 32);
        if ((omFlags & ~OmapFlagsValidMask) != 0) r.Error(CoreText.T("Check.OmapFlags", what, omFlags));
        else if ((omFlags & OmapCryptoBusyFlags) != 0) r.WriteBlockers.Add($"{what}: {CoreText.T("Check.LabelEncryptionInProgress")}");
        // Bekleyen geri dönüş (om_pending_revert_min/max): macOS sonraki bağlamada eski hâle döner, yazılanlar kaybolur
        if (Bin.U64(omap.Raw, 72) != 0 || Bin.U64(omap.Raw, 80) != 0) r.WriteBlockers.Add($"{what}: {CoreText.T("Check.LabelRevert")}");
        return omap.AllRecords().ToList();
    }

    private static void CheckVolume(ApfsContainer c, byte[] apsb, ApfsVolume? vol, ApfsException? openError, string label,
        Action<ulong, ulong, string> mark, CheckReport r)
    {
        var reader = c.Reader;
        ulong blockSize = c.BlockSize;
        ulong volOmapPaddr = Bin.U64(apsb, 128);
        ulong extentRefOid = Bin.U64(apsb, 144), snapMetaOid = Bin.U64(apsb, 152);
        ulong allocCount = Bin.U64(apsb, 88);
        // Bu yapıların blokları çözümlenmiyor: sahiplik denetimi o birim için eksik kalır (yazma kapısı bu birimleri zaten reddeder)
        bool unsupported = false;
        if (Bin.U64(apsb, 216) != 0) { unsupported = true; r.Warnings.Add(CoreText.T("Check.VolumeHasSnapshots", label)); }
        // Anlık görüntüye / eski süper bloğa geri dönüş ya da anlık görüntüden kök (apfs_revert_to_xid, revert_to_sblock_oid,
        // root_to_xid): macOS birimi o hâle döndürür, yazılanlar kaybolur (1.9.1; apfsck "not supported")
        if (Bin.U64(apsb, 160) != 0 || Bin.U64(apsb, 168) != 0 || Bin.U64(apsb, 968) != 0)
            r.WriteBlockers.Add($"{label}: {CoreText.T("Check.LabelRevert")}");
        foreach (var (off, name) in new[] { (976, CoreText.T("Check.LabelEncryptionInProgress")), (1024, CoreText.T("Check.LabelIntegrityMeta")), (1032, CoreText.T("Check.LabelFextTree")), (1000, CoreText.T("Check.LabelSnapshotExtension")) })
            if (Bin.U64(apsb, off) != 0) { unsupported = true; r.Warnings.Add(CoreText.T("Check.UnsupportedStructure", label, name)); }
        if (unsupported) r.OwnershipIncomplete = true;

        ulong counted = 0;
        void MarkVol(ulong start, ulong count, string what) { mark(start, count, $"{label}: {what}"); counted += count; }

        // Birim nesne haritası ve kayıtları (fs ağacı düğümleri)
        var omap = vol?.Omap ?? new ObjectMap(reader, volOmapPaddr);
        var flaggedOmap = new List<ulong>();
        var records = MarkObjectMap(omap, volOmapPaddr, MarkVol, "omap", out _, r, flaggedOmap);
        FlagErrors(r, label, CoreText.T("Check.FlagOmapNode"), flaggedOmap);
        var recordOids = new HashSet<ulong>();
        bool encrypted = (Bin.U64(apsb, 264) & 3) != ApfsConstants.ApfsFsUnencrypted;
        bool cryptoInProgress = Bin.U64(apsb, 976) != 0; // er_state: macOS düğümleri hâlâ şifreliyor/çözüyor olabilir
        int unflagged = 0;
        foreach (var rec in records)
        {
            if (rec.Xid > c.Xid) { r.Error(CoreText.T("Check.VolOmapFutureXid", label, rec.Oid)); continue; }
            if (!recordOids.Add(rec.Oid)) r.Error(CoreText.T("Check.VolOmapMultipleRecords", label, rec.Oid));
            if ((rec.Flags & ApfsConstants.OmapValDeleted) != 0) r.Error(CoreText.T("Check.VolOmapDeleted", label, rec.Oid));
            if (rec.Size != blockSize) r.Error(CoreText.T("Check.VolOmapRecordSize", label, rec.Oid, rec.Size));
            if ((rec.Flags & ~OmapValValidMask) != 0) r.Error(CoreText.T("Check.OmapRecordFlags", $"{label}: omap", rec.Oid, rec.Flags));
            if (!encrypted && (rec.Flags & ApfsConstants.OmapValEncrypted) != 0)
                r.Error(CoreText.T("Check.EncryptedFlagOnPlain", label, rec.Oid));
            if (encrypted && !cryptoInProgress && (rec.Flags & ApfsConstants.OmapValEncrypted) == 0 && unflagged++ < 5)
                r.Error(CoreText.T("Check.MissingEncryptedFlag", label, rec.Oid));
            MarkVol(rec.Paddr, 1, CoreText.T("Check.LabelFsNode", rec.Oid));
        }

        OidsBelowNext(r, $"{label}: omap", records.Select(x => x.Oid), c.Superblock.NextOid);

        // Fiziksel ağaçlar
        var phys = new List<(ulong Paddr, ulong Len, int Refcnt)>();
        if (extentRefOid != 0)
        {
            var tree = new BTree.BTree(reader, extentRefOid, ApfsConstants.TypeBlockRefTree, null);
            var flagged = new List<ulong>();
            var order = new BTree.BTree.KeyOrderCheck(Write.KeyOrder.ExtentRef);
            foreach (var n in tree.EnumerateNodes(order))
            {
                MarkVol(n.Paddr, 1, CoreText.T("Check.LabelExtentrefNode"));
                if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged.Add(n.Paddr);
            }
            OrderErrors(r, $"{label}: {CoreText.T("Check.LabelExtentrefTree")}", order);
            FlagErrors(r, label, CoreText.T("Check.FlagExtentrefNode"), flagged);
            foreach (var e in tree.Scan(_ => 0))
            {
                if (e.Key.Length < 8 || e.Value.Length < 20) { r.Error(CoreText.T("Check.ExtentrefRecordShort", label)); continue; }
                ulong paddr = Bin.U64(e.Key, 0) & 0x0FFFFFFFFFFFFFFF;
                ulong len = Bin.U64(e.Value, 0) & 0x0FFFFFFFFFFFFFFF;
                int refcnt = Bin.I32(e.Value, 16);
                if (len == 0) r.Error(CoreText.T("Check.PhysExtentZeroLength", label, paddr));
                if (refcnt <= 0) r.Error(CoreText.T("Check.PhysExtentRefcnt", label, paddr, refcnt));
                phys.Add((paddr, len, refcnt));
                MarkVol(paddr, len, CoreText.T("Check.LabelFileData", paddr));
            }
        }
        if (snapMetaOid != 0)
        {
            var tree = new BTree.BTree(reader, snapMetaOid, ApfsConstants.TypeSnapMetaTree, null);
            var flagged = new List<ulong>();
            var order = new BTree.BTree.KeyOrderCheck(Write.KeyOrder.Fs);
            foreach (var n in tree.EnumerateNodes(order))
            {
                MarkVol(n.Paddr, 1, CoreText.T("Check.LabelSnapMetaNode"));
                if ((n.ObjType & ApfsConstants.ObjEncrypted) != 0) flagged.Add(n.Paddr);
            }
            FlagErrors(r, label, CoreText.T("Check.FlagSnapMetaNode"), flagged);
            OrderErrors(r, $"{label}: {CoreText.T("Check.LabelSnapMetaTree")}", order);
        }

        if (counted != allocCount)
        {
            string msg = CoreText.T("Check.AllocCount", label, allocCount, counted);
            if (unsupported) r.Warnings.Add(CoreText.T("Check.AllocCountMaybeUnchecked", msg)); else r.Error(msg);
        }

        if (vol is null)
        {
            // Yalnız kilitli (anahtarı verilmemiş) birim uyarıdır. Bozuk birim (ör. fs ağacının kökü için omap kaydı yok: 1.9.1,
            // apfsck "Object map: record missing") önce "şifreli olabilir" uyarısıyla geçiyor, kapı açılıyordu; desteklenmeyen
            // yapıdaki birimin blok sahipliği çözümlenemez (kapı "çözümlenemeyen yapı" der).
            switch (openError?.Code)
            {
                case ApfsErrorCode.Encrypted or ApfsErrorCode.WrongPassword:
                    break;
                case ApfsErrorCode.Unsupported:
                    r.OwnershipIncomplete = true;
                    r.Warnings.Add(CoreText.T("Check.VolumeUnsupported", label, openError.Detail ?? openError.Message));
                    break;
                default:
                    r.Error(CoreText.T("Check.VolumeOpenFailed", label, openError?.Detail ?? openError?.Message ?? ""));
                    break;
            }
            r.Warnings.Add(CoreText.T("Check.VolumeNotOpened", label));
            r.Info.Add(CoreText.T("Check.FsFlagNotChecked", label));
            return;
        }

        // fs ağacı düğümleri ile omap kayıtları birebir; OBJ_ENCRYPTED ⇔ birim şifreli (er_state doluyken macOS düğümleri dönüştürüyor olabilir)
        var nodeOids = new HashSet<ulong>();
        var wrongFlag = new List<ulong>();
        int fsNodes = 0, fsFlagged = 0;
        var fsOrder = new BTree.BTree.KeyOrderCheck(Write.KeyOrder.Fs);
        foreach (var n in vol.FsTree.EnumerateNodes(fsOrder))
        {
            if (!nodeOids.Add(n.Oid)) r.Error(CoreText.T("Check.FsNodeUsedTwice", label, n.Oid));
            fsNodes++;
            bool flag = (n.ObjType & ApfsConstants.ObjEncrypted) != 0;
            if (flag) fsFlagged++;
            if (flag != encrypted) wrongFlag.Add(n.Oid);
        }
        r.Info.Add(CoreText.T("Check.FsNodeStats", label, fsNodes, fsFlagged));
        OrderErrors(r, $"{label}: {CoreText.T("Check.LabelFsTree")}", fsOrder);
        if (!cryptoInProgress)
            FlagErrors(r, label, encrypted ? CoreText.T("Check.FsNodeMissingFlag") : CoreText.T("Check.FsNodeWrongFlag"), wrongFlag);
        foreach (ulong oid in recordOids)
            if (!nodeOids.Contains(oid)) r.Error(CoreText.T("Check.OmapRecordUnused", label, oid));

        CheckFsTree(c, vol, label, phys, r);
    }

    /// <summary>Sanal ve geçici nesne kimlikleri kapsayıcının next_oid'inden küçük olmalı; ilk 5 kimlik adıyla, fazlası toplamla.</summary>
    private static void OidsBelowNext(CheckReport r, string what, IEnumerable<ulong> oids, ulong next)
    {
        var bad = oids.Where(o => o >= next).ToList();
        foreach (ulong o in bad.Take(5)) r.Error(CoreText.T("Check.OidNotBelowNext", what, o, next));
        if (bad.Count > 5) r.Error(CoreText.T("Check.OidNotBelowNextTotal", what, bad.Count, next));
    }

    /// <summary>Anahtar sırası hataları: ilk 5 düğüm adresiyle, fazlası toplamla.</summary>
    private static void OrderErrors(CheckReport r, string what, BTree.BTree.KeyOrderCheck order)
    {
        foreach (var (kind, paddr) in order.Problems)
            r.Error(kind switch
            {
                BTree.BTree.KeyOrderProblem.OutOfOrder => CoreText.T("Check.KeysOutOfOrder", what, paddr),
                BTree.BTree.KeyOrderProblem.IndexKeyNotFirst => CoreText.T("Check.IndexKeyNotFirst", what, paddr),
                _ => CoreText.T("Check.KeysRepeated", what, paddr),
            });
        if (order.ProblemCount > order.Problems.Count) r.Error(CoreText.T("Check.KeyOrderTotal", what, order.ProblemCount));
    }

    /// <summary>OBJ_ENCRYPTED kuralının hataları: ilk 5 nesne adıyla, fazlası toplam sayıyla.</summary>
    private static void FlagErrors(CheckReport r, string label, string what, List<ulong> ids)
    {
        foreach (ulong id in ids.Take(5)) r.Error($"{label}: {what} {id}");
        if (ids.Count > 5) r.Error(CoreText.T("Check.FlagTotal", label, what, ids.Count));
    }

    private sealed class InodeFacts(Inode ino)
    {
        public Inode Inode { get; } = ino;
        public int Drecs;
        public int Children;
        /// <summary>Anahtar sırasıyla ilk dizin girdisinin üstü ve adı (temizlenebilir klasördeki girdi sayılmaz).</summary>
        public ulong FirstParent;
        public string? FirstName;
    }

    /// <summary>Sert bağın bir kardeşi: dizin girdisi ve kardeş kaydı aynı (üst, ad)'ı göstermeli; kardeş haritası inode'u.</summary>
    private sealed class SiblingFacts
    {
        public ulong Parent;
        public string? Name;
        public bool FromEntry, FromLink, Mapped, Mismatch;

        public void SetOrCheck(ulong parent, string name, bool link)
        {
            if (!FromEntry && !FromLink) { Parent = parent; Name = name; }
            else if (Parent != parent || !string.Equals(Name, name, StringComparison.Ordinal)) Mismatch = true;
            if (link) FromLink = true; else FromEntry = true;
        }
    }

    private static void CheckFsTree(ApfsContainer c, ApfsVolume v, string label, List<(ulong Paddr, ulong Len, int Refcnt)> phys, CheckReport r)
    {
        int badCrypto = 0, cryptoStates = 0;
        bool tweakRules = v.IsEncrypted && (v.IncompatibleFeatures & ApfsConstants.ApfsIncompatEncRolled) == 0;
        ulong blockSize = c.BlockSize;
        var inodes = new Dictionary<ulong, InodeFacts>();
        var drecs = new List<(ulong Parent, DirEntry Entry, ulong SiblingId)>();
        var siblings = new Dictionary<ulong, SortedDictionary<ulong, SiblingFacts>>(); // inode → kardeş kimliği → bilgiler
        var dstreamRefs = new Dictionary<ulong, uint>();
        var extents = new Dictionary<ulong, List<(ulong Logical, ulong Len, ulong Phys)>>();
        var xattrStreams = new List<(ulong Owner, ulong StreamId, ulong Size, ulong Alloc)>();
        ulong maxId = 0;
        var blockers = new HashSet<string>(StringComparer.Ordinal);
        var rules = new RecordRules(label, v.IsEncrypted, r);

        foreach (var e in v.AllRecords())
        {
            ulong id = JKey.ObjId(e.Key);
            var type = JKey.Type(e.Key);
            if (type is not JType.Extent and not JType.SnapMetadata) maxId = Math.Max(maxId, id);
            switch (type)
            {
                case JType.Inode:
                    inodes[id] = new InodeFacts(Inode.Parse(id, e.Value));
                    if ((inodes[id].Inode.InternalFlags & 0x2) != 0) blockers.Add(CoreText.T("Check.LabelDirStats"));
                    rules.Inode(id, inodes[id].Inode, e.Value);
                    maxId = Math.Max(maxId, inodes[id].Inode.ParentId); // üst de kullanılan kimliktir (apfsck check_inode_ids)
                    break;
                case JType.DirStats:
                    blockers.Add(CoreText.T("Check.LabelDirStats"));
                    break;
                case JType.FileInfo:
                    blockers.Add(CoreText.T("Check.LabelFileInfo"));
                    break;
                case JType.DirRec:
                    var de = ApfsVolume.ParseDirRec(e.Key, e.Value);
                    if (de is null) { r.Error(CoreText.T("Check.UnnamedDirEntry", label, id)); break; }
                    if (!ValidEntryName(e.Key)) r.Error(CoreText.T("Check.DirEntryNameInvalid", label, id));
                    else if (!ValidUtf8Name(e.Key)) r.Error(CoreText.T("Check.DirEntryNameNotUtf8", label, id));
                    drecs.Add((id, de, DrecSiblingId(e.Value)));
                    maxId = Math.Max(maxId, de.FileId);
                    int hashedLen = (int)(Bin.U32(e.Key, 8) & 0x3FF);
                    if (12 + hashedLen == e.Key.Length && ApfsNames.Hash(de.Name, v.IsCaseInsensitive) != Bin.U32(e.Key, 8) >> 10)
                        r.Error(CoreText.T("Check.NameHashWrong", label, de.Name));
                    break;
                case JType.SiblingLink:
                    if (e.Key.Length < 16 || e.Value.Length < 10 || e.Value.Length != 10 + Bin.U16(e.Value, 8)
                        || Bin.U16(e.Value, 8) == 0 || e.Value[^1] != 0)
                    { r.Error(CoreText.T("Check.SiblingRecordBad", label, id)); break; }
                    Sibling(siblings, id, Bin.U64(e.Key, 8)).SetOrCheck(Bin.U64(e.Value, 0), Bin.Utf8NameZ(Bin.Slice(e.Value, 10, Bin.U16(e.Value, 8))), link: true);
                    break;
                case JType.SiblingMap:
                    if (e.Value.Length != 8) { r.Error(CoreText.T("Check.SiblingRecordBad", label, id)); break; }
                    Sibling(siblings, Bin.U64(e.Value, 0), id).Mapped = true;
                    break;
                case JType.DstreamId:
                    if (e.Value.Length < 4) { r.Error(CoreText.T("Check.DstreamRecordShort", label, id)); break; }
                    dstreamRefs[id] = Bin.U32(e.Value, 0);
                    break;
                case JType.CryptoState:
                    rules.CryptoState(id);
                    // CRYPTO_SW_ID'nin yer tutucusu: anahtarsız (key_len 0); dosyaların default_crypto_id'si bunu gösterir
                    if (id != CryptoSwId || e.Value.Length < 24 || Bin.U16(e.Value, 22) != 0) cryptoStates++;
                    break;
                case JType.FileExtent:
                    if (e.Key.Length < 16 || e.Value.Length < 24) { r.Error(CoreText.T("Check.ExtentRecordShort", label, id)); break; }
                    ulong len = Bin.U64(e.Value, 0) & 0x00FFFFFFFFFFFFFF;
                    if (tweakRules && Bin.U64(e.Value, 8) != 0)
                    {
                        if (Bin.U64(e.Value, 16) is 0 or CryptoSwId && badCrypto++ < 5)
                            r.Error(CoreText.T("Check.ExtentCryptoId", label, id, Bin.U64(e.Value, 16)));
                        if ((Bin.U64(e.Value, 0) >> 56 & FextCryptoIdIsTweak) == 0 && badCrypto++ < 5)
                            r.Error(CoreText.T("Check.ExtentNoTweakFlag", label, id));
                    }
                    if (len == 0 || len % blockSize != 0) r.Error(CoreText.T("Check.ExtentLength", label, id, len));
                    rules.Extent(id, e.Value);
                    if (!extents.TryGetValue(id, out var list)) extents[id] = list = [];
                    list.Add((Bin.U64(e.Key, 8), len, Bin.U64(e.Value, 8)));
                    break;
                case JType.Xattr:
                    rules.Xattr(id, e.Key, e.Value);
                    if (e.Value.Length >= 4 && (Bin.U16(e.Value, 0) & ApfsVolume.XattrDataStream) != 0)
                    {
                        if (e.Value.Length < 4 + 24) { r.Error(CoreText.T("Check.XattrStreamRecordShort", label, id)); break; }
                        xattrStreams.Add((id, Bin.U64(e.Value, 4), Bin.U64(e.Value, 12), Bin.U64(e.Value, 20)));
                    }
                    break;
            }
        }

        rules.Finish();
        if (cryptoStates > 0 && (v.FsFlags & ApfsConstants.ApfsFsOneKey) != 0)
            r.Error(CoreText.T("Check.PerFileKeysOnOneKey", label, cryptoStates));
        foreach (var b in blockers) r.WriteBlockers.Add($"{label}: {b}");
        if (!inodes.ContainsKey(Inode.RootDirId)) r.Error(CoreText.T("Check.RootDirMissing", label));

        // Dizin girdileri → inode
        foreach (var (parent, de, siblingId) in drecs)
        {
            // Kök klasör ve özel klasörün girdileri, inode'u olmayan sanal üstte (1) durur
            if (parent != Inode.RootDirParent)
            {
                if (!inodes.TryGetValue(parent, out var p)) { r.Error(CoreText.T("Check.EntryParentMissing", label, de.Name, parent)); continue; }
                if (!p.Inode.IsDirectory) r.Error(CoreText.T("Check.EntryParentNotDir", label, de.Name));
                p.Children++;
            }
            else if (de.FileId is not (Inode.RootDirId or PrivateDirId)) r.Error(CoreText.T("Check.EntryVirtualParentNotRoot", label, de.Name));
            if (!inodes.TryGetValue(de.FileId, out var t)) { r.Error(CoreText.T("Check.EntryInodeMissing", label, de.Name, de.FileId)); continue; }
            t.Drecs++;
            if (parent != PurgeableDirId && t.FirstName is null) { t.FirstName = de.Name; t.FirstParent = parent; }
            if (siblingId != 0) Sibling(siblings, de.FileId, siblingId).SetOrCheck(parent, de.Name, link: false);
            if (ExpectedType(t.Inode) != de.Type) r.Error(CoreText.T("Check.EntryTypeMismatch", label, de.Name));
        }

        CheckParentChains(inodes, label, r);
        CheckNames(inodes, siblings, v.NextObjId, label, r);

        long files = 0, dirs = 0, symlinks = 0, others = 0;
        var owners = new Dictionary<ulong, uint>();
        foreach (var (id, f) in inodes)
        {
            var ino = f.Inode;
            if (ino.IsDirectory)
            {
                if (id != Inode.RootDirId && id != PrivateDirId) dirs++;
                if (ino.ChildrenOrLinks != f.Children) r.Error(CoreText.T("Check.DirChildCount", label, id, ino.ChildrenOrLinks, f.Children));
                if (id != Inode.RootDirId && id != PrivateDirId && f.Drecs != 1) r.Error(CoreText.T("Check.DirMultiplyReferenced", label, id, f.Drecs));
            }
            else
            {
                if (ino.IsRegularFile) files++;
                else if (ino.IsSymlink) symlinks++;
                else others++;
                if (ino.ChildrenOrLinks != f.Drecs) r.Error(CoreText.T("Check.InodeLinkCount", label, id, ino.ChildrenOrLinks, f.Drecs));
            }
            if (ino.HasDataStream)
            {
                if (!ino.IsRegularFile) r.Error(CoreText.T("Check.InodeNotFileHasStream", label, id));
                owners[ino.PrivateId] = owners.GetValueOrDefault(ino.PrivateId) + 1;
                CheckStream(label, $"inode {id}", extents.GetValueOrDefault(ino.PrivateId), ino.DataSize, ino.AllocatedSize, r);
            }
        }
        // Başvuru sayıları: dosya veri akışlarının dstream_id kaydı vardır ve sayısı sahiplerle aynıdır (klonlarda 2+).
        foreach (var (sid, n) in owners)
        {
            if (dstreamRefs.TryGetValue(sid, out uint refcnt)) { if (refcnt != n) r.Error(CoreText.T("Check.DstreamRefcnt", label, sid, refcnt, n)); }
            else if (extents.ContainsKey(sid)) r.Error(CoreText.T("Check.DstreamNoRefRecord", label, sid));
        }
        // Öznitelik veri akışları paylaşılamaz ve dstream_id kaydı taşımaz (apfsck: "xattrs can't be cloned")
        var xattrOwners = new Dictionary<ulong, int>();
        foreach (var (owner, sid, size, alloc) in xattrStreams)
        {
            xattrOwners[sid] = xattrOwners.GetValueOrDefault(sid) + 1;
            if (owners.ContainsKey(sid)) r.Error(CoreText.T("Check.StreamFileAndXattr", label, sid));
            if (dstreamRefs.ContainsKey(sid)) r.Error(CoreText.T("Check.XattrStreamHasRefRecord", label, sid));
            CheckStream(label, CoreText.T("Check.LabelXattrStream", sid, owner), extents.GetValueOrDefault(sid), size, alloc, r);
        }
        foreach (var (sid, n) in xattrOwners)
            if (n != 1) r.Error(CoreText.T("Check.XattrStreamShared", label, sid, n));
        foreach (var sid in dstreamRefs.Keys)
            if (!owners.ContainsKey(sid)) r.Error(CoreText.T("Check.OrphanDstreamRecord", label, sid));
        foreach (var sid in extents.Keys)
            if (!owners.ContainsKey(sid) && !xattrOwners.ContainsKey(sid)) r.Error(CoreText.T("Check.OrphanExtents", label, sid));

        CheckPhysicalExtents(label, extents, phys, blockSize, r);

        if (files != (long)v.NumFiles || dirs != (long)v.NumDirectories || symlinks != (long)v.NumSymlinks || others != (long)v.NumOtherFsObjects)
            r.Error(CoreText.T("Check.Counters", label, v.NumFiles, v.NumDirectories, v.NumSymlinks, v.NumOtherFsObjects, files, dirs, symlinks, others));
        if (v.NextObjId <= maxId) r.Error(CoreText.T("Check.NextObjId", label, v.NextObjId, maxId));
        r.Info.Add(CoreText.T("Check.FsSummary", label, files, dirs, symlinks, extents.Count, phys.Count));
    }

    /// <summary>
    /// Klasör üst zinciri (1.8.1): her klasörün inode parent_id'leri izlenince kök (2) ya da özel klasör (3) üzerinden
    /// sanal üste (1) varılmalı; kökün ve özel klasörün üstü 1, başka hiçbir klasörünki 1 değil. Kökü kendini gösteren ya da iki
    /// klasörü birbirinin üstü yapan birim önceden temiz geçiyor, yazıcının "kendi altına taşıma" yürüyüşü sonsuza dönüyordu
    /// (apfsck: "Root inode record: bad parent id"). Sonuçlar belleklenir (O(n)); hata zincirin koptuğu klasörde BİR kez yazılır,
    /// altındakiler ayrıca yazılmaz.
    /// </summary>
    private static void CheckParentChains(Dictionary<ulong, InodeFacts> inodes, string label, CheckReport r)
    {
        var ok = new Dictionary<ulong, bool>();
        var path = new List<ulong>();
        var onPath = new HashSet<ulong>();
        foreach (var (start, facts) in inodes)
        {
            if (!facts.Inode.IsDirectory || ok.ContainsKey(start)) continue;
            path.Clear();
            onPath.Clear();
            ulong id = start;
            bool result;
            while (true)
            {
                if (ok.TryGetValue(id, out result)) break;              // zincirin kalanı zaten biliniyor (kötüyse orada yazıldı)
                if (!onPath.Add(id))                                     // döngü
                {
                    r.Error(CoreText.T("Check.DirParentChain", label, id, inodes[id].Inode.ParentId));
                    result = false;
                    break;
                }
                path.Add(id);
                ulong parent = inodes[id].Inode.ParentId;
                bool special = id is Inode.RootDirId or PrivateDirId;
                if (special || parent == Inode.RootDirParent)
                {
                    result = special && parent == Inode.RootDirParent;   // yalnız kök ve özel klasör sanal üste bağlanır
                    if (!result) r.Error(CoreText.T("Check.DirParentChain", label, id, parent));
                    break;
                }
                if (!inodes.TryGetValue(parent, out var p) || !p.Inode.IsDirectory)
                {
                    r.Error(CoreText.T("Check.DirParentChain", label, id, parent));
                    result = false;
                    break;
                }
                id = parent;
            }
            foreach (ulong d in path) ok[d] = result;
        }
    }

    private static SiblingFacts Sibling(Dictionary<ulong, SortedDictionary<ulong, SiblingFacts>> all, ulong inode, ulong sibling)
    {
        if (!all.TryGetValue(inode, out var list)) all[inode] = list = [];
        if (!list.TryGetValue(sibling, out var s)) list[sibling] = s = new SiblingFacts();
        return s;
    }

    /// <summary>
    /// Dizin girdisinin adı (1.9.1; apfsck key.c read_dir_rec_key): NUL ile biter, içinde başka NUL yok (ad uzunluğu NUL
    /// dahil), en çok 256 bayt; "." ve ".." değil, "/" içermez (POSIX yol ayırıcısı; Finder'ın "/"su diskte ":" durur). Böyle bir
    /// girdi okuyucuda kesik ya da yol bileşeni gibi görünür, yazıcıda başka bir girdiyle karışabilir.
    /// </summary>
    private static bool ValidEntryName(byte[] key)
    {
        int hashedLen = (int)(Bin.U32(key, 8) & 0x3FF);
        var name = 12 + hashedLen == key.Length ? key.AsSpan(12) : key.AsSpan(10);
        if (name.Length is 0 or > 256 || name[^1] != 0 || name.IndexOf((byte)0) != name.Length - 1) return false;
        var bare = name[..^1];
        return !bare.SequenceEqual("."u8) && !bare.SequenceEqual(".."u8) && bare.IndexOf((byte)'/') < 0;
    }

    /// <summary>
    /// Dizin girdisinin adı geçerli UTF-8 mi (1.9.1; apfsck'in ad karması ve normalleştirmesi UTF-8 ister). Okuyucu geçersiz
    /// baytı kaçışla gösterir (<see cref="Bin.Utf8NameZ"/>); yazıcı böyle adı yeniden kodlayamaz → kapı kapanır.
    /// </summary>
    private static bool ValidUtf8Name(byte[] key)
    {
        int hashedLen = (int)(Bin.U32(key, 8) & 0x3FF);
        var name = 12 + hashedLen == key.Length ? key.AsSpan(12) : key.AsSpan(10);
        return System.Text.Unicode.Utf8.IsValid(name[..^1]);
    }

    /// <summary>Dizin girdisi değerindeki SIBLING_ID genişletilmiş alanı (yoksa 0).</summary>
    private static ulong DrecSiblingId(byte[] val)
    {
        if (val.Length <= 18) return 0;
        foreach (var x in Write.Records.ParseXFields(val.AsSpan(18)))
            if (x.Type == DrecExtSiblingId && x.Data.Length >= 8) return Bin.U64(x.Data, 0);
        return 0;
    }

    /// <summary>
    /// Inode'un adı ve üstü (1.9.1, apfsck inode.c free_inode_names, set_or_check_sibling, parse_sibling_record):
    /// her inode NAME alanı taşır. Kardeşi olmayan inode'un adı/üstü ilk (tek) dizin girdisininkidir — özel klasöre (3) taşınmış
    /// dosya eski adını ve üstünü korur, denetlenmez. Sert bağda her kardeşin dizin girdisi (SIBLING_ID) ile kardeş kaydı aynı
    /// (üst, ad)'ı gösterir ve kardeş haritası vardır; birincil = en küçük kardeş kimliği, inode'un adı/üstü onunkidir; kardeş
    /// sayısı bağlantı sayısına eşittir (tek bağlantıda kardeş kaydı olmayabilir); kardeş kimliği 16 ≤ id &lt; next_obj_id.
    /// Klasörün yanlış üstü yazıcının "kendi altına taşıma" denetimini atlatıyordu; eksik kardeş kaydı yanlış birincil
    /// adı yazdırıyordu.
    /// </summary>
    private static void CheckNames(Dictionary<ulong, InodeFacts> inodes, Dictionary<ulong, SortedDictionary<ulong, SiblingFacts>> siblings,
        ulong nextObjId, string label, CheckReport r)
    {
        foreach (var (inode, list) in siblings)
            if (!inodes.ContainsKey(inode)) r.Error(CoreText.T("Check.SiblingInodeMissing", label, inode));
        foreach (var (id, f) in inodes)
        {
            var ino = f.Inode;
            if (ino.Name is null) { r.Error(CoreText.T("Check.InodeNoName", label, id)); continue; }
            if (f.FirstName is null) continue; // girdisiz inode: bağlantı/çocuk sayısı denetimleri yazar
            siblings.TryGetValue(id, out var sibs);
            if (sibs is { Count: > 0 })
            {
                if (f.FirstParent == PrivateDirId) { r.Error(CoreText.T("Check.OrphanHasSiblings", label, id)); continue; }
                foreach (var (sid, s) in sibs)
                {
                    if (sid < MinUserId || sid >= nextObjId) r.Error(CoreText.T("Check.SiblingIdInvalid", label, id, sid));
                    if (!s.FromEntry || !s.FromLink) r.Error(CoreText.T("Check.SiblingUnmatched", label, id, sid));
                    else if (s.Mismatch) r.Error(CoreText.T("Check.SiblingMismatch", label, id, sid));
                    if (!s.Mapped) r.Error(CoreText.T("Check.SiblingNoMap", label, id, sid));
                }
                var (primaryId, primary) = sibs.First();
                if (!string.Equals(ino.Name, primary.Name, StringComparison.Ordinal))
                    r.Error(CoreText.T("Check.PrimaryLinkName", label, id, primaryId, primary.Name ?? ""));
                if (ino.ParentId != primary.Parent) r.Error(CoreText.T("Check.PrimaryLinkParent", label, id, ino.ParentId, primary.Parent));
                if (sibs.Count != ino.ChildrenOrLinks && !ino.IsDirectory)
                    r.Error(CoreText.T("Check.SiblingCount", label, id, ino.ChildrenOrLinks, sibs.Count));
            }
            else
            {
                if (f.FirstParent == PrivateDirId) continue; // özel klasöre taşınan dosya eski adını ve üstünü korur
                if (!ino.IsDirectory && ino.ChildrenOrLinks > 1) r.Error(CoreText.T("Check.SiblingCount", label, id, ino.ChildrenOrLinks, 0));
                if (!string.Equals(ino.Name, f.FirstName, StringComparison.Ordinal))
                    r.Error(CoreText.T("Check.OnlyLinkName", label, id, f.FirstName));
                if (ino.ParentId != f.FirstParent) r.Error(CoreText.T("Check.OnlyLinkParent", label, id, ino.ParentId, f.FirstParent));
            }
        }
    }

    /// <summary>OMAP_MANUALLY_MANAGED | ENCRYPTING | DECRYPTING | KEYROLLING | CRYPTO_GENERATION; şifreleme işlemi bitleri.</summary>
    private const uint OmapFlagsValidMask = 0x1F, OmapCryptoBusyFlags = 0x1E;

    /// <summary>OMAP_VAL_DELETED | SAVED | ENCRYPTED | NOHEADER | CRYPTO_GENERATION.</summary>
    private const uint OmapValValidMask = 0x1F;

    /// <summary>DREC_EXT_TYPE_SIBLING_ID.</summary>
    private const byte DrecExtSiblingId = 1;

    /// <summary>Kullanıcı nesnelerinin en küçük kimliği (APFS_MIN_USER_INO_NUM).</summary>
    private const ulong MinUserId = 16;

    /// <summary>Temizlenebilir (purgeable) dosyaların üstü; buradaki girdi inode'un adı/üstü sayılmaz (apfsck dir.c).</summary>
    private const ulong PurgeableDirId = 7;

    /// <summary>CRYPTO_SW_ID: yazılım şifrelemeli birimin VEK'ine ayrılmış kimlik; veri extent'inin tweak'i olamaz.</summary>
    private const ulong CryptoSwId = 4;

    /// <summary>j_file_extent_val_t bayrağı: crypto_id alanı bir tweak'tir.</summary>
    private const ulong FextCryptoIdIsTweak = 0x1;

    /// <summary>Özel klasör (private-dir): sert bağlantılar ve silinmeyi bekleyen dosyalar için.</summary>
    private const ulong PrivateDirId = 3;

    private static DirEntryType ExpectedType(Inode i) => (i.Mode & Inode.ModeTypeMask) switch
    {
        Inode.ModeDir => DirEntryType.Directory,
        Inode.ModeRegular => DirEntryType.Regular,
        Inode.ModeSymlink => DirEntryType.Symlink,
        0x1000 => DirEntryType.Fifo,
        0x2000 => DirEntryType.CharDevice,
        0x6000 => DirEntryType.BlockDevice,
        0xC000 => DirEntryType.Socket,
        _ => DirEntryType.Unknown,
    };

    private static void CheckStream(string label, string what, List<(ulong Logical, ulong Len, ulong Phys)>? list, ulong size, ulong alloc, CheckReport r)
    {
        ulong next = 0;
        if (list is not null)
        {
            list.Sort((a, b) => a.Logical.CompareTo(b.Logical));
            foreach (var x in list)
            {
                if (x.Logical != next) { r.Error(CoreText.T("Check.StreamGapOrOverlap", label, what, x.Logical, next)); break; }
                next += x.Len;
            }
        }
        if (next != alloc) r.Error(CoreText.T("Check.StreamAllocSize", label, what, alloc, next));
        if (size > alloc) r.Error(CoreText.T("Check.StreamSizeTooLarge", label, what, size, alloc));
    }

    /// <summary>
    /// Her dosya extent'i bir fiziksel extent kaydının içinde olmalı ve her fiziksel extent'in her bloğunu tam olarak başvuru
    /// sayısı kadar dosya extent'i göstermeli (klonlarda 2+).
    /// </summary>
    private static void CheckPhysicalExtents(string label, Dictionary<ulong, List<(ulong Logical, ulong Len, ulong Phys)>> extents,
        List<(ulong Paddr, ulong Len, int Refcnt)> phys, ulong blockSize, CheckReport r)
    {
        // Dosya extent'lerinin kapsama sayısı: süpürme çizgisi
        var events = new List<(ulong Pos, int Delta)>();
        foreach (var list in extents.Values)
            foreach (var x in list)
                if (x.Phys != 0)
                {
                    events.Add((x.Phys, +1));
                    events.Add((x.Phys + x.Len / blockSize, -1));
                }
        events.Sort((a, b) => a.Pos != b.Pos ? a.Pos.CompareTo(b.Pos) : a.Delta.CompareTo(b.Delta));
        var segments = new List<(ulong Start, ulong End, int Count)>();
        int cur = 0;
        ulong last = 0;
        foreach (var (pos, delta) in events)
        {
            if (cur > 0 && pos > last) segments.Add((last, pos, cur));
            cur += delta;
            last = pos;
        }

        phys.Sort((a, b) => a.Paddr.CompareTo(b.Paddr));
        for (int i = 1; i < phys.Count; i++)
            if (phys[i].Paddr < phys[i - 1].Paddr + phys[i - 1].Len) r.Error(CoreText.T("Check.PhysExtentsOverlap", label, phys[i].Paddr));

        // Her fiziksel kaydın her bloğu, başvuru sayısı kadar dosya extent'iyle kapsanmalı. Bir dosya extent'i birden çok bitişik
        // fiziksel kayda yayılabilir (apfsck de böyle sayar); bu yüzden kapsama parçaları kayıt sınırlarında bölünür.
        int errors = 0, s0 = 0;
        ulong segTotal = 0, matched = 0;
        foreach (var seg in segments) segTotal += seg.End - seg.Start;
        foreach (var p in phys)
        {
            ulong pEnd = p.Paddr + p.Len, covered = 0;
            while (s0 < segments.Count && segments[s0].End <= p.Paddr) s0++;
            for (int k = s0; k < segments.Count && segments[k].Start < pEnd; k++)
            {
                var seg = segments[k];
                ulong a = Math.Max(seg.Start, p.Paddr), b = Math.Min(seg.End, pEnd);
                if (a >= b) continue;
                if (seg.Count != p.Refcnt && errors++ < 5) r.Error(CoreText.T("Check.PhysExtentCoverage", label, p.Paddr, p.Refcnt, seg.Count));
                covered += b - a;
            }
            matched += covered;
            if (covered != p.Len && errors++ < 5) r.Error(CoreText.T("Check.PhysExtentUncovered", label, p.Paddr, p.Len));
        }
        if (matched != segTotal && errors++ < 5) r.Error(CoreText.T("Check.FileDataNoPhysExtent", label, segTotal - matched));
    }

    // ---- bitmap karşılaştırması ----

    private static void CompareMain(Spaceman sm, IReadOnlyList<ChunkInfo> chunks, BlockSet used, CheckReport r)
    {
        ulong leaked = 0, freeButUsed = 0, sumFree = 0;
        var examples = new List<string>();
        foreach (var ch in chunks)
        {
            var bm = sm.ReadChunkBitmap(ch);
            uint free = Spaceman.CountFree(bm, ch.BlockCount);
            sumFree += free;
            if (free != ch.FreeCount) r.Error(CoreText.T("Check.ChunkFreeCount", ch.Addr, ch.FreeCount, free));
            // Parçanın ötesindeki bitler boş (1.9.1; apfsck count_chunk_free bitmap bloğunun tamamını sayar)
            if (TailHasBits(bm, ch.BlockCount)) r.Error(CoreText.T("Check.ChunkBitmapTail", ch.Addr));
            for (uint i = 0; i < ch.BlockCount; i++)
            {
                bool onDisk = (bm[i >> 3] & (1 << (int)(i & 7))) != 0;
                bool expected = used.Get(ch.Addr + i);
                if (onDisk == expected) continue;
                if (onDisk) leaked++; else freeButUsed++;
                if (examples.Count < 5) examples.Add(CoreText.T("Check.BitmapExample", ch.Addr + i, (onDisk ? CoreText.T("Check.MarkedUsedNotUsed") : CoreText.T("Check.UsedMarkedFree"))));
            }
        }
        if (sumFree != sm.Main.FreeCount) r.Error(CoreText.T("Check.DeviceFreeCount", sm.Main.FreeCount, sumFree));
        if (freeButUsed > 0) r.Error(CoreText.T("Check.UsedButFree", freeButUsed));
        if (leaked > 0)
        {
            // Denetlenmeyen yapılar varsa dolu ama sahipsiz görünen bloklar onlarındır; sızıntı denemez
            if (r.OwnershipIncomplete) r.Warnings.Add(CoreText.T("Check.LeakedUnchecked", leaked));
            else r.Error(CoreText.T("Check.Leaked", leaked));
        }
        if (freeButUsed > 0 || (leaked > 0 && !r.OwnershipIncomplete)) r.Error(CoreText.T("Check.BitmapExamples", string.Join("; ", examples)));
        string match = freeButUsed + leaked == 0 ? CoreText.T("Check.OwnershipIdentical") : freeButUsed == 0 && r.OwnershipIncomplete ? CoreText.T("Check.OwnershipIdenticalChecked", leaked) : CoreText.T("Check.OwnershipDifferent");
        r.Info.Add(CoreText.T("Check.SpacemanSummary", sm.Main.BlockCount, sm.Main.FreeCount, match));
    }

    /// <summary><paramref name="bits"/>. bitten sonra (bayt dizisinin sonuna dek) dolu bit var mı.</summary>
    private static bool TailHasBits(byte[] bm, ulong bits)
    {
        ulong total = (ulong)bm.Length * 8;
        if (bits >= total) return false;
        int edge = (int)(bits / 8);
        if ((bm[edge] & (0xFF << (int)(bits % 8)) & 0xFF) != 0) return true;
        return bm.AsSpan(edge + 1).IndexOfAnyExcept((byte)0) >= 0;
    }

    private static void CompareIp(Spaceman sm, BlockSet ipUsed, CheckReport r)
    {
        // İç havuzun boyu = (parça + CIB + CAB) × 3 (1.9.1; apfsck check_internal_pool); bitmap iç havuzun ötesinde boş
        // (apfsck bitmap bloğunu bütünüyle karşılaştırır: "bad ip allocation bitmap")
        if ((sm.Main.ChunkCount + sm.Main.CibCount + sm.Main.CabCount) * 3 != sm.IpBlockCount)
            r.Error(CoreText.T("Check.IpPoolSize", sm.IpBlockCount));
        var ip = sm.ReadIpBitmap();
        if (TailHasBits(ip, sm.IpBlockCount)) r.Error(CoreText.T("Check.IpBitmapTail"));
        ulong diff = 0;
        for (ulong i = 0; i < sm.IpBlockCount; i++)
        {
            bool onDisk = (ip[i >> 3] & (1 << (int)(i & 7))) != 0;
            if (onDisk != ipUsed.Get(i)) diff++;
        }
        if (diff > 0) r.Error(CoreText.T("Check.IpBitmapDiff", diff));
    }

    /// <summary>Bellek içi blok kümesi (1 bit = 1 blok).</summary>
    private sealed class BlockSet(ulong count)
    {
        private readonly ulong[] _bits = new ulong[(count + 63) / 64];
        private ulong _charged;

        public bool Get(ulong b) => b < count && (_bits[b >> 6] & (1UL << (int)(b & 63))) != 0;

        /// <summary>İşaret bütçesi: şimdiye dek işaretlenenlerle birlikte kümenin boyunu aşmıyorsa sayar ve true döner.</summary>
        public bool TryCharge(ulong len)
        {
            if (len > count - _charged) return false;
            _charged += len;
            return true;
        }

        /// <summary>Aralığı işaretler; zaten işaretli bir blok varsa ilkini döndürür (yine de tamamını işaretler).</summary>
        public ulong? Mark(ulong start, ulong len)
        {
            ulong? dup = null;
            for (ulong b = start; b < start + len; b++)
            {
                ref ulong w = ref _bits[b >> 6];
                ulong bit = 1UL << (int)(b & 63);
                if ((w & bit) != 0) dup ??= b;
                w |= bit;
            }
            return dup;
        }
    }
}
