using Apfs.Util;
using Apfs.Volume;

namespace Apfs.Write;

/// <summary>
/// Bir değiştiren işlemin commit'e ekleyebileceği üst veri (<see cref="MutableBTree.Growth"/>'a gider). Dosya sistemi ağacında:
/// dokunduğu en çok yaprak (her kayıt ayrı yaprakta olabilir), eklenen ya da büyüyen kaydın girdiği en çok yaprak, ilk anahtarı
/// değişebilen ardışık anahtar aralığı sayısı (bir nesnenin kayıtları — inode, öznitelikler, extent'ler — ardışıktır) ve eklenen
/// bayt. Extentref ağacında aynısı (anahtarı sabit boylu: aralık yok).
/// </summary>
internal readonly record struct OpCost(ulong FsTouch, ulong FsGrow, ulong FsRanges, ulong FsBytes,
    ulong ExtTouch = 0, ulong ExtGrow = 0, ulong ExtBytes = 0)
{
    public static OpCost operator +(OpCost a, OpCost b) =>
        new(a.FsTouch + b.FsTouch, a.FsGrow + b.FsGrow, a.FsRanges + b.FsRanges, a.FsBytes + b.FsBytes,
            a.ExtTouch + b.ExtTouch, a.ExtGrow + b.ExtGrow, a.ExtBytes + b.ExtBytes);
}

/// <summary>
/// Yer denetimi (1.8.2, tamDuzeltmePlan P3). Commit kalıcı veriyi yerinde değiştirmez: değişen her ağaç
/// düğümü, birim süper bloğu ve nesne haritası nesnesi commit'te YENİ blok ister; bu işlemde bırakılan bloklar commit'e dek
/// karantinadadır (boş sayılmaz). Commit'in bu ayırmaları yapamaması oturumu arızalı yapar (son kayıttan beri değişiklikler
/// kaybolur). Bu yüzden her değiştiren işlem DENETİM evresinde şunu ister:
/// <c>boş ≥ pay + commit'in üst sınırı (bu işlem de olursa) + yeni veri blokları</c> — yetmezse hiçbir şey değişmeden DiskFull.
/// Pay üç kademelidir (<see cref="Room"/>): veri yazımı tam payı (<see cref="ReserveBlocks"/>; macOS'un kendi işleri için) bırakır;
/// yalnız üst veri büyüten işler (oluşturma, ad, zaman, seyrek uzatma) yalnız küçük bir silme payı bırakır
/// (<see cref="MetadataReserve"/>); yer açan işler (silme, kısaltma) payın tamamını kullanabilir — commit'leri yine sığmak
/// zorundadır. Böylece veriyle dolmuş diskte dosya oluşturulabilir, ad değiştirilebilir ve her zaman silinerek yer açılır
/// (silinen bloklar bir sonraki commit'te boşa çıkar).
/// </summary>
public sealed partial class VolumeWriter
{
    /// <summary>Denetimde hangi pay korunur.</summary>
    private enum Room
    {
        /// <summary>Yeni veri bloğu ayıran yazım: tam pay.</summary>
        Data,
        /// <summary>Yalnız üst veri büyüten iş: payın yarısı.</summary>
        Metadata,
        /// <summary>Yer açan iş (silme, kısaltma): pay kullanılabilir, yalnız commit'in kendisi sığmalı.</summary>
        Release,
    }

    // Kayıt boyları (bayt; anahtar + değer + içindekiler tablosu girdisi, cömert): extent 16 + 24 + 8, fiziksel extent 8 + 20 + 8,
    // nesne haritası 16 + 16 + 4. Ad taşıyan kayıtlar (inode NAME alanı, dizin girdisi; ad en çok 255 bayt UTF-8) en çok ~1 KiB.
    private const ulong ExtentRecordBytes = 56, PhysRecordBytes = 40, NamedRecordBytes = 1024, InodeGrowthBytes = 128;
    internal const ulong OmapRecordBytes = 40;

