using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;
using Apfs.Windows.Tray;
using static Apfs.Tray.Native;

namespace Apfs.Tray;

/// <summary>
/// Bildirim alanı uygulaması (1.8.0'a kadar WinForms TrayApp): tepsi simgesi (sol tık: küçük panel, sağ tık: menü), ana pencere
/// ve hizmet bildirimleri. Tüm iş hizmette yapılır; tepsi yalnızca arayüzdür. Mantık <see cref="AppController"/> ve
/// <see cref="TrayFeatures"/>'ta (arayüzden bağımsız; winuiPlan P1), arayüz kapısı <see cref="WpfUiHost"/>.
/// </summary>
internal sealed class TrayHost
{
    private readonly TrayIcon _icon;
    private readonly AppController _app;
    private readonly TrayFeatures _features;
    private readonly FlyoutWindow _flyout;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _reveal;
    private readonly RegisteredWaitHandle? _showWait;
    private MainWindow? _main;
    private bool _exiting;

    /// <summary>
    /// Tek tıktan sonra bekler; dolarsa kurulu panel yerine gelir. Süre Windows'un çift tık süresinin %40'ı (varsayılan 500 ms →
    /// 200 ms): tam süre ve %60'ı kullanıcıya geç geldi (1.6.0); çift tıkta ikinci basış çoğunlukla bundan önce gelir. Zamanlayıcı
    /// panel kurulmadan ÖNCE başlar: kurulum süresi beklemeye eklenmez.
    /// </summary>
    internal static int RevealDelay => Math.Max(1, (int)GetDoubleClickTime() * 2 / 5);

    /// <summary>Arayüz iş parçacığında, WPF'in eşitleme bağlamı (DispatcherSynchronizationContext) kuruluyken çağrılır.</summary>
    public TrayHost(LaunchOptions options)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        var ui = SynchronizationContext.Current!;
        _icon = new TrayIcon(Branding.ProductName);
        // Arayüzden bağımsız mantık (winuiPlan P1): kutular, parola penceresi, Hello sahibi ve balonlar WPF kapısından
        var host = new WpfUiHost((text, level) => _icon.ShowBalloon(5000, text, level));
        _app = new AppController(host, ui);
        _flyout = new FlyoutWindow(_app, ShowMain);
        _app.Changed += UpdateTooltip;

