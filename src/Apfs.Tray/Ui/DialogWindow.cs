using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using static Apfs.Tray.Native;

namespace Apfs.Tray.Ui;

/// <summary>
/// Programın açık pencereleri (ana pencere, panel, kutular): kalıcı kutu açılınca görünenler kilitlenir (WinForms'un
/// <c>ShowDialog</c>'u gibi: yalnız o an GÖRÜNEN pencereler; sonradan açılan panel kilitli olmaz — Ek A E8).
/// </summary>
internal static class OpenWindows
{
    private static readonly HashSet<Window> Open = [];
    private static readonly Dictionary<IntPtr, int> Locks = [];

    public static void Add(Window w)
    {
        Open.Add(w);
        w.Closed += (_, _) => Open.Remove(w);
    }

    /// <summary>Görünen öbür pencereleri kilitler; dönen işlem kilitleri açar (iç içe kutularda sayaçla).</summary>
    public static Action Lock(Window except)
    {
        var locked = new List<IntPtr>();
        foreach (var w in Open)
        {
            if (w == except) continue;
            IntPtr h = Handle(w);
            if (h == IntPtr.Zero || !IsWindowVisible(h)) continue;
            Locks[h] = Locks.GetValueOrDefault(h) + 1;
            if (Locks[h] == 1) EnableWindow(h, false);
            locked.Add(h);
        }
        return () =>
        {
            foreach (var h in locked)
            {
                int n = Locks.GetValueOrDefault(h) - 1;
                if (n <= 0)
                {
                    Locks.Remove(h);
                    EnableWindow(h, true);
                }
                else Locks[h] = n;
            }
        };
    }

    /// <summary>Pencere tutamacı (henüz kurulmadıysa sıfır).</summary>
    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    /// <summary>Pencerenin ekran ölçeği (96 DPI = 1).</summary>
    public static double Scale(IntPtr h) => h == IntPtr.Zero ? 1 : GetDpiForWindow(h) / 96.0;

    /// <summary>
    /// "Ekranın ortası" (WinForms CenterScreen): ön plandaki pencerenin, o yoksa imlecin bulunduğu ekranın çalışma alanı (Ek A S3).
    /// </summary>
    public static RECT ActiveWorkArea()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero) return WorkArea(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST));
        GetCursorPos(out var p);
        return WorkArea(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>Dikdörtgenin bulunduğu ekranın çalışma alanı.</summary>
    public static RECT WorkAreaOf(RECT r) => WorkArea(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST));

    /// <summary>Noktanın bulunduğu ekranın çalışma alanı.</summary>
    public static RECT WorkAreaAt(POINT p) => WorkArea(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST));

    /// <summary>Bütün ekranların sağı: pencere orada hazırlanır, kullanıcının ekranına girmez.</summary>
    public static POINT OffScreen => new()
    {
        X = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN) + 200, Y = GetSystemMetrics(SM_YVIRTUALSCREEN),
    };

    /// <summary>Pencereyi fiziksel piksel konumuna taşır (etkinleştirmeden, sırasını değiştirmeden).</summary>
    public static void Move(IntPtr h, int x, int y) => SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>Pencereyi fiziksel piksel konumuna ve boyuna getirir (etkinleştirmeden).</summary>
    public static void MoveResize(IntPtr h, int x, int y, int width, int height) =>
        SetWindowPos(h, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>
    /// Enter varsayılan düğmeye mi gider: odak bir düğmede (onay kutusu sayılmaz) ya da bağlantıda değilse evet. Düğme ve bağlantı
    /// Enter'la kendisi basılır (<see cref="PressFocused"/>).
    /// </summary>
    public static bool EnterGoesToDefault(object? focused) => focused is not (Hyperlink or ButtonBase) || focused is ToggleButton;

    /// <summary>Enter odaktaki düğmeyi ya da bağlantıyı basar (WPF'in kendi Enter davranışına bırakılmaz: tek ve belirli tık).</summary>
    public static void PressFocused(object? focused)
    {
        if (focused is ButtonBase { IsEnabled: true } b) b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
        else if (focused is Hyperlink { IsEnabled: true } h) h.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent, h));
    }
}

/// <summary>
/// Kutuların ortak penceresi (1.8.0'a kadar WinForms MessageDialog/PasswordForm/ConsentForm; WinUI'deki DialogWindow sözleşmesi): içerik
/// koddan kurulur, pencere içeriğe göre boylanır, Windows 11 kutusu düzeni (üstte içerik, altta eşit genişlikte düğmeler). Sahipli kutu
/// sahibin ortasında, görev çubuğunda değil; sahipsiz kutu ekranın ortasında, en üstte, görev çubuğunda (Ek A S3). Kalıcı kutu açıkken
/// programın görünen öbür pencereleri kilitli (<c>ShowDialog</c>'un iç içe döngüsü YOK: gösterilir, kapanması beklenir). Esc,
/// kapatma ve <see cref="Cancel"/> aynı sonuç. Enter odaktaki düğmeyi basar; odak düğmede değilse <see cref="OnEnter"/>.
/// </summary>
internal class DialogWindow : Window
{
    protected readonly Grid Root = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Window? _owner;
    private readonly bool _modal;
    private readonly bool _centerOnScreen;
    private Action? _unlock;