    /// <summary>
    /// Yedek pay (macOS'un kendi işleri için her zaman boş bırakılır): diskin %1'i, en az 4 MiB, en çok 64 MiB (ve diskin %10'u).
    /// 1.8.2'ye dek tabanı yoktu (64 MiB'lık birimde 650 KB): commit'in kesin üst sınırları düğüm doluluğuna bakmadığı için bir
    /// işlemde yüz bloğu bulabilir; payın yarısı silmeye yetmeli (bkz. <see cref="Room"/>). 400 MB'tan büyük disklerde değişmedi.
    /// </summary>
    private ulong ReserveBlocks
    {
        get
        {
            ulong total = _s.TotalBlocks, mib = 1024 * 1024 / Bs;
            return Math.Min(Math.Min(Math.Max(total / 100, 4 * mib), 64 * mib), total / 10);
        }
    }

    private ulong ReserveFor(Room room) => room switch
    {
        Room.Data => ReserveBlocks,
        Room.Metadata => MetadataReserve(),
        _ => 0,
    };

    /// <summary>
    /// Yalnız üst veri büyüten işlerin bıraktığı pay: sıradan bir dosyanın silinmesinin temiz durumdaki (commit'ten sonraki) üst
    /// sınırı, en az 1 MiB, en çok tam payın yarısı. Üst veri işleri böylece payın çoğunu kullanabilir (1.8.1'de sınırsızdı; büyük
    /// diskte payın yarısı 32 MiB olurdu ve macOS'un az boş bıraktığı birimde ad değiştirme bile reddedilirdi), ama üst veri payı
    /// bitince de bir silme her zaman sığar (yer açmanın yolu kapanmaz).
    /// </summary>
    private ulong MetadataReserve()
    {
        ulong mib = 1024 * 1024 / Bs;
        return Math.Min(ReserveBlocks / 2, Math.Max(mib, _s.CleanCommitNeed(this, TypicalDeleteCost)));
    }

    /// <summary>Sıradan dosyanın silinmesi: dizin girdisi, üst klasör, inode, dstream_id, bir extent; bir fiziksel kayıt.</summary>
    private static OpCost TypicalDeleteCost => new(5, 0, 3, 0, 2, 1, 2 * PhysRecordBytes);

    /// <summary>
    /// En büyük dosya boyu: extent uzunluğu diskte 56 bittir; daha büyük boy ya da ofset sessizce kırpılıp yapıyı bozuyordu.
    /// Blok katı ve 2^56'dan küçük.
    /// </summary>
    public ulong MaxFileSize => (1UL << 56) - Bs;

    private void RequireWithinMaxSize(ulong end)
    {
        if (end > MaxFileSize) throw new ApfsException(ApfsErrorCode.DiskFull, CoreText.T("VolWriter.FileTooLarge", MaxFileSize));
    }

    /// <summary>Son yer denetiminin hesapladığı commit üst sınırı (testler: işlemden sonraki gerçek ihtiyaç bunu aşmamalı).</summary>
    internal ulong LastCheckedNeed { get; private set; }

    /// <summary>Yapılan yer denetimi sayısı (testler: <see cref="LastCheckedNeed"/> bu işlemin mi).</summary>
    internal ulong SpaceChecks { get; private set; }

