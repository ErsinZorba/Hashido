using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Localization;
using Apfs.Windows.Tray;
using static Apfs.Tray.Native;

namespace Apfs.Tray;

/// <summary>
/// Tepsi simgesine sol tıklayınca açılan küçük panel (Ek A D1–D8): başlıkta mühür, "Hashido", "Yeniden tara" ve "Ana pencereyi aç";
/// sıkışık disk listesi. Çerçevesiz, en üstte, görev çubuğunda yok, Windows 11'de yuvarlak köşe ve gölge. Tıklama anında ekran
/// DIŞINDA kurulup etkinleşir (öne gelme hakkı yalnız o an), kısa beklemeden sonra imlecin üstüne gelir ve yukarı kayarak belirir
/// (1.6.0'da kullanıcıyla ayarlanan geçiş korundu: Win32 katmanlı pencere saydamlığı + adım adım kayma; WPF'in AllowsTransparency'si
/// YOK, Windows'un köşesi ve gölgesi kalsın). Odak gidince gizlenir; X/Alt+F4 gizler.
/// </summary>
internal sealed class FlyoutWindow : Window
{
    private readonly AppController _app;
    private readonly DiskList _list;
    private readonly ActionButton _rescan;
    private readonly Grid _header;
    private readonly ScrollViewer _scroll;
    private readonly Grid _root = new();
    private readonly DispatcherTimer _fade;
    private const double PanelWidth = 360;
    private int _targetTop, _targetLeft;
    private long _hiddenAt;
    private RECT _work;        // panelin açılacağı (tıklanan simgenin) ekranının çalışma alanı
    private bool _armed;       // ekran dışında kurulu, yerine gelmeyi bekliyor
    private bool _layered;
    private double _opacity = 1;
    private bool _exiting;

    internal FlyoutWindow(AppController app, Action openMain)
    {
        _app = app;
        Title = Branding.ProductName;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = PanelWidth;
        Height = 200;
        Look.Frame(this);
        OpenWindows.Add(this);
        SourceInitialized += (_, _) =>
        {
            int round = 2; // DWMWCP_ROUND: Windows 11'de yuvarlak köşe ve gölge (Windows 10'da etkisiz)
            _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };

        // Başlık: mühür, "Hashido", sağda yeniden tara ve ana pencere (simge düğmeleri; metinleri araç ipucu)
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _header = new Grid { Margin = new Thickness(18, 10, 8, 4) };
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition());
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var seal = Look.Seal(20);
        seal.VerticalAlignment = VerticalAlignment.Center;
        _header.Children.Add(seal);
        var title = UiKit.Heading(Branding.ProductName, 16);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(title, 1);
        _header.Children.Add(title);
        _rescan = new ActionButton(WinText.U("Action.Rescan"), ButtonKind.Quiet, "", iconOnly: true) { BusyText = WinText.U("Action.Rescanning") };
        _rescan.Invoked += async () => await _app.RescanAsync();
        Grid.SetColumn(_rescan, 2);
        _header.Children.Add(_rescan);
        var main = new ActionButton(WinText.U("Action.OpenMain"), ButtonKind.Quiet, "", iconOnly: true);
        main.Invoked += () => { HidePanel(); openMain(); };
        Grid.SetColumn(main, 3);
        _header.Children.Add(main);
        _root.Children.Add(_header);