    /// <summary>İçeriğin genişliği (DIP; pencerenin iç genişliği).</summary>
    protected double ContentWidth { get; set; } = 448;

    /// <param name="owner">Sahip pencere; null = sahipsiz (ekranın ortası, en üstte, görev çubuğunda).</param>
    /// <param name="modal">Kalıcı: açıkken öbür pencereler kilitli, açık-pencere sayacı çağıranda (Hello sahibi kalıcı değil).</param>
    /// <param name="titleBar">Başlık çubuğu (Hello sahibinde yok: kapatma düğmesi olmasın).</param>
    /// <param name="topmost">Sahipsiz kutu en üstte (rapor onayı DEĞİL: Ek A M2).</param>
    /// <param name="inTaskbar">Görev çubuğunda (sahipsiz kutular; rapor onayı sahipliyken de).</param>
    /// <param name="centerOnScreen">Sahipliyken de ekranın ortasında (rapor onayı: Ek A M2).</param>
    protected DialogWindow(string title, Window? owner, bool modal = true, bool titleBar = true, bool? topmost = null, bool? inTaskbar = null,
        bool centerOnScreen = false)
    {
        _owner = owner;
        _modal = modal;
        _centerOnScreen = centerOnScreen;
        Title = title;
        Icon = Look.AppIcon;
        Content = Root;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = titleBar ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Topmost = topmost ?? owner is null;
        ShowInTaskbar = inTaskbar ?? owner is null;
        if (owner is not null && OpenWindows.Handle(owner) != IntPtr.Zero) Owner = owner; // sahiplik
        Look.Frame(this);
        if (!titleBar)
            SourceInitialized += (_, _) =>
            {
                int round = 2; // DWMWCP_ROUND: çerçevesiz pencere de Windows 11'de yuvarlak köşeli ve gölgeli
                _ = DwmSetWindowAttribute(OpenWindows.Handle(this), DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            };
        OpenWindows.Add(this);
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, e) =>
        {
            OnClosing();
            // Kilitler pencere YOK EDİLMEDEN açılır: Windows etkinliği kapanan kutudan sonra kilitsiz bir pencereye verir; kilit
            // Closed'da (WM_DESTROY) açılınca sahip/ana pencere hâlâ kilitliydi ve etkinlik başka bir programa gidiyordu (bağımsız
            // denetçi, wpfPlan 2.7; WPF'in kendi ShowDialog'u da kapatmadan önce açar)
            if (!e.Cancel) Release();
        };
        Closed += (_, _) =>
        {
            IsClosed = true;
            Release(); // kapanış olayı atlanırsa (yedek; ikinci çağrı etkisiz)
            _closed.TrySetResult();
        };
    }

    /// <summary>Kilitli pencereleri açar ve (kalıcı, sahipli kutuda) sahibi öne alır; bir kez.</summary>
    private void Release()
    {
        if (_unlock is null) return;
        _unlock();
        _unlock = null;
        if (_owner is not null && _modal && OpenWindows.Handle(_owner) is var h && h != IntPtr.Zero) SetForegroundWindow(h);
    }

    /// <summary>Pencere kapandı (kapatma düğmesi, Alt+F4 ya da <see cref="Window.Close"/>).</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Pencere kapanıyor (kapatma düğmesi, Alt+F4 ya da <see cref="Window.Close"/>): sonuç verilmediyse vazgeçmiş sayılır.</summary>
    protected virtual void OnClosing() { }

    /// <summary>Enter, odak bir düğmede değilken (metin alanı, onay kutusu…): kutunun varsayılan düğmesi (WinForms AcceptButton).</summary>
    protected virtual void OnEnter() { }

