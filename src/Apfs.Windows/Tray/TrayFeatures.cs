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
            return Uri.TryCreate(UserSettings.UpdateUrl, UriKind.Absolute, out var u) && u.Host == "github.com" ? new UpdateInfo(v, u) : null;
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
        _ = Task.Run(() => UpdateDownloader.CleanUp(_services.UpdatesDirectory, current));
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
        bool newerKnown = Version.TryParse(UserSettings.UpdateNotifiedVersion, out var known) && UpdateChecker.Normalize(known) > UpdateChecker.Normalize(current);
        if ((_checking > 0 && trigger != UpdateTrigger.Manual) || !UpdatePolicy.ShouldCheck(trigger, UserSettings.UpdateNotify, UserSettings.UpdateLastCheckUtc, DateTime.UtcNow, newerKnown && AutoInstall))
            return none;
        _checking++;
        try
        {
            var result = await Task.Run(() => _services.QueryUpdate(current));
            UserSettings.UpdateLastCheckUtc = DateTime.UtcNow;
            if (result.Update is not { } info) return result;
            _latest = info;
            bool isAdmin = _services.IsAdministrator();
            if (UpdateChecker.ShouldNotify(info, UserSettings.UpdateNotifiedVersion))
            {
                UserSettings.UpdateNotifiedVersion = info.Version.ToString(3);
                UserSettings.UpdateUrl = info.ReleasePage.ToString();
                if (UserSettings.UpdateNotify && UpdatePolicy.ShouldAnnounceFound(AutoInstall, info.Setup is not null, isAdmin))
                    // Kullanıcı Ayarlar › Güncellemeler'de "haber ver"i açıkça istedi: "yalnız önemli"de de gösterilir (yoksa iki ayar çakışırdı)
                    Announce(WinText.U("Update.Available", info.Version.ToString(3), Branding.ProductName), requested: true);
            }
            UpdateChanged?.Invoke();
            if (UpdatePolicy.ShouldDownload(AutoInstall, info.Setup is not null, isAdmin))
            {
                // Elle denetim indirmeyi beklemez (Ayarlar'daki düğme ve alt bağlantı hemen sonuç alır; indirme arkada sürer)
                if (trigger == UpdateTrigger.Manual) _ = DownloadAsync(info);
                else if (await DownloadAsync(info) is not null && trigger == UpdateTrigger.Startup) await TryAutoInstallAsync();
            }
            return result;
        }
        catch (Exception) { return none; } // güncelleme tepsiyi asla düşürmez
        finally { _checking--; }
    }

    /// <summary>
    /// Setup'ı indirir ve doğrular (önceden indirilmiş ve doğrulananı kullanır); aynı anda tek indirme. Başarısızlık günlüğe
    /// yazılır, null döner.
    /// </summary>
    public async Task<DownloadedSetup?> DownloadAsync(UpdateInfo info)
    {
        if (info.Setup is null) return null;
        if (_ready is { } r && UpdateChecker.Normalize(r.Version) == UpdateChecker.Normalize(info.Version)) return r;
        if (_download is { } running)
        {
            var got = await running;
            if (got is not null && UpdateChecker.Normalize(got.Version) == UpdateChecker.Normalize(info.Version)) return got;
        }
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
        if (_ready is not { } ready || SetupRunning) return;
        int? disks = await _diskCount();
        var c = new UpdateInstallConditions(
            AutoInstall, _ready == ready, disks, _services.IsAdministrator(),
            UpdatePolicy.DeclinedRecently(ready.Version, UserSettings.UpdateDeclinedVersion, UserSettings.UpdateDeclinedUtc, DateTime.UtcNow),
            SetupRunning || _services.AnySetupRunning());
        if (!UpdatePolicy.ShouldAutoInstall(c)) return;
        string version = ready.Version.ToString(3);
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
    /// Doğrulanmış setup'ı sessiz kurulum kipinde başlatır (<c>runas</c> yok: Inno izni kendisi ister) ve izler. Setup tepsiyi
    /// kapatır; tepsi hâlâ yaşarken setup sıfırdan farklı kodla biterse (izin verilmedi, hata) o sürüm 24 saat kendiliğinden
    /// önerilmez. Aynı anda tek setup (başka oturumdaki dahil). Başlatılamazsa false.
    /// </summary>
    public bool StartSetup(DownloadedSetup setup)
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
        _ = WatchSetupAsync(process, setup);
        return true;
    }

    private async Task WatchSetupAsync(System.Diagnostics.Process process, DownloadedSetup setup)
    {
        try
        {
            await process.WaitForExitAsync();
            int code = process.ExitCode;
            if (code != 0)
            {
                // Başarılı kurulumda tepsi buraya gelmeden kapatılır (PrepareToInstall); yaşıyorsak kurulum olmadı
                _services.Log(WinText.T("Update.Declined", setup.Version.ToString(3), code));
                Decline(setup.Version);
            }
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
        if (Version.TryParse(UserSettings.UpdateNotifiedVersion, out var target) && UpdateChecker.Normalize(current) < UpdateChecker.Normalize(target))
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
    /// Alt bağlantı (1.8.0): önce TAZE denetim (yeniden açılan tepside setup bilgisi bilinmez) → doğrulanabilir setup varsa onay →
    /// her takılı disk "Güvenle çıkar" akışıyla çıkarılır (biri çıkarılamaz ya da vazgeçilirse güncelleme iptal) → hazır değilse
    /// indirilir (bağlantıda yüzde) → setup başlar (izni Windows sorar). Setup bilgisi yoksa, denetlenemezse ya da indirilemezse
    /// Release sayfası açılır (önceki davranış). Sürerken ya da setup çalışırken yeni tıklama yok sayılır.
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
            if (info?.Setup is null)
            {
                if ((info?.ReleasePage ?? known?.ReleasePage) is { } page) _services.OpenPage(page);
                return;
            }
            string version = info.Version.ToString(3);
            if (!await AppController.ModalAsync(() => _host.ConfirmAsync(owner, WinText.U("Update.ConfirmHeading", Branding.ProductName, version),
                WinText.U("Update.ConfirmText"), WinText.U("Action.Install"), ConfirmTone.Caution))) return;
            foreach (var d in app.Status?.Disks.ToList() ?? [])
                if (!await app.EjectAsync(d, owner)) return;
            var setup = await DownloadAsync(info);
            if (setup is null || !StartSetup(setup)) _services.OpenPage(info.ReleasePage);
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
