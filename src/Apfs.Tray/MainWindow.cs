using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Tray;
using static Apfs.Tray.Native;

namespace Apfs.Tray;

/// <summary>
/// Ana pencere: Diskler ⇄ Ayarlar (WinUI'deki DrillIn sayfa geçişiyle, <see cref="Motion.DrillIn"/>; kabul turu 2026-11-21). Kapatınca (X, Alt+F4)
/// tepsiye gizlenir; programdan çıkış tepsi menüsündendir (diskler hizmette bağlı kalır; Ek A A11, C6). Windows kapanışını ya da
/// oturum kapatmayı engellemez: oturum sonu iletilerine dokunulmaz (<see cref="Program"/>.Run; A17). Tek pencere: gizle-göster
/// arasında konumu ve boyu korunur, yalnız ilk gösterişte ortalanır (C9, C10).
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly Grid _host = new();
    private readonly DisksView _disks;
    private SettingsView? _settings;
    private bool _hintShown;
    private bool _shownOnce;
    private bool _closing;

    internal AppController Controller { get; }
    internal TrayFeatures? Features { get; }

    /// <summary>Pencere ilk kez tepsiye gizlendiğinde (kullanıcıya bir kez bilgi vermek için).</summary>
    internal event Action? HiddenToTray;

    /// <summary>Kullanıcı pencereyi kapattı (tepsiye gizlendi; her seferinde). Güncelleme bu anda kendiliğinden kurulabilir (1.8.0).</summary>
    internal event Action? ClosedToTray;

    /// <summary>Dil ya da tasarım değişti (kaydedildi): program HEMEN yeniden açılır (Ek A A18).</summary>
    internal event Action? RestartRequested;

    /// <param name="features">Raporlama ve güncelleme (Ayarlar ve "yeni sürüm" bağlantısı); düzenekte null olabilir.</param>
    internal MainWindow(AppController app, TrayFeatures? features)
    {
        Controller = app;
        Features = features;
        Title = Branding.ProductName;
        Icon = Look.AppIcon;
        Width = 500;
        Height = 640;
        MinWidth = 380;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Look.Frame(this);
        OpenWindows.Add(this);
        Content = _host;
        _disks = new DisksView(this);
        Motion.DrillIn(_host, null, _disks.View, forward: true);
        Closing += OnClosing;
        // Boş alan yalnız pencere (ya da panel) açıkken sorulur (B13)
        IsVisibleChanged += (_, _) => Controller.WatchSpace(this, IsVisible);
        PreviewKeyDown += (_, e) => _settings?.OnKey(e);
    }

    internal IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Görünür mü (gizli ya da hiç gösterilmemişse false; simge durumunda true).</summary>
    internal bool IsShown => Handle != IntPtr.Zero && IsWindowVisible(Handle);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return; // programdan çıkış: gerçekten kapanır (Windows kapanışı pencereyi kapatmaz, engellemez: A17)
        e.Cancel = true;
        Dispatcher.BeginInvoke(HideToTray); // kapanış olayının içinde değil, hemen ardından
    }

    /// <summary>Alt çubuk sığmazsa pencere en az onun kadar geniş (uzun dillerde dil düğmesi imzayla çakışmasın; C1).</summary>
    internal void EnsureMinimumWidth(double contentWidth)
    {
        double frame = 16;
        if (Handle != IntPtr.Zero && GetWindowRect(Handle, out var outer) && GetClientRect(Handle, out var inner))
            frame = (outer.Width - inner.Width) / OpenWindows.Scale(Handle);
        double need = Math.Ceiling(contentWidth + frame);
        if (need > MinWidth) MinWidth = need;
    }

    /// <summary>
    /// Gizliyse gösterir, simge durumundaysa geri getirir ve öne alır. Windows 11 çalışan programın gösterdiği pencereye açılış
    /// animasyonu oynatmıyor, simge durumundan geri getirmeye kendi geçişini oynatıyor: pencere simge durumunda gösterilip hemen
    /// geri getirilir (🔴 vanilla animasyon; 1.6.0 MainForm.ShowAndActivate ile aynı yol, C7).
    /// </summary>
    internal void ShowAndActivate()
    {
        IntPtr h = new WindowInteropHelper(this).EnsureHandle();
        if (!IsWindowVisible(h))
        {
            bool maximized = WindowState == WindowState.Maximized; // büyütülmüşken gizlenmiş pencere büyütülmüş döner
            if (!_shownOnce)
            {
                _shownOnce = true;
                var area = OpenWindows.ActiveWorkArea();
                double s = OpenWindows.Scale(h);
                Left = (area.Left + (area.Width - Width * s) / 2) / s;
                Top = (area.Top + Math.Max(0, (area.Height - Height * s) / 2)) / s;
            }
            ShowActivated = false;
            WindowState = WindowState.Minimized;
            Show();
            ShowActivated = true;
            ShowWindow(h, maximized ? SW_SHOWMAXIMIZED : SW_RESTORE);
        }
        // SW_RESTORE simge durumundan önceki hâline döndürür (büyütülmüşse büyütülmüş; C7, WinUI kabul turu 2026-11-18)
        else if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        Activate();
        SetForegroundWindow(h);
        Controller.WatchSpace(this, true); // boş alan yalnız pencere (ya da panel) açıkken sorulur (B13; görünürlük olayına ek olarak)
    }

    private void HideToTray()
    {
        if (!IsVisible) return;
        // Ayarlar eskiden kalıcı kutuydu (açıkken ana pencere kapatılamazdı, kapatmak İptal'di): vazgeçilerek kapanır, pencere yeniden
        // açılınca Diskler gelir (WinUI bağımsız denetçisi, P2.9)
        if (_settings is not null) Back();
        Hide();
        Controller.WatchSpace(this, false);
        ClosedToTray?.Invoke();
        // İpucu kullanıcı başına bir kez (kalıcı işaret); kayıt yazılamazsa en azından bu oturumda bir kez
        if (!_hintShown && !UserSettings.TrayHintShown)
        {
            // Ayarlar › Bildirimler "yalnız önemli" ya da "kapalı": gösterilmez ve işaretlenmez (seçim "hepsi"ne dönünce bir kez çıkar)
            if (!TrayModel.ShowsNotice(UserSettings.Notifications, NoticeLevel.Info, requested: false)) return;
            UserSettings.TrayHintShown = true;
            HiddenToTray?.Invoke();
        }
        _hintShown = true;
    }

    internal void RequestRestart() => RestartRequested?.Invoke();

    // ---- Diskler ⇄ Ayarlar ----

    /// <summary>Ayarlar açık mı (düzenek ve sınamalar).</summary>
    internal bool SettingsOpen => _settings is not null;

    internal void OpenSettings()
    {
        if (_settings is not null) return;
        _settings = new SettingsView(this);
        Motion.DrillIn(_host, _disks.View, _settings.View, forward: true);
        _settings.Focus();
    }

    /// <summary>Ayarlar'dan Diskler'e (Tamam, İptal, geri, Esc ya da pencere kapatılınca).</summary>
    internal void Back()
    {
        if (_settings is not { } s) return;
        _settings = null;
        s.Closed();
        Motion.DrillIn(_host, s.View, _disks.View, forward: false);
        _disks.Returned();
    }

    /// <summary>Program kapanırken: gerçekten kapanır (tepsiye gizlenmez).</summary>
    internal void CloseForExit()
    {
        _closing = true;
        Back();
        Close();
    }
}

/// <summary>Klavye girdisini Ayarlar'a iletmek için (ana pencere yakalar).</summary>
internal static class KeyRouting
{
    /// <summary>Açılır liste açıkken Esc ve Enter listeye kalır.</summary>
    public static bool InsideOpenList(object? focused) => focused is ComboBoxItem || focused is ComboBox { IsDropDownOpen: true };
}