    /// <summary>Esc: kutu vazgeçilerek kapanır.</summary>
    protected virtual void Cancel() => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
        else if (e.Key == Key.Enter)
        {
            // Önizlemede: onay kutusu Enter'ı kendisi işlemesin (Enter varsayılan düğmeye gider)
            e.Handled = true;
            var focused = Keyboard.FocusedElement;
            if (OpenWindows.EnterGoesToDefault(focused)) OnEnter();
            else OpenWindows.PressFocused(focused);
        }
    }

    /// <summary>Görüntü düzeneği: pencere ekran dışında kalır, etkinleşmez (kullanıcının odağı alınmaz).</summary>
    internal static bool Offscreen { get; set; }

    /// <summary>Gösterildi (düzenek görüntüyü bu andan sonra alır).</summary>
    internal static event Action<DialogWindow>? Shown;

    /// <summary>Pencereyi içeriğe göre boylar, yerine koyar, gösterir ve kapanana kadar bekler.</summary>
    protected async Task ShowAndWaitAsync(IInputElement? focus = null)
    {
        await PresentAsync(focus);
        await _closed.Task;
    }

    /// <summary>Pencereyi gösterir (kapanmasını beklemez).</summary>
    protected async Task PresentAsync(IInputElement? focus = null)
    {
        IntPtr h = new WindowInteropHelper(this).EnsureHandle();
        double scale = OpenWindows.Scale(h);
        FitSize();
        if (Offscreen)
        {
            // Görüntü düzeneği: ekran dışında, etkinleştirmeden göster, içerik yüklenince ölç
            var off = OpenWindows.OffScreen;
            Left = off.X / scale;
            Top = off.Y / scale;
            ShowActivated = false;
            Show();
            await LoadedAsync();
            FitSize();
            if (_modal) _unlock = OpenWindows.Lock(this);
            Shown?.Invoke(this);
            return;
        }
        // Windows açılış animasyonunu bir pencerenin yalnız İLK gösterilişinde oynatıyor: kutu ekran dışında ölçülüp ortaya
        // taşınınca animasyon sağdan kayarak, gizlenip yeniden gösterilince hiç oynamıyordu (WinUI kabul turu 2026-11-18). Bu yüzden
        // kutu GÖSTERİLMEDEN ölçülür, boyu ve yeri ona göre verilip ilk kez yerinde gösterilir; içerik yüklenince boy gerekirse
        // ortası sabit kalarak düzeltilir.
        if (_modal) _unlock = OpenWindows.Lock(this);
        // Yer: sahipliyse sahibin ortası, değilse etkin ekranın ortası
        RECT area;
        IntPtr ownerHandle = _owner is null ? IntPtr.Zero : OpenWindows.Handle(_owner);
        if (ownerHandle != IntPtr.Zero && !_centerOnScreen && IsWindowVisible(ownerHandle) && GetWindowRect(ownerHandle, out var r)) area = r;
        else area = OpenWindows.ActiveWorkArea();
        var work = OpenWindows.WorkAreaOf(area);
        int width = (int)Math.Round(Width * scale), height = (int)Math.Round(Height * scale);
        int x = Math.Clamp(area.Left + (area.Width - width) / 2, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(area.Top + (area.Height - height) / 2, work.Top, Math.Max(work.Top, work.Bottom - height));
        Left = x / scale;
        Top = y / scale;
        // Önde ve etkin (sahipsiz kutu arkada kalmasın)
        Show();
        Activate();
        SetForegroundWindow(h);
        await LoadedAsync();
        double before = Height;
        FitSize();
        double delta = Height - before;
        if (Math.Abs(delta) >= 1) Top = Math.Max(work.Top / scale, Top - delta / 2);
        if (focus is not null) Keyboard.Focus(focus); // varsayılan düğme odakta (Enter/Esc); halkası yalnız klavye kullanılırken (FocusRing)
        Shown?.Invoke(this);
    }

    private async Task LoadedAsync()
    {
        if (Root.IsLoaded) return;
        var loaded = new TaskCompletionSource();
        Root.Loaded += (_, _) => loaded.TrySetResult();
        await loaded.Task;
    }

    /// <summary>Pencere boyu içeriğe göre (iç genişlik sabit <see cref="ContentWidth"/>; çerçeve kurulu pencereden ölçülür).</summary>
    private void FitSize()
    {
        IntPtr h = OpenWindows.Handle(this);
        double scale = OpenWindows.Scale(h);
        double frameW = 0, frameH = 0;
        if (h != IntPtr.Zero && GetWindowRect(h, out var outer) && GetClientRect(h, out var inner))
        {
            frameW = (outer.Width - inner.Width) / scale;
            frameH = (outer.Height - inner.Height) / scale;
        }
        Root.Measure(new Size(ContentWidth, double.PositiveInfinity));
        double content = Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 160;
        Width = ContentWidth + frameW;
        Height = Math.Ceiling(content) + frameH;
    }

    // ---- Windows 11 kutusu düzeni ----

    /// <summary>Üst alan (içerik) + alt alan (düğmeler eşit genişlikte; tek düğme sağ yarıda).</summary>
    protected void Layout(UIElement body, params Button[] buttons)
    {
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new Border { Padding = new Thickness(24, 22, 24, 22), BorderThickness = new Thickness(0, 0, 0, 1), Child = body };
        top.SetResourceReference(Border.BackgroundProperty, "LayerFillColorAltBrush");
        top.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Root.Children.Add(top);
        var footer = new Grid { Margin = new Thickness(24, 20, 24, 24) };
        int columns = Math.Max(2, buttons.Length);
        for (int i = 0; i < columns; i++)
        {
            if (i > 0) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        int start = columns - buttons.Length;
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(buttons[i], (start + i) * 2);
            footer.Children.Add(buttons[i]);
        }
        Grid.SetRow(footer, 1);
        Root.Children.Add(footer);
    }

    /// <summary>Düğme: kutunun düğmesi metniyle (eylem kutuyu kapatır).</summary>
    protected static Button Button(string text, ButtonKind kind, Action onClick) => UiKit.Button(text, kind, onClick);
}