    /// <summary>
    /// Denetim evresinde: işlem (<paramref name="cost"/>) ve <paramref name="dataBlocks"/> yeni veri bloğu, <paramref name="room"/>
    /// payı ve commit'in üst sınırıyla birlikte boş alana sığmalı; sığmazsa hiçbir şey değişmeden DiskFull.
    /// <paramref name="credit"/>: işlemin, ayırmadan önce hemen boşa çıkaracağı (bu işlemde ayrılmış) bloklar. İç içe çağrı
    /// (<see cref="Rename"/> içindeki <see cref="Delete"/>) denetlenmez: dıştaki işlemin denetimi onu da kapsar.
    /// </summary>
    private void EnsureSpace(Room room, OpCost cost, ulong dataBlocks = 0, ulong credit = 0)
    {
        if (_mutationDepth > 0) return;
        ulong need = _s.CommitNeed(this, cost);
        LastCheckedNeed = need;
        SpaceChecks++;
        ulong free = _s.FreeBlocks + credit;
        if (free < ReserveFor(room) + need + dataBlocks) throw new ApfsException(ApfsErrorCode.DiskFull);
    }

    /// <summary>
    /// Bu birimin commit'te ana aygıttan ayıracağı blokların üst sınırı; <paramref name="acting"/> ise <paramref name="c"/> işlemi
    /// de yapılmış sayılır. Ayırmalar (<see cref="Flush"/>): extentref ve fs ağacının değişmiş her düğümü, nesne haritasının değişen
    /// düğümleri ve yeni nesnesi, birim süper bloğu.
    /// </summary>
    /// <paramref name="clean"/>: değişmiş düğüm yokmuş gibi (commit'ten hemen sonraki durum; <see cref="MetadataReserve"/>).
    internal ulong CommitNeed(OpCost c, bool acting, bool clean = false)
    {
        if (!_dirty && !acting) return 0; // Flush yalnız değişmiş birimde
        var (fsTouched, fsCreated) = _fs.Growth(c.FsTouch, c.FsGrow, c.FsRanges, c.FsBytes, clean);
        ulong fs = (clean ? 0 : _fs.DirtyCount) + fsTouched + fsCreated;
        var (extTouched, extCreated) = _extRef.Growth(c.ExtTouch, c.ExtGrow, 0, c.ExtBytes, clean);
        ulong ext = (clean ? 0 : _extRef.DirtyCount) + extTouched + extCreated;
        // Nesne haritası (Relocate/RemoveVirtual): yazılan her fs düğümü kaydını yeniler (sil + aynı kimlikle ekle: kayıt yaprağın
        // ilk kaydıysa ayraç ilerler ve kayıt ÖNCEKİ yaprağa girer — yaprak başına en çok bir kez), ağaçtan çıkan her düğüm kaydını
        // siler (çıkan düğümler fs'te sayılı: ya değişmişti ya dokunulandı), yeni her düğüm kayıt ekler — yeni kimlikler en büyük
        // kimliklerdir, son yaprağa girer
        ulong records = fs + (clean ? 0 : (ulong)_fs.DeletedOids.Count);
        ulong rerouted = Math.Min(records, _omap.NodeCount);
        ulong inserts = (clean ? 0 : _fs.CreatedCount) + fsCreated + rerouted;
        var (omapTouched, omapCreated) = _omap.Growth(2 * records, 1 + rerouted, 0, inserts * OmapRecordBytes, clean);
        ulong omap = (clean ? 0 : _omap.DirtyCount) + omapTouched + omapCreated + 1; // + omap nesnesi
        return ext + fs + omap + 1; // + birim süper bloğu
    }

    // ---------------------------------------------------------------------------------------------------------------
    // İşlemlerin maliyeti (denetim evresinde, hiçbir şey değişmeden)

    /// <summary>Yeni dosya/klasör: yeni inode (yeni kimlik: kendi aralığı), dizin girdisi, üst klasörün inode'u.</summary>
    private static OpCost CreateCost => new(3, 2, 3, 2 * NamedRecordBytes);

    /// <summary>Zaman ya da gizli bayrağı: yalnız inode (anahtarı ve boyu değişmez).</summary>
    private static OpCost InodeOnlyCost => new(1, 0, 0, 0);

    /// <summary>Ad değiştirme: eski ve yeni dizin girdisi, iki üst klasör, inode (NAME alanı) ve kardeş kaydı.</summary>
    private static OpCost RenameCost => new(6, 3, 6, 2 * NamedRecordBytes);