        // Panelden açılan kutuların sahibi yok: panel odak kaybında gizlenir (D8)
        _list = new DiskList(app, compact: true, () => null);
        _scroll = new ScrollViewer
        {
            Content = _list.View, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18, 4, 18, 12), Focusable = false,
        };
        Motion.SmoothWheel(_scroll);
        Grid.SetRow(_scroll, 1);
        _root.Children.Add(_scroll);
        Content = _root;

        _app.Changed += () =>
        {
            _rescan.Busy = _app.Rescanning;
            _list.Rebuild();
            if (IsShown) FitHeight();
        };
        // Doluluk satırı gelince/gidince satır boyu değişir (liste kendini yerinde günceller; bu abonelik ondan sonra çalışır)
        _app.SpaceChanged += () => { if (IsShown) FitHeight(); };
        _fade = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _fade.Tick += (_, _) => FadeStep();

        Deactivated += (_, _) => { if (!Offscreen) HidePanel(); };
        Closing += OnClosing;
        IsVisibleChanged += (_, _) =>
        {
            _app.WatchSpace(this, IsVisible); // boş alan yalnız panel (ya da ana pencere) açıkken sorulur
            if (!IsVisible)
            {
                _hiddenAt = Environment.TickCount64;
                _armed = false;
                _fade.Stop();
            }
        };
    }

    /// <summary>Görüntü düzeneği: panel ekran dışında kalır, odak kaybında gizlenmez.</summary>
    internal static bool Offscreen { get; set; }

    internal IntPtr Handle => new WindowInteropHelper(this).Handle;

    private double Scale => OpenWindows.Scale(Handle);

    internal bool IsShown => Handle != IntPtr.Zero && IsWindowVisible(Handle);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(HidePanel);
    }

    private void HidePanel()
    {
        if (IsVisible) Hide();
    }

    /// <summary>
    /// Simgeye tek tıklanınca, tıklama anında: açıksa kapatır ve false döner; kapalıysa listeyi kurar, hedef konumu imleçten
    /// hesaplar ve paneli ekran DIŞINDA gösterip etkinleştirir, true döner — çağıran kısa bekleme (<see cref="TrayHost.RevealDelay"/>)
    /// dolunca <see cref="Reveal"/> der. Bekleme sırasında odak giderse panel kendiliğinden gizlenir.
    /// </summary>
    internal bool Arm()
    {
        if (IsShown) { HidePanel(); return false; }
        // Panel açıkken simgeye tıklamak önce paneli kapatır (odak kaybı); aynı tık yeniden açmasın (D3)
        if (Environment.TickCount64 - _hiddenAt < 300) return false;
        GetCursorPos(out var cursor);
        _work = OpenWindows.WorkAreaAt(cursor);
        _rescan.Busy = _app.Rescanning;
        _list.Rebuild();
        IntPtr h = new WindowInteropHelper(this).EnsureHandle();
        PlaceOffscreen(h, 200);
        SetOpacity(0);
        Show();
        Activate();
        SetForegroundWindow(h);
        UpdateLayout(); // metin ölçüsü pencerenin ekran ölçeğiyle (gösterilmeden önce ölçülen boy birkaç piksel kısa kalıyordu)
        FitHeight();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FitHeight); // ilk yerleşimden sonra bir kez daha (alt kenar sabit)
        int gap = (int)(12 * Scale);
        GetWindowRect(h, out var r);
        _targetLeft = Math.Clamp(cursor.X - r.Width / 2, _work.Left + gap, Math.Max(_work.Left + gap, _work.Right - r.Width - gap));
        _targetTop = Math.Clamp(cursor.Y - r.Height - gap, _work.Top + gap, Math.Max(_work.Top + gap, _work.Bottom - r.Height - gap));
        _armed = true;
        return true;
    }

    /// <summary>Paneli ekran dışına koyar (WPF'in konum ve boy özellikleri de: ilk gösterişte onları uygular).</summary>
    private void PlaceOffscreen(IntPtr h, double height)
    {
        var off = OpenWindows.OffScreen;
        double s = Scale;
        Left = off.X / s;
        Top = off.Y / s;
        Width = PanelWidth;
        Height = height;
        OpenWindows.MoveResize(h, off.X, off.Y, (int)(PanelWidth * s), (int)(height * s));
    }

    /// <summary>Bekleme doldu: panel hâlâ kurulu ve açıksa (odak gitmediyse) yerine gelip yukarı kayarak belirir.</summary>
    internal void Reveal()
    {
        if (!_armed || !IsShown) return;
        _armed = false;
        OpenWindows.Move(Handle, _targetLeft, _targetTop + (int)(10 * Scale));
        _fade.Start();
    }

    /// <summary>Çift tık: kurulu panel hiç görünmeden gizlenir.</summary>
    internal void Cancel()
    {
        _armed = false;
        HidePanel();
    }

    /// <summary>Boy içeriğe uyar (en az 200, en çok çalışma alanının %80'i); boy değişince ALT kenar sabit (D6).</summary>
    private void FitHeight()
    {
        IntPtr h = Handle;
        if (h == IntPtr.Zero) return;
        double s = Scale;
        // Bütün içerik sınırsız boyda ölçülür: kaydırma alanı kendi dolgusu ve kenarıyla, kaydırma çubuğu olmadan
        _root.Measure(new Size(PanelWidth, double.PositiveInfinity));
        double content = _root.DesiredSize.Height;
        int max = (int)((_work.Height > 0 ? _work.Height : 1000) * 0.8);
        int wanted = Math.Clamp((int)Math.Ceiling(content * s) + 2, (int)(200 * s), Math.Max((int)(200 * s), max));
        GetWindowRect(h, out var r);
        int delta = wanted - r.Height;
        if (delta == 0) return;
        bool placed = r.Left < OpenWindows.OffScreen.X;
        OpenWindows.MoveResize(h, r.Left, placed ? r.Top - delta : r.Top, r.Width, wanted); // alt kenar sabit
        _targetTop -= delta;
    }

    /// <summary>~4 adımda belirir ve yerine kayar (1.6.0: 0,15 ile ~7 adım kullanıcıya ağır geldi).</summary>
    private void FadeStep()
    {
        SetOpacity(Math.Min(1, _opacity + 0.25));
        GetWindowRect(Handle, out var r);
        int step = Math.Max(1, (r.Top - _targetTop + 1) / 2);
        int top = Math.Max(_targetTop, r.Top - step);
        OpenWindows.Move(Handle, r.Left, top);
        if (_opacity >= 1 && top == _targetTop) _fade.Stop();
    }

    /// <summary>Pencerenin bütününün saydamlığı (katmanlı pencere; tam görünürken katman kalkar).</summary>
    private void SetOpacity(double value)
    {
        _opacity = value;
        IntPtr h = Handle;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if (value >= 1)
        {
            if (_layered) SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex & ~WS_EX_LAYERED));
            _layered = false;
            return;
        }
        if (!_layered) SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
        _layered = true;
        SetLayeredWindowAttributes(h, 0, (byte)Math.Round(value * 255), LWA_ALPHA);
    }

    /// <summary>Görüntü düzeneği: paneli ekran dışında, liste kurulmuş ve boyu ayarlanmış olarak gösterir.</summary>
    internal void ShowForShot()
    {
        _list.Rebuild();
        PlaceOffscreen(new WindowInteropHelper(this).EnsureHandle(), 400);
        ShowActivated = false;
        Show();
        _work = new RECT { Bottom = 2000 };
        Dispatcher.BeginInvoke(DispatcherPriority.Background, FitHeight);
    }

    internal void CloseForExit()
    {
        _exiting = true;
        _fade.Stop();
        _list.Detach();
        Close();
    }
}
