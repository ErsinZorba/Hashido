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
    private readonly Dictionary<string, DateTime> _recentNotices = [];
    private readonly Dictionary<string, DateTime> _recentQueued = []; // AddNotice queueKey → son kuyruğa giriş
    private readonly IReadOnlyList<string>? _imageSources;
    private readonly HashSet<string> _released = new(StringComparer.OrdinalIgnoreCase); // güvenli kaldırma sonrası yeniden bağlanmasın
    private long _noticeId;
    private readonly ReportRecorder? _reports;
    private readonly Dictionary<string, int> _devOpenAttempts = new(StringComparer.OrdinalIgnoreCase); // APFS_DEV_OPEN_FAILURES

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
    private sealed class TrackedDisk(string key, IBlockDevice device, string description)
    {
        public string Key { get; } = key;
        public IBlockDevice Device { get; } = device;
        /// <summary>Takıldığındaki APFS kimliği (<see cref="PartitionLocator.Fingerprint"/>); değişirse disk başka bir programca değiştirilmiştir.</summary>
        public string Fingerprint { get; set; } = "";
        public string Description { get; } = description;
        /// <summary>APFS bölüm sırası → salt okunur açılmış kapsayıcı (yazmaya açık bölümde yok; onu oturum tutar).</summary>
        public Dictionary<int, ApfsContainer> Containers { get; } = [];
        public List<TrackedVolume> Volumes { get; } = [];
        public DeviceNotifications.Registration? RemovalWatch { get; set; }
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
        /// Oturumun bir commit'i yarıda kaldı (<see cref="WriteSession.IsFaulted"/>; sürücünün <c>Faulted</c> olayı, oturum başına
        /// bir kez): hatanın iletisi. Doluysa oturum kaydedilmeden bırakılmalı (<see cref="HandleFault"/>).
        /// </summary>
        public volatile string? FaultMessage;
    }

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

    private void RescanCore()
    {
        foreach (var (key, open) in EnumerateSources())
        {
            lock (_gate)
            {
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
                AttachDisk(key, device);
                ForgetUnreadable(key);
                device = null; // sahipliği TrackedDisk'e geçti
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

    /// <summary>Disk anahtarı → okunamadığı ilk an (<see cref="_gate"/> altında); okunabilince ya da kaybolunca silinir.</summary>
    private readonly Dictionary<string, DateTime> _unreadableSince = new(StringComparer.OrdinalIgnoreCase);

    private void OnDiskUnreadable(string key, Exception e)
    {
        var now = DateTime.UtcNow;
        bool first, warn = false;
        TimeSpan elapsed;
        lock (_gate)
        {
            first = !_unreadableSince.TryGetValue(key, out var since);
            if (first) _unreadableSince[key] = since = now;
            elapsed = now - since;
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
            lock (_gate)
            {
                log = !_recentNotices.TryGetValue("unreadable#" + key, out var last) || now - last >= TimeSpan.FromMinutes(1);
                if (log) _recentNotices["unreadable#" + key] = now;
            }
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

    private void AttachDisk(string key, IBlockDevice device)
    {
        var disk = new TrackedDisk(key, device, DescribeDisk(key, device)) { Fingerprint = PartitionLocator.Fingerprint(device) };
        int count = PartitionLocator.FindApfsPartitions(device).Count;
        disk.Facts = new DiskFacts(device is RawDiskDevice r ? ReportEvents.BusToken(r.QueryBusType() ?? -1) : "image", device.SectorSize,
            PartitionLocator.LooksLikeContainer(device, 0) ? "none" : "gpt", count);
        for (int p = 0; p < count; p++) MountContainer(disk, p, report: true);
        // Yalnız geliştirme modu (testler): bağlama ile diskin listeye girmesi arasındaki pencereyi genişletir
        if (_imageSources is not null && int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_ATTACH_DELAY_MS"), out int delay))
            Thread.Sleep(delay);

        if (device is RawDiskDevice raw)
            disk.RemovalWatch = DeviceNotifications.WatchHandle(raw.Handle, a => OnHandleEvent(key, a));
        List<TrackedVolume> mounted;
        lock (_gate)
        {
            _disks[key] = disk;
            mounted = [.. disk.Volumes.Where(v => v.State == VolumeStatus.Mounted && v.Mount is not null)
                .OrderBy(v => v.ContainerIndex).ThenBy(v => v.Index)];
        }
        // Disk başına tek özet (çok bölümlü/birimli diskte birim başına bildirim yağıyordu); kilitli birim parola penceresiyle sorulur
        if (mounted.Count == 1)
            AddNotice("bilgi", WinText.U("Notice.Mounted", mounted[0].Name, mounted[0].Mount!.MountPoint));
        else if (mounted.Count > 1)
            AddNotice("bilgi", WinText.U("Notice.DiskMounted", disk.Description, string.Join(", ", mounted.Select(v => v.Mount!.MountPoint))));
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
            ContainerFacts? facts = null;
            try { facts = Apfs.Diagnostics.ContainerFacts.Of(c); } catch (Exception e) when (e is ApfsException or ObjectDisposedException) { }
            var volumes = new List<TrackedVolume>();
            foreach (var r in c.OpenVolumes(keys: KeysOf(disk)))
            {
                ulong oid = c.Superblock.FsOids[r.Index];
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
        try { ApfsVolume.Open(tv.Container, tv.Oid, info => { seen = info; return null; }); }
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
        tv.Name = v.Name;
        if (!v.IsUserVisible)
        {
            tv.State = VolumeStatus.Hidden;
            tv.Message = WinText.U("Volume.InternalRole", v.RoleName);
            return;
        }
        try
        {
            string description = disk.Description, volumeKey = $"{disk.Key}#{tv.ContainerIndex}#{tv.Index}";
            var m = ApfsMount.Mount(v, null, msg => OnFileError(description, v.Name, volumeKey, msg));
            tv.Mount = m;
            tv.State = VolumeStatus.Mounted;
            tv.Message = null;
            ServiceLog.Write(WinText.T("Svc.Mounted", disk.Key, v.Name, m.MountPoint));
        }
        catch (ApfsException e)
        {
            tv.State = VolumeStatus.Failed;
            tv.Message = e.Message;
            tv.FailureCode = e.Code.ToString();
            if (notifyFailure) AddNotice("hata", WinText.U("Notice.MountFailed", v.Name, e.Message));
            else ServiceLog.Write($"{disk.Key}: {WinText.U("Notice.MountFailed", v.Name, e.Message)}");
        }
    }

    // ---------------- Parola ----------------

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

    private UnlockResponse UnlockCore(TrackedDisk disk, TrackedVolume tv, int container, string password)
    {
        VolumeKey? key = null;
        var watch = Stopwatch.StartNew();
        try
        {
            var uuid = tv.Container.VolumeUuid(tv.Oid);
            key = VolumeKey.Unlock(tv.Container, uuid, password);
            var v = ApfsVolume.Open(tv.Container, tv.Oid, key);
            lock (_gate)
            {
                if (disk.Keys.Remove(uuid, out var old)) old.Dispose();
                disk.Keys[uuid] = key;
            }
            key = null; // sahipliği diske geçti
            TryMount(disk, tv, v, notifyFailure: false); // sonuç yalnız yanıtta: tepsi balonu ya da parola penceresi
            _reports?.Record(() => Event("unlock", tv.State == VolumeStatus.Mounted ? "ok" : "fail", disk, container)
                .WithCode(tv.FailureCode).WithDuration(watch.Elapsed));
            return tv.State == VolumeStatus.Mounted
                ? new UnlockResponse(true, WinText.U("Unlock.Mounted", v.Name, tv.Mount!.MountPoint))
                : new UnlockResponse(false, tv.Message ?? WinText.U("Unlock.MountFailed"));
        }
        catch (ApfsException e)
        {
            // Parola yanlışsa günlüğe yalnızca olay yazılır, parola asla.
            ServiceLog.Write(WinText.T("Svc.UnlockFailed", disk.Key, tv.Name, e.Code));
            _reports?.Record(() => Event("unlock", "fail", disk, container).WithCode(e.Code.ToString()).WithDuration(watch.Elapsed));
            return new UnlockResponse(false, e.Message, Code: e.Code.ToString());
        }
        finally
        {
            key?.Dispose();
        }
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
        List<string> busy;
        lock (_gate)
        {
            disk = _disks.GetValueOrDefault(diskKey);
            busy = disk is null ? [] : BusyVolumes(disk);
        }
        if (disk is null) return new UnlockResponse(false, WinText.U("Disk.Gone"));
        if (!force && busy.Count > 0)
        {
            ServiceLog.Write(WinText.T("Svc.EjectDeferred", diskKey, string.Join(", ", busy)));
            _reports?.Record(() => Event("eject", "busy", disk, null));
            return new UnlockResponse(false,
                WinText.U("Eject.Busy", disk.Description, string.Join(", ", busy)), Busy: true);
        }

        DiskEjector.Target? target = null;
        if (_imageSources is null && diskKey.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(diskKey.AsSpan(13), out int number))
            target = DiskEjector.Find(number);

        ReleaseDisk(diskKey, "", rememberReleased: true, notify: false, closeKind: "eject");
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
        ThreadPool.QueueUserWorkItem(_ => Rescan());
        return new UnlockResponse(false, WinText.U("Eject.Remounting", error));
    }

    // ---------------- Yazma (deneysel) ----------------

    private static List<string> BusyVolumes(TrackedDisk disk, int? container = null) =>
        [.. disk.Volumes.Where(v => container is null || v.ContainerIndex == container)
            .Where(v => v.Mount?.FileSystem.IsBusy == true || v.WritableMount?.FileSystem.IsBusy == true)
            .Select(v => $"{v.Mount?.MountPoint ?? v.WritableMount?.MountPoint} \"{v.Name}\"")];

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
        lock (_gate)
        {
            disk = _disks.GetValueOrDefault(diskKey);
            if (disk is null) return new UnlockResponse(false, WinText.U("Disk.Gone"));
            if (disk.Switching.Contains(container)) return new UnlockResponse(false, WinText.U("Write.PreviousRequestPending"));
            if (!disk.Containers.ContainsKey(container) && !disk.Writes.ContainsKey(container))
                return new UnlockResponse(false, WinText.U("Write.NoSuchPartition"));
            bool writable = disk.Writes.ContainsKey(container);
            if (enable == writable)
                return new UnlockResponse(true, enable ? WinText.U("Write.AlreadyWritable") : WinText.U("Write.AlreadyReadOnly"));
            var busy = BusyVolumes(disk, container);
            if (!force && busy.Count > 0)
                return new UnlockResponse(false,
                    WinText.U("Write.Busy", disk.Description, string.Join(", ", busy)), Busy: true);
            if (!disk.Switching.Add(container)) return new UnlockResponse(false, WinText.U("Write.PreviousRequestPending"));
        }
        try
        {
            return enable ? EnableWrite(disk, container) : DisableWrite(disk, container);
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

    private UnlockResponse EnableWrite(TrackedDisk disk, int container)
    {
        string key = disk.Key, description = disk.Description;
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
            // Yalnız geliştirme modu (testler): yazmaya açma süren bir istekle yarışan isteği sınamak için pencereyi genişletir
            if (_imageSources is not null && int.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_ENABLE_DELAY_MS"), out int delay))
                Thread.Sleep(delay);
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
        var tracked = new List<TrackedVolume>();
        var writers = new List<TrackedVolume>();
        try
        {
            IWritableBlockDevice raw = _imageSources is not null ? new FileImageWriter(key, grant) : new RawDiskWriter(int.Parse(key.AsSpan(13)), grant);
            // Yalnız geliştirme modu (testler): her yazma bariyeri hata verir → ilk commit düşer (WriteSession.Open bariyer kullanmaz)
            if (_imageSources is not null && Environment.GetEnvironmentVariable("APFS_DEV_COMMIT_FAIL") == "1")
                raw = new DevFailingFlushDevice(raw, grant);
            resources.Add(raw);
            var p = PartitionLocator.FindApfsPartitions(raw)[container];
            IWritableBlockDevice window = p.Offset == 0 && p.Length == raw.Length ? raw : new WritableOffsetDevice(raw, p.Offset, p.Length, grant);
            if (!ReferenceEquals(window, raw)) resources.Insert(0, window);
            var session = WriteSession.Open(window, grant);
            var thisWrite = write = new ContainerWrite(session, resources);
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
                    msg => OnFileError(description, name, volumeKey, msg), closeSession: false,
                    // Değişiklik kaybı (kaydedilemedi/silinemedi) birim seyreltmesine girmez: kullanıcı onu başka yerden göremez.
                    // Çok birimde commit bütün oturumundur (hangi sürücünün zamanlayıcısı yazdıysa): ileti diskle, birim adı olmadan
                    onChangeLost: msg => AddNotice("uyarı", many ? $"{description}: {msg}" : $"{description} \"{name}\": {msg}", throttleKey: msg),
                    // Commit düştü (oturum başına bir kez): oturum kaydedilmeden bırakılır, bölüm salt okunur bağlanır. Bu bir WinFsp
                    // geri çağrısı (Flush/zamanlayıcı, oturum kilidi altında): sürücü burada ayrılamaz → iş parçacığı havuzuna
                    onFaulted: msg =>
                    {
                        thisWrite.FaultMessage = msg;
                        QueueFault(key, container, thisWrite);
                    });
                tv.State = VolumeStatus.Mounted;
            }
            if (raw is RawDiskWriter rw) write.RemovalWatch = DeviceNotifications.WatchHandle(rw.Handle, a => OnHandleEvent(key, a));
            bool attached;
            lock (_gate)
            {
                attached = _disks.TryGetValue(key, out var current) && ReferenceEquals(current, disk);
                if (attached)
                {
                    disk.Writes[container] = write;
                    disk.WriteBlockReasons.Remove(container);
                    disk.Volumes.AddRange(tracked);
                }
            }
            if (!attached) throw new ApfsException(ApfsErrorCode.NotFound, WinText.T("Svc.DiskRemovedMeanwhile"));
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
                text = WinText.U("Notice.MountedWritableMany", description, string.Join(", ", writers.Select(t => t.WritableMount!.MountPoint)));
                AddNotice("bilgi", text, requested: true);
            }
            _reports?.Record(() => Event("write-enable", "ok", disk, container).WithDuration(watch.Elapsed));
            return new UnlockResponse(true, text);
        }
        catch (Exception e)
        {
            // Oturum yayımlanmadan bırakılır: önce bütün sürücülerin zamanlayıcısı durur (commit yok), sonra ayrılır; blok 0
            // yazılmaz, kimlik değişmez
            foreach (var t in writers) { try { t.WritableMount?.FileSystem.Dispose(); } catch { } }
            foreach (var t in writers) { try { t.WritableMount?.Dispose(); } catch { } }
            write?.RemovalWatch?.Dispose();
            write?.Session.Dispose();
            foreach (var r in resources) { try { r.Dispose(); } catch { } }
            string msg = WinText.U("Write.EnableFailed", e.Message);
            ServiceLog.Write($"{key}: {msg}");
            _reports?.Record(() => Event("write-enable", "fail", disk, container)
                .WithCode(e is ApfsException ae ? ae.Code.ToString() : e.GetType().Name).WithDuration(watch.Elapsed));
            bool stillAttached;
            lock (_gate) stillAttached = _disks.TryGetValue(key, out var current) && ReferenceEquals(current, disk);
            if (stillAttached) RemountReadOnly(disk, container, msg);
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
        bool saved = CloseWrite(disk, container, abandon: false, closeKind: "read-only"); // son commit + blok 0; kimlik kilit içinde tazelenir
        RemountReadOnly(disk, container, null);
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
    private bool CloseWrite(TrackedDisk disk, int container, bool abandon, string closeKind) =>
        CloseWrite(disk, container, abandon, closeKind, out _);

    /// <param name="closed">true: oturumu bu çağrı kapattı (kaydederek ya da terk ederek); false: başka bir yol kapatmıştı.</param>
    private bool CloseWrite(TrackedDisk disk, int container, bool abandon, string closeKind, out bool closed)
    {
        closed = false;
        lock (disk.IdentityLock)
        {
            ContainerWrite? write;
            List<TrackedVolume> volumes;
            lock (_gate)
            {
                if (!disk.Writes.Remove(container, out write)) return false; // başka bir yol (çıkarma, değişme) kapattı
                closed = true;
                volumes = disk.Volumes.Where(v => v.ContainerIndex == container).ToList();
                disk.Volumes.RemoveAll(v => v.ContainerIndex == container);
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
                    AddNotice("hata", WinText.U("Notice.CloseWriteFailed", disk.Description, e.Message));
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
            if (!abandon)
            {
                // Close blok 0'ı yazdı: kimlik yeni hâline tazelenir (okunamazsa eski kalır; sonraki tarama WinText.U("Release.Removed") der)
                try { disk.Fingerprint = PartitionLocator.Fingerprint(disk.Device); }
                catch (Exception e) when (e is ApfsException or ObjectDisposedException) { }
            }
            return !abandon && !closeFailed;
        }
    }

    /// <summary>Arızalı oturumun işini iş parçacığı havuzuna bırakır (WinFsp geri çağrısında sürücü ayırmak kilitlenir).</summary>
    private void QueueFault(string diskKey, int container, ContainerWrite write) =>
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { HandleFault(diskKey, container, write); }
            catch (Exception e) { ServiceLog.Write($"{diskKey}: {e.Message}"); }
        });

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
            CloseWrite(disk, container, abandon: true, closeKind: "failed", out bool closed);
            if (!closed) return; // bu arada çıkarma kapattı (kendi bildirimiyle)
            RemountReadOnly(disk, container, null, notifyFailure: false);
            ScanRequested?.Invoke();
            AddNotice("hata", WinText.U("Notice.CloseWriteFailed", disk.Description, msg));
        }
        finally
        {
            lock (_gate) disk.Switching.Remove(container);
        }
    }

    /// <summary>"Yeniden tara": daha önce ayrılmış (ama çıkarılmamış) diskler de yeniden bağlansın.</summary>
    public void ForgetReleased()
    {
        lock (_gate) _released.Clear();
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
                ThreadPool.QueueUserWorkItem(_ => Rescan());
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
                try { now = PartitionLocator.Fingerprint(d.Device); }
                catch (ApfsException e) when (e.Code is ApfsErrorCode.IoError or ApfsErrorCode.NotFound) { now = null; }
                catch (ObjectDisposedException) { now = null; } // bu arada başka bir istek diski bıraktı
                catch (ApfsException) { now = ""; }              // bölüm tablosu/kapsayıcı başı bozuk: içerik değişmiş
                catch (Exception e)
                {
                    ServiceLog.Write(WinText.T("Svc.DiskCheckFailed", d.Key, e.Message));
                    continue;
                }
                // Yazma oturumundaki disk doğrulanamıyorsa da kaydedilmeden bırakılır: biçimlendirme okuma hatası da
                // doğurabilir; gerçekten çekilmiş diske zaten yazılamaz.
                if (now is null)
                    ReleaseDisk(d.Key, WinText.U("Release.Removed"), rememberReleased: false, abandonWrites: true, expected: d);
                else if (now != d.Fingerprint)
                    ReleaseDisk(d.Key, ChangedReason, rememberReleased: false, abandonWrites: true, expected: d, contentChanged: true);
            }
        }
        // Güvenli kaldırılmış ama aslında hâlâ takılı olmayan diskler listeden düşsün (yeniden takılınca bağlansın)
        if (_imageSources is null)
        {
            List<string> released;
            lock (_gate) released = [.. _released];
            foreach (var key in released)
            {
                bool present;
                try { using var dev = new RawDiskDevice(key); present = true; }
                catch (ApfsException) { present = false; }
                if (!present) lock (_gate) _released.Remove(key);
            }
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
    private void ReleaseDisk(string key, string reason, bool rememberReleased, bool notify = true, bool abandonWrites = false,
        TrackedDisk? expected = null, bool contentChanged = false, string closeKind = "removed")
    {
        TrackedDisk? disk;
        lock (_gate)
        {
            if (!_disks.TryGetValue(key, out disk) || (expected is not null && !ReferenceEquals(disk, expected))) return;
            _disks.Remove(key);
            if (rememberReleased) _released.Add(key);
        }
        bool abandoned;
        List<TrackedVolume> mounted;
        List<int> writes;
        lock (disk.IdentityLock) // süren bir bölüm kapatması bitsin; aygıtlar onun altından çekilmesin
        {
            lock (_gate)
            {
                writes = [.. disk.Writes.Keys];
                mounted = [.. disk.Volumes];
            }
            abandoned = abandonWrites && writes.Count > 0;
            foreach (int p in writes) CloseWrite(disk, p, abandonWrites, contentChanged ? "changed" : closeKind);
            foreach (var tv in mounted)
            {
                try { tv.Mount?.Dispose(); } catch (Exception e) { ServiceLog.Write(WinText.T("Svc.UnmountError", key, e.Message)); }
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
        if (contentChanged) _reports?.Record(() => Event("disk-changed", abandoned ? "abandoned" : "ok", disk, null));
        if (reason.Length > 0) ServiceLog.Write($"{key}: {reason}.");
        if (!notify) return;
        // Salt okunur diskin kablosu çekildi: veri riski yok, kullanıcı biliyor (Windows da USB çekilince susar) → yalnız günlük.
        // Yazma oturumu vardıysa ya da içerik değiştiyse uyarı kalır. Karar türle verilir, çevrilmiş metinle değil.
        if (closeKind == "removed" && !contentChanged && writes.Count == 0) return;
        var names = mounted.Where(v => v.State == VolumeStatus.Mounted).Select(v => $"\"{v.Name}\"").ToList();
        string text = $"{disk.Description}{(names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")} {reason}.";
        if (abandoned)
            text += " " + (contentChanged
                ? WinText.U("Release.UnsavedNotWrittenChanged")
                : WinText.U("Release.UnsavedLost"));
        AddNotice(abandoned ? "uyarı" : "bilgi", text);
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

    public StatusResponse Status(long since)
    {
        lock (_gate)
        {
            var disks = _disks.Values.Select(d => new DiskStatus(d.Key, d.Key, d.Description,
                d.Volumes.OrderBy(v => v.ContainerIndex).ThenBy(v => v.Index)
                    .Select(v => new VolumeStatus(v.Index, v.Name, v.State, v.Mount?.MountPoint ?? v.WritableMount?.MountPoint, v.Hint, v.Message,
                        Writable: v.WritableMount is not null, Container: v.ContainerIndex,
                        WriteBlockReason: d.WriteBlockReasons.GetValueOrDefault(v.ContainerIndex),
                        Uuid: v.Uuid?.ToString())).ToList(),
                Writable: d.Writes.Count > 0,
                WriteBlockReason: d.WriteBlockReasons.OrderBy(r => r.Key).Select(r => r.Value).FirstOrDefault())).ToList();
            return new StatusResponse(disks, _notices.Where(n => n.Id > since).ToList(), _noticeId);
        }
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
            var now = DateTime.UtcNow;
            if (throttleKey is not null)
            {
                if (_recentNotices.TryGetValue(throttleKey, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
                _recentNotices[throttleKey] = now;
            }
            bool queue = true;
            if (queueKey is not null)
            {
                queue = !_recentQueued.TryGetValue(queueKey, out var last) || now - last >= queueWindow;
                if (queue) _recentQueued[queueKey] = now;
            }
            if (queue)
            {
                _notices.Add(new Notice(++_noticeId, level, text, DateTime.Now, requested));
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

    private void OnFileError(string description, string volumeName, string volumeKey, string msg) =>
        AddNotice("uyarı", $"{description} \"{volumeName}\": {msg}", throttleKey: msg, queueKey: volumeKey, queueWindow: FileErrorNoticeWindow);

    /// <summary>
    /// Disk başlığı (tepsi, menü, bildirimler). Boyut birim satırı ve Gezgin gibi ikili birimle, tek biçimleyiciden
    /// (<see cref="SizeText"/>; 1.7.1'e dek ondalıktı: 1 TB disk "1000,2 GB", birim satırı "931 GB").
    /// </summary>
    private static string DescribeDisk(string key, IBlockDevice device)
    {
        string size = SizeText.Format(device.Length);
        return key.StartsWith("PhysicalDrive", StringComparison.OrdinalIgnoreCase) ? WinText.U("Disk.PhysicalName", key[13..], size) : $"{Path.GetFileName(key)} ({size})";
    }

    public void Dispose()
    {
        List<string> keys;
        lock (_gate) keys = [.. _disks.Keys];
        foreach (var k in keys) ReleaseDisk(k, WinText.U("Release.ServiceStopped"), rememberReleased: false, closeKind: "service-stop");
    }
}
