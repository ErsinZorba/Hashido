using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using Apfs.Windows.Reporting;
using Microsoft.Win32;

namespace Apfs.Tray;

/// <summary>
/// Bildirim alanı uygulaması: tepsi simgesi (sol tık: küçük panel, sağ tık: menü), ana pencere ve hizmet bildirimleri.
/// Tüm iş hizmette yapılır; tepsi yalnızca arayüzdür.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly AppController _app = new();
    private readonly TrayFlyout _flyout;
    private readonly WindowsFormsSynchronizationContext _ui = new();
    private readonly RegisteredWaitHandle _showWait;
    private readonly TrayFeatures _features;
    private MainForm? _main;
    private Uri? _balloonLink; // son balon bir güncelleme bildirimiyse tıklayınca açılacak sayfa

    /// <param name="showEvent">Başka bir örnek (Başlat menüsü kısayolu) "pencereyi göster" dediğinde işaretlenir.</param>
    /// <param name="localReports">Tepsinin kendi rapor kuyruğu (çökme kancası da buna yazar).</param>
    public TrayApp(EventWaitHandle showEvent, bool showMain, ReportRecorder localReports)
    {
        _icon = new NotifyIcon { Icon = Theme.AppIcon(SystemInformation.SmallIconSize.Width), Text = Branding.ProductName, Visible = true, ContextMenuStrip = new ContextMenuStrip { Renderer = Theme.MenuRenderer } };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _flyout!.Toggle(); };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonLink is { } link) OpenLink(link);
            else ShowMain();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonLink = null;

        _flyout = new TrayFlyout(_app, ShowMain);
        _app.Changed += UpdateTooltip;
        _app.Notify += (text, level) => { _balloonLink = null; _icon.ShowBalloonTip(5000, Branding.ProductName, text, level); };
        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => _ui.Post(_ => ShowMain(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        _features = new TrayFeatures(_ui, localReports);
        _features.UpdateFound += (text, page) =>
        {
            _balloonLink = page;
            _icon.ShowBalloonTip(8000, Branding.ProductName, text, ToolTipIcon.Info);
        };
        // Windows teması değişince (açık ↔ koyu) yeniden başla; renkler süreç başında seçilir
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // Kuyruğa atılarak: Changed'in öbür aboneleri (ana pencere, panel) çalışırken süreç kapanıp onları elden çıkarmasın
        _app.Changed += () => _ui.Post(_ => TryThemeRestart(), null);
        _themeRetry.Tick += (_, _) => TryThemeRestart();
        _app.Start();
        _features.Start();
        if (showMain) ShowMain();
        // İlk açılışta (ya da daha önce cevaplanmadıysa) bir kez raporlama onayı sorulur; cevap gelene kadar hiçbir şey toplanmaz
        if (UserSettings.ReportsConsent is null) _ui.Post(_ => AskConsent(), null);
    }

    private static void OpenLink(Uri page) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.ToString()) { UseShellExecute = true });

    private bool _askingConsent;

    private void AskConsent()
    {
        if (_askingConsent || UserSettings.ReportsConsent is not null) return;
        _askingConsent = true;
        try
        {
            using var form = new ConsentForm();
            var owner = _main is { Visible: true } m ? m : null;
            AppController.Modal(() => owner is null ? form.ShowDialog() : form.ShowDialog(owner));
            if (form.Answer is not { } answer) return; // kapatıldı: bir sonraki açılışta yine sorulur
            UserSettings.ReportsConsent = answer;
            _features.ConsentChanged();
        }
        finally { _askingConsent = false; }
    }

    private void ShowMain()
    {
        if (_main is null || _main.IsDisposed)
        {
            _main = new MainForm(_app, _features);
            _main.HiddenToTray += () => _icon.ShowBalloonTip(4000, Branding.ProductName,
                WinText.U("Tray.StillRunning"), ToolTipIcon.Info);
            _main.RestartRequested += () => Restart();
        }
        _main.ShowAndActivate();
    }

    private void UpdateTooltip()
    {
        string t;
        if (_app.ServiceDown) t = WinText.U("Tray.TipServiceDown", Branding.ProductName);
        else
        {
            int mounted = _app.MountedVolumes.Count();
            t = mounted == 0 ? WinText.U("Tray.TipNoVolumes", Branding.ProductName) : WinText.U("Tray.TipMounted", Branding.ProductName, mounted);
        }
        _icon.Text = t.Length > 63 ? t[..63] : t;
    }

    private void BuildMenu()
    {
        var m = _icon.ContextMenuStrip!;
        m.Items.Clear();
        m.Items.Add(new ToolStripMenuItem(WinText.U("Action.OpenMain"), null, (_, _) => ShowMain()) { Font = new Font(m.Font, FontStyle.Bold) });
        m.Items.Add(new ToolStripSeparator());
        var status = _app.Status;
        if (_app.ServiceDown || status is null)
            m.Items.Add(new ToolStripMenuItem(WinText.U("Tray.MenuServiceDown")) { Enabled = false });
        else if (status.Disks.Count == 0)
            m.Items.Add(new ToolStripMenuItem(WinText.U("Tray.MenuNoDisk")) { Enabled = false });
        else
            foreach (var d in status.Disks)
            {
                string drives = string.Join(" ", d.Volumes.Where(v => v.State == VolumeStatus.Mounted).Select(v => v.MountPoint));
                m.Items.Add(new ToolStripMenuItem(WinText.U("Tray.MenuEject", d.Description, (drives.Length > 0 ? "  " + drives : "")), null,
                    async (_, _) => await _app.EjectAsync(d, null)) { Enabled = !_app.IsEjecting(d.Id) });
            }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(WinText.U("Action.Rescan"), null, async (_, _) => await _app.RescanAsync());
        m.Items.Add(WinText.U("Action.OpenLog"), null, (_, _) => AppController.OpenLog());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(WinText.U("Action.Exit"), null, (_, _) => ExitThread());
    }

    // ---------------- Tema değişimi (1.5.0 P3) ----------------

    private readonly System.Windows.Forms.Timer _themeRetry = new() { Interval = 1000 };

    /// <summary>Ayar yayını (WM_SETTINGCHANGE; tema "ImmersiveColorSet" ile gelir): tema gerçekten değiştiyse yeniden başlamaya çalış.</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => _ui.Post(_ => TryThemeRestart(), null);

    /// <summary>
    /// Windows'un teması bu sürecin renklerinden farklıysa ve tepsi boştaysa (<see cref="AppController.Idle"/>: açık pencere,
    /// çıkarma, yazma değişimi, kilit açma, tarama yok) yeniden başlar; değilse her <see cref="AppController.Changed"/>'de ve
    /// saniyede bir yeniden denenir. Ana pencere açıksa yeni örnekte de açılır. Tema eski hâline dönerse bekleyen istek düşer.
    /// </summary>
    private void TryThemeRestart()
    {
        if (_exiting || Theme.SystemPalette() == Theme.Colors)
        {
            _themeRetry.Stop();
            return;
        }
        if (!_app.Idle)
        {
            _themeRetry.Start();
            return;
        }
        _themeRetry.Stop();
        Restart(show: _main is { IsDisposed: false, Visible: true });
    }

    private bool _exiting;

    /// <summary>
    /// Dil ya da tema değişti: yeni örneği başlat ve kapan. Yeni örnek bu süreç tek örnek kilidini bırakana kadar bekler (`--restart`).
    /// Yol derlemenin yanındaki exe'dir (geliştirme düzeneğinde de doğru exe başlasın). Başlatılamazsa (ör. Uygulama Denetimi
    /// engelledi) bu örnek açık kalır; seçim kaydedildiği için yeni dil bir sonraki açılışta geçerli olur.
    /// </summary>
    /// <param name="show">Yeni örnek ana pencereyi açsın (dil seçimi ana pencereden yapılır; tema değişiminde yalnız açıksa).</param>
    private void Restart(bool show = true)
    {
        string exe = Path.ChangeExtension(typeof(TrayApp).Assembly.Location, ".exe");
        string args = "--restart" + (show ? " --show" : "") + (PipeProtocol.PipeName == "APFSforWindows" ? "" : $" --pipe \"{PipeProtocol.PipeName}\"");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return; }
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged; // statik olay: abonelik süreci tutmasın
        _themeRetry.Dispose();
        _showWait.Unregister(null);
        _app.Dispose();
        _features.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _flyout.Dispose();
        _main?.Dispose();
        base.ExitThreadCore();
    }
}
