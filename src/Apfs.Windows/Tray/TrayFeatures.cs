using Apfs.Windows.Reporting;

namespace Apfs.Windows.Tray;

/// <summary>
/// Tepsinin arka plan işleri: otomatik raporlar (<see cref="ReportClient"/>) ve güncelleme (1.8.0: denetim, setup'ı indirip
/// doğrulama, kendiliğinden ya da elle kurulum; <c>guncellemePlan</c> Parça 2). Raporlar arayüz iş parçacığının dışında; güncelleme
/// akışının durumu yalnız arayüz iş parçacığında değişir (ağ ve dosya işi arka planda). Ağ hataları sessizce yutulur, setup
/// reddi günlüğe yazılır. Onay penceresi cevaplanmadıysa hiçbir rapor gönderilmez. winuiPlan P1'den beri arayüz kütüphanesinden
/// bağımsız (zamanlayıcılar <see cref="UiTimer"/>, kutular <see cref="IUiHost"/>, dış dünya <see cref="UpdateServices"/>).
/// </summary>
public sealed class TrayFeatures : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly IUiHost _host;
    private readonly UpdateServices _services;
    private readonly Func<Task<int?>> _diskCount;
    private readonly UiTimer _startupTimer;
    private readonly UiTimer _periodicTimer;
    private ReportClient? _client;
    /// <summary>Güncelleme klasörü yerel (ağ yolunda değil): yalnız o zaman setup indirilir ve kurulur (1.9.1).</summary>
    private readonly bool _localUpdates;

    /// <param name="ui">Arayüz iş parçacığının bağlamı (zamanlayıcılar ve "güncellendi" balonu buraya).</param>
    /// <param name="host">"Kur" onayı (alt bağlantı).</param>
    /// <param name="local">Tepsinin kendi kuyruğu (Program'daki çökme kancası da buna yazar).</param>
    /// <param name="diskCount">Hizmete taze sorulan disk sayısı; yanıt yoksa null (kendiliğinden kurulum koşulu).</param>
    /// <param name="services">Dış dünya (testte sahte); null = gerçekleri.</param>
    /// <param name="time">Zaman (testte elle ilerletilen); null = sistem saati.</param>
    public TrayFeatures(SynchronizationContext ui, IUiHost host, ReportRecorder local, Func<Task<int?>> diskCount,
        UpdateServices? services = null, TimeProvider? time = null)
    {
        _ui = ui;
        _host = host;
        _services = services ?? UpdateServices.Default;
        _diskCount = diskCount;
        _localUpdates = !UpdatePolicy.IsNetworkPath(_services.UpdatesDirectory);
        Local = local;
        Sent = new SentLog(Path.Combine(local.Directory, "sent.jsonl"));
        _startupTimer = new UiTimer(ui, TimeSpan.FromSeconds(30), time);
        _periodicTimer = new UiTimer(ui, TimeSpan.FromHours(6), time);
        // async void (WinForms Tick'indeki gibi)
        _startupTimer.Tick += async () =>
        {
            _startupTimer.Stop();
            await CheckAsync(UpdateTrigger.Startup);
        };
        _periodicTimer.Tick += async () => await CheckAsync(UpdateTrigger.Timer);
    }

    public ReportRecorder Local { get; }
    public SentLog Sent { get; }

    /// <summary>Kullanıcıya balon (arayüz iş parçacığında): yeni sürüm (tıklayınca ana pencere), kuruluyor, güncellendi.</summary>
    public event Action<string>? Balloon;

    /// <summary>Alt çubuktaki bağlantının durumu değişti (yeni sürüm bilindi, indirme ilerledi, setup başladı/bitti, elle güncelleme başladı/bitti).</summary>
    public event Action? UpdateChanged;

    /// <summary>Bilinen daha yeni sürüm (bildirimi kapalıysa null): alt çubukta bağlantı olarak görünür.</summary>
    public UpdateInfo? AvailableUpdate
    {
        get
        {
            if (!UserSettings.UpdateNotify) return null;
            if (!Version.TryParse(UserSettings.UpdateNotifiedVersion, out var v) || UpdateChecker.Normalize(v) <= UpdateChecker.Normalize(_services.CurrentVersion())) return null;
            // Kayıttaki değer "1.9" gibi iki bileşenli olabilir (elle değiştirilmiş): metinler ToString(3) kullanır
            return Uri.TryCreate(UserSettings.UpdateUrl, UriKind.Absolute, out var u) && UpdateChecker.IsReleasePage(u) ? new UpdateInfo(UpdateChecker.Normalize(v), u) : null;
        }
    }

    /// <summary>Süren indirmenin yüzdesi (alt bağlantıda "İndiriliyor… %NN"); indirme yokken null.</summary>
    public int? DownloadPercent { get; private set; }

    /// <summary>Bu tepsinin başlattığı setup sürüyor.</summary>
    public bool SetupRunning { get; private set; }

    /// <summary>Alt bağlantının elle güncellemesi sürüyor (bağlantı pasif).</summary>
    public bool Updating { get; private set; }

    /// <param name="updated">Tepsi setup'ın sonunda <c>--updated</c> ile açıldı.</param>
    public void Start(bool updated)
    {
        _client = _services.NewReportClient(Local, Sent);
        _client?.Start(TimeSpan.FromSeconds(30));
        var current = _services.CurrentVersion();
        _ = Task.Run(() =>
        {
            // Beklenmeyen hata da sessizce kaybolmasın (1.9.1): günlüğe
            try { UpdateDownloader.CleanUp(_services.UpdatesDirectory, current); }
            catch (Exception e) { _services.Log(WinText.T("Update.CleanUpFailed", e.GetType().Name, e.Message)); }
        });
        if (updated) ReportUpdated(current);
        // Güncelleme: açılıştan ~30 sn sonra (saatte bir; bekleyen sürüm varsa her açılışta), sonra 6 saatte bir (günde bir)
        _startupTimer.Start();
        _periodicTimer.Start();
    }

    /// <summary>Raporlama seçimi değişti (onay penceresi ya da Ayarlar): hemen bir tur (hizmete bildirim + gönderim).</summary>
    public void ConsentChanged()
    {
        Local.SetEnabled(UserSettings.ReportsConsent == true);
        _client?.Nudge();
    }

    // ---------------- Raporlama onayı (1.4.0) ----------------

    private bool _askingConsent;

    /// <summary>
    /// İlk açılışta (ya da daha önce cevaplanmadıysa) raporlama onayı: aynı anda tek pencere; cevaplanmışsa sorulmaz. Pencere
    /// cevapsız kapatılırsa (X, Esc; <paramref name="ask"/> null döner) hiçbir şey kaydedilmez, bir sonraki açılışta yine sorulur;
    /// Evet/Hayır kaydedilir ve hemen bir tur (<see cref="ConsentChanged"/>). Açıkken tepsi yeniden başlamaz.
    /// </summary>
    /// <param name="ask">Onay penceresini gösterir: true evet, false hayır, null cevapsız.</param>
    public async Task AskConsentAsync(Func<Task<bool?>> ask)
    {
        if (_askingConsent || UserSettings.ReportsConsent is not null) return;
        _askingConsent = true;
        try
        {
            if (await AppController.ModalAsync(ask) is not { } answer) return; // kapatıldı: bir sonraki açılışta yine sorulur
            UserSettings.ReportsConsent = answer;
            ConsentChanged();
        }
        finally { _askingConsent = false; }
    }

    // ---------------- Güncelleme (1.8.0) ----------------

    private int _checking; // süren denetim sayısı (elle denetim, süren otomatik denetimi beklemez)
    private UpdateInfo? _latest;          // son denetimin bulduğu sürüm (setup bilgisiyle)
    private DownloadedSetup? _ready;      // indirilip doğrulanmış setup (yazmaya kapalı tutamak)
    private Task<DownloadedSetup?>? _download;
    private string? _announcedInstall;    // "kuruluyor" balonu gösterilen sürüm (sürüm başına bir kez)

    private static bool AutoInstall => UserSettings.UpdateNotify && UserSettings.UpdateAutoInstall;

    /// <summary>Ana pencere açıldı: son denetimden en az bir saat geçtiyse denetle.</summary>
    public void MainWindowShown() => _ = CheckAsync(UpdateTrigger.MainWindow);

    /// <summary>Ana pencere kapatıldı (tepsiye gizlendi): koşullar tutarsa kendiliğinden kur.</summary>
    public void MainWindowClosed() => _ = TryAutoInstallAsync();

    /// <summary>Elle denetim (Ayarlar › Şimdi denetle, alt bağlantı): sınırsız; bulunan sürüm her zamanki gibi işlenir.</summary>
    public Task<UpdateCheckResult> CheckNowAsync() => CheckAsync(UpdateTrigger.Manual);

    private async Task<UpdateCheckResult> CheckAsync(UpdateTrigger trigger)
    {
        var none = new UpdateCheckResult(false, null);
        var current = _services.CurrentVersion();
        // Bilinen yeni sürüm bekliyorsa açılış her seferinde denetler (kurulum için setup bilgisi gerekir); o sürüm beklemedeyse
        // (reddedildi ya da doğrulanamadı, 24 sa) gerekmez
        bool newerKnown = Version.TryParse(UserSettings.UpdateNotifiedVersion, out var known) && UpdateChecker.Normalize(known) > UpdateChecker.Normalize(current)
            && !UpdatePolicy.DeclinedRecently(known, UserSettings.UpdateDeclinedVersion, UserSettings.UpdateDeclinedUtc, DateTime.UtcNow);
        if ((_checking > 0 && trigger != UpdateTrigger.Manual) || !UpdatePolicy.ShouldCheck(trigger, UserSettings.UpdateNotify, UserSettings.UpdateLastCheckUtc, DateTime.UtcNow, newerKnown && AutoInstall))
            return none;
        _checking++;
        try
        {
            var result = await Task.Run(() => _services.QueryUpdate(current));
            UserSettings.UpdateLastCheckUtc = DateTime.UtcNow;
            if (result.Update is not { } info)
            {
                if (result.Answered) ForgetWithdrawn(current);
                return result;
            }
            _latest = info;
            bool isAdmin = _services.IsAdministrator();
            bool hasSetup = info.Setup is not null && _localUpdates; // ağ yolundaki klasöre indirilmez: yalnız haber
            if (UpdateChecker.ShouldNotify(info, UserSettings.UpdateNotifiedVersion))
            {
                UserSettings.UpdateNotifiedVersion = info.Version.ToString(3);
                UserSettings.UpdateUrl = info.ReleasePage.ToString();
                if (UserSettings.UpdateNotify && UpdatePolicy.ShouldAnnounceFound(AutoInstall, hasSetup, isAdmin))
                    // Kullanıcı Ayarlar › Güncellemeler'de "haber ver"i açıkça istedi: "yalnız önemli"de de gösterilir (yoksa iki ayar çakışırdı)
                    Announce(WinText.U("Update.Available", info.Version.ToString(3), Branding.ProductName), requested: true);
            }
            UpdateChanged?.Invoke();
            bool held = UpdatePolicy.DeclinedRecently(info.Version, UserSettings.UpdateDeclinedVersion, UserSettings.UpdateDeclinedUtc, DateTime.UtcNow);
            if (UpdatePolicy.ShouldDownload(AutoInstall, hasSetup, isAdmin) && !held)
            {
                // Elle denetim indirmeyi beklemez (Ayarlar'daki düğme ve alt bağlantı hemen sonuç alır; indirme arkada sürer)
                if (trigger == UpdateTrigger.Manual) _ = DownloadAsync(info);
                else if (await DownloadAsync(info) is { } got)
                {
                    if (trigger == UpdateTrigger.Startup) await TryAutoInstallAsync();
                    // Zamanlayıcı kurmaz (kurulum anı açılış ve ana pencere kapanışı), ama haber verir (1.9.1): yoksa tepsisi günlerce
                    // açık kalan yönetici indirilen sürümü hiç duymuyordu
                    else if (trigger == UpdateTrigger.Timer) AnnounceTimerReady(got);
                }
            }
            return result;
        }
        catch (Exception) { return none; } // güncelleme tepsiyi asla düşürmez
        finally { _checking--; }
    }

    /// <summary>
    /// Sunucu "güncel" dedi: bildirilmiş daha yeni sürüm geri çekilmiş demektir. Alt bağlantı ve kayıt unutulur, elde doğrulanmış
    /// setup varsa bırakılır (geri çekilen sürüm kendiliğinden kurulmasın). Sunucu yanıt vermediyse çağrılmaz. Elle "Kur" ya da setup
    /// sürerken elde tutulan setup'a dokunulmaz (1.9.1): Kur onu başlatacak; bırakılırsa dosya doğrulandıktan sonra değiştirilebilirdi.
    /// </summary>
    private void ForgetWithdrawn(Version current)
    {
        _latest = null;
        if (_ready is { } r && !UpdateBusy)
        {
            _ready = null;
            r.Dispose();
        }
        if (Version.TryParse(UserSettings.UpdateNotifiedVersion, out var n) && UpdateChecker.Normalize(n) > UpdateChecker.Normalize(current))
        {
            UserSettings.UpdateNotifiedVersion = null;
            UserSettings.UpdateUrl = null;
            UpdateChanged?.Invoke();
        }
    }

    /// <summary>Elle "Kur" ya da bu tepsinin başlattığı setup sürüyor: elde tutulan setup'ı yalnız Kur'un kendisi değiştirebilir.</summary>
    private bool UpdateBusy => Updating || SetupRunning;

    /// <summary>
    /// Setup'ı indirir ve doğrular (önceden indirilmiş ve doğrulananı kullanır); aynı anda tek indirme. Başarısızlık günlüğe
    /// yazılır, null döner. Elle "Kur" ya da setup sürerken başka bir sürüm için elde tutulanı bırakmaz, null döner (1.9.1).
    /// </summary>
    public Task<DownloadedSetup?> DownloadAsync(UpdateInfo info) => DownloadAsync(info, owner: false);

    /// <param name="owner">Çağıran elle "Kur" akışı: elde tutulanı değiştirebilir.</param>
    private async Task<DownloadedSetup?> DownloadAsync(UpdateInfo info, bool owner)
    {
        if (info.Setup is null || !_localUpdates) return null;
        // Süren indirme bitene dek beklenir; aynı sürümü getirdiyse o kullanılır. Döngü: bekleyen ikinci çağıran, öncekinin başlattığı
        // yeni indirmeyi de bekler (önceden ikisi birden yeni indirme başlatabiliyordu)
        while (true)
        {
            if (_ready is { } r && UpdateChecker.Normalize(r.Version) == UpdateChecker.Normalize(info.Version) && r.IsOpen) return r;
            if (_download is not { } running) break;
            var got = await running;
            if (got is not null && UpdateChecker.Normalize(got.Version) == UpdateChecker.Normalize(info.Version) && got.IsOpen) return got;
        }
        if (UpdateBusy && !owner) return null;
        _ready?.Dispose();
        _ready = null;
        var report = new Progress<int>(p => { if (_download is not null) { DownloadPercent = p; UpdateChanged?.Invoke(); } });
        DownloadPercent = 0;
        UpdateChanged?.Invoke();
        _download = Task.Run(async () =>
        {
            using var downloader = _services.NewDownloader(_services.UpdatesDirectory);
            try { return (DownloadedSetup?)await downloader.DownloadAsync(info, report); }
            catch (UpdateDownloadException e)
            {
                _services.Log(e.Message);
                if (e.Unverifiable)
                {
                    // Dosya Release'tekiyle tutmuyor (yanlış özet, boyut ya da sürüm): her denetimde ~40 MB yeniden indirilmesin
                    Decline(info.Version);
                    _services.Log(WinText.T("Update.Held", info.Version.ToString(3)));
                }
                return null;
            }
        });
        try
        {
            _ready = await _download;
            return _ready;
        }
        finally
        {
            _download = null;
            DownloadPercent = null;
            UpdateChanged?.Invoke();
        }
    }

    /// <summary>Kendiliğinden kurulum koşulları tutuyorsa (<see cref="UpdatePolicy.ShouldAutoInstall"/>) setup'ı başlatır.</summary>
    private async Task TryAutoInstallAsync()
    {
        // Elle "Kur" sürüyorsa kurulumu o sahiplenir (1.9.1): çıkardığı diskleri ret ya da hata olursa yalnız o geri bağlar
        if (_ready is not { } ready || SetupRunning || Updating) return;
        // Yalnız hâlâ bildirilen sürüm (sunucu sonradan "güncel" dediyse geri çekilmiştir)
        if (!Version.TryParse(UserSettings.UpdateNotifiedVersion, out var notified) || UpdateChecker.Normalize(notified) != UpdateChecker.Normalize(ready.Version)) return;
        int? disks = await _diskCount();
        if (Updating) return; // disk sorgusu sürerken elle Kur başladı
        var c = new UpdateInstallConditions(
            AutoInstall, _ready == ready, disks, _services.IsAdministrator(),
            UpdatePolicy.DeclinedRecently(ready.Version, UserSettings.UpdateDeclinedVersion, UserSettings.UpdateDeclinedUtc, DateTime.UtcNow),
            SetupRunning || _services.AnySetupRunning());
        string version = ready.Version.ToString(3);
        if (UpdatePolicy.ShouldAnnounceReady(c)) AnnounceReady(version);
        if (!UpdatePolicy.ShouldAutoInstall(c)) return;
        if (_announcedInstall != version)
        {
            _announcedInstall = version;
            // Ayarlar › Bildirimler'den BAĞIMSIZ, "tamamen kapat"ta da (kullanıcı 2026-11-22 "Kalsın"): bildirimden çok hemen açılacak
            // Windows izin penceresinin açıklaması; habersiz izin penceresi şüphe uyandırır
            Balloon?.Invoke(WinText.U("Update.Installing", Branding.ProductName, version));
        }
        StartSetup(ready);
    }

    /// <summary>
    /// "Hazır" haberi, sürüm başına bir kez (HKCU <c>UpdateReady</c>). Yalnız haber (kullanıcı kararı 2026-11-23): kurulum anı
    /// değişmez; tık ana pencereyi açar (alt bağlantı ve Ayarlar'da Kur). "Yeni sürüm çıkınca haber ver"in sonucu → "yalnız önemli"de de.
    /// </summary>
    private void AnnounceReady(string version)
    {
        if (UserSettings.UpdateReadyAnnounced == version) return;
        UserSettings.UpdateReadyAnnounced = version;
        Announce(WinText.U("Update.Ready", Branding.ProductName, version), requested: true);
    }

    /// <summary>
    /// 6 saatlik denetimin indirdiği sürüm (1.9.1): kurulmaz, kendiliğinden kurulumun öbür koşulları tutuyorsa (disk sayısı hariç:
    /// şimdi kurulmayacak) "hazır" haberi verilir. Yalnız hâlâ bildirilen sürüm, Kur ya da setup sürmüyorken.
    /// </summary>
    private void AnnounceTimerReady(DownloadedSetup ready)
    {
        if (UpdateBusy || _ready != ready) return;
        if (!Version.TryParse(UserSettings.UpdateNotifiedVersion, out var notified) || UpdateChecker.Normalize(notified) != UpdateChecker.Normalize(ready.Version)) return;
        var c = new UpdateInstallConditions(AutoInstall, true, null, _services.IsAdministrator(),
            UpdatePolicy.DeclinedRecently(ready.Version, UserSettings.UpdateDeclinedVersion, UserSettings.UpdateDeclinedUtc, DateTime.UtcNow),
            _services.AnySetupRunning());
        if (UpdatePolicy.ShouldAnnounceTimerReady(c)) AnnounceReady(ready.Version.ToString(3));
    }

    /// <summary>
    /// Doğrulanmış setup'ı sessiz kurulum kipinde başlatır (<c>runas</c> yok: Inno izni kendisi ister) ve izler. Setup tepsiyi
    /// kapatır; tepsi hâlâ yaşarken setup sıfırdan farklı kodla biterse (izin verilmedi, hata) o sürüm 24 saat kendiliğinden
    /// önerilmez ve <paramref name="afterFailure"/> çalışır (elle güncellemenin çıkardığı diskler yeniden bağlansın). Aynı anda tek
    /// setup (başka oturumdaki dahil). Başlatılamazsa false (<paramref name="afterFailure"/> çağıranın işi).
    /// </summary>
    public bool StartSetup(DownloadedSetup setup, Func<Task>? afterFailure = null)
    {
        if (SetupRunning || _services.AnySetupRunning()) return false;
        string version = setup.Version.ToString(3);
        System.Diagnostics.Process? process;
        try
        {
            process = _services.StartProcess(setup.Path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /HASHIDOUPDATE=1");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _services.Log(WinText.T("Update.StartFailed", version, e.Message));
            Decline(setup.Version);
            return false;
        }
        if (process is null) return false;
        _services.Log(WinText.T("Update.Started", version));
        SetupRunning = true;
        UpdateChanged?.Invoke();
        _ = WatchSetupAsync(process, setup, afterFailure);
        return true;
    }

    private async Task WatchSetupAsync(System.Diagnostics.Process process, DownloadedSetup setup, Func<Task>? afterFailure)
    {
        bool failed = false;
        try
        {
            await process.WaitForExitAsync();
            int code = process.ExitCode;
            if (code != 0)
            {
                // Başarılı kurulumda tepsi buraya gelmeden kapatılır (PrepareToInstall); yaşıyorsak kurulum olmadı
                _services.Log(WinText.T("Update.Declined", setup.Version.ToString(3), code));
                Decline(setup.Version);
                failed = true;
            }
            // Elle güncellemenin çıkardığı diskler SetupRunning düşmeden geri bağlanır (1.9.1): bekleyen dil/tasarım yeniden
            // başlaması "setup bitti" olayıyla tetiklenip geri bağlamayı kesmesin
            if (failed && afterFailure is not null) await afterFailure();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally
        {
            process.Dispose();
            SetupRunning = false;
            if (_ready == setup) _ready = null;
            setup.Dispose(); // tutamak bırakılır; dosya klasörde kalır, yeniden indirmeden doğrulanıp kullanılabilir
            UpdateChanged?.Invoke();
        }
    }

    /// <summary>Sürüm 24 saat kendiliğinden indirilmez/kurulmaz: setup reddedildi, uygulanamadı ya da doğrulanamadı.</summary>
    private static void Decline(Version version)
    {
        UserSettings.UpdateDeclinedVersion = version.ToString(3);
        UserSettings.UpdateDeclinedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// <c>--updated</c>: kurulu sürüm indirilen sürüme ulaştıysa tek balon "güncellendi"; ulaşmadıysa (kurulum yarım kaldı) o sürüm
    /// 24 saat kendiliğinden önerilmez (yoksa her açılışta yeniden denenip izin isterdi).
    /// </summary>
    private void ReportUpdated(Version current)
    {
        if (Version.TryParse(UserSettings.UpdateNotifiedVersion, out var notified) && UpdateChecker.Normalize(notified) is var target
            && UpdateChecker.Normalize(current) < target)
        {
            _services.Log(WinText.T("Update.NotApplied", target.ToString(3), current.ToString(3)));
            Decline(target);
            return;
        }
        _ui.Post(_ => Announce(WinText.U("Update.Updated", Branding.ProductName, current.ToString(3)), requested: true), null);
    }

    /// <summary>
    /// Güncelleme balonu, Ayarlar › Bildirimler'e göre (<see cref="TrayModel.ShowsNotice"/>): ikisi de kullanıcının istediği şey ("yeni
    /// sürüm var" yalnız "Yeni sürüm çıkınca haber ver" açıkken gelir; "kuruldu" açtığı kendiliğinden kurulumun sonucu) → "yalnız
    /// önemli"de gösterilir, "tamamen kapat"ta gösterilmez (ana penceredeki bağlantı yine sunar). "Kuruluyor" buradan GEÇMEZ (yukarıda).
    /// </summary>
    private void Announce(string text, bool requested)
    {
        if (TrayModel.ShowsNotice(UserSettings.Notifications, NoticeLevel.Info, requested)) Balloon?.Invoke(text);
    }

    // ---------------- Alt bağlantı: şimdi güncelle (1.8.0; winuiPlan P1'de MainForm'dan taşındı) ----------------

    /// <summary>
    /// Alt bağlantı ve Ayarlar › Güncellemeler'deki "Kur" (1.8.0; 1.9.0'dan beri sıra indir → çıkar): önce TAZE denetim (yeniden
    /// açılan tepside setup bilgisi bilinmez) → doğrulanabilir setup varsa onay → hazır değilse indirilir (bağlantıda yüzde; diskler bu
    /// sırada kullanılabilir kalır) → her takılı disk "Güvenle çıkar" akışıyla çıkarılır (biri çıkarılamaz ya da vazgeçilirse
    /// güncelleme iptal) → hizmete TAZE sorulur: arada takılan disk de çıkarılır, yine kalırsa iptal → setup başlar (izni Windows
    /// sorar). İptalde, setup başlamazsa ya da izin verilmezse bu akışın çıkardığı diskler yeniden taranıp bağlanır. Setup bilgisi
    /// yoksa, denetlenemezse ya da indirilemezse Release sayfası açılır. Sürerken ya da setup çalışırken yeni tıklama yok sayılır.
    /// </summary>
    /// <param name="owner">Bağlantının penceresi (onay ve çıkarma kutularının sahibi).</param>
    public async Task UpdateNowAsync(AppController app, object? owner)
    {
        if (Updating || SetupRunning) return;
        Updating = true;
        UpdateChanged?.Invoke();
        try
        {
            var known = AvailableUpdate;
            var result = await CheckNowAsync();
            var info = result.Update;
            if (info?.Setup is null || !_localUpdates) // setup bilgisi yok ya da indirme klasörü ağ yolunda: Release sayfası
            {
                if (result.Answered && info is null) return; // sunucu "güncel" dedi: sürüm geri çekilmiş, bağlantı kalktı (sayfası yok)
                if ((info?.ReleasePage ?? known?.ReleasePage) is { } page) _services.OpenPage(page);
                return;
            }
            string version = info.Version.ToString(3);
            if (!await AppController.ModalAsync(() => _host.ConfirmAsync(owner, WinText.U("Update.ConfirmHeading", Branding.ProductName, version),
                WinText.U("Update.ConfirmText"), WinText.U("Action.Install"), ConfirmTone.Caution))) return;
            var setup = await DownloadAsync(info, owner: true);
            if (setup is null)
            {
                _services.OpenPage(info.ReleasePage);
                return;
            }
            bool ejected = false;
            for (int round = 0; ; round++)
            {
                foreach (var d in app.Status?.Disks.ToList() ?? [])
                {
                    if (!await app.EjectAsync(d, owner))
                    {
                        if (ejected) await app.RescanAsync();
                        return;
                    }
                    ejected = true;
                }
                // Kurulumdan hemen önce: indirme ve çıkarma sürerken takılan disk (yanıt yoksa hizmet kapalı: setup onu da kurar)
                if (await _diskCount() is not > 0) break;
                if (round == 1)
                {
                    if (ejected) await app.RescanAsync();
                    return;
                }
                await app.PollAsync();
            }
            Func<Task>? restore = ejected ? app.RescanAsync : null;
            // Tutamak kapandıysa (dosya doğrulandıktan sonra değişmiş olabilir) yeniden doğrulanır (1.9.1)
            if (!setup.IsOpen)
            {
                if (await DownloadAsync(info, owner: true) is not { } fresh)
                {
                    if (restore is not null) await restore();
                    _services.OpenPage(info.ReleasePage);
                    return;
                }
                setup = fresh;
            }
            if (!StartSetup(setup, restore))
            {
                if (SetupRunning) return; // arada kendiliğinden kurulum başladı: diskler onunla kalır, sayfa açılmaz
                if (restore is not null) await restore();
                _services.OpenPage(info.ReleasePage);
            }
        }
        finally
        {
            Updating = false;
            UpdateChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _startupTimer.Dispose();
        _periodicTimer.Dispose();
        _ready?.Dispose();
        _client?.Dispose();
    }
}