    /// <summary>
    /// Bir aralıkla kesişen extent'ler ve fiziksel kayıtları (<see cref="Overlap"/>): paylaşılan (başvurusu 1'den çok) kayıtlar
    /// bölünür; aralığı aşan (kısmen kaplanan) kayıtlar silinip kalan parçası yeniden konur.
    /// </summary>
    private readonly record struct Overlapped(ulong Extents, ulong Phys, ulong SharedPhys, ulong PartialPhys = 0);

    // Aynı anahtarla sil + koy (kalan parça, birleşen komşu): anahtar yaprağın ilk kaydıysa ayraç ilerler ve kayıt ÖNCEKİ yaprağa
    // girer — o yaprak da değişir ve dolu olabilir. Bu yüzden böyle her kayıt bir yaprak daha ve bir ekleme sayılır.

    /// <summary>
    /// [first, last) bloklarını <paramref name="runs"/> parçada yeni bloklara yazmak. fs: kesişen extent'ler (sol parçası aynı
    /// anahtarla geri konur), birleşebilen iki komşu, delik, inode ve dstream_id (inode ve akış kimliğinin iki aralığı), dosyanın
    /// önceden birleşmemiş <paramref name="mergeablePairs"/> bitişik extent çifti (birleştirme döngüsü bütün listeyi dolaşır).
    /// Extentref: her parçanın kaydı ve bitişik sol kayıtla birleşmesi, bırakılan blokların kayıtları.
    /// </summary>
    private static OpCost WriteCost(ulong runs, Overlapped o, ulong mergeablePairs = 0) =>
        new(2 * o.Extents + 6 + 2 * mergeablePairs, o.Extents + 2 + mergeablePairs, 2,
            (2 * runs + 2 + o.Extents + mergeablePairs) * ExtentRecordBytes + InodeGrowthBytes,
            2 * runs + o.Phys + o.PartialPhys, runs + o.SharedPhys + o.PartialPhys,
            (runs + 2 * o.SharedPhys + 2 * o.PartialPhys) * PhysRecordBytes);

    /// <summary>
    /// Extent listesinde <see cref="ReplaceRange"/>'in birleştireceği önceden var olan bitişik çiftler (macOS bitişik extent'leri her
    /// zaman birleştirmez): aynı koşul — ikisi de delik ya da fiziksel ve crypto_id olarak ardışık, bayrakları aynı.
    /// </summary>
    private static ulong MergeablePairs(List<Ext> exts)
    {
        ulong n = 0;
        for (int i = 1; i < exts.Count; i++)
        {
            Ext l = exts[i - 1], r = exts[i];
            if (l.End == r.Start && ((l.Phys == 0 && r.Phys == 0) ||
                (l.Phys != 0 && r.Phys == l.Phys + l.Blocks && r.Crypto == l.CryptoAt(l.Blocks) && r.Flags == l.Flags))) n++;
        }
        return n;
    }

    /// <summary>
    /// Kısaltma: kesilen extent'ler silinir ya da kısalır (kısalan aynı anahtarla geri konur), inode'un seyrek alanı büyüyebilir;
    /// fiziksel kayıtlar bırakılır. Dokunulan yapraklar anahtarlarından sayılır (bir dosyanın extent'leri ardışıktır).
    /// </summary>
    private OpCost TruncateCost(ulong id, ulong streamId, List<Ext> exts, ulong newBlocks)
    {
        var fsKeys = new List<byte[]> { Records.Key(id, JType.Inode), Records.Key(streamId, JType.DstreamId) };
        var extKeys = new List<byte[]>();
        var (shared, partial) = CollectExtentKeys(streamId, exts, newBlocks, fsKeys, extKeys);
        return new(_fs.DistinctLeaves(fsKeys) + 1, 2, 2, InodeGrowthBytes + ExtentRecordBytes,
            _extRef.DistinctLeaves(extKeys) + partial, shared + partial, 2 * (shared + partial) * PhysRecordBytes);
    }

