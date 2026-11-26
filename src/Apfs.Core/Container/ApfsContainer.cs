using Apfs.IO;
using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Container;

/// <summary>
/// APFS kapsayıcısı. Açılırken checkpoint alanındaki en yeni GEÇERLİ süper blok seçilir;
/// o checkpoint'in nesne haritası okunamıyorsa bir öncekine düşülür.
/// </summary>
public sealed class ApfsContainer : IDisposable
{
    private readonly bool _ownsDevice;

    private ApfsContainer(IBlockDevice device, ObjectReader reader, NxSuperblock sb, ObjectMap omap, bool ownsDevice, IReadOnlyList<string> warnings)
    {
        Device = device;
        Reader = reader;
        Superblock = sb;
        Omap = omap;
        _ownsDevice = ownsDevice;
        Warnings = warnings;
    }

    public IBlockDevice Device { get; }
    public ObjectReader Reader { get; }
    public NxSuperblock Superblock { get; }
    public ObjectMap Omap { get; }
    public ulong Xid => Superblock.Xid;
    public uint BlockSize => Superblock.BlockSize;

    /// <summary>Açılış sırasında karşılaşılan, açılmayı engellemeyen sorunlar (Türkçe).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Kapsayıcının GPT bölümü APFS dışı bir bölümle örtüşüyor (<see cref="Partitions.ApfsPartition.OverlapsOther"/>):
    /// tam denetim hata sayar, yazma kapısı reddeder.</summary>
    public bool PartitionOverlapsOther { get; private init; }

    /// <param name="partitionOverlapsOther">Bölümü açan (<c>SourceOpener</c>) bölüm tablosundan bildirir.</param>
    public static ApfsContainer Open(IBlockDevice device, bool ownsDevice = false, bool partitionOverlapsOther = false)
    {
        if (device.Length < ApfsConstants.MinBlockSize)
            throw new ApfsException(ApfsErrorCode.NotApfs, CoreText.T("Container.DeviceTooSmall"));

        var head = new byte[ApfsConstants.MinBlockSize];
        device.Read(0, head);
        if (Bin.U32(head, 32) != ApfsConstants.NxMagic)
            throw new ApfsException(ApfsErrorCode.NotApfs);
        uint blockSize = Bin.U32(head, 36);
        var reader = new ObjectReader(device, blockSize);
        var block0 = reader.ReadBlock(0);
        var sb0 = NxSuperblock.Parse(block0, 0);
        bool block0Valid = Fletcher64.Verify(block0);

        var warnings = new List<string>();
        if (!block0Valid) warnings.Add(CoreText.T("Container.Block0SuperblockCorrupt"));

        // Aday süper blokları topla (en yeni xid önce).
        var candidates = new List<NxSuperblock>();
        if ((sb0.XpDescBlocks & ApfsConstants.XpDescBlocksNonContiguous) == 0)
        {
            ulong count = sb0.XpDescBlocks;
            if (count > 0 && count <= MaxDescRingBlocks && count * blockSize <= MaxCandidateScanBytes
                && sb0.XpDescBase < reader.BlockCount && sb0.XpDescBase + count <= reader.BlockCount)
            {
                ScanCandidates(device, reader, sb0.XpDescBase, count, blockSize, candidates, warnings);
            }
            else
            {
                warnings.Add(CoreText.T("Container.CheckpointAreaInvalid"));
            }
        }
        else
        {
            warnings.Add(CoreText.T("Container.NonContiguousCheckpointArea"));
        }
        if (block0Valid) candidates.Add(sb0);
        KeepOwnContainer(candidates, block0Valid ? sb0.Uuid : null, warnings);

        var ordered = candidates.OrderByDescending(c => c.Xid).ThenBy(c => c.Paddr == 0 ? 1 : 0).ToList();
        var errors = new List<string>();
        foreach (var sb in ordered)
        {
            try
            {
                Validate(sb, reader);
                var omap = new ObjectMap(reader, sb.OmapOid);
                if (sb != ordered[0])
                    warnings.Add(CoreText.T("Container.OlderCheckpointUsed", sb.Xid));
                return new ApfsContainer(device, reader, sb, omap, ownsDevice, warnings) { PartitionOverlapsOther = partitionOverlapsOther };
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError and not ApfsErrorCode.AccessDenied)
            {
                errors.Add($"xid {sb.Xid}: {e.Detail ?? e.Code.ToString()}");
            }
        }
        throw new ApfsException(ApfsErrorCode.NoValidCheckpoint, errors.Count > 0 ? string.Join("; ", errors.Take(3)) : null);
    }

