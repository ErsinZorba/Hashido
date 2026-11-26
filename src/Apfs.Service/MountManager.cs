using System.Diagnostics;
using System.Text.Json.Nodes;
using Apfs.Container;
using Apfs.Crypto;
using Apfs.Diagnostics;
using Apfs.IO;
using Apfs.Mount;
using Apfs.Partitions;
using Apfs.Volume;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;
using Apfs.Windows.Reporting;
using Apfs.Write;

namespace Apfs.Service;

/// <summary>
/// Takılı APFS disklerini bulur ve birimlerini salt okunur bağlar. Şifreli birimler için tepsiden parola bekler.
/// "Donanımı güvenli kaldır" isteğinde birimleri ayırıp disk tanıtıcısını kapatır; disk çekilirse temizlik yapar.
/// Tüm durum değişiklikleri tek kilit altında yapılır.
/// </summary>
internal sealed class MountManager : IDisposable
{
    private const int MaxPhysicalDrives = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, TrackedDisk> _disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Notice> _notices = [];
    // Seyreltmeler tekdüze saatle ve sınırlı (1.8.2): saat geri alınınca iletiler susmaz, düşmanca klasörün sayısız farklı
    // iletisi belleği büyütmez
    private readonly RecentKeys _recentNotices = new(); // AddNotice throttleKey (ve "unreadable#disk") → günlük/bildirim seyreltmesi
    private readonly RecentKeys _recentQueued = new();  // AddNotice queueKey → kuyruk seyreltmesi
    private readonly IReadOnlyList<string>? _imageSources;
    private readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase); // güvenli kaldırma sonrası yeniden bağlanmasın
    private long _noticeId;
    /// <summary>Bu hizmet çalışmasının kimliği ("status" yanıtında): tepsi hizmetin yeniden başladığını buradan anlar.</summary>
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly ReportRecorder? _reports;
    private readonly Dictionary<string, int> _devOpenAttempts = new(StringComparer.OrdinalIgnoreCase); // APFS_DEV_OPEN_FAILURES
    private int _devMountCalls; // APFS_DEV_MOUNT_THROW_AT
    /// <summary>Hizmet durduruluyor (<see cref="Dispose"/>; <see cref="_gate"/> altında): süren tarama diski yayımlamaz, yenisi başlamaz.</summary>
    private bool _disposed;
    /// <summary>
    /// Güç dönemi (1.9.1; <see cref="_gate"/> altında): her uyku başında artar. Yazmaya açma oturumu yayımlamadan önce karşılaştırır:
    /// arada uyku olduysa (bekletilmemiş oturum uyanışta yazardı) var olan geri alma yoluyla salt okunur bağlar.
    /// </summary>
    private long _powerEpoch;
    /// <summary>
    /// Canlı bütün yazma oturumlarının uyku kapıları (1.9.1; <see cref="_gate"/> altında): oluşturulduğu andan kaynakları bırakılana dek
    /// — yayımlanmış, yazmaya açılmakta ya da kaydederek kapanmakta olan. Uyku hepsini dondurur (İnceleme: kapanmakta olan oturum
    /// <c>Writes</c>'ta değil, uykuyu görmeden uyanışta kaydedebiliyordu).
    /// </summary>
    private readonly HashSet<PowerGateDevice> _liveGates = [];

    /// <param name="imageSources">Yalnızca geliştirme (konsol) modunda: fiziksel diskler yerine bu görüntü dosyaları kullanılır.</param>
    /// <param name="reports">Raporlama (onay verilmediyse kayıt hiçbir şey yapmaz; bkz. <see cref="ReportRecorder"/>).</param>
    public MountManager(IReadOnlyList<string>? imageSources = null, ReportRecorder? reports = null)
    {
        _imageSources = imageSources;
        _reports = reports;
    }

    /// <summary>
    /// Takılı bir disk. <see cref="Device"/> diskin tamamını salt okunur açar (bütün salt okunur kapsayıcılar ve kimlik denetimi
    /// bunu kullanır). Yazmaya açılan her APFS bölümünün kendi oturumu ve yazma tanıtıcısı vardır (<see cref="Writes"/>).
    /// </summary>
    private sealed class TrackedDisk(string key, IBlockDevice device)
    {
        public string Key { get; } = key;
        public IBlockDevice Device { get; } = device;
        /// <summary>Takıldığındaki APFS kimliği (<see cref="PartitionLocator.Fingerprint"/>); değişirse disk başka bir programca değiştirilmiştir.</summary>
        public string Fingerprint { get; set; } = "";
        /// <summary>Takıldığındaki boyut (aygıt bırakıldıktan sonra da başlık yazılabilsin).</summary>
        private readonly long _length = device.Length;
        /// <summary>
        /// Disk başlığı ("Disk 2 (1,81 TB)"; tepsi, menü, bildirimler, yanıtlar). Kullanıldığı ANDA üretilir (1.8.2): o anki
        /// arayüz dili ve sayı biçimiyle — istek sürerken isteyeninki, kendiliğinden işte varsayılan (<see cref="ServiceLanguage"/>).
        /// </summary>
        public string Description => DescribeDisk(Key, _length);
        /// <summary>APFS bölüm sırası → salt okunur açılmış kapsayıcı (yazmaya açık bölümde yok; onu oturum tutar).</summary>
        public Dictionary<int, ApfsContainer> Containers { get; } = [];
        public List<TrackedVolume> Volumes { get; } = [];
        public DeviceNotifications.Registration? RemovalWatch { get; set; }
        /// <summary>Disk bırakılınca (çıkarma, içerik değişimi, hizmet durması) iptal edilir: süren kilit açma kayıtlar arasında durur.</summary>
        public CancellationTokenSource Released { get; } = new();
        /// <summary>APFS bölüm sırası → yazma oturumu (o bölüm yazmaya açıksa).</summary>
        public Dictionary<int, ContainerWrite> Writes { get; } = [];
        /// <summary>APFS bölüm sırası → son "yazmaya izin ver" denemesinin ret nedeni.</summary>
        public Dictionary<int, string> WriteBlockReasons { get; } = [];
        /// <summary>Şu anda yazma durumu değişen bölümler (aynı bölüme ikinci istek reddedilir).</summary>
        public HashSet<int> Switching { get; } = [];
        /// <summary>
        /// Disk kimliğini değiştiren tek iş (oturumun Close'u blok 0'ı yazar) ile kimlik denetimi bu kilitle sıralanır: bir bölüm
        /// kapanırken araya giren tarama kimliği "değişmiş" görüp ÖBÜR bölümün oturumunu terk etmesin. Kimlik kilit içinde tazelenir.
        /// </summary>
        public object IdentityLock { get; } = new();
        /// <summary>
        /// Yazma oturumlu diskte kimlik bir kez okunamadı (1.9.1; <see cref="IdentityLock"/> altında): oturumlar bekletildi, sonraki
        /// denetim karar verir (doğrulanırsa bekletme kalkar; yine hata ya da farklı kimlik → kaydedilmeden bırakılır).
        /// </summary>
        public bool IdentityUnreadOnce { get; set; }
        /// <summary>Bu diskin son uyku bildiriminin güç dönemi ve sonucu (<see cref="_gate"/> altında; dönem başına tek bildirim).</summary>
        public long SleepNoticeEpoch { get; set; } = -1;
        public int SleepNoticeOutcome { get; set; }
        /// <summary>
        /// Kilidi açılmış şifreli birimlerin anahtarları (birim UUID'si → VEK kopyası; <see cref="_gate"/> altında). Bölüm yeniden
        /// bağlanırken (yazmaya açma/kapama, ret) parola yeniden sorulmaz; disk bırakılınca sıfırlanır. Günlüğe yazılmaz.
        /// </summary>
        public Dictionary<Guid, VolumeKey> Keys { get; } = [];
        /// <summary>Raporlama: aygıt düzeyi bilgiler (takılırken bir kez).</summary>
        public DiskFacts? Facts { get; set; }
        /// <summary>Raporlama: APFS bölüm sırası → kapsayıcının yapı bilgileri (bağlanırken; <see cref="_gate"/> altında).</summary>
        public Dictionary<int, ContainerFacts> ContainerFacts { get; } = [];
    }

    /// <summary>Bir APFS bölümünün yazma oturumu ve onu kapatınca bırakılacak yazılabilir aygıtlar.</summary>
    private sealed class ContainerWrite(WriteSession session, IReadOnlyList<IDisposable> resources)
    {
        public WriteSession Session { get; } = session;
        /// <summary>Oturumun başlangıcı (raporda süre aralığı).</summary>
        public Stopwatch Age { get; } = Stopwatch.StartNew();
        public IReadOnlyList<IDisposable> Resources { get; } = resources;
        /// <summary>Yazma tanıtıcısının güvenli kaldırma kaydı (her açık tanıtıcı izlenir; hepsi aynı ReleaseDisk'e gider).</summary>
        public DeviceNotifications.Registration? RemovalWatch { get; set; }
        /// <summary>
        /// Oturumun bir commit'i ya da (1.8.1) değiştiren bir işlemi yarıda kaldı (<see cref="WriteSession.IsFaulted"/>; sürücünün
        /// <c>Faulted</c> olayı, oturum başına bir kez): hatanın iletisi. Doluysa oturum kaydedilmeden bırakılmalı (<see cref="HandleFault"/>).
        /// </summary>
        public volatile string? FaultMessage;
        /// <summary>Uyku kapısı (1.9.1): uykudan önce dondurulur, oturumdan aygıta tek bayt gitmez.</summary>
        public PowerGateDevice? Gate { get; init; }
        /// <summary>
        /// Uyku işareti (1.9.1; <see cref="_gate"/> altında): <see cref="SleepNone"/>, uykudan önce <see cref="SleepNotSaved"/>
        /// (kayıt tutmadı / sürüyor) ya da <see cref="SleepSaved"/>. Oturum bırakılana dek kalır: işaretli oturum yalnız kaydedilmeden
        /// bırakılır (uyanışta, çıkarmada, "Salt okunura dön"de) ve karar bildirimi tek.
        /// </summary>
        public int Sleep;
        /// <summary>Disk kimliği bir kez okunamadığı için bekletildi (1.9.1; <see cref="TrackedDisk.IdentityLock"/> altında).</summary>
        public volatile bool IdentityHold;
    }

    private const int SleepNone = 0, SleepNotSaved = 1, SleepSaved = 2;

    private sealed class TrackedVolume(ApfsContainer container, int containerIndex, int index, ulong oid, string name)
    {
        public ApfsContainer Container { get; } = container;
        public int ContainerIndex { get; } = containerIndex;
        public int Index { get; } = index;
        public ulong Oid { get; } = oid;
        public string Name { get; set; } = name;
        public string State { get; set; } = VolumeStatus.Failed;
        public string? Hint { get; set; }
        public string? Message { get; set; }
        /// <summary>Kilitli birimin UUID'si (parola olmadan, kilit bilgisiyle öğrenilir; tepsi kayıtlı parolayı buna bağlar).</summary>
        public Guid? Uuid { get; set; }
        /// <summary>Raporlama: hata durumunun kodu (<see cref="ApfsErrorCode"/> adı; ileti değil).</summary>
        public string? FailureCode { get; set; }
        public ApfsMount? Mount { get; set; }
        public ApfsWritableMount? WritableMount { get; set; }
    }

    // ---------------- Tarama ----------------

    /// <summary>
    /// Taramalar tek tek çalışır: disk ancak bütün birimleri bağlandıktan sonra <see cref="_disks"/>'e girer; aynı anda iki tarama
    /// (zamanlanmış + "Yeniden tara", ya da hata yollarındaki yeniden tarama) onu ikisi de yeni sanıp birimleri iki kez bağlıyordu.
    /// Bekleyen ikinci tarama ilki bitince diski listede görür.
    /// </summary>
    private readonly Lock _scanLock = new();

    /// <summary>Yeni diskleri bağlar, kaybolanları temizler.</summary>
    public void Rescan()
    {
        lock (_scanLock) RescanCore();
    }

    private readonly Lock _rescanQueue = new();
    /// <summary>Henüz başlamamış birleşik tarama (<see cref="_rescanQueue"/> altında); yeni "Yeniden tara" istekleri buna katılır.</summary>
    private TaskCompletionSource? _nextRescan;
    private bool _rescanWorkerRunning;

    /// <summary>
    /// Kanaldan "Yeniden tara" (1.9.1): istekler birleşir. Süren birleşik tarama varsa onun bitişi, sonra bu istekten SONRA başlayan
    /// TEK tarama beklenir; o tarama başlamadan gelen bütün istekler aynısını bekler (aynı <see cref="TaskCompletionSource"/>).
    /// Önceden her istek bir havuz iş parçacığını <see cref="_scanLock"/>'ta bekletip ayrı tarama yapıyordu (N istek → N iş parçacığı).
    /// Tarama arka plan işidir: isteğin dili/biçimi taşınmaz (kuyruk <c>UnsafeQueueUserWorkItem</c>), hata günlüğe yazılır ve
    /// bekleyenlerin her biri yine tek yanıt alır.
    /// </summary>
    public Task RescanAsync()
    {
        lock (_rescanQueue)
        {
            var next = _nextRescan ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_rescanWorkerRunning)
            {
                _rescanWorkerRunning = true;
                ThreadPool.UnsafeQueueUserWorkItem(_ => RunRescans(), null); // arka plan işi: çağıranın dili/biçimi taşınmaz
            }
            return next.Task;
        }
    }

    /// <summary>Bekleyen birleşik taramaları sırayla yapar (tek iş parçacığı); bekleyen kalmayınca biter.</summary>
    private void RunRescans()
    {
        while (true)
        {
            TaskCompletionSource current;
            lock (_rescanQueue)
            {
                if (_nextRescan is null)
                {
                    _rescanWorkerRunning = false;
                    return;
                }
                current = _nextRescan;
                _nextRescan = null; // bundan sonra gelen istek bir sonraki taramayı bekler
            }
            try
            {
                DevPause("APFS_DEV_RESCAN_DELAY_MS"); // yalnız geliştirme modu: birleşik tarama süresini uzatır, her tarama günlükte bir satır
                try { Rescan(); }
                catch (Exception e) { ServiceLog.Write(WinText.T("Svc.ScanError", e.Message)); }
            }
            finally { current.TrySetResult(); }
        }
    }

    private void RescanCore()
    {
        foreach (var (key, open) in EnumerateSources())
        {
            lock (_gate)
            {
                if (_disposed) return; // hizmet durduruluyor
                if (_disks.ContainsKey(key) || _released.Contains(key)) continue;
            }
            IBlockDevice? device = null;
            try
            {
                device = open();
                if (device is null) { ForgetUnreadable(key); continue; }
                if (PartitionLocator.FindApfsPartitions(device).Count == 0)
                {
                    ForgetUnreadable(key);
                    device.Dispose();
                    continue;
                }
                if (AttachDisk(key, device)) device = null; // sahipliği TrackedDisk'e geçti (durdurulurken yayımlanmadıysa finally bırakır)
                ForgetUnreadable(key);
            }
            catch (ApfsException e) when (e.Code is ApfsErrorCode.NotFound or ApfsErrorCode.AccessDenied)
            {
                // disk yok veya erişilemiyor: sessizce geç
                ForgetUnreadable(key);
            }
            catch (Exception e)
            {
                OnDiskUnreadable(key, e);
            }
            finally
            {
                device?.Dispose();
            }
        }
        CheckForRemovedDisks();
    }

    /// <summary>
    /// Takılan diskin aygıtı birkaç saniye hazır olmayabilir (ARM sanal makinesinde her takışta "Var olmayan bir aygıt", disk
    /// 5–16 sn sonra bağlanıyordu). İlk okuma hatasından sonra bu süre boyunca uyarı yerine sessizce (1 sn gecikmeli tarama
    /// isteğiyle) yeniden denenir; süre dolunca uyarı eskisi gibi çıkar ve 15 sn'lik zamanlayıcıya dönülür.
    /// </summary>
    private static readonly TimeSpan UnreadableGrace = TimeSpan.FromSeconds(20);

    /// <summary>Disk anahtarı → okunamadığı ilk an (<see cref="Environment.TickCount64"/>, <see cref="_gate"/> altında); okunabilince ya
    /// da kaybolunca silinir.</summary>
    private readonly Dictionary<string, long> _unreadableSince = new(StringComparer.OrdinalIgnoreCase);

    private void OnDiskUnreadable(string key, Exception e)
    {
        long now = Environment.TickCount64; // tekdüze saat
        bool first, warn = false;
        TimeSpan elapsed;
        lock (_gate)
        {
            first = !_unreadableSince.TryGetValue(key, out long since);
            if (first) _unreadableSince[key] = since = now;
            elapsed = TimeSpan.FromMilliseconds(now - since);
            if (elapsed >= UnreadableGrace) warn = _unreadableWarned.Add(key);
        }
        if (elapsed < UnreadableGrace)
        {
            if (first) ServiceLog.Write(WinText.T("Svc.DiskNotReady", key, (int)UnreadableGrace.TotalSeconds, e.Message));
            ScanRequested?.Invoke();
            return;
        }
        // Okunamaz kaldığı sürece BİR kez (önceden takılı kaldıkça dakikada bir yineleniyordu); AddNotice kilit dışında
        string text = WinText.U("Notice.DiskUnreadable", key, e.Message), level = "uyarı"; // Notice.Level protokol anahtarı
        if (warn) AddNotice(level, text);
        else
        {
            // Sonraki hatalar yalnız günlüğe, önceki gibi dakikada bir (hata iletisi değişse de iz kalsın)
            bool log;
            lock (_gate) log = _recentNotices.TryMark("unreadable#" + key, TimeSpan.FromMinutes(1));
            if (log) ServiceLog.Write($"[{level}] {text}"); // AddNotice'in günlük satırıyla aynı biçim
        }
    }

    /// <summary>Okunamadığı için uyarılmış diskler (<see cref="_gate"/> altında); okunabilince ya da kaybolunca silinir.</summary>
    private readonly HashSet<string> _unreadableWarned = new(StringComparer.OrdinalIgnoreCase);

    private void ForgetUnreadable(string key)
    {
        lock (_gate)
        {
            _unreadableSince.Remove(key);
            _unreadableWarned.Remove(key);
        }
    }

    private IEnumerable<(string Key, Func<IBlockDevice?> Open)> EnumerateSources()
    {
        if (_imageSources is not null)
        {
            // Yalnız geliştirme modu (testler): her görüntünün ilk N açılışı okuma hatası verir (takılan diskin aygıtı henüz
            // hazır değil). Her deneme günlüğe yazılır; testler tarama sıklığını buradan sayar.
            int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_OPEN_FAILURES"), out int failures);
            foreach (var path in _imageSources)
                yield return (path, () =>
                {
                    if (failures > 0)
                    {
                        int n;
                        lock (_gate) n = _devOpenAttempts[path] = _devOpenAttempts.GetValueOrDefault(path) + 1;
                        if (n <= failures)
                        {
                            ServiceLog.Write($"APFS_DEV_OPEN_FAILURES #{n}");
                            throw new ApfsException(ApfsErrorCode.IoError, "APFS_DEV_OPEN_FAILURES");
                        }
                    }
                    return File.Exists(path) ? new FileImageDevice(path) : null;
                });
            yield break;
        }
        for (int n = 0; n < MaxPhysicalDrives; n++)
        {
            int disk = n;
            yield return ($"PhysicalDrive{disk}", () => new RawDiskDevice(disk));
        }
    }

    /// <returns>true: disk yayımlandı (aygıtın sahibi artık o); false: hizmet bu arada durduruldu, bağlananlar ayrıldı (aygıtı
    /// çağıran bırakır).</returns>
    private bool AttachDisk(string key, IBlockDevice device)
    {
        var disk = new TrackedDisk(key, device) { Fingerprint = PartitionLocator.Fingerprint(device) };
        int count = PartitionLocator.FindApfsPartitions(device).Count;
        disk.Facts = new DiskFacts(device is RawDiskDevice r ? ReportEvents.BusToken(r.QueryBusType() ?? -1) : "image", device.SectorSize,
            PartitionLocator.LooksLikeContainer(device, 0) ? "none" : "gpt", count);
        try
        {
            for (int p = 0; p < count; p++) MountContainer(disk, p, report: true);
            // Yalnız geliştirme modu (testler): bağlama ile diskin listeye girmesi arasındaki pencereyi genişletir
            DevPause("APFS_DEV_ATTACH_DELAY_MS");
            if (device is RawDiskDevice raw)
                disk.RemovalWatch = DeviceNotifications.WatchHandle(raw.Handle, a => OnHandleEvent(key, a));
        }
        catch
        {
            // Bir bölüm bağlanırken ApfsException dışı istisna (1.9.1): önceki bölümlerin sürücüleri sahipsiz kalıyordu (öksüz
            // harf; disk hiç yayımlanmadı, tarama yeniden denediğinde ikinci harfle bağlanıyordu). İstisna yine yukarı (RescanCore).
            DetachUnpublished(disk);
            throw;
        }
        List<TrackedVolume> mounted = [];
        bool stopping;
        lock (_gate)
        {
            stopping = _disposed;
            if (!stopping)
            {
                _disks[key] = disk;
                mounted = [.. disk.Volumes.Where(v => v.State == VolumeStatus.Mounted && v.Mount is not null)
                    .OrderBy(v => v.ContainerIndex).ThenBy(v => v.Index)];
            }
        }
        if (stopping)
        {
            // Hizmet bağlarken durduruldu (1.9.1): Dispose diski göremezdi; bağlananlar burada ayrılır, "bağlandı" denmez
            DetachUnpublished(disk);
            ServiceLog.Write(WinText.T("Svc.AttachStopped", key));
            return false;
        }
        // Disk başına tek özet (çok bölümlü/birimli diskte birim başına bildirim yağıyordu); kilitli birim parola penceresiyle sorulur
        if (mounted.Count == 1)
            AddNotice("bilgi", WinText.U("Notice.Mounted", mounted[0].Name, mounted[0].Mount!.MountPoint));
        else if (mounted.Count > 1)
            AddNotice("bilgi", WinText.U("Notice.DiskMounted", disk.Description, string.Join(", ", mounted.Select(v => v.Mount!.MountPoint))));
        return true;
    }

    /// <summary>
    /// Yayımlanmamış diskin (bağlanırken hata ya da hizmet durduruluyor) sürücülerini ayırır, kapsayıcılarını ve aygıt olayı
    /// kaydını bırakır; aygıtı çağıran bırakır. Disk listede olmadığı için başka iş parçacığı ona dokunmaz.
    /// </summary>
    private static void DetachUnpublished(TrackedDisk disk)
    {
        foreach (var tv in disk.Volumes)
        {
            try { tv.Mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", disk.Key, e.Message)); }
            tv.Mount = null;
        }
        foreach (var c in disk.Containers.Values) c.Dispose();
        disk.Volumes.Clear();
        disk.Containers.Clear();
        disk.RemovalWatch?.Dispose();
        disk.RemovalWatch = null;
    }

    /// <summary>
    /// <paramref name="p"/>. APFS bölümünü diskin salt okunur aygıtından (yeniden) açar ve birimlerini salt okunur bağlar.
    /// Disk yayımlanmış olabileceği için listeler <see cref="_gate"/> altında değiştirilir.
    /// </summary>
    /// <param name="report">Diskin takılması (rapora "mount" olayı); yazma durumu değişirken yeniden bağlamada olay yok.</param>
    /// <param name="notifyFailure">Açma/bağlama hataları bildirilsin mi (arızalı yazma oturumundan sonra hayır: disk çekilmiş
    /// olabilir, tek uyarı <c>Notice.CloseWriteFailed</c>; hata yalnız günlüğe, tarama devralır).</param>
    private void MountContainer(TrackedDisk disk, int p, bool report = false, bool notifyFailure = true)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var c = SourceOpener.OpenContainer(disk.Device, p, ownsDevice: false);
            // Açılış uyarıları (eski checkpoint, bozuk blok 0, okunamayan aday…) salt okunur bağlamada da günlüğe (1.9.1;
            // balon yok: "gerektiği kadar"; yazmaya açma zaten reddeder)
            foreach (var w in c.Warnings) ServiceLog.Write(WinText.T("Svc.ContainerWarning", disk.Key, p, w));
            ContainerFacts? facts = null;
            try { facts = Apfs.Diagnostics.ContainerFacts.Of(c); } catch (Exception e) when (e is ApfsException or ObjectDisposedException) { }
            var volumes = new List<TrackedVolume>();
            var seen = new HashSet<ulong>();
            int duplicates = 0;
            IReadOnlyList<VolumeOpenResult> opened = [];
            try
            {
                opened = c.OpenVolumes(keys: KeysOf(disk));
                foreach (var r in opened)
                {
                    ulong oid = c.Superblock.FsOids[r.Index];
                    // Aynı birimi (fs_oid) bir kez bağla (1.9.1): bozuk/düşmanca süper blok aynı oid'i 100 kez listeleyebilir; her biri
                    // ayrı harfle bağlanıyor, status yanıtını şişiriyordu. Tam denetim bunu zaten KİRLİ der (yazmaya açılmaz).
                    if (!seen.Add(oid))
                    {
                        duplicates++;
                        r.Volume?.Dispose();
                        continue;
                    }
                    var tv = new TrackedVolume(c, p, r.Index, oid, r.Volume?.Name ?? WinText.U("Volume.DefaultName", r.Index));
                    volumes.Add(tv);
                    if (r.Volume is { } v) TryMount(disk, tv, v, notifyFailure);
                    else if (r.Error!.Code == ApfsErrorCode.Encrypted) MarkNeedsPassword(disk, tv);
                    else
                    {
                        tv.State = VolumeStatus.Failed;
                        tv.Message = r.Error.Message;
                        tv.FailureCode = r.Error.Code.ToString();
                        string text = WinText.U("Notice.VolumeOpenFailed", disk.Description, r.Index, r.Error.Message);
                        if (notifyFailure) AddNotice("hata", text);
                        else ServiceLog.Write($"{disk.Key}: {text}");
                    }
                }
            }
            catch
            {
                // Birim döngüsünde istisna (1.9.1; ör. WinFsp'nin bağlama hatası, bu arada kapatılan aygıt): o ana dek bağlanan
                // sürücüler sahipsiz kalıyordu (öksüz harf). Ayrılır, kapsayıcı bırakılır; istisna yine yukarı gider.
                foreach (var tv in volumes)
                {
                    try { tv.Mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", disk.Key, e.Message)); }
                    tv.Mount = null;
                }
                foreach (var r in opened) r.Volume?.Dispose(); // bağlanmayan/işlenmeyen birimler de (kapsayıcı zaten kapanıyor; iki kez atmak zararsız)
                c.Dispose();
                throw;
            }
            if (duplicates > 0) ServiceLog.Write(WinText.T("Svc.DuplicateVolumeOid", disk.Key, p, duplicates));
            bool kept;
            lock (_gate)
            {
                // Disk henüz yayımlanmadıysa (AttachDisk) ya da hâlâ bizdeyse eklenir; bu arada bırakıldıysa geri alınır
                kept = !_disks.TryGetValue(disk.Key, out var current) || ReferenceEquals(current, disk);
                if (kept)
                {
                    disk.Containers[p] = c;
                    disk.Volumes.AddRange(volumes);
                    if (facts is not null) disk.ContainerFacts[p] = facts;
                }
            }
            if (report)
                _reports?.Record(() =>
                {
                    int failed = volumes.Count(v => v.State == VolumeStatus.Failed);
                    return Event("mount", failed > 0 ? "fail" : "ok", disk, p, facts)
                        .WithVolumeCounts(volumes.Count(v => v.State == VolumeStatus.Mounted), volumes.Count(v => v.State == VolumeStatus.NeedsPassword),
                            volumes.Count(v => v.State == VolumeStatus.Hidden), failed)
                        .WithCode(volumes.FirstOrDefault(v => v.State == VolumeStatus.Failed)?.FailureCode)
                        .WithDuration(watch.Elapsed);
                });
            if (kept) return;
            foreach (var tv in volumes) { try { tv.Mount?.Dispose(); } catch { } }
            c.Dispose();
        }
        catch (ApfsException e)
        {
            string text = WinText.U("Notice.ContainerOpenFailed", disk.Description, e.Message);
            if (notifyFailure) AddNotice("hata", text);
            else ServiceLog.Write($"{disk.Key}: {text}");
            if (report) _reports?.Record(() => Event("mount", "fail", disk, p, null).WithCode(e.Code.ToString()).WithDuration(watch.Elapsed));
        }
        catch (ObjectDisposedException) { } // disk bu arada bırakıldı (çıkarıldı)
    }

    /// <summary><paramref name="p"/>. bölümün salt okunur birimlerini ayırır ve kapsayıcısını bırakır (yazma durumu değişirken).</summary>
    private void UnmountContainer(TrackedDisk disk, int p)
    {
        List<TrackedVolume> volumes;
        ApfsContainer? c;
        lock (_gate)
        {
            volumes = disk.Volumes.Where(v => v.ContainerIndex == p).ToList();
            disk.Volumes.RemoveAll(v => v.ContainerIndex == p);
            disk.Containers.Remove(p, out c);
        }
        foreach (var tv in volumes)
        {
            try { tv.Mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", disk.Key, e.Message)); }
        }
        c?.Dispose();
    }

    private List<VolumeKey> KeysOf(TrackedDisk disk)
    {
        lock (_gate) return [.. disk.Keys.Values];
    }

    private void MarkNeedsPassword(TrackedDisk disk, TrackedVolume tv)
    {
        MarkLocked(tv);
        tv.Message = null;
        // Bildirim yok: tepsi parola penceresini kendiliğinden açar
        ServiceLog.Write(WinText.T("Svc.WaitingForPassword", disk.Key, tv.Name));
    }

    /// <summary>
    /// Birimi kilitli (parola bekliyor) işaretler: ad, ipucu ve UUID kilit bilgisinden (tek okuma). İki yol da bunu kullanır: takışta
    /// <see cref="MarkNeedsPassword"/> ve yazma oturumundaki kilitli komşu.
    /// </summary>
    private static void MarkLocked(TrackedVolume tv)
    {
        var info = ProbeLockInfo(tv);
        tv.Name = info?.Name ?? tv.Name;
        tv.Hint = info?.Hint;
        tv.Uuid = info?.Uuid;
        tv.State = VolumeStatus.NeedsPassword;
    }

    /// <summary>Şifreli birimin adını ve ipucunu parola olmadan öğrenir (sağlayıcı null döndürür, bilgiyi yakalarız).</summary>
    private static VolumeLockInfo? ProbeLockInfo(TrackedVolume tv)
    {
        VolumeLockInfo? seen = null;
        try { ApfsVolume.Open(tv.Container, tv.Oid, info => { seen = info; return null; }).Dispose(); }
        catch (ApfsException) { }
        return seen;
    }

    /// <summary>
    /// Birimi salt okunur bağlar. "Bağlandı" bildirimi burada YOK: takınca disk başına tek özet (<see cref="AttachDisk"/>);
    /// yazma durumu değişirken yeniden bağlamanın ve kilit açmanın sonucu isteğin yanıtıyla döner.
    /// </summary>
    /// <param name="notifyFailure">Bağlama hatası bildirilsin mi (kilit açmada hayır: hata parola penceresinde gösterilir).</param>
    private void TryMount(TrackedDisk disk, TrackedVolume tv, ApfsVolume v, bool notifyFailure = true)
    {
        bool mounted = false;
        try { mounted = TryMountCore(disk, tv, v, notifyFailure); }
        finally { if (!mounted) v.Dispose(); } // birim bağlanmadıysa (gizli, hata) burada atılır; bağlandıysa sürücünün (1.9.1)
    }

    private bool TryMountCore(TrackedDisk disk, TrackedVolume tv, ApfsVolume v, bool notifyFailure)
    {
        tv.Name = v.Name;
        if (!v.IsUserVisible)
        {
            tv.State = VolumeStatus.Hidden;
            tv.Message = WinText.U("Volume.InternalRole", v.RoleName);
            return false;
        }
        // Yalnız geliştirme modu (testler): n. bağlama ApfsException dışı istisna verir (bağlama döngülerinin hata yolu, bir kez)
        if (_imageSources is not null && int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_MOUNT_THROW_AT"), out int throwAt)
            && Interlocked.Increment(ref _devMountCalls) == throwAt)
        {
            ServiceLog.Write("APFS_DEV_MOUNT_THROW_AT");
            throw new InvalidOperationException("APFS_DEV_MOUNT_THROW_AT");
        }
        try
        {
            string volumeKey = $"{disk.Key}#{tv.ContainerIndex}#{tv.Index}";
            var m = ApfsMount.Mount(v, null, msg => OnFileError(disk, v.Name, volumeKey, msg),
                onLog: msg => ServiceLog.Write($"{disk.Description} \"{v.Name}\": {msg}"));
            tv.Mount = m;
            tv.State = VolumeStatus.Mounted;
            tv.Message = null;
            ServiceLog.Write(WinText.T("Svc.Mounted", disk.Key, v.Name, m.MountPoint));
            return true;
        }
        catch (ApfsException e)
        {
            tv.State = VolumeStatus.Failed;
            tv.Message = e.Message;
            tv.FailureCode = e.Code.ToString();
            if (notifyFailure) AddNotice("hata", WinText.U("Notice.MountFailed", v.Name, e.Message));
            else ServiceLog.Write($"{disk.Key}: {WinText.U("Notice.MountFailed", v.Name, e.Message)}");
            return false;
        }
    }

    // ---------------- Parola ----------------

    /// <summary>
    /// Parola denemesi sınırı (1.8.2; kullanıcı kararı 2026-11-24 "disk kimliğine bağla"): sayaç bölümün APFS kimliği
    /// (kapsayıcı UUID'si) + deneyen hesabın SID'i başına, hizmetin belleğinde. İlk <see cref="FreeAttempts"/> yanlış beklemesiz;
    /// sonra her deneme 1, 2, 4… en çok <see cref="MaxDelayUnits"/> birim (1 sn) bekler — eşzamansız, <see cref="_gate"/> ve
    /// <c>Switching</c> dışında. Doğru parolada sıfırlanır; disk çıkarılıp takılınca SÜRER (çıkar + yeniden tara döngüsüyle aşılmasın),
    /// başka disk kendi sayacını alır. Başka hesabın denemeleri sahibini bekletmez. Aynı sayaca bekleyen/süren deneme varken yenisi
    /// "önceki istek bitmedi". Toplam: kilit açma bütçesi (≤ ~20 sn) + 20 sn, tepsinin 60 sn'sinin altında. Arayüz metni değişmez.
    /// </summary>
    private sealed class UnlockAttempts
    {
        public int Failures;
        public bool Busy;
        public long LastUse;
    }

    private const int FreeAttempts = 5, MaxDelayUnits = 20, MaxAttemptCounters = 1024;
    private readonly Dictionary<string, UnlockAttempts> _attempts = new(StringComparer.Ordinal); // _gate altında

    /// <summary>Bekleme birimi: 1 sn; yalnız geliştirme modunda (testler) <c>APFS_DEV_UNLOCK_DELAY_UNIT_MS</c> ile kısaltılır.</summary>
    private int DelayUnitMs =>
        _imageSources is not null && int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_UNLOCK_DELAY_UNIT_MS"), out int ms) && ms > 0 ? ms : 1000;

    /// <summary>Kanalda hesabı öğrenilemeyen çağıranın kimliği (<c>PipeServer.CallerOf</c>).</summary>
    public const string AnonymousCaller = "?";

    /// <summary>Kanaldan gelen kilit açma: deneme sınırından geçer, sonra <see cref="Unlock"/>.</summary>
    /// <param name="caller">İsteyen hesabın SID'i (öğrenilemezse "?").</param>
    public async Task<UnlockResponse> UnlockAsync(string diskKey, int container, int volumeIndex, string password, string caller)
    {
        // Hesabını vermeyen (anonim düzeyle bağlanan) istemci parola denemez (1.9.1): önceden ortak "?" sayacıyla her hesaba
        // ikinci bir deneme hakkı açılıyordu. Ürünün istemcileri hep Identification düzeyiyle bağlanır.
        if (caller == AnonymousCaller) return new UnlockResponse(false, WinText.U("Unlock.Failed"));
        UnlockAttempts? attempts = null;
        int delayMs = 0;
        lock (_gate)
        {
            // Sayaç yalnız gerçekten parola bekleyen salt okunur bölümde (öbür durumlarda Unlock parolaya bakmadan reddeder)
            if (_disks.GetValueOrDefault(diskKey) is { } disk && disk.Containers.GetValueOrDefault(container) is { } c
                && disk.Volumes.Any(v => v.ContainerIndex == container && v.Index == volumeIndex && v.State == VolumeStatus.NeedsPassword))
            {
                string id = $"{c.Superblock.Uuid:D}|{caller}";
                if (!_attempts.TryGetValue(id, out attempts))
                {
                    if (_attempts.Count >= MaxAttemptCounters) ForgetOldestAttempts();
                    _attempts[id] = attempts = new UnlockAttempts();
                }
                if (attempts.Busy) return new UnlockResponse(false, WinText.U("Write.PreviousRequestPending"));
                attempts.Busy = true;
                attempts.LastUse = Environment.TickCount64;
                if (attempts.Failures >= FreeAttempts)
                    delayMs = DelayUnitMs * Math.Min(MaxDelayUnits, 1 << Math.Min(attempts.Failures - FreeAttempts, 5));
            }
        }
        try
        {
            if (delayMs > 0)
            {
                ServiceLog.Write(WinText.T("Svc.UnlockDelayed", diskKey, delayMs));
                await Task.Delay(delayMs);
            }
            var r = await Task.Run(() => Unlock(diskKey, container, volumeIndex, password));
            if (attempts is not null)
                lock (_gate)
                {
                    if (r.Ok) attempts.Failures = 0;
                    else if (r.Code == nameof(ApfsErrorCode.WrongPassword)) attempts.Failures++;
                }
            return r;
        }
        finally
        {
            if (attempts is not null) lock (_gate) attempts.Busy = false;
        }
    }

    /// <summary>Sayaç sınırı dolunca en uzun süredir kullanılmayan boştaki sayaç bırakılır (<see cref="_gate"/> altında).</summary>
    private void ForgetOldestAttempts()
    {
        string? oldest = null;
        long when = long.MaxValue;
        foreach (var (id, a) in _attempts)
            if (!a.Busy && a.LastUse < when) (oldest, when) = (id, a.LastUse);
        if (oldest is not null) _attempts.Remove(oldest);
    }

    /// <param name="container">APFS bölüm sırası (birim sırası bölüm içindedir; çok bölümlü diskte ikisi de gerekir).</param>
    public UnlockResponse Unlock(string diskKey, int container, int volumeIndex, string password)
    {
        TrackedDisk? disk;
        TrackedVolume? tv;
        lock (_gate)
        {
            disk = _disks.GetValueOrDefault(diskKey);
            if (disk is null) return new UnlockResponse(false, WinText.U("Unlock.NotWaiting"));
            // Kilit açma da bölümün durumunu değiştirir: yazma geçişiyle (aynı bölümün birimleri ayrılıp yeniden kurulurken) yarışmasın
            if (disk.Switching.Contains(container)) return new UnlockResponse(false, WinText.U("Write.PreviousRequestPending"));
            tv = disk.Volumes.FirstOrDefault(x => x.ContainerIndex == container && x.Index == volumeIndex && x.State == VolumeStatus.NeedsPassword);
            if (tv is null) return new UnlockResponse(false, WinText.U("Unlock.NotWaiting"));
            // Yazma açıkken kilitli birim dokunulmadan kalır (1.7.0): kapsayıcısı oturumdan önceki hâlidir, açılırsa bayat okurdu
            if (disk.Writes.ContainsKey(container)) return new UnlockResponse(false, WinText.U("Unlock.ReadOnlyFirst"));
            disk.Switching.Add(container);
        }
        try
        {
            return UnlockCore(disk, tv, container, password);
        }
        finally
        {
            lock (_gate) disk.Switching.Remove(container);
        }
    }

    /// <remarks>
    /// 1.8.2: kilit açma sürerken disk bırakılabilir (Güvenle çıkar, kablo, içerik değişti). <see cref="ReleaseDisk"/> o anda
    /// diskin anahtarlarını siler ve bağlı sürücülerini ayırır; ama bu çağrının henüz eklemediği anahtarı ve henüz bağlamadığı
    /// sürücüyü göremez. Bu yüzden anahtar ancak disk hâlâ listedeyken eklenir (<see cref="_gate"/> altında: ekleme ya bırakmadan
    /// önce olur — bırakma onu siler — ya da hiç olmaz), bağlamadan sonra yeniden bakılır: disk gittiyse sürücü burada ayrılır ve
    /// "bağlandı" denmez (önceden sahipsiz bir sürücü harfi, yanlış "bağlandı" yanıtı ve bellekte kalan anahtar olabiliyordu).
    /// </remarks>
    private UnlockResponse UnlockCore(TrackedDisk disk, TrackedVolume tv, int container, string password)
    {
        VolumeKey? key = null;
        ApfsVolume? opened = null; // TryMount'a geçene dek bu yöntemin (disk giderse finally atar: şifreleyici bellekte kalmasın)
        Guid? uuid = null;
        UnlockResponse? gone = null;
        var watch = Stopwatch.StartNew();
        try
        {
            uuid = tv.Container.VolumeUuid(tv.Oid);
            key = VolumeKey.Unlock(tv.Container, uuid.Value, password, disk.Released.Token);
            var v = opened = ApfsVolume.Open(tv.Container, tv.Oid, key);
            // Yalnız geliştirme modu (testler): kilit açma ↔ çıkarma yarışını sınamak için pencereyi genişletir — anahtar türetildi, henüz eklenmedi
            DevPause("APFS_DEV_UNLOCK_DELAY_MS");
            lock (_gate)
            {
                if (!IsAttached(disk)) return gone = DiskGoneDuringUnlock(disk, tv, uuid); // anahtar eklenmez, finally siler
                if (disk.Keys.Remove(uuid.Value, out var old)) old.Dispose();
                disk.Keys[uuid.Value] = key;
            }
            key = null; // sahipliği diske geçti (disk bundan sonra bırakılırsa ReleaseDisk siler)
            opened = null; // birim TryMount'un (bağlarsa sürücünün, bağlamazsa atar)
            TryMount(disk, tv, v, notifyFailure: false); // sonuç yalnız yanıtta: tepsi balonu ya da parola penceresi
            DevPause("APFS_DEV_UNLOCK_MOUNTED_DELAY_MS"); // yalnız geliştirme modu: birim bağlandı, yanıt henüz yok
            // Bağlılık, durum, bağlama noktası ve ileti AYNI kilit altında okunur: arada disk bırakılırsa (ReleaseDisk sürücüyü
            // boşaltır, durumu değiştirmez) yanıt boş sürücüye erişip istisnayla yanıtsız kalıyordu
            bool attached;
            string state;
            string? mountPoint, message, failureCode;
            lock (_gate) (attached, state, mountPoint, message, failureCode) = (IsAttached(disk), tv.State, tv.Mount?.MountPoint, tv.Message, tv.FailureCode);
            if (!attached) return gone = DiskGoneDuringUnlock(disk, tv, uuid);
            DevPause("APFS_DEV_UNLOCK_RESPOND_DELAY_MS"); // yalnız geliştirme modu: bağlılık denetlendi, yanıt henüz kurulmadı
            if (state == VolumeStatus.Mounted && mountPoint is null) return gone = DiskGoneDuringUnlock(disk, tv, uuid);
            _reports?.Record(() => Event("unlock", state == VolumeStatus.Mounted ? "ok" : "fail", disk, container)
                .WithCode(failureCode).WithDuration(watch.Elapsed));
            return state == VolumeStatus.Mounted
                ? new UnlockResponse(true, WinText.U("Unlock.Mounted", v.Name, mountPoint))
                : new UnlockResponse(false, message ?? WinText.U("Unlock.MountFailed"));
        }
        catch (ApfsException e)
        {
            // Bu arada bırakılan diskte (PBKDF2 iptal edildi, aygıt kapandı) sonuç parola hakkında bir şey söylemez
            bool attached;
            lock (_gate) attached = IsAttached(disk);
            if (!attached) return gone = DiskGoneDuringUnlock(disk, tv, uuid);
            // Parola yanlışsa günlüğe yalnızca olay yazılır, parola asla.
            ServiceLog.Write(WinText.T("Svc.UnlockFailed", disk.Key, tv.Name, e.Code));
            _reports?.Record(() => Event("unlock", "fail", disk, container).WithCode(e.Code.ToString()).WithDuration(watch.Elapsed));
            return new UnlockResponse(false, e.Message, Code: e.Code.ToString());
        }
        catch (ObjectDisposedException)
        {
            // Bırakılan diskin kapsayıcısı/aygıtı kapatıldı (önceden yakalanmıyordu: istek yanıtsız kalıyordu)
            return gone = DiskGoneDuringUnlock(disk, tv, uuid);
        }
        finally
        {
            opened?.Dispose();
            key?.Dispose();
            // Yalnız geliştirme modu (testler): yarıştan sonra bellekte kalan anahtar ve şifreleyici sayısı (0 olmalı)
            if (gone is not null && _imageSources is not null) ServiceLog.Write($"APFS_DEV_LIVE_KEYS {VolumeKey.LiveCount} crypto {VolumeCrypto.LiveCount}");
        }
    }

    /// <summary>
    /// Yalnız geliştirme modu (testler, <c>--image</c>): ortam değişkeninde ms verilmişse günlüğe değişkenin adını yazıp o kadar bekler
    /// (test o satırı görünce yarışan isteği gönderir).
    /// </summary>
    private void DevPause(string variable)
    {
        if (_imageSources is null || !int.TryParse(Environment.GetEnvironmentVariable(variable), out int ms)) return;
        ServiceLog.Write(variable);
        Thread.Sleep(ms);
    }

    /// <summary>Disk hâlâ bu nesneyle listede mi (<see cref="_gate"/> altında çağrılır).</summary>
    private bool IsAttached(TrackedDisk disk) => _disks.TryGetValue(disk.Key, out var current) && ReferenceEquals(current, disk);

    /// <summary>
    /// Kilit açılırken disk bırakıldı: bu çağrının bağladığı sürücü (bırakma onu görmediyse) ayrılır; yanıt "disk artık takılı
    /// değil", kod yok (tepsi kayıtlı parolaya dokunmaz). Anahtarı finally (eklenmediyse) ya da bırakma siler; bırakmadan SONRA
    /// eklenmiş olsa da (ekleme öncesi denetim bunu zaten önler) burada silinir: hiçbir yolda bellekte kalmaz.
    /// </summary>
    private UnlockResponse DiskGoneDuringUnlock(TrackedDisk disk, TrackedVolume tv, Guid? uuid)
    {
        ApfsMount? mount;
        VolumeKey? orphan = null;
        lock (_gate)
        {
            (mount, tv.Mount) = (tv.Mount, null);
            if (uuid is Guid u && disk.Keys.Remove(u, out var k)) orphan = k;
        }
        orphan?.Dispose();
        try { mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", disk.Key, e.Message)); }
        ServiceLog.Write(WinText.T("Svc.UnlockDiskGone", disk.Key, tv.Name));
        return new UnlockResponse(false, WinText.U("Disk.Gone"));
    }

    // ---------------- Çıkarma ----------------

    /// <summary>
    /// Tepsideki "Güvenle çıkar": birimleri ayırır, disk tanıtıcısını kapatır ve diskin çıkarılabilir üst aygıtını Windows'a
    /// çıkarttırır. Windows reddederse (başka bir program diski kullanıyorsa) disk yeniden bağlanır ve neden bildirilir.
    /// </summary>
    /// <param name="force">false ise diskte açık dosya veya süren kopyalama varken hiçbir şey yapılmaz, Busy yanıtı döner.</param>
    public UnlockResponse Eject(string diskKey, bool force = false)
    {
        TrackedDisk? disk;
        lock (_gate) disk = _disks.GetValueOrDefault(diskKey);
        if (disk is null) return new UnlockResponse(false, WinText.U("Disk.Gone"));
        List<string> busy = force ? [] : BusyAfterSecondLook(disk);
        List<Action<bool>> refuseOpens = [];
        if (busy.Count == 0)
        {
            // Ayırma kararı verildi: yeni açılışlar reddedilir — karar ile ayırma arasında başlayan kopyalama yarıda
            // kesilmesin, hiç başlamasın. Denetimi reddin açılmasından önce geçmiş, henüz sayılmamış açılışlar biter (en çok
            // OpeningWaitMs); sonra açık dosya varsa yine "kullanımda" ve açılışlar geri açılır. Uyarı verilecekse ret hiç açılmaz:
            // kullanıcıya "kopyalama sürüyor" sorulurken kopyanın sonraki dosyası reddedilmesin.
            lock (_gate) refuseOpens = OpenGates(disk);
            foreach (var refuse in refuseOpens) refuse(true);
            bool settled = WaitForOpens(disk);
            if (!force)
                lock (_gate)
                    busy = settled
                        ? [.. disk.Volumes.Where(v => OpenFiles(v) > 0).Select(VolumeLabel)]
                        : [.. disk.Volumes.Where(v => OpenFiles(v) + Opening(v) > 0).Select(VolumeLabel)];
        }
        if (busy.Count > 0)
        {
            foreach (var refuse in refuseOpens) refuse(false);
            ServiceLog.Write(WinText.T("Svc.EjectDeferred", diskKey, string.Join(", ", busy)));
            _reports?.Record(() => Event("eject", "busy", disk, null));
            return new UnlockResponse(false,
                WinText.U("Eject.Busy", disk.Description, string.Join(", ", busy)), Busy: true);
        }
        // Yalnız geliştirme modu (testler): karar ile ayırma arasındaki pencereyi genişletir (bu arada açılış reddedilmeli)
        DevPause("APFS_DEV_EJECT_DELAY_MS");

        DiskEjector.Target? target = null;
        if (_imageSources is null && diskKey.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(diskKey.AsSpan(13), out int number))
            target = DiskEjector.Find(number);

        // Yalnız denetlenen disk nesnesi (ikinci bakış sırasında çekilip yeniden takılan diskin yerine geçen yeni nesne değil)
        if (!ReleaseDisk(diskKey, "", rememberReleased: true, notify: false, closeKind: "eject", expected: disk))
            return new UnlockResponse(false, WinText.U("Disk.Gone"));
        if (target is not { Removable: true })
        {
            _reports?.Record(() => Event("eject", "detached", disk, null));
            AddNotice("bilgi", WinText.U("Notice.Detached", disk.Description), requested: true);
            return new UnlockResponse(true, WinText.U("Eject.Detached"));
        }
        var (ok, error) = DiskEjector.Eject(target);
        _reports?.Record(() => Event("eject", ok ? "ok" : "fail", disk, null));
        if (ok)
        {
            AddNotice("bilgi", WinText.U("Notice.Ejected", disk.Description), requested: true);
            return new UnlockResponse(true, WinText.U("Eject.Ejected"));
        }
        ServiceLog.Write(WinText.T("Svc.EjectRefused", diskKey, error));
        lock (_gate) _released.Remove(diskKey);
        QueueRescan();
        return new UnlockResponse(false, WinText.U("Eject.Remounting", error));
    }

    // ---------------- Yazma (deneysel) ----------------

    private static List<string> BusyVolumes(TrackedDisk disk, int? container = null) =>
        [.. disk.Volumes.Where(v => container is null || v.ContainerIndex == container)
            .Where(v => v.Mount?.FileSystem.IsBusy == true || v.WritableMount?.FileSystem.IsBusy == true)
            .Select(VolumeLabel)];

    private static string VolumeLabel(TrackedVolume v) => $"{v.Mount?.MountPoint ?? v.WritableMount?.MountPoint} \"{v.Name}\"";

    /// <summary>
    /// Güvenle çıkar'ın ikinci bakışının en uzun süresi ve "sessiz" sayılan süre. Sessizlik ilk bakışınkiyle (IsBusy) aynı
    /// 3 sn: dosyalar arasında 1–3 sn duraklayan kopya (yavaş hedef, her dosyayı tarayan virüs koruması) eskisi gibi yakalanır.
    /// </summary>
    private const int SecondLookMs = 4500, QuietMs = 3000;

    /// <summary>Reddi açtıktan sonra süren açılışların bitmesi için en uzun bekleme.</summary>
    private const int OpeningWaitMs = 5000;

    private static int OpenFiles(TrackedVolume v) => (v.Mount?.FileSystem.OpenFileCount ?? 0) + (v.WritableMount?.FileSystem.OpenFileCount ?? 0);
    private static int Opening(TrackedVolume v) => (v.Mount?.FileSystem.OpeningCount ?? 0) + (v.WritableMount?.FileSystem.OpeningCount ?? 0);

    /// <summary>Diskin bütün sürücülerinde süren açılışlar bitene dek bekler (en çok <see cref="OpeningWaitMs"/>); bittiyse true.</summary>
    private bool WaitForOpens(TrackedDisk disk)
    {
        long until = Environment.TickCount64 + OpeningWaitMs;
        while (true)
        {
            int opening;
            lock (_gate) opening = disk.Volumes.Sum(Opening);
            if (opening == 0) return true;
            if (Environment.TickCount64 >= until) return false;
            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Güvenle çıkar'ın meşguliyet denetimi (1.8.2). İlk bakış eskisi gibi (açık dosya ya da son 3 sn'de okuma); meşgul
    /// görünürse uyarmadan önce kısa bir ikinci bakış: Gezgin'in önizleme/küçük resmi ya da virüs taraması dosyayı birkaç yüz ms
    /// açık tutup okur ve bırakır, önceden "kopyalama sürüyor gibi" yanlış alarmı veriyordu. En çok <see cref="SecondLookMs"/>
    /// beklenir; açık dosya kalmadıysa ve son <see cref="QuietMs"/>'de okuma/yazma olmadıysa meşgul değil. Kopya sürerse (yeni
    /// okuma, açık dosya) ve uzun yaşayan tanıtıcıda (oynatıcı) uyarı verilir.
    /// </summary>
    /// <param name="container">Yalnız bu APFS bölümünün birimleri ("Salt okunura dön" / "Yazmaya izin ver"; 1.9.1); null = bütün disk.</param>
    private List<string> BusyAfterSecondLook(TrackedDisk disk, int? container = null)
    {
        List<string> busy;
        lock (_gate) busy = BusyVolumes(disk, container);
        if (busy.Count == 0) return busy;
        long until = Environment.TickCount64 + SecondLookMs;
        while (true)
        {
            Thread.Sleep(100);
            long now = Environment.TickCount64;
            lock (_gate)
                busy = [.. disk.Volumes.Where(v => container is null || v.ContainerIndex == container)
                    .Where(v => OpenFiles(v) > 0
                        || now - Math.Max(v.Mount?.FileSystem.LastIoTicks ?? long.MinValue / 2, v.WritableMount?.FileSystem.LastIoTicks ?? long.MinValue / 2) < QuietMs)
                    .Select(VolumeLabel)];
            if (busy.Count == 0 || now >= until) return busy;
        }
    }

    /// <summary>Diskin bağlı bütün sürücülerinin (iki tür) yeni açılışları reddetme anahtarları (<see cref="_gate"/> altında).</summary>
    private static List<Action<bool>> OpenGates(TrackedDisk disk)
    {
        var list = new List<Action<bool>>();
        foreach (var v in disk.Volumes)
        {
            if (v.Mount?.FileSystem is { } ro) list.Add(x => ro.RefuseOpens = x);
            if (v.WritableMount?.FileSystem is { } rw) list.Add(x => rw.RefuseOpens = x);
        }
        return list;
    }

    /// <summary>
    /// Tepsideki "Yazmaya izin ver" / "Salt okunura dön", APFS bölümü başına (<paramref name="container"/> = bölüm sırası; diskteki
    /// öbür bölümlere dokunulmaz). Açarken: bölümün birimleri ayrılır, bölüm diskten yeniden açılıp yazma kapısından geçirilir;
    /// geçerse yazma izniyle açılıp yazılabilir bağlanır, geçmezse salt okunur yeniden bağlanır ve neden karta yazılır.
    /// Kapatırken: sürücü ayrılır, son değişiklikler kaydedilir (blok 0 dahil), bölüm salt okunur yeniden bağlanır.
    /// Geçiş (<c>Switching</c>) denetimi bölüm denetiminden ÖNCE (Unlock'taki sıra): geçiş sürerken bölüm ne salt okunur ne
    /// yazılabilir listededir; ikinci istek "böyle bir bölüm yok" değil "önceki istek henüz bitmedi" almalı (1.7.1).
    /// </summary>
    public UnlockResponse SetWritable(string diskKey, int container, bool enable, bool force)
    {
        TrackedDisk? disk;
        long epoch;
        for (bool looked = false; ; looked = true)
        {
            lock (_gate)
            {
                // Her bakışta durum yeniden doğrulanır (ikinci bakış sırasında disk gidebilir, başka istek bölümü alabilir)
                disk = _disks.GetValueOrDefault(diskKey);
                if (disk is null) return new UnlockResponse(false, WinText.U("Disk.Gone"));
                if (disk.Switching.Contains(container)) return new UnlockResponse(false, WinText.U("Write.PreviousRequestPending"));
                if (!disk.Containers.ContainsKey(container) && !disk.Writes.ContainsKey(container))
                    return new UnlockResponse(false, WinText.U("Write.NoSuchPartition"));
                bool writable = disk.Writes.ContainsKey(container);
                if (enable == writable)
                    return new UnlockResponse(true, enable ? WinText.U("Write.AlreadyWritable") : WinText.U("Write.AlreadyReadOnly"));
                List<string> busy = force ? [] : BusyVolumes(disk, container);
                if (busy.Count > 0 && looked)
                    return new UnlockResponse(false,
                        WinText.U("Write.Busy", disk.Description, string.Join(", ", busy)), Busy: true);
                if (busy.Count == 0)
                {
                    if (!disk.Switching.Add(container)) return new UnlockResponse(false, WinText.U("Write.PreviousRequestPending"));
                    epoch = _powerEpoch;
                    break;
                }
            }
            // İlk bakışta meşgul (açık dosya ya da son 3 sn'de okuma/yazma): uyarmadan önce Güvenle çıkar'ınki gibi kısa ikinci bakış
            // (1.9.1), _gate DIŞINDA. Kopyalamadan hemen sonra Defender'ın kapanış taraması dosyayı birkaç yüz ms açık tutuyor,
            // "Salt okunura dön" "dosya açık ya da kopyalanıyor" diyordu; 2,5 sn sonra sorun yoktu.
            BusyAfterSecondLook(disk, container);
        }
        try
        {
            return enable ? EnableWrite(disk, container, epoch) : DisableWrite(disk, container);
        }
        finally
        {
            ContainerWrite? faulted;
            lock (_gate)
            {
                disk.Switching.Remove(container);
                faulted = disk.Writes.GetValueOrDefault(container) is { FaultMessage: not null } w ? w : null;
            }
            // Geçiş sürerken (ör. yazmaya açarken, oturum yayımlanmadan) gelen arıza işleyicide hiçbir şey bulamadı: şimdi yapılır
            if (faulted is not null) QueueFault(disk.Key, container, faulted);
        }
    }

    /// <param name="epoch">İsteğin başındaki güç dönemi (<see cref="_powerEpoch"/>).</param>
    private UnlockResponse EnableWrite(TrackedDisk disk, int container, long epoch)
    {
        string key = disk.Key;
        var watch = Stopwatch.StartNew();
        UnmountContainer(disk, container); // bölümün salt okunur sürücüleri; disk ve öbür bölümler bağlı kalır

        // 1) Bölümü diskten yeniden aç ve yazma kapısından geçir (salt okunur)
        GateResult gate;
        string? refusal = null;
        List<string> refusalKeys = []; // raporda metin yerine anahtarlar
        string? failCode = null;
        var keys = KeysOf(disk); // kapı ve yazıcılar aynı anahtarlarla (kapının kilitli saydığı birim yazıcıda da kilitli)
        try
        {
            // Yalnız geliştirme modu (testler): yazmaya açma süren bir istekle (ya da uykuyla) yarışan işi sınamak için pencereyi genişletir
            DevPause("APFS_DEV_ENABLE_DELAY_MS");
            using var c = SourceOpener.OpenContainer(disk.Device, container, ownsDevice: false);
            gate = WriteGate.Evaluate(c, keys);
            if (!gate.Allowed)
            {
                refusal = string.Join(" ", gate.Reasons);
                refusalKeys.AddRange(gate.ReasonKeys);
            }
        }
        catch (Exception e)
        {
            gate = new GateResult(false, [], null, null);
            refusal = WinText.U("Write.CheckFailed", e.Message);
            refusalKeys.Add("Write.CheckFailed");
            failCode = e is ApfsException ae ? ae.Code.ToString() : e.GetType().Name;
        }
        if (refusal is not null)
        {
            ServiceLog.Write(WinText.T("Svc.WriteNotEnabled", key, container, refusal));
            // Denetimin ayrıntısı yalnız günlüğe (teşhis için); dosya ve klasör adları gizlenir
            foreach (var e in gate.Report?.Errors.Take(10) ?? [])
                ServiceLog.Write(WinText.T("Svc.WriteCheck", key, container, Apfs.Diagnostics.CheckReport.HideNames(e)));
            RemountReadOnly(disk, container, refusal);
            // Bildirim yok: neden yanıtta (tepsi penceresi açar) ve bölümün satırında
            _reports?.Record(() => Event("write-enable", failCode is null ? "refused" : "fail", disk, container)
                .WithGate(refusalKeys).WithCode(failCode).WithDuration(watch.Elapsed));
            return new UnlockResponse(false, refusal);
        }

        // 2) Yazma izniyle aç, oturumu başlat. Bölümün kilitli olmayan BÜTÜN birimlerinin yazıcısı sürücülerden ÖNCE kurulur: bir
        //    sürücünün ilk commit'inden sonra oturum başka birim açamaz (WriteSession.Volume). Kilitli birim (kapı yanında yazılabilir
        //    birim varsa izin verir) dokunulmadan kalır; adı/ipucu da şimdi, commit'ten önce okunur: yazma açıkken bu kapsayıcıyı
        //    okuyan tek yer budur. Sonra her yazılabilir birim bağlanır; herhangi bir hata hepsini geri alır.
        var grant = gate.Grant!;
        var resources = new List<IDisposable>();
        ContainerWrite? write = null;
        PowerGateDevice? liveGate = null;
        bool slept = false;
        var tracked = new List<TrackedVolume>();
        var writers = new List<TrackedVolume>();
        try
        {
            // Denetim sürerken uyku olduysa yazma aygıtı hiç açılmaz (1.9.1, inceleme)
            lock (_gate) slept = _powerEpoch != epoch;
            if (slept) throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Svc.SleptDuringEnable"));
            IWritableBlockDevice raw = _imageSources is not null ? new FileImageWriter(key, grant) : new RawDiskWriter(int.Parse(key.AsSpan(13)), grant);
            // Yalnız geliştirme modu (testler): her yazma bariyeri hata verir → ilk commit düşer (WriteSession.Open bariyer kullanmaz)
            if (_imageSources is not null && Environment.GetEnvironmentVariable("APFS_DEV_COMMIT_FAIL") == "1")
                raw = new DevFailingFlushDevice(raw, grant);
            // Yalnız geliştirme modu (testler): kapatırken blok 0 yazımı hata verir (bölümsüz görüntü)
            else if (_imageSources is not null && Environment.GetEnvironmentVariable("APFS_DEV_BLOCK0_FAIL") == "1")
                raw = new DevFailingFlushDevice(raw, grant, block0Only: true);
            resources.Add(raw);
            // Uyku kapısı (1.9.1): uykudan önce dondurulur; oturum ve bölüm penceresi aygıta yalnız ondan geçer
            var gateDevice = new PowerGateDevice(raw, grant);
            // Kapı canlılar kümesine dönemle AYNI kilitte girer: ya uyku bundan önceydi (geri alınır) ya da kapıyı görür ve dondurur
            lock (_gate)
            {
                slept = _powerEpoch != epoch;
                if (!slept) _liveGates.Add(liveGate = gateDevice);
            }
            if (slept) throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Svc.SleptDuringEnable"));
            var p = PartitionLocator.FindApfsPartitions(gateDevice)[container];
            IWritableBlockDevice window = p.Offset == 0 && p.Length == raw.Length ? gateDevice : new WritableOffsetDevice(gateDevice, p.Offset, p.Length, grant);
            if (!ReferenceEquals(window, gateDevice)) resources.Insert(0, window);
            var session = WriteSession.Open(window, grant);
            // Commit kaydedildi ama ardından bir aygıt işlemi yapılamadı: yalnız günlük, oturum sağlam
            session.Warning += msg => ServiceLog.Write($"{key}#{container}: {msg}");
            var thisWrite = write = new ContainerWrite(session, resources) { Gate = gateDevice };
            var sc = session.Container;
            for (int i = 0; i < sc.Superblock.FsOids.Count; i++)
            {
                ulong oid = sc.Superblock.FsOids[i];
                if (gate.LockedVolumes.FirstOrDefault(l => l.Index == i) is { } locked)
                {
                    var ltv = new TrackedVolume(sc, container, i, oid, locked.Name);
                    MarkLocked(ltv);
                    tracked.Add(ltv);
                    continue;
                }
                var uuid = sc.VolumeUuid(oid);
                var vw = session.Volume(i, keys.FirstOrDefault(k => k.VolumeUuid == uuid)); // şifreli birimde anahtar şart
                var tv = new TrackedVolume(sc, container, i, vw.Oid, vw.Name);
                tracked.Add(tv);
                writers.Add(tv);
            }
            if (writers.Count == 0) throw new ApfsException(ApfsErrorCode.Unsupported, WinText.T("Svc.NoWritableVolume"));
            bool many = writers.Count > 1;
            foreach (var tv in writers)
            {
                string name = tv.Name, volumeKey = $"{key}#{container}#{tv.Index}";
                tv.WritableMount = ApfsWritableMount.Mount(session, tv.Index, null,
                    msg => OnFileError(disk, name, volumeKey, msg), closeSession: false,
                    // Değişiklik kaybı (kaydedilemedi/silinemedi) birim seyreltmesine girmez: kullanıcı onu başka yerden göremez.
                    // Çok birimde commit bütün oturumundur (hangi sürücünün zamanlayıcısı yazdıysa): ileti diskle, birim adı olmadan
                    onChangeLost: msg => AddNotice("uyarı", many ? $"{disk.Description}: {msg}" : $"{disk.Description} \"{name}\": {msg}", throttleKey: msg),
                    // Commit düştü (oturum başına bir kez): oturum kaydedilmeden bırakılır, bölüm salt okunur bağlanır. Bu bir WinFsp
                    // geri çağrısı (Flush/zamanlayıcı, oturum kilidi altında): sürücü burada ayrılamaz → iş parçacığı havuzuna
                    onFaulted: msg =>
                    {
                        thisWrite.FaultMessage = msg;
                        QueueFault(key, container, thisWrite);
                    },
                    onLog: msg => ServiceLog.Write($"{disk.Description} \"{name}\": {msg}"));
                tv.State = VolumeStatus.Mounted;
            }
            if (raw is RawDiskWriter rw) write.RemovalWatch = DeviceNotifications.WatchHandle(rw.Handle, a => OnHandleEvent(key, a));
            bool attached;
            lock (_gate)
            {
                attached = _disks.TryGetValue(key, out var current) && ReferenceEquals(current, disk);
                // Açarken uyku olduysa (1.9.1): uyku bu oturumu görmedi, bekletmedi; yayımlanmaz, var olan geri alma yolu
                slept = _powerEpoch != epoch;
                if (attached && !slept)
                {
                    disk.Writes[container] = write;
                    disk.WriteBlockReasons.Remove(container);
                    disk.Volumes.AddRange(tracked);
                }
            }
            if (!attached) throw new ApfsException(ApfsErrorCode.NotFound, WinText.T("Svc.DiskRemovedMeanwhile"));
            if (slept) throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Svc.SleptDuringEnable"));
            foreach (var tv in writers) ServiceLog.Write(WinText.T("Svc.MountedWritable", key, tv.Name, tv.WritableMount!.MountPoint));
            foreach (var tv in tracked.Where(t => t.State == VolumeStatus.NeedsPassword))
                ServiceLog.Write(WinText.T("Svc.LockedVolumeUntouched", key, tv.Name));
            // Tek balon; çok birimde yanıt da aynı cümle
            string text;
            if (!many)
            {
                AddNotice("bilgi", WinText.U("Notice.MountedWritable", writers[0].Name, writers[0].WritableMount!.MountPoint), requested: true);
                text = WinText.U("Write.Enabled", writers[0].Name, writers[0].WritableMount!.MountPoint);
            }
            else
            {
                text = WinText.U("Notice.MountedWritableMany", disk.Description, string.Join(", ", writers.Select(t => t.WritableMount!.MountPoint)));
                AddNotice("bilgi", text, requested: true);
            }
            _reports?.Record(() => Event("write-enable", "ok", disk, container).WithDuration(watch.Elapsed));
            return new UnlockResponse(true, text);
        }
        catch (Exception e)
        {
            lock (_gate) slept |= _powerEpoch != epoch; // (ör. bağlarken donmuş kapıya takıldı) ret nedeni satıra yazılmasın
            // Oturum yayımlanmadan bırakılır: önce bütün sürücülerin zamanlayıcısı durur (commit yok), sonra ayrılır; blok 0
            // yazılmaz, kimlik değişmez
            foreach (var t in writers) { try { t.WritableMount?.FileSystem.Dispose(); } catch { } }
            foreach (var t in writers) { try { t.WritableMount?.Dispose(); } catch { } }
            write?.RemovalWatch?.Dispose();
            write?.Session.Dispose();
            foreach (var r in resources) { try { r.Dispose(); } catch { } }
            if (liveGate is not null) lock (_gate) _liveGates.Remove(liveGate);
            string msg = WinText.U("Write.EnableFailed", e.Message);
            ServiceLog.Write($"{key}: {msg}");
            _reports?.Record(() => Event("write-enable", "fail", disk, container)
                .WithCode(e is ApfsException ae ? ae.Code.ToString() : e.GetType().Name).WithDuration(watch.Elapsed));
            bool stillAttached;
            lock (_gate) stillAttached = _disks.TryGetValue(key, out var current) && ReferenceEquals(current, disk);
            // Uyku yüzünden geri alındıysa bölüm satırına ret nedeni yazılmaz (diskin bir özelliği değil; yeniden denenebilir)
            if (stillAttached) RemountReadOnly(disk, container, slept ? null : msg);
            // Bildirim yok: hata yanıtta (tepsi penceresi açar); günlük satırı yukarıda
            return new UnlockResponse(false, msg);
        }
    }

    private UnlockResponse DisableWrite(TrackedDisk disk, int container)
    {
        // Yalnız geliştirme modu (testler): bölüm geçişteyken (Switching) oturumun hâlâ yayımlı olduğu pencereyi genişletir
        // (arıza işleyicisi bu arada bölüme dokunmamalı: NoticeTests.FaultRacingWithReadOnlyGivesOneLetterAndOneNotice)
        if (_imageSources is not null && int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_DISABLE_DELAY_MS"), out int delay))
            Thread.Sleep(delay);
        bool saved = CloseWrite(disk, container, abandon: false, closeKind: "read-only", out _, out _, out int sleep); // son commit + blok 0; kimlik kilit içinde tazelenir
        RemountReadOnly(disk, container, null);
        if (sleep != SleepNone) AddSleepNotice(disk, sleep); // uyku işaretli oturum yazmadan bırakıldı: karar bildirimi
        // "Kaydedildi" yalnız bu çağrı kaydettiyse: Close başarısızsa "kaydedilemedi" (Notice.CloseWriteFailed) zaten eklendi,
        // oturumu bu arada başka bir yol kapattıysa onun bildirimi var; ikisinde de "kaydedildi" çelişirdi (1.6.5)
        if (saved) AddNotice("bilgi", WinText.U("Notice.RemountedReadOnly", disk.Description), requested: true);
        return new UnlockResponse(true, WinText.U("Write.RemountedReadOnly"));
    }

    /// <summary>Bölümü diskten salt okunur yeniden açıp bağlar (yazma denemesinden ya da oturumdan sonra; bayat önbellek kalmasın).</summary>
    private void RemountReadOnly(TrackedDisk disk, int container, string? writeBlockReason, bool notifyFailure = true)
    {
        lock (_gate) if (!_disks.TryGetValue(disk.Key, out var current) || !ReferenceEquals(current, disk)) return; // bu arada bırakıldı
        MountContainer(disk, container, notifyFailure: notifyFailure);
        lock (_gate)
        {
            if (writeBlockReason is null) disk.WriteBlockReasons.Remove(container);
            else disk.WriteBlockReasons[container] = writeBlockReason;
        }
    }

    /// <summary>
    /// Bir bölümün yazma oturumunu kapatır: sürücü ayrılır (bekleyen veri gelir), son commit ve blok 0, yazılabilir aygıtlar
    /// bırakılır. Kapatma diskin kimliğini değiştirdiği için kimlik denetimiyle aynı kilit altında yapılır ve kimlik tazelenir.
    /// <paramref name="abandon"/>: diskin içeriği başka bir programca değiştirilmiş (veya doğrulanamıyor) — diske HİÇBİR ŞEY
    /// yazılmaz (commit ve blok 0 yok); önce kayıtlar durdurulur ki ayırma sırasında gelen Flush da yazmasın.
    /// </summary>
    /// <param name="closeKind">Raporda kapanış biçimi: read-only, eject, removed, changed, service-stop.</param>
    /// <returns>true yalnız bu çağrı oturumu kaydederek kapattıysa; false: Close başarısız (<c>Notice.CloseWriteFailed</c>
    /// eklendi), terk edildi (<paramref name="abandon"/>) ya da oturum bu arada başka bir yolla kapatılmıştı.</returns>
    /// <param name="closed">true: oturumu bu çağrı kapattı (kaydederek ya da terk ederek); false: başka bir yol kapatmıştı.</param>
    /// <param name="lostChanges">Terk edilen oturumda kaydedilmemiş değişiklik vardı (1.9.1; değer oturum bırakılmadan önce, sürücüler
    /// durduktan sonra okunur): bildirimin "kaydedilmemiş değişiklikler yazılamadı" cümlesi yalnız o zaman.</param>
    /// <param name="sleepOutcome">Oturum uyku işaretliydi ya da kapısı uykuda donduruldu (1.9.1): <see cref="SleepSaved"/> /
    /// <see cref="SleepNotSaved"/>; o zaman HER ZAMAN kaydedilmeden bırakıldı ve çağıran yalnız uyku kararının bildirimini verir
    /// (<see cref="AddSleepNotice"/>, disk başına tek); <see cref="SleepNone"/> değilse <paramref name="lostChanges"/> false.</param>
    private bool CloseWrite(TrackedDisk disk, int container, bool abandon, string closeKind, out bool closed, out bool lostChanges,
        out int sleepOutcome)
    {
        closed = false;
        lostChanges = false;
        sleepOutcome = SleepNone;
        lock (disk.IdentityLock)
        {
            ContainerWrite? write;
            List<TrackedVolume> volumes;
            int sleep;
            lock (_gate)
            {
                if (!disk.Writes.Remove(container, out write)) return false; // başka bir yol (çıkarma, değişme) kapattı
                closed = true;
                volumes = disk.Volumes.Where(v => v.ContainerIndex == container).ToList();
                disk.Volumes.RemoveAll(v => v.ContainerIndex == container);
                sleep = write.Sleep;
            }
            // Yalnız geliştirme modu (testler): oturum listeden çıktı, henüz kaydedilmedi (bu arada gelen uykuyu sınamak için)
            if (!abandon) DevPause("APFS_DEV_CLOSE_SAVE_DELAY_MS");
            // İşaretsiz ama kapısı uykuda donmuş oturum (uyku geldiğinde kaydederek kapanmak üzereydi): aynı yol, yazmadan bırakılır
            // (1.9.1, inceleme); sonucu sürücüler durduktan SONRA (kaydedilmemiş değişiklik kaldı mı) belirlenir
            bool frozenUnmarked = sleep == SleepNone && write.Gate?.Frozen == true;
            if (frozenUnmarked) sleep = SleepNotSaved;
            if (sleep != SleepNone)
            {
                // Uyku işaretli oturum diske tek bayt yazmadan bırakılır (1.9.1; kararı: kullanıcı 2026-11-25): uykudayken disk başka
                // bir bilgisayarda değişmiş olabilir. Kapı zaten donduruldu
                abandon = true;
                closeKind = sleep == SleepSaved ? "read-only" : "failed";
            }
            else if (!abandon && write.IdentityHold)
            {
                // Kimlik bir kez okunamadığı için bekletilen oturum kaydedilecekse önce kimlik yeniden okunur (1.1.1 güvencesi): tutmazsa
                // ya da okunamazsa kaydedilmeden bırakılır
                string? now = null;
                try { now = PartitionLocator.Fingerprint(disk.Device); }
                catch (Exception e) when (e is ApfsException or ObjectDisposedException) { }
                if (now != disk.Fingerprint) abandon = true;
            }
            var stats = volumes.Select(v => v.WritableMount?.FileSystem.Stats).OfType<WriteStats>()
                .Aggregate(new WriteCounters(0, 0, 0), (a, b) => new WriteCounters(a.Commits + b.Commits, a.Bytes + b.Bytes, a.Operations + b.Operations));
            bool closeFailed = false;
            // Terk etmede önce BÜTÜN sürücüler durur (commit zamanlayıcısı ve Flush artık yazmaz), sonra ayrılır: tek tek
            // yapılınca ayrılmayı bekleyen kardeş sürücünün zamanlayıcısı arada oturumu commit edebilirdi
            if (abandon)
                foreach (var tv in volumes)
                {
                    try { tv.WritableMount?.FileSystem.Dispose(); }
                    catch (Exception e) { ServiceLog.Write(WinText.T("Svc.WritableUnmountError", disk.Key, e.Message)); }
                }
            foreach (var tv in volumes)
            {
                try
                {
                    tv.WritableMount?.Dispose();
                }
                catch (Exception e) { ServiceLog.Write(WinText.T("Svc.WritableUnmountError", disk.Key, e.Message)); }
                tv.WritableMount = null;
            }
            if (abandon)
            {
                // Sürücüler durdu (yeni değişiklik gelmez); terk edilen değişiklik var mı, oturum bırakılmadan önce (uykuda: karar bildirimi)
                if (sleep == SleepNone) lock (write.Session.SyncRoot) lostChanges = write.Session.HasUncommittedChanges;
                else if (frozenUnmarked)
                    lock (write.Session.SyncRoot)
                    {
                        sleep = write.Session.HasUncommittedChanges || write.Session.IsFaulted ? SleepNotSaved : SleepSaved;
                        closeKind = sleep == SleepSaved ? "read-only" : "failed";
                    }
                write.Session.Dispose(); // açık işlem yazılmaz
                ServiceLog.Write(WinText.T("Svc.WriteSessionAbandoned", disk.Key, container, write.Session.CommittedXid));
            }
            else
            {
                try
                {
                    // Yalnız geliştirme modu (testler): kaydederek kapatmanın başarısız olması (ör. disk bu sırada çekildi)
                    if (_imageSources is not null && Environment.GetEnvironmentVariable("APFS_DEV_CLOSE_FAIL") == "1")
                        throw new ApfsException(ApfsErrorCode.IoError, "APFS_DEV_CLOSE_FAIL");
                    write.Session.Close();
                    ServiceLog.Write(WinText.T("Svc.WriteSessionClosed", disk.Key, container, write.Session.CommittedXid));
                }
                catch (Exception e)
                {
                    closeFailed = true;
                    // Kayıt uyku kapısına takıldı (uyku tam kaydederken geldi; 1.9.1, inceleme): uyku kararının bildirimi (çağıran verir)
                    if (write.Gate?.Frozen == true) sleep = SleepNotSaved;
                    else AddNotice("hata", WinText.U("Notice.CloseWriteFailed", disk.Description, e.Message));
                }
                finally
                {
                    // Kaydederek kapatma da oturumu bırakır: şifreli birimde yazıcının çözülmüş anahtar kopyası ancak Dispose'da
                    // sıfırlanır (Close yalnız kaydeder); önceden her yazma döngüsü bellekte bir kopya bırakıyordu
                    write.Session.Dispose();
                }
            }
            _reports?.Record(() => Event("write-session", closeFailed ? "fail" : abandon ? "abandoned" : "ok", disk, container)
                .WithWrite(closeFailed ? "failed" : closeKind, stats)
                .WithDuration(write.Age.Elapsed));
            // Yalnız geliştirme modu (testler): Close ile kimlik tazeleme arasındaki pencereyi genişletir
            if (_imageSources is not null && int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_CLOSE_DELAY_MS"), out int delay))
                Thread.Sleep(delay);
            write.RemovalWatch?.Dispose(); // kayıt kaldırma geri çağrı dışında (Dispose iş parçacığı havuzunda yapar)
            foreach (var r in write.Resources) { try { r.Dispose(); } catch { } }
            if (write.Gate is not null) lock (_gate) _liveGates.Remove(write.Gate);
            if (!abandon)
            {
                // Close blok 0'ı yazdı: kimlik yeni hâline tazelenir (okunamazsa eski kalır; sonraki tarama WinText.U("Release.Removed") der)
                try { disk.Fingerprint = PartitionLocator.Fingerprint(disk.Device); }
                catch (Exception e) when (e is ApfsException or ObjectDisposedException) { }
            }
            sleepOutcome = sleep;
            return !abandon && !closeFailed;
        }
    }

    /// <summary>
    /// Uyku kararının bildirimi (1.9.1; disk başına TEK, çağıran birleştirir): uyarı, kendiliğinden iş (requested yok); kayıt
    /// tuttuysa "değişiklikler kaydedildi", tutmadıysa "son değişikliklerin bir kısmı… uykudan önce kaydedilemedi".
    /// </summary>
    private void AddSleepNotice(TrackedDisk disk, int outcome)
    {
        lock (_gate)
        {
            // Aynı uyku döneminde aynı disk için farklı yollardan (arıza, Salt okunura dön, uyanış, çıkarma) gelen ikinci bildirim yalnız
            // daha kötü haberse verilir (inceleme)
            if (disk.SleepNoticeEpoch == _powerEpoch && (disk.SleepNoticeOutcome == SleepNotSaved || outcome == disk.SleepNoticeOutcome)) return;
            disk.SleepNoticeEpoch = _powerEpoch;
            disk.SleepNoticeOutcome = outcome;
        }
        AddNotice("uyarı", outcome == SleepSaved
            ? WinText.U("Notice.RemountedReadOnly", disk.Description)
            : WinText.U("Notice.CloseWriteFailed", disk.Description, WinText.T("Svc.SleepNotSaved")));
    }

    /// <summary>İki uyku sonucunun kötüsü (birleşik bildirim için).</summary>
    private static int WorseSleep(int a, int b) => a == SleepNotSaved || b == SleepNotSaved ? SleepNotSaved : Math.Max(a, b);

    /// <summary>Arızalı oturumun işini iş parçacığı havuzuna bırakır (WinFsp geri çağrısında sürücü ayırmak kilitlenir).</summary>
    private void QueueFault(string diskKey, int container, ContainerWrite write) =>
        ThreadPool.UnsafeQueueUserWorkItem(_ => // arka plan işi: çağıranın dili/biçimi taşınmaz
        {
            try { HandleFault(diskKey, container, write); }
            catch (Exception e) { ServiceLog.Write($"{diskKey}: {e.Message}"); }
        }, null);

    /// <summary>
    /// Açık depo bildirimi #1: oturumun bir commit'i yarıda kaldı. Oturum bir daha yazamaz (<see cref="WriteSession.IsFaulted"/>);
    /// kaydedilmeden bırakılır, bölüm diskteki son sağlam kayıttan salt okunur yeniden bağlanır, TEK uyarı
    /// (<c>Notice.CloseWriteFailed</c>: "son değişikliklerin bir kısmı… önceki kayıtlı hâliyle açılır").
    /// Bölümün sahibi o anda başkasıysa (geçiş sürüyor: "Salt okunura dön" — onun Close'u arızalı oturumda atar ve aynı uyarıyı
    /// verir —, yazmaya açma — bitince <see cref="SetWritable"/> yeniden bakar —; ya da çıkarma diski zaten bıraktı) hiçbir şey
    /// yapılmaz. Yeniden bağlama hatası SESSİZ (kablo çekildiyse "açılamadı" balonu çıkmasın) ve hemen tarama istenir: çıkarma
    /// algısı olağan yolundan devralır.
    /// </summary>
    private void HandleFault(string diskKey, int container, ContainerWrite write)
    {
        TrackedDisk? disk;
        lock (_gate)
        {
            disk = _disks.GetValueOrDefault(diskKey);
            if (disk is null || !ReferenceEquals(disk.Writes.GetValueOrDefault(container), write)) return;
            if (!disk.Switching.Add(container)) return;
        }
        try
        {
            string msg = write.FaultMessage ?? CoreText.T("Write.SessionFaulted");
            ServiceLog.Write(WinText.T("Svc.WriteSessionFaulted", diskKey, container, msg));
            CloseWrite(disk, container, abandon: true, closeKind: "failed", out bool closed, out _, out int sleep);
            if (!closed) return; // bu arada çıkarma kapattı (kendi bildirimiyle)
            RemountReadOnly(disk, container, null, notifyFailure: false);
            ScanRequested?.Invoke();
            if (sleep != SleepNone) AddSleepNotice(disk, sleep); // uykuda: karar bildirimi tek
            else AddNotice("hata", WinText.U("Notice.CloseWriteFailed", disk.Description, msg));
        }
        finally
        {
            lock (_gate) disk.Switching.Remove(container);
        }
    }

    /// <summary>
    /// Arka planda tek tarama (Windows çıkarmayı reddetti). Arka plan işi: isteğin dili/biçimi taşınmaz. İstisna günlüğe yazılır
    /// (1.8.2): iş parçacığı havuzunda yakalanmayan istisna hizmet sürecini düşürürdü.
    /// </summary>
    private void QueueRescan() =>
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { Rescan(); }
            catch (Exception e) { ServiceLog.Write(WinText.T("Svc.ScanError", e.Message)); }
        }, null);

    /// <summary>"Yeniden tara": daha önce ayrılmış (ama çıkarılmamış) diskler de yeniden bağlansın.</summary>
    public void ForgetReleased()
    {
        lock (_gate) _released.Clear();
        RawDiskDevice.ForgetStalled(); // okuması yanıt vermediği için bir süre açılmayan diskler de
    }

    private void OnHandleEvent(string key, DeviceNotifications.NotifyAction action)
    {
        switch (action)
        {
            case DeviceNotifications.NotifyAction.QueryRemove:
                // Windows diski çıkarmak istiyor: tanıtıcıyı geri çağrının içinde, eşzamanlı olarak kapat.
                ReleaseDisk(key, WinText.U("Release.SafeToRemove"), rememberReleased: true, closeKind: "eject");
                break;
            case DeviceNotifications.NotifyAction.QueryRemoveFailed:
                lock (_gate) _released.Remove(key);
                QueueRescan();
                break;
            case DeviceNotifications.NotifyAction.RemovePending:
            case DeviceNotifications.NotifyAction.RemoveComplete:
                ReleaseDisk(key, WinText.U("Release.Removed"), rememberReleased: false);
                lock (_gate) _released.Remove(key);
                break;
            case DeviceNotifications.NotifyAction.CustomEvent:
                // ör. başka bir program bölüm tablosunu yazdı: geri çağrıyı bekletmeden tarama iste (kimlik denetlenir)
                ScanRequested?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Hizmetten tarama isteği (diske özel aygıt olayı geldi ya da takılan disk henüz hazır değil); hizmet kısa bir gecikmeyle
    /// tek tarama yapar.
    /// </summary>
    public event Action? ScanRequested;

    private static string ChangedReason =>
        WinText.U("Release.Changed");

    /// <summary>Yalnız geliştirme modu: sıradaki bu kadar kimlik okuması hata verir ("dev-identity-fail").</summary>
    private int _devIdentityFailures;

    /// <summary>Bölümün yazılabilir sürücüleri (<see cref="_gate"/> altında çağrılır).</summary>
    private static List<ApfsWritableFileSystem> WritableFileSystems(TrackedDisk disk, int container) =>
        [.. disk.Volumes.Where(v => v.ContainerIndex == container && v.WritableMount is not null).Select(v => v.WritableMount!.FileSystem)];

    /// <summary>Diskin bütün yazma oturumlarını kimlik yüzünden bekletir (<see cref="TrackedDisk.IdentityLock"/> altında); oturum yoksa false.</summary>
    private bool HoldWritesForIdentity(TrackedDisk disk)
    {
        bool any = false;
        lock (_gate) // bayrak kararla aynı kilitte (uyku bekletmesiyle yarışmasın)
            foreach (var (p, w) in disk.Writes)
            {
                w.IdentityHold = true;
                foreach (var fs in WritableFileSystems(disk, p)) fs.SetWriteHold(true);
                any = true;
            }
        return any;
    }

    /// <summary>Kimlik doğrulandı: kimlik bekletmesi kalkar (uyku işaretli oturum bekletmede kalır; <see cref="TrackedDisk.IdentityLock"/> altında).</summary>
    private void ReleaseIdentityHold(TrackedDisk disk)
    {
        lock (_gate) // karar ve bayrak aynı kilitte: araya giren uyku işaretli oturumu bekletmesiz bırakamaz (inceleme)
            foreach (var (p, w) in disk.Writes)
            {
                if (!w.IdentityHold) continue;
                w.IdentityHold = false;
                if (w.Sleep == SleepNone)
                    foreach (var fs in WritableFileSystems(disk, p)) fs.SetWriteHold(false);
            }
    }

    /// <summary>
    /// Kaybolan (okuma hatası veren) ve içeriği değişen (APFS kimliği tutmayan: ör. Rufus ile biçimlendirilmiş) diskleri
    /// bırakır. Değişen diskte yazma oturumu kaydedilmeden terk edilir; disk "bırakıldı" sayılmaz, yeni içerik APFS ise
    /// sonraki tarama onu bağlar.
    /// </summary>
    private void CheckForRemovedDisks()
    {
        List<TrackedDisk> disks;
        lock (_gate) disks = [.. _disks.Values];
        foreach (var d in disks)
        {
            // Kimlik oku-karşılaştır-uygula, bir bölümün Close'uyla (kimliği değiştirir ve tazeler) aynı kilit altında
            lock (d.IdentityLock)
            {
                lock (_gate) if (!_disks.TryGetValue(d.Key, out var current) || !ReferenceEquals(current, d)) continue;
                string? now; // null = okunamadı (çıkarılmış)
                try
                {
                    // Yalnız geliştirme modu (testler, "dev-identity-fail"): sıradaki n kimlik okuması okuma hatası verir
                    if (_imageSources is not null && Interlocked.Decrement(ref _devIdentityFailures) >= 0)
                    {
                        ServiceLog.Write("APFS_DEV_IDENTITY_FAIL");
                        throw new ApfsException(ApfsErrorCode.IoError, "APFS_DEV_IDENTITY_FAIL");
                    }
                    now = PartitionLocator.Fingerprint(d.Device);
                }
                catch (ApfsException e) when (e.Code is ApfsErrorCode.IoError or ApfsErrorCode.NotFound) { now = null; }
                catch (ObjectDisposedException) { now = null; } // bu arada başka bir istek diski bıraktı
                catch (ApfsException) { now = ""; }              // bölüm tablosu/kapsayıcı başı bozuk: içerik değişmiş
                catch (Exception e)
                {
                    ServiceLog.Write(WinText.T("Svc.DiskCheckFailed", d.Key, e.Message));
                    continue;
                }
                // Yazma oturumlu diskte İLK okuma hatası (1.9.1): oturum HEMEN bekletilir (commit yok, değiştiren çağrı ret) ve
                // tarama istenir; uyanıştaki geçici G/Ç hatası kaydedilmemiş değişiklikleri attırmasın. Sonraki denetim karar verir:
                // kimlik tutarsa bekletme kalkar; yine hata ya da farklı kimlik → aşağıdaki gibi kaydedilmeden bırakılır. Bekletme
                // 1.1.1 güvencesini korur (biçimlendirme okuma hatası da doğurabilir: bekletilen oturum diske yazmaz).
                if (now is null && !d.IdentityUnreadOnce && HoldWritesForIdentity(d))
                {
                    d.IdentityUnreadOnce = true;
                    ServiceLog.Write(WinText.T("Svc.WritesHeldUnreadable", d.Key));
                    ScanRequested?.Invoke();
                    continue;
                }
                // Yazma oturumundaki disk doğrulanamıyorsa da kaydedilmeden bırakılır: biçimlendirme okuma hatası da
                // doğurabilir; gerçekten çekilmiş diske zaten yazılamaz.
                if (now is null)
                    ReleaseDisk(d.Key, WinText.U("Release.Removed"), rememberReleased: false, abandonWrites: true, expected: d);
                else if (now != d.Fingerprint)
                    ReleaseDisk(d.Key, ChangedReason, rememberReleased: false, abandonWrites: true, expected: d, contentChanged: true);
                else if (d.IdentityUnreadOnce)
                {
                    d.IdentityUnreadOnce = false;
                    ReleaseIdentityHold(d);
                    ServiceLog.Write(WinText.T("Svc.WritesResumedVerified", d.Key));
                }
            }
        }
        // Güvenli kaldırılmış ama aslında hâlâ takılı olmayan diskler listeden düşsün (yeniden takılınca bağlansın)
        if (_imageSources is null)
        {
            List<string> released;
            lock (_gate) released = [.. _released];
            // Aygıt yolu \\.\ önekiyle (1.8.2): öneksiz ad göreli dosya yoluydu, çıkarılamayan disk hemen yeniden bağlanıyordu
            foreach (var key in released)
                if (!RawDiskDevice.IsPresent(key)) lock (_gate) _released.Remove(key);
        }
    }

    /// <param name="abandonWrites">Yazma oturumları varsa kaydetmeden bırak (bkz. <see cref="CloseWrite"/>).</param>
    /// <param name="expected">Verilirse yalnız anahtardaki disk hâlâ BU nesneyse bırakılır: tarama eski bir anlık görüntüyle
    /// çalışırken aynı anahtara yeni bağlanmış diski düşürmesin.</param>
    /// <param name="contentChanged">Disk başka bir programca değiştirildi (bildirimde kaydedilmeyen değişikliklerin nedeni farklı).</param>
    /// <remarks>
    /// Tekrar çağrılabilir: diskin her açık tanıtıcısının (salt okunur + her yazma tanıtıcısı) güvenli kaldırma kaydı buraya gelir;
    /// ilk çağrı BÜTÜN tanıtıcıları eşzamanlı kapatır (QueryRemove geri çağrısının içinde), sonrakiler bir şey bulmaz.
    /// </remarks>
    /// <param name="closeKind">Yazma oturumu varsa raporda kapanış biçimi (bkz. <see cref="CloseWrite"/>).</param>
    /// <returns>true: disk bu çağrıyla bırakıldı; false: listede yoktu ya da <paramref name="expected"/> değildi.</returns>
    private bool ReleaseDisk(string key, string reason, bool rememberReleased, bool notify = true, bool abandonWrites = false,
        TrackedDisk? expected = null, bool contentChanged = false, string closeKind = "removed")
    {
        TrackedDisk? disk;
        lock (_gate)
        {
            if (!_disks.TryGetValue(key, out disk) || (expected is not null && !ReferenceEquals(disk, expected))) return false;
            _disks.Remove(key);
            if (rememberReleased) _released.Add(key);
        }
        disk.Released.Cancel(); // süren kilit açma (PBKDF2) sıradaki kayda geçmeden dursun
        bool abandoned, lostChanges = false;
        int sleepOutcome = SleepNone, plainWrites = 0;
        List<TrackedVolume> mounted;
        List<int> writes;
        lock (disk.IdentityLock) // süren bir bölüm kapatması bitsin; aygıtlar onun altından çekilmesin
        {
            lock (_gate)
            {
                writes = [.. disk.Writes.Keys];
                mounted = [.. disk.Volumes];
            }
            foreach (int p in writes)
            {
                CloseWrite(disk, p, abandonWrites, contentChanged ? "changed" : closeKind, out bool closed, out bool lost, out int sleep);
                lostChanges |= lost;
                if (sleep != SleepNone) sleepOutcome = WorseSleep(sleepOutcome, sleep);
                else if (closed) plainWrites++;
            }
            abandoned = abandonWrites && plainWrites > 0;
            foreach (var tv in mounted)
            {
                // Sürücü alınıp boşaltılır (_gate altında): o anda kilit açan istek aynı sürücüyü ikinci kez ayırmasın
                ApfsMount? mount;
                lock (_gate) (mount, tv.Mount) = (tv.Mount, null);
                try { mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", key, e.Message)); }
            }
            List<ApfsContainer> containers;
            lock (_gate) containers = [.. disk.Containers.Values];
            foreach (var c in containers) c.Dispose();
            disk.Device.Dispose();
            disk.RemovalWatch?.Dispose();
            List<VolumeKey> keys;
            lock (_gate) { keys = [.. disk.Keys.Values]; disk.Keys.Clear(); }
            foreach (var k in keys) k.Dispose(); // çıkarılan/değişen diskin anahtarı bellekte kalmasın
        }
        // Yalnız geliştirme modu (testler): bırakılan diskten sonra bellekte kalan çözülmüş anahtar sayısı
        if (_imageSources is not null && Environment.GetEnvironmentVariable("APFS_DEV_LIVE_KEYS") == "1")
            ServiceLog.Write($"APFS_DEV_LIVE_KEYS released {VolumeKey.LiveCount} crypto {VolumeCrypto.LiveCount}");
        if (contentChanged) _reports?.Record(() => Event("disk-changed", abandoned ? "abandoned" : "ok", disk, null));
        if (reason.Length > 0) ServiceLog.Write($"{key}: {reason}.");
        // Uyku işaretli oturumların diski (uyanışta çekilip gelen USB, ya da uyanış gelmeden Güvenle çıkar): karar bildirimi her yolda,
        // disk başına tek (1.9.1; inceleme: Güvenle çıkar'ın notify:false'u yutuyordu). Diskte uykudan sonra açılmış işaretsiz bir
        // oturum da terk edildiyse onun bildirimi ayrıca verilir
        if (sleepOutcome != SleepNone) AddSleepNotice(disk, sleepOutcome);
        if (!notify) return true;
        if (sleepOutcome != SleepNone && plainWrites == 0) return true;
        // Salt okunur diskin kablosu çekildi: veri riski yok, kullanıcı biliyor (Windows da USB çekilince susar) → yalnız günlük.
        // Yazma oturumu vardıysa ya da içerik değiştiyse uyarı kalır. Karar türle verilir, çevrilmiş metinle değil.
        if (closeKind == "removed" && !contentChanged && plainWrites == 0) return true;
        var names = mounted.Where(v => v.State == VolumeStatus.Mounted).Select(v => $"\"{v.Name}\"").ToList();
        string text = $"{disk.Description}{(names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")} {reason}.";
        // Kayıp cümlesi yalnız terk edilen oturumda kaydedilmemiş değişiklik varsa (1.9.1): önceden son değişiklikler zaten
        // kaydedilmişken de "kaydedilmemiş son değişiklikler yazılamadı" deniyordu. Düzey yazma oturumu terk edildiyse yine "uyarı".
        if (lostChanges)
            text += " " + (contentChanged
                ? WinText.U("Release.UnsavedNotWrittenChanged")
                : WinText.U("Release.UnsavedLost"));
        AddNotice(abandoned ? "uyarı" : "bilgi", text);
        return true;
    }

    // ---------------- Uyku (1.9.1; karar: kullanıcı 2026-11-25 "kaydet, salt okunura dön") ----------------

    /// <summary>Uykudan önce yazma oturumlarının kaydı için en uzun süre (sistem geri çağrıyı ~2 sn bekler).</summary>
    public static readonly TimeSpan SleepBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Uyku başlıyor (PBT_APMSUSPEND; geri çağrının içinde, eşzamanlı). Güç dönemi artar (süren yazmaya açma oturumu yayımlamaz); her
    /// yazma oturumu işaretlenir ve ÖNCE bekletilir (değiştiren çağrılar ret, okuma sürer, zamanlayıcı/Flush commit etmez), SONRA
    /// bütçe içinde paralel kaydedilir (oturum kilidini en çok kalan süre kadar bekler); en sonda kapısı dondurulur: kayıt bütçeyi
    /// aşıp uyku sırasında yarıda kalırsa da aygıta bundan sonra tek bayt gitmez (önceki checkpoint sağlam). İşaret oturum bırakılana
    /// dek kalır; uyanışta (<see cref="ResumeFromSleep"/>) oturum diske yazmadan bırakılır.
    /// </summary>
    public void PrepareForSleep(TimeSpan budget)
    {
        long start = Environment.TickCount64;
        TimeSpan Remaining() => TimeSpan.FromMilliseconds(Math.Max(0, (long)budget.TotalMilliseconds - (Environment.TickCount64 - start)));
        var held = new List<(ContainerWrite Write, List<ApfsWritableFileSystem> Fs, bool Save)>();
        List<PowerGateDevice> gates;
        lock (_gate)
        {
            _powerEpoch++;
            foreach (var d in _disks.Values)
                foreach (var (p, w) in d.Writes)
                {
                    if (w.Sleep != SleepNone) continue; // (art arda ikinci uyku bildirimi) zaten işaretli
                    w.Sleep = SleepNotSaved;
                    var fsList = WritableFileSystems(d, p);
                    foreach (var fs in fsList) fs.SetWriteHold(true); // ÖNCE bekletme (bayrak işaretle aynı kilitte)
                    // Kimliği doğrulanmamış diske (bir kez okunamadı) uyku kaydı da yazılmaz (1.1.1 güvencesi; inceleme)
                    held.Add((w, fsList, !w.IdentityHold));
                }
            gates = [.. _liveGates]; // yayımlanmış + açılmakta + kapanmakta olan bütün oturumlar
        }
        DevHoldSessionsBusy(held.Select(h => h.Write.Session));
        // SONRA kayıt (oturum başına tek commit, bütün birimleri), paralel
        var saves = held.Select(h => h.Save && h.Fs.Count > 0
            ? Task.Run(() => h.Fs[0].CommitForSleep(Remaining(), () => !h.Write.IdentityHold)) // kilit altında yeniden: bu arada bekletildiyse yazma
            : Task.FromResult(false)).ToArray();
        try { Task.WaitAll(saves, Remaining()); } catch (AggregateException) { }
        // En sonda BÜTÜN kapılar birlikte donar, kapıyı geçmiş yazmalar tek beklemede beklenir; sonra sonuçlar okunur (bütçe sınırında
        // biten kayıt da sayılır: donmadan önce bitmiş olmalı)
        foreach (var g in gates) g.Freeze();
        long until = Environment.TickCount64 + Math.Max(100, (long)Remaining().TotalMilliseconds);
        int busy = gates.Count(g => !g.WaitIdle(TimeSpan.FromMilliseconds(Math.Max(0, until - Environment.TickCount64))));
        int saved = 0;
        lock (_gate)
            for (int i = 0; i < held.Count; i++)
                if (saves[i].IsCompletedSuccessfully && saves[i].Result) { held[i].Write.Sleep = SleepSaved; saved++; }
        ServiceLog.Write(WinText.T("Svc.PowerSuspend", held.Count, saved, Environment.TickCount64 - start, gates.Count, busy));
    }

    /// <summary>
    /// Yalnız geliştirme modu (testler, <c>APFS_DEV_SLEEP_BUSY_MS</c>): uyku kaydından önce her oturumun kilidini bu kadar süre tutan
    /// iş parçacığı başlatır (süren uzun bir yazmayı taklit eder; kayıt kilidi alamaz). Kilit alınınca döner.
    /// </summary>
    private void DevHoldSessionsBusy(IEnumerable<WriteSession> sessions)
    {
        if (_imageSources is null || !int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_SLEEP_BUSY_MS"), out int ms)) return;
        foreach (var session in sessions)
        {
            var taken = new ManualResetEventSlim();
            new Thread(() =>
            {
                lock (session.SyncRoot)
                {
                    taken.Set();
                    Thread.Sleep(ms);
                }
            }) { IsBackground = true }.Start();
            taken.Wait();
        }
        ServiceLog.Write("APFS_DEV_SLEEP_BUSY_MS");
    }

    /// <summary>
    /// Uyanış (iş parçacığı havuzunda; PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND, ikisi de gelebilir). Uyku işaretli her oturum diske
    /// tek bayt yazmadan (blok 0 dahil) bırakılır, bölüm sessizce salt okunur bağlanır, tek bildirim (karar metni; <see cref="CloseWrite(TrackedDisk, int, bool, string, out bool, out bool, out int)"/>
    /// ekler); yazmak için yeniden "Yazmaya izin ver". Oturumu bu arada başka bir yol aldıysa (çıkarma, süren "Salt okunura dön") o yol
    /// işaretli oturumu yine yazmadan bırakır ve aynı bildirimi verir. Tarama istenir (uykuda disk çıkarılmış/değişmiş olabilir).
    /// </summary>
    public void ResumeFromSleep()
    {
        List<(TrackedDisk Disk, int Container, ContainerWrite Write)> marked;
        lock (_gate)
            marked = [.. _disks.Values.SelectMany(d => d.Writes.Where(kv => kv.Value.Sleep != SleepNone).Select(kv => (d, kv.Key, kv.Value)))];
        int closedCount = 0;
        foreach (var group in marked.GroupBy(m => m.Disk))
        {
            var disk = group.Key;
            int outcome = SleepNone;
            foreach (var (_, p, w) in group)
            {
                lock (_gate)
                {
                    if (!IsAttached(disk) || !ReferenceEquals(disk.Writes.GetValueOrDefault(p), w)) continue;
                    if (!disk.Switching.Add(p)) continue; // süren geçiş oturumu kendisi bırakır (işaretliyse yazmadan, aynı bildirimle)
                }
                try
                {
                    CloseWrite(disk, p, abandon: true, closeKind: "failed", out bool closed, out _, out int sleep);
                    if (!closed) continue;
                    RemountReadOnly(disk, p, null, notifyFailure: false);
                    outcome = WorseSleep(outcome, sleep);
                    closedCount++;
                }
                finally
                {
                    lock (_gate) disk.Switching.Remove(p);
                }
            }
            if (outcome != SleepNone) AddSleepNotice(disk, outcome); // disk başına tek
        }
        ServiceLog.Write(WinText.T("Svc.PowerResume", closedCount));
        if (marked.Count > 0) ScanRequested?.Invoke();
    }

    /// <summary>Yalnız geliştirme modu (testler): görüntü kaynaklı hizmet.</summary>
    public bool IsDevelopment => _imageSources is not null;

    /// <summary>Yalnız geliştirme modu ("dev-power"): uyku (true) ya da uyanış (false), gerçek güç olayının yoluyla.</summary>
    public void DevPower(bool sleep)
    {
        if (_imageSources is null) return;
        if (sleep) PrepareForSleep(SleepBudget);
        else ThreadPool.UnsafeQueueUserWorkItem(_ => // gerçek uyanış gibi arka planda, korumalı
        {
            try { ResumeFromSleep(); }
            catch (Exception e) { ServiceLog.Write(WinText.T("Svc.PowerEventFailed", e.Message)); }
        }, null);
    }

    /// <summary>Yalnız geliştirme modu ("dev-identity-fail"): sıradaki <paramref name="count"/> kimlik okuması hata verir.</summary>
    public void DevIdentityFail(int count)
    {
        if (_imageSources is not null) Interlocked.Exchange(ref _devIdentityFailures, Math.Clamp(count, 0, 100));
    }

    // ---------------- Raporlama ----------------

    /// <summary>
    /// Olay: ortak alanlar + diskin aygıt bilgileri + (bölüm verildiyse) kapsayıcının yapı bilgileri. Yalnız sayılar ve sabit
    /// simgeler; disk/birim adı, yol, ileti GİRMEZ (<see cref="ReportEvents"/>).
    /// </summary>
    private JsonObject Event(string type, string result, TrackedDisk disk, int? container, ContainerFacts? facts = null)
    {
        if (facts is null && container is int p) lock (_gate) facts = disk.ContainerFacts.GetValueOrDefault(p);
        return ReportEvents.Create(type, result, "service").WithDisk(disk.Facts, facts);
    }

    // ---------------- Durum ve bildirimler ----------------

    private int _statusTrimLogged; // disk listesinin kırpıldığı günlüğe bir kez

    public StatusResponse Status(long since)
    {
        List<DiskStatus> all;
        List<Notice> notices;
        long lastId;
        lock (_gate)
        {
            // Diskten gelen metinler sınırlı ve gösterilebilir (1.9.1): ad ≤ 255, ileti ≤ 1 024, başlık ≤ 256 karakter
            all = _disks.Values.Select(d => new DiskStatus(d.Key, d.Key, PipeProtocol.ClampDiskDescription(d.Description),
                d.Volumes.OrderBy(v => v.ContainerIndex).ThenBy(v => v.Index)
                    .Select(v => new VolumeStatus(v.Index, PipeProtocol.ClampVolumeName(v.Name), v.State, v.Mount?.MountPoint ?? v.WritableMount?.MountPoint,
                        PipeProtocol.ClampHint(v.Hint), PipeProtocol.ClampVolumeMessage(v.Message),
                        Writable: v.WritableMount is not null, Container: v.ContainerIndex,
                        WriteBlockReason: d.WriteBlockReasons.GetValueOrDefault(v.ContainerIndex),
                        Uuid: v.Uuid?.ToString())).ToList(),
                Writable: d.Writes.Count > 0,
                WriteBlockReason: d.WriteBlockReasons.OrderBy(r => r.Key).Select(r => r.Value).FirstOrDefault())).ToList();
            notices = _notices.Where(n => n.Id > since).ToList();
            lastId = _noticeId;
        }
        // Disk listesi bütçeyle (1.9.1; diskler hep, birimler bütçe dolana dek) ve bildirimler bayt bütçesiyle (en yeniler kalır;
        // LastNoticeId değişmez): yanıt istemcinin sınırını aşınca tepsi hizmetten kopuyordu
        var disks = PipeProtocol.DisksWithinBudget(all, out int omitted);
        if (omitted > 0 && Interlocked.Exchange(ref _statusTrimLogged, 1) == 0)
            ServiceLog.Write(WinText.T("Svc.StatusTrimmed", omitted));
        return new StatusResponse(disks, PipeProtocol.NewestWithinBudget(notices), lastId, _instance);
    }

    /// <param name="throttleKey">Aynı ileti (ör. bozuk bir dosyaya tekrar tekrar erişim) bir dakikada bir kez yazılır: hem
    /// günlükte hem bildirimde.</param>
    /// <param name="queueKey">Bildirim kuyruğunu ayrıca seyreltir: bu anahtarla <paramref name="queueWindow"/> içinde en çok bir
    /// bildirim (ör. birim başına dosya hataları; ileti dosya adı içerdiği için her dosya ayrı ileti). Günlük yalnız
    /// <paramref name="throttleKey"/>'e bağlıdır; ayrıntı kaybolmaz.</param>
    /// <param name="requested">Kullanıcının istediği işin sonucu (<see cref="Notice.Requested"/>; tepsi "yalnız önemli bildirimler"de
    /// de gösterir). Yalnız Güvenle çıkar ve yazmaya aç/kapat yanıtlarında true.</param>
    private void AddNotice(string level, string text, string? throttleKey = null, string? queueKey = null, TimeSpan queueWindow = default,
        bool requested = false)
    {
        lock (_gate)
        {
            if (throttleKey is not null && !_recentNotices.TryMark(throttleKey, TimeSpan.FromMinutes(1))) return;
            bool queue = queueKey is null || _recentQueued.TryMark(queueKey, queueWindow);
            if (queue)
            {
                // Kuyruktaki metin kısaltılır (tek bildirim yanıt bütçesine sığsın); günlüğe tamamı
                // Diskten gelen ad ve iletideki denetim/yön karakterleri gösterilmez (1.9.1; günlükte kaçışlı)
                _notices.Add(new Notice(++_noticeId, level, PipeProtocol.ClampNoticeText(Apfs.Util.TextSafety.ForDisplay(text)), DateTime.Now, requested));
                if (_notices.Count > 200) _notices.RemoveAt(0);
            }
        }
        ServiceLog.Write($"[{level}] {text}");
    }

    /// <summary>
    /// Birimin dosya hatası bildirimleri bu sürede en çok bir kez (günlükte hepsi; bkz. <see cref="AddNotice"/>). Değişiklik
    /// kaybı (<see cref="ApfsWritableFileSystem.ChangeLost"/>) buraya girmez (1.6.4).
    /// </summary>
    private static readonly TimeSpan FileErrorNoticeWindow = TimeSpan.FromMinutes(10);

    private void OnFileError(TrackedDisk disk, string volumeName, string volumeKey, string msg) =>
        AddNotice("uyarı", $"{disk.Description} \"{volumeName}\": {msg}", throttleKey: msg, queueKey: volumeKey, queueWindow: FileErrorNoticeWindow);

    /// <summary>
    /// Disk başlığı (tepsi, menü, bildirimler). Boyut birim satırı ve Gezgin gibi ikili birimle, tek biçimleyiciden
    /// (<see cref="SizeText"/>; 1.7.1'e dek ondalıktı: 1 TB disk "1000,2 GB", birim satırı "931 GB"). Sayı biçimi isteyenin bölge
    /// ayarı (önceden hizmetin sistem yereli: birim satırı "1,81 TB" iken başlık "1.81 TB" olabiliyordu).
    /// </summary>
    private static string DescribeDisk(string key, long length)
    {
        string size = SizeText.Format(length, ServiceLanguage.Format);
        return key.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase) ? WinText.U("Disk.PhysicalName", key[13..], size) : $"{Path.GetFileName(key)} ({size})";
    }

    /// <summary>Hizmet durdurulurken süren taramanın bitmesi için en uzun bekleme.</summary>
    private static readonly TimeSpan StopScanWait = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        // Önce bayrak (1.9.1): süren tarama bundan sonra diski yayımlamaz (bağladıklarını kendisi ayırır), yenisi başlamaz. Süren
        // tarama en çok StopScanWait beklenir; önceden beklenmiyordu, bu arada bağlanan disk hiç bırakılmadan kalıyordu.
        lock (_gate) _disposed = true;
        bool scanHeld = _scanLock.TryEnter(StopScanWait);
        try
        {
            // Önce bütün yazma oturumları PARALEL kaydedilir (1.9.1; bekletilmiş oturum hariç: sürücüsü commit etmez), sonra sırayla
            // bırakılır (Close'ta değişiklik kalmadı: yalnız blok 0). Kapanış Windows'un hizmet bekleme süresine (~5 sn) sığsın; önceden
            // her disk kendi commit'ini sırayla yapıyordu.
            List<List<ApfsWritableFileSystem>> sessions;
            lock (_gate) sessions = [.. _disks.Values.SelectMany(d => d.Writes.Keys.Select(p => WritableFileSystems(d, p)))];
            Parallel.ForEach(sessions, fsList =>
            {
                foreach (var fs in fsList)
                {
                    try { fs.CommitIfDirty(); }
                    catch (Exception e) { ServiceLog.Write(WinText.T("Svc.WritableUnmountError", "-", e.Message)); }
                }
            });
            List<string> keys;
            lock (_gate) keys = [.. _disks.Keys];
            foreach (var k in keys) ReleaseDisk(k, WinText.U("Release.ServiceStopped"), rememberReleased: false, closeKind: "service-stop");
        }
        finally
        {
            if (scanHeld) _scanLock.Exit();
        }
    }
}