    /// <summary>
    /// <paramref name="fromBlock"/>'tan sonrası kesilen/silinen extent'lerin fs anahtarları ve fiziksel kayıtlarının extentref
    /// anahtarları; döner: paylaşılan (başvurusu 1'den çok) ve bırakılan aralığı aşan (kalan parçası geri konan) kayıt sayısı.
    /// </summary>
    private (ulong Shared, ulong Partial) CollectExtentKeys(ulong streamId, List<Ext> exts, ulong fromBlock, List<byte[]> fsKeys, List<byte[]> extKeys)
    {
        ulong shared = 0, partial = 0;
        foreach (var x in exts)
        {
            if (x.End <= fromBlock) continue;
            fsKeys.Add(Records.FileExtentKey(streamId, x.Start * Bs));
            if (x.Phys == 0) continue;
            ulong keep = x.Start < fromBlock ? fromBlock - x.Start : 0;
            ulong start = x.Phys + keep, end = x.Phys + x.Blocks;
            foreach (var p in PhysOverlapping(start, end - start))
            {
                extKeys.Add(Records.PhysExtKey(p.Start));
                if (p.Refcnt > 1) shared++;
                if (p.Start < start || p.End > end) partial++;
            }
        }
        return (shared, partial);
    }

    /// <summary>Seyrek uzatma: delik extent'i (öncekiyle birleşebilir), inode ve dstream_id; önceden birleşmemiş çiftler.</summary>
    private static OpCost ExtendCost(ulong mergeablePairs) =>
        new(4 + 2 * mergeablePairs, 2 + mergeablePairs, 2, (2 + mergeablePairs) * ExtentRecordBytes + InodeGrowthBytes);

    /// <summary>Yalnız boy (blok sayısı aynı): inode ve dstream_id.</summary>
    private static OpCost ResizeCost => new(2, 1, 1, InodeGrowthBytes);

    /// <summary>[first, last) ile kesişen extent sayısı ve fiziksel bloklarının extentref kayıtları (paylaşılan, kısmen kaplanan).</summary>
    private Overlapped Overlap(List<Ext> exts, ulong first, ulong last)
    {
        ulong n = 0, phys = 0, shared = 0, partial = 0;
        foreach (var x in exts)
        {
            if (x.End <= first || x.Start >= last) continue;
            n++;
            if (x.Phys == 0) continue;
            ulong a = Math.Max(x.Start, first), b = Math.Min(x.End, last);
            ulong start = x.Phys + (a - x.Start), end = start + (b - a);
            foreach (var p in PhysOverlapping(start, b - a))
            {
                phys++;
                if (p.Refcnt > 1) shared++;
                if (p.Start < start || p.End > end) partial++;
            }
        }
        return new(n, phys, shared, partial);
    }

    /// <summary>
    /// [first, last) yazımının yeni veri blokları ve parça sayısı üst sınırı: bu işlemde ayrılmış (yerinde yazılan) bloklar dışındaki
    /// her blok yeni bloğa gider; ayırma her kesintisiz aralıkta (yerinde yazılan bloklar arası) yeniden başlar, alan yöneticisi her
    /// aralığı boş parçalarına göre böler (<see cref="SpacemanWriter.CountRuns"/>).
    /// </summary>
    private (ulong Blocks, ulong Runs) NewBlocksPlan(List<Ext> exts, ulong first, ulong last)
    {
        ulong blocks = 0, segments = 0, pos = first;
        bool inSegment = false;
        foreach (var x in exts) // başlangıca göre sıralı, örtüşmeyen
        {
            if (x.End <= first || x.Start >= last) continue;
            ulong a = Math.Max(x.Start, first), b = Math.Min(x.End, last);
            if (a > pos) { blocks += a - pos; if (!inSegment) segments++; inSegment = true; } // önündeki boşluk (delik/dosya sonu)
            for (ulong blk = a; blk < b; blk++)
            {
                if (x.Phys != 0 && _s.Spaceman.IsAllocatedThisTx(x.Phys + (blk - x.Start))) { inSegment = false; continue; }
                blocks++;
                if (!inSegment) segments++;
                inSegment = true;
            }
            pos = b;
        }
        if (last > pos) { blocks += last - pos; if (!inSegment) segments++; }
        if (blocks == 0) return (0, 0);
        // Yer yoksa parça sayısı önemsiz (denetim zaten reddeder); tarama boş alanla sınırlı
        ulong runs = blocks > _s.FreeBlocks ? 1 : _s.Spaceman.CountRuns(blocks);
        return (blocks, runs + segments);
    }