        // Tek tık: panel hemen (ekran dışında) kurulup etkinleşir, kısa bekleme dolunca yerine gelir. Çift tık: panel hiç görünmeden
        // gizlenir, ana pencere açılır.
        _reveal = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(RevealDelay) };
        _reveal.Tick += (_, _) =>
        {
            _reveal.Stop();
            _flyout.Reveal();
        };
        _icon.Click += () =>
        {
            _reveal.Stop();
            _reveal.Start(); // tıklama anından say (Arm'ın kurulum süresi beklemenin içinde kalsın)
            if (!_flyout.Arm()) _reveal.Stop();
        };
        _icon.DoubleClick += () =>
        {
            _reveal.Stop();
            _flyout.Cancel();
            ShowMain();
        };
        _icon.RightClick += ShowMenu;
        // Her balon tıklanınca ana pencere (1.8.0: "yeni sürüm" balonu da; güncelleme alt bağlantıdan yapılır)
        _icon.BalloonClicked += ShowMain;
        // Windows teması değişince açık/koyu yeniden başlamadan uygulanır (Ek A R2 ⟳)
        _icon.SettingChanged += () => _dispatcher.BeginInvoke(Look.Refresh);

        if (options.ShowEvent is { } showEvent)
            _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => _dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);
        // Kendiliğinden kurulum koşulu: hizmete taze sorulan disk sayısı (yanıt yoksa null = kurma)
        _features = new TrayFeatures(ui, host, options.Reports, async () =>
        {
            await _app.PollAsync();
            return _app.ServiceDown || _app.Status is null ? null : _app.Status.Disks.Count;
        });
        _features.Balloon += text => _icon.ShowBalloon(8000, text, NoticeLevel.Info);
        _app.Start();
        _features.Start(options.Updated);
        if (options.Show) ShowMain();
        // İlk açılışta (ya da daha önce cevaplanmadıysa) bir kez raporlama onayı sorulur; cevap gelene kadar hiçbir şey toplanmaz
        // (beklenmeyen istisna yutulmaz, süreci düşürür)
        if (UserSettings.ReportsConsent is null)
            Post(async () => await _features.AskConsentAsync(() => ConsentWindow.AskAsync(_main is { IsShown: true } m ? m : null)));
    }

    /// <summary>İşi arayüz kuyruğuna koyar. Async iş "async void" olarak çalışır: istisnası yutulmaz, süreci düşürür (A9).</summary>
    private void Post(Action work) => _dispatcher.BeginInvoke(work);

    private void ShowMain()
    {
        if (_exiting) return;
        if (_main is null)
        {
            _main = new MainWindow(_app, _features);
            _main.HiddenToTray += () => _icon.ShowBalloon(4000, WinText.U("Tray.StillRunning"), NoticeLevel.Info);
            _main.RestartRequested += () => Restart();
            _main.ClosedToTray += _features.MainWindowClosed;
        }
        bool wasVisible = _main.IsShown;
        _main.ShowAndActivate();
        if (!wasVisible) _features.MainWindowShown();
    }

    private void UpdateTooltip() => _icon.Tip = TrayModel.Tooltip(_app.ServiceDown, _app.MountedVolumes.Count());

    /// <summary>Sağ tık menüsü: öğeler <see cref="TrayModel.Menu"/>'den, her açılışta yeniden (Windows'un menüsü; koyu temada koyu).</summary>
    private void ShowMenu()
    {
        var items = TrayModel.Menu(_app.Status, _app.ServiceDown, _app.IsEjecting);
        var rows = items.Select((item, i) => (Id: i + 1, Text: item.Kind == TrayMenuKind.Separator ? null : item.Text, item.Enabled,
            Default: item.Kind == TrayMenuKind.OpenMain)).ToList();
        int chosen = _icon.ShowMenu(rows, Look.IsDark);
        if (chosen <= 0 || chosen > items.Count) return;
        var picked = items[chosen - 1];
        switch (picked.Kind)
        {
            case TrayMenuKind.OpenMain: ShowMain(); break;
            // Menüden çıkarma sahipsiz kutularla (ekranın ortasında, en üstte; J5)
            case TrayMenuKind.Eject: Post(async () => await _app.EjectAsync(picked.Disk!, null)); break;
            case TrayMenuKind.Rescan: Post(async () => await _app.RescanAsync()); break;
            case TrayMenuKind.OpenLog: Post(async () => await _app.OpenLogAsync()); break;
            case TrayMenuKind.Exit: Exit(); break;
        }
    }

    /// <summary>
    /// Dil ya da tasarım değişti: yeni örneği başlat ve kapan. Yeni örnek bu süreç tek örnek kilidini bırakana kadar bekler
    /// (`--restart`). Başlatılamazsa (ör. Uygulama Denetimi engelledi) bu örnek açık kalır; seçim kaydedildiği için bir sonraki
    /// açılışta geçerli olur (A14). Yeni örnek ana pencereyi açar (dil ve tasarım ana pencereden seçilir).
    /// </summary>
    private void Restart(bool show = true)
    {
        string exe = Environment.ProcessPath!;
        string args = "--restart" + (show ? " --show" : "") + (PipeProtocol.PipeName == PipeProtocol.DefaultPipeName ? "" : $" --pipe \"{PipeProtocol.PipeName}\"");
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return; }
        Exit();
    }

    /// <summary>Çıkış: onaysız ve hemen (A16); simge kalkar, zamanlayıcılar durur, pencereler kapanır (A13). Diskler hizmette bağlı kalır.</summary>
    private void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        _reveal.Stop();
        _showWait?.Unregister(null);
        _app.Dispose();
        _features.Dispose();
        _icon.Dispose();
        _flyout.CloseForExit();
        _main?.CloseForExit();
        Application.Current.Shutdown();
    }
}
