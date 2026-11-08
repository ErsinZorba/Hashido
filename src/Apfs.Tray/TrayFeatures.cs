using Apfs.Windows;
using Apfs.Windows.Reporting;

namespace Apfs.Tray;

/// <summary>
/// Tepsinin arka plan işleri: otomatik raporlar (<see cref="ReportClient"/>) ve günde bir "yeni sürüm var" denetimi.
/// Hepsi arayüz iş parçacığının dışında; ağ hataları sessizce yutulur. Onay penceresi cevaplanmadıysa hiçbir şey gönderilmez.
/// </summary>
internal sealed class TrayFeatures : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _updateTimer;
    private ReportClient? _client;

    /// <param name="local">Tepsinin kendi kuyruğu (Program'daki çökme kancası da buna yazar).</param>
    public TrayFeatures(SynchronizationContext ui, ReportRecorder local)
    {
        _ui = ui;
        Local = local;
        Sent = new SentLog(Path.Combine(local.Directory, "sent.jsonl"));
        _updateTimer = new System.Threading.Timer(_ => _ = CheckForUpdateAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ReportRecorder Local { get; }
    public SentLog Sent { get; }

    /// <summary>Yeni sürüm bulunup kullanıcıya ilk kez bildirilecekse (arayüz iş parçacığında): metin, Release sayfası.</summary>
    public event Action<string, Uri>? UpdateFound;

    /// <summary>Alt çubuktaki bağlantının durumu değişti.</summary>
    public event Action? UpdateChanged;

    /// <summary>Bilinen daha yeni sürüm (bildirimi kapalıysa null): alt çubukta bağlantı olarak görünür.</summary>
    public UpdateInfo? AvailableUpdate
    {
        get
        {
            if (!UserSettings.UpdateNotify) return null;
            if (!Version.TryParse(UserSettings.UpdateNotifiedVersion, out var v) || v <= UpdateChecker.CurrentVersion()) return null;
            return Uri.TryCreate(UserSettings.UpdateUrl, UriKind.Absolute, out var u) && u.Host == "github.com" ? new UpdateInfo(v, u) : null;
        }
    }

    public void Start()
    {
        _client = new ReportClient(() => UserSettings.ReportsConsent, Local, new ServiceReportSource(),
            new ReportUploader(ReportClient.Endpoint, Sent));
        _client.Start(TimeSpan.FromSeconds(30));
        // Güncelleme: açılıştan 1 dk sonra, sonra 6 saatte bir bakılır; gerçek istek günde en çok bir kez (ShouldCheck)
        _updateTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
    }

    /// <summary>Raporlama seçimi değişti (onay penceresi ya da Ayarlar): hemen bir tur (hizmete bildirim + gönderim).</summary>
    public void ConsentChanged()
    {
        Local.SetEnabled(UserSettings.ReportsConsent == true);
        _client?.Nudge();
    }

    private int _checking;

    private async Task CheckForUpdateAsync()
    {
        if (!UserSettings.UpdateNotify || Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            if (!UpdateChecker.ShouldCheck(UserSettings.UpdateLastCheckUtc, DateTime.UtcNow)) return;
            using var checker = new UpdateChecker(UpdateChecker.Api);
            var info = await checker.CheckAsync(UpdateChecker.CurrentVersion());
            UserSettings.UpdateLastCheckUtc = DateTime.UtcNow;
            if (info is null || !UpdateChecker.ShouldNotify(info, UserSettings.UpdateNotifiedVersion)) return;
            UserSettings.UpdateNotifiedVersion = info.Version.ToString(3);
            UserSettings.UpdateUrl = info.ReleasePage.ToString();
            _ui.Post(_ =>
            {
                UpdateFound?.Invoke(WinText.U("Update.Available", info.Version.ToString(3)), info.ReleasePage);
                UpdateChanged?.Invoke();
            }, null);
        }
        catch (Exception) { } // güncelleme denetimi tepsiyi asla düşürmez
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    public void Dispose()
    {
        _updateTimer.Dispose();
        _client?.Dispose();
    }
}