    /// <summary>
    /// Girdinin silinmesi: dizin girdisi, üst klasör; son bağlantıysa inode, öznitelikleri (veri akışlarıyla), kardeş kayıtları ve
    /// haritaları, dstream_id ve kendi akışı (klonda yalnız sayı düşer); başka bağlantısı varsa kardeş kaydı ve birincil ad.
    /// Dokunulan yapraklar silinecek kayıtların anahtarlarından sayılır; silmede kayıt eklenmez (yalnız adı değişen birincil
    /// bağlantıda).
    /// </summary>
    private OpCost DeleteCost(ulong parentId, byte[] drecKey, ulong id, InodeRecord target)
    {
        var fsKeys = new List<byte[]> { drecKey, Records.Key(parentId, JType.Inode), Records.Key(id, JType.Inode) };
        ulong ranges = 3; // dizin girdisi, üst klasör, nesnenin aralığı (inode, öznitelikler, kardeş kayıtları, dstream_id)
        foreach (var (sib, _, _) in Siblings(id))
        {
            fsKeys.Add(SiblingLinkKey(id, sib));
            fsKeys.Add(Records.Key(sib, JType.SiblingMap));
            ranges++; // kardeş haritası kardeş kimliğiyle
        }
        if (!target.IsDirectory && target.ChildrenOrLinks > 1)
            return new(_fs.DistinctLeaves(fsKeys), 1, ranges, NamedRecordBytes);

        var extKeys = new List<byte[]>();
        ulong shared = 0, partial = 0;
        void Stream(ulong streamId)
        {
            fsKeys.Add(Records.Key(streamId, JType.DstreamId));
            var (s, p) = CollectExtentKeys(streamId, LoadExtents(streamId), 0, fsKeys, extKeys);
            shared += s;
            partial += p;
            if (streamId != id) ranges++;
        }
        foreach (var e in _fs.Scan(k => JKey.ComparePrefix(k, id, JType.Xattr)))
        {
            fsKeys.Add(e.Key);
            if (e.Value.Length >= 4 + 8 && (Bin.U16(e.Value, 0) & Records.XattrDataStream) != 0) Stream(Bin.U64(e.Value, 4));
        }
        if (IsCloned(target)) fsKeys.Add(Records.Key(target.PrivateId, JType.DstreamId));
        else Stream(target.PrivateId);
        return new(_fs.DistinctLeaves(fsKeys), 0, ranges, 0, _extRef.DistinctLeaves(extKeys) + partial, shared + partial,
            2 * (shared + partial) * PhysRecordBytes);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Gezgin'e bildirilen boş alan ve ön ayırma

    /// <summary>
    /// Yeni bir dosyaya şimdi yazılabilecek veri (bayt): boş alandan yedek pay, commit'in üst sınırı (bir dosya oluşturulup tek
    /// parça yazılırsa) ve aynı büyümenin bir payı daha düşülür (yazım sürerken değişen düğümler). Gezgin kopyalamadan önce bunu
    /// dosyanın boyuyla karşılaştırır; önceden yedek pay da boş sayılıyordu ve kopya sonda "disk dolu" ile kesiliyordu.
    /// </summary>
    public ulong AvailableBytes()
    {
        var cost = CreateCost + WriteCost(1, default);
        ulong now = _s.CommitNeed();
        ulong need = _s.CommitNeed(this, cost);
        ulong want = ReserveBlocks + need + (need - now);
        ulong free = _s.FreeBlocks;
        return free > want ? (free - want) * Bs : 0;
    }

    /// <summary>
    /// Yeni oluşturulacak (ya da baştan yazılacak: <paramref name="replacing"/>) dosyanın ön ayırması: <paramref name="allocationSize"/>
    /// baytlık veri sığar mı. Baştan yazılan dosyanın paylaşılmayan blokları sayılır (kesilince bir sonraki commit'te boşa çıkar;
    /// sürücü yer yetmeyen yazımda commit edip yeniden dener — NTFS de eski ayırmayı önce bırakır). Hiçbir şey değişmez; sığmazsa
    /// DiskFull. Ön ayırma bir tahmindir (<see cref="AvailableBytes"/> gibi tek parça varsayar): gösterilen boş alan kadarını
    /// reddetmez; parçalı boş alanda asıl yazımlar (her biri kesin denetlenir) daha önce durabilir.
    /// </summary>
    public void EnsureNewFileRoom(ulong allocationSize, ulong? replacing = null)
    {
        if (allocationSize == 0) return;
        RequireWithinMaxSize(allocationSize);
        ulong blocks = (allocationSize + Bs - 1) / Bs;
        ulong released = replacing is ulong id && GetInode(id) is { } ino ? UnsharedBlocks(LoadExtents(ino.PrivateId)) : 0;
        EnsureSpace(Room.Data, CreateCost + WriteCost(1, default), blocks, released);
    }

    /// <summary>
    /// Ön ayırma (WinFsp ayırma boyutu): dosyanın [0, <paramref name="allocationSize"/>) aralığındaki henüz verisi olmayan
    /// blokları şimdi yazılsa sığar mı (tek parça tahmini, bkz. <see cref="EnsureNewFileRoom"/>). Hiçbir şey ayrılmaz ve değişmez
    /// (APFS'te ön ayırma yok); sığmazsa DiskFull — Gezgin kopyayı başlamadan "yer yok" diye durdurur, yarıda kesilmez.
    /// </summary>
    public void EnsureAllocationRoom(ulong id, ulong allocationSize)
    {
        var ino = RequireWritableFile(id);
        RequireWithinMaxSize(allocationSize);
        ulong blocks = (allocationSize + Bs - 1) / Bs;
        var exts = LoadExtents(ino.PrivateId);
        // Dosya sonunun ötesindeki (ön ayrılmış) bloklar sayılmaz: uzatma onları deliğe çevirir
        ulong mapped = 0, within = Math.Min(blocks, (GetSize(id) + Bs - 1) / Bs);
        foreach (var x in exts)
            if (x.Phys != 0 && x.Start < within) mapped += Math.Min(x.End, within) - x.Start;
        if (blocks <= mapped) return;
        EnsureSpace(Room.Data, WriteCost(1, new Overlapped((ulong)exts.Count, 0, 0)), blocks - mapped);
    }

    /// <summary>Extent'lerin başvurusu 1 olan (silinince boşa çıkacak) fiziksel blok sayısı.</summary>
    private ulong UnsharedBlocks(List<Ext> exts)
    {
        ulong n = 0;
        foreach (var x in exts)
        {
            if (x.Phys == 0) continue;
            foreach (var p in PhysOverlapping(x.Phys, x.Blocks))
                if (p.Refcnt == 1) n += Math.Min(p.End, x.Phys + x.Blocks) - Math.Max(p.Start, x.Phys);
        }
        return n;
    }
}