    /// <summary>
    /// Adaylardan yalnız bu kapsayıcınınkiler kalır (1.9.1): blok 0 geçerliyse onun <c>nx_uuid</c>'i, değilse en çok adayın
    /// paylaştığı UUID (eşitlikte en yüksek xid'li). Halkada başka UUID'li süper blok kalabilir (yeniden biçimlendirilmiş disk, kopyalanmış
    /// alan); xid'i daha yüksekse önceden seçilip başka bir kapsayıcının checkpoint'i açılıyordu. Bizimkinden yenisi atlandıysa uyarı
    /// (kapı reddeder); eskileri sessizce (biçimlendirme artığı olağan).
    /// </summary>
    private static void KeepOwnContainer(List<NxSuperblock> candidates, Guid? block0Uuid, List<string> warnings)
    {
        if (candidates.Count == 0) return;
        Guid own = block0Uuid ?? candidates.GroupBy(c => c.Uuid)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(c => c.Xid)).First().Key;
        var foreign = candidates.Where(c => c.Uuid != own).ToList();
        if (foreign.Count == 0) return;
        candidates.RemoveAll(c => c.Uuid != own);
        ulong newestOwn = candidates.Count > 0 ? candidates.Max(c => c.Xid) : 0;
        ulong newestForeign = foreign.Max(c => c.Xid);
        if (newestForeign > newestOwn) warnings.Add(CoreText.T("Container.ForeignCheckpointSkipped", newestForeign, foreign[0].Uuid));
    }

    /// <summary>
    /// Tanımlayıcı halkasındaki süper blok adayları. Halka büyük parçalarla ardışık okunur (parça başına bir G/Ç; eskiden blok
    /// başına bir okuma); parça okunamazsa o parçanın blokları tek tek denenir. Tek tek okumada G/Ç hatası bir kez yeniden denenir;
    /// yine okunamazsa blok atlanır ve UYARI eklenir (1.9.1: geçici bir USB hatası en yeni süper bloğu sessizce atlatıp diski
    /// bayat checkpoint'le bağlıyordu; uyarı yazmaya açmayı reddettirir, hizmet günlüğe yazar). Bozuk/başka tür blok sessizce atlanır.
    /// </summary>
    private static void ScanCandidates(IBlockDevice device, ObjectReader reader, ulong start, ulong count, uint blockSize,
        List<NxSuperblock> candidates, List<string> warnings)
    {
        int unreadable = 0;
        ulong firstUnreadable = 0;
        int chunkBlocks = (int)Math.Max(1, CandidateChunkBytes / blockSize);
        var chunk = new byte[(long)Math.Min((ulong)chunkBlocks, count) * blockSize];
        for (ulong first = 0; first < count; first += (ulong)chunkBlocks)
        {
            int n = (int)Math.Min((ulong)chunkBlocks, count - first);
            var span = chunk.AsSpan(0, n * (int)blockSize);
            bool whole;
            try { device.Read((long)((start + first) * blockSize), span); whole = true; }
            catch (ApfsException) { whole = false; }
            for (int k = 0; k < n; k++)
            {
                ulong paddr = start + first + (ulong)k;
                try
                {
                    byte[]? b;
                    if (whole) b = span.Slice(k * (int)blockSize, (int)blockSize).ToArray();
                    else if ((b = ReadWithRetry(reader, paddr)) is null)
                    {
                        if (unreadable++ == 0) firstUnreadable = paddr;
                        continue;
                    }
                    if (!Fletcher64.Verify(b) || (Bin.U32(b, 24) & ApfsConstants.ObjectTypeMask) != ApfsConstants.TypeNxSuperblock) continue;
                    if (Bin.U32(b, 32) != ApfsConstants.NxMagic || Bin.U32(b, 36) != blockSize) continue;
                    candidates.Add(NxSuperblock.Parse(b, paddr));
                }
                catch (ApfsException) { /* bu blok kullanılamaz; diğerlerine bak */ }
            }
        }
        if (unreadable > 0) warnings.Add(CoreText.T("Container.CandidateUnreadable", unreadable, firstUnreadable));
    }

    /// <summary>Bloğu okur; G/Ç hatasında bir kez daha dener, yine olmazsa null. Başka hatalar (sınır vb.) yukarı gider.</summary>
    private static byte[]? ReadWithRetry(ObjectReader reader, ulong paddr)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return reader.ReadBlock(paddr); }
            catch (ApfsException e) when (e.Code == ApfsErrorCode.IoError)
            {
                if (attempt == 1) return null;
            }
        }
    }

    /// <summary>
    /// Aday taramasının ve boş alan okumasının güvendiği en büyük tanımlayıcı halkası (blok). Gerçek halkalar küçük
    /// (tamDuzeltmePlan P0 Mac ölçümü: 512 MB'ta 8, 64 GB–16 TB'ta 280–292 blok; gerçek Disk 3 C 280): ×28 pay. Aşılırsa açılış
    /// yalnız blok 0'ı kullanır (uyarıyla), boş alan birim sayaçlarından. 1.8.1'e dek 1 000 000 (bozuk diskte çok uzun okuma).
    /// </summary>
    private const uint MaxDescRingBlocks = 8192;

    /// <summary>Aday taramasının okuyabileceği en çok bayt (büyük blok boyutunda halka sınırı bundan da küçülür).</summary>
    private const ulong MaxCandidateScanBytes = 64UL << 20;

    /// <summary>Aday taramasında tek okumanın boyu.</summary>
    private const uint CandidateChunkBytes = 1 << 20;

    private static void Validate(NxSuperblock sb, ObjectReader reader)
    {
        if ((sb.IncompatibleFeatures & ApfsConstants.NxIncompatVersion2) == 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Container.OldApfsVersion"));
        if ((sb.IncompatibleFeatures & ApfsConstants.NxIncompatFusion) != 0)
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Container.FusionDrive"));
        if (sb.BlockCount > reader.BlockCount)
            throw ApfsException.Corrupt(CoreText.T("Container.LargerThanDevice", sb.BlockCount, reader.BlockCount));
    }

    /// <summary>
    /// Kapsayıcıdaki tüm birimleri açar. Açılamayan birimler hata bilgisiyle döner; biri bozuksa diğerleri etkilenmez.
    /// Şifreli birimler için <paramref name="passwordProvider"/> çağrılır (verilmezse Encrypted hatasıyla döner).
    /// </summary>
    /// <param name="keys">Kilidi önceden açılmış birim anahtarları; birimin UUID'siyle eşleşen anahtar varsa parola sorulmaz.</param>
    public IReadOnlyList<VolumeOpenResult> OpenVolumes(Func<VolumeLockInfo, string?>? passwordProvider = null,
        IEnumerable<Crypto.VolumeKey>? keys = null)
    {
        var result = new List<VolumeOpenResult>();
        var uuids = new Dictionary<Guid, ulong>();
        for (int i = 0; i < Superblock.FsOids.Count; i++)
        {
            ulong oid = Superblock.FsOids[i];
            try
            {
                // Birim UUID'si kapsayıcıda tek (1.9.1): anahtar ve parola UUID ile eşlenir; aynı UUID'li ikinci birim (başka
                // oid) birincinin anahtarıyla açılmaya çalışılırdı. Aynı oid'in tekrarı ayrı konu (hizmet bir kez bağlar).
                Guid? uuid = null;
                try { uuid = VolumeUuid(oid); } catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { }
                if (uuid is { } u)
                {
                    if (uuids.TryGetValue(u, out ulong first) && first != oid)
                        throw ApfsException.Corrupt(CoreText.T("Container.VolumeUuidRepeated", u, first));
                    uuids.TryAdd(u, oid);
                }
                var key = keys is null ? null : keys.FirstOrDefault(k => k.VolumeUuid == (uuid ?? VolumeUuid(oid)));
                result.Add(new VolumeOpenResult(i, key is null ? ApfsVolume.Open(this, oid, passwordProvider) : ApfsVolume.Open(this, oid, key), null));
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError)
            {
                result.Add(new VolumeOpenResult(i, null, e));
            }
        }
        return result;
    }

    /// <summary>Birimin UUID'si (süper bloğu şifresizdir; kilit gerekmez).</summary>
    public Guid VolumeUuid(ulong oid) =>
        Bin.Uuid(Reader.ReadObject(Omap.Resolve(oid, Xid).Paddr, ApfsConstants.TypeFs, oid), 240);

    /// <summary>
    /// Kapsayıcıdaki boş alan (bayt). Önce alan yöneticisinin (spaceman) sayacı okunur: macOS'un gösterdiği değerdir ve şifreli
    /// değildir. Okunamazsa tüm birim süper bloklarındaki kullanım sayaçlarının (fs_alloc_count; o da şifresiz) toplamından hesaplanır.
    /// Şifreli birimin kilidi açılmamış olsa bile doğru sonuç verir.
    /// </summary>
    public long FreeBytes => _freeBytes ??= ComputeFreeBytes();
    private long? _freeBytes;

    /// <summary>Boş alanın nereden okunduğu (teşhis için): "alan yöneticisi" veya "birim sayaçları".</summary>
    public string FreeSpaceSource
    {
        get { _ = FreeBytes; return _freeSpaceSource; }
        private set => _freeSpaceSource = value;
    }
    private string _freeSpaceSource = "";

    private long ComputeFreeBytes()
    {
        long total = (long)Superblock.BlockCount * BlockSize;
        if (TryReadSpacemanFreeBlocks() is ulong free && free <= Superblock.BlockCount)
        {
            FreeSpaceSource = CoreText.T("Container.FreeSourceSpaceman");
            return (long)free * BlockSize;
        }
        ulong used = 0;
        foreach (ulong oid in Superblock.FsOids)
        {
            try
            {
                var loc = Omap.Lookup(oid, Xid);
                if (loc is null) continue;
                used += Bin.U64(Reader.ReadObject(loc.Value.Paddr, ApfsConstants.TypeFs, oid), 88); // apfs_fs_alloc_count
            }
            catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { }
        }
        FreeSpaceSource = CoreText.T("Container.FreeSourceVolumeCounters");
        return Math.Max(0, total - (long)Math.Min(used, Superblock.BlockCount) * BlockSize);
    }

    /// <summary>
    /// Alan yöneticisi geçici (ephemeral) bir nesnedir; yeri seçilen checkpoint'in eşleme bloklarındadır
    /// (checkpoint_map_phys_t → cpm_oid == nx_spaceman_oid → cpm_paddr). sm_dev[SD_MAIN].sm_free_count = ofset 72.
    /// Halkanın boyu ve uzunluğu diskten gelir: halka ≤ <see cref="MaxDescRingBlocks"/> blok ve kapsayıcının içinde, uzunluk ≤
    /// <see cref="MaxDescLen"/> olmalı; değilse null (birim sayaçları). Yoksa blok 0'ı bozuk büyük bir disk bütün aygıtı 4 KiB'lık
    /// okumalarla taratıyordu (1.8.1; gerçek Mac diski: 280 blok, uzunluk 2). Halka sınırı <see cref="Open"/>'ın aday taramasıyla
    /// aynı (1.8.2'den beri 8192).
    /// </summary>
    private ulong? TryReadSpacemanFreeBlocks()
    {
        var sb = Superblock;
        uint ringSize = sb.XpDescBlocks & ~ApfsConstants.XpDescBlocksNonContiguous;
        if (sb.SpacemanOid == 0 || ringSize == 0 || (sb.XpDescBlocks & ApfsConstants.XpDescBlocksNonContiguous) != 0 || sb.XpDescLen > ringSize)
            return null;
        if (ringSize > MaxDescRingBlocks || sb.XpDescBase > sb.BlockCount || ringSize > sb.BlockCount - sb.XpDescBase || sb.XpDescLen > MaxDescLen)
            return null;
        try
        {
            for (uint i = 0; i + 1 < sb.XpDescLen; i++)
            {
                ulong paddr = sb.XpDescBase + (sb.XpDescIndex + i) % ringSize;
                if (!Reader.TryReadValidObject(paddr, ApfsConstants.TypeCheckpointMap, out var map)) continue;
                uint count = Bin.U32(map, 36);
                for (int m = 0; m < count && 40 + (m + 1) * 40 <= map.Length; m++)
                {
                    int e = 40 + m * 40;
                    if (Bin.U64(map, e + 24) != sb.SpacemanOid) continue;
                    // Nesne birden çok blok sürebilir; sağlama toplamı cpm_size baytın tamamı üzerindendir
                    uint size = Bin.U32(map, e + 8);
                    ulong start = Bin.U64(map, e + 32);
                    if (size < 128 || size > 64 * BlockSize || size % BlockSize != 0) return null;
                    var sm = new byte[size];
                    for (uint k = 0; k < size / BlockSize; k++)
                        Reader.ReadBlock(start + k).CopyTo(sm, (int)(k * BlockSize));
                    if (!Fletcher64.Verify(sm) || (Bin.U32(sm, 24) & ApfsConstants.ObjectTypeMask) != ApfsConstants.TypeSpaceman) return null;
                    return Bin.U64(sm, 72);
                }
            }
        }
        catch (ApfsException e) when (e.Code is not ApfsErrorCode.IoError) { }
        return null;
    }

    /// <summary>Boş alan okumasının güvendiği en uzun checkpoint (tanımlayıcı blok sayısı; macOS'ta birkaç blok).</summary>
    internal const uint MaxDescLen = 4096;

    public void Dispose()
    {
        if (_ownsDevice) Device.Dispose();
    }
}

public sealed record VolumeOpenResult(int Index, ApfsVolume? Volume, ApfsException? Error);
