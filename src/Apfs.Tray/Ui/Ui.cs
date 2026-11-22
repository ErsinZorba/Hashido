using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Apfs.Windows.Tray;
using WpfPath = System.Windows.Shapes.Path;

namespace Apfs.Tray.Ui;

/// <summary>Düğmenin görünüşü: ikincil (çerçeveli), birincil (vurgu dolgulu), sessiz (zeminsiz).</summary>
internal enum ButtonKind { Secondary, Accent, Quiet }

/// <summary>Kodla kurulan arayüz öğeleri (metinler <c>WinText.U</c>'dan; XAML yok).</summary>
internal static class UiKit
{
    /// <summary>Windows 11 simge yazı tipi (Windows 10'da MDL2).</summary>
    public const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>
    /// Düğmenin Fluent stili, kaynak başvurusuyla (açık/koyu değişince Fluent sözlüğüyle birlikte yenilenir): birincil düğmede
    /// "AccentButtonStyle", öbürlerinde Fluent'in düğme stili. Alt sınıflara (<see cref="ActionButton"/>) örtük stil kendiliğinden
    /// uygulanmaz (WPF örtük stili tam türle arar): açıkça verilir.
    /// </summary>
    public static T Styled<T>(T button, ButtonKind kind) where T : Button
    {
        button.SetResourceReference(FrameworkElement.StyleProperty, kind == ButtonKind.Accent ? "AccentButtonStyle" : typeof(Button));
        return button;
    }

    /// <summary>Metin: kaydırılır, mürekkep (rol verilirse rol rengi).</summary>
    public static TextBlock Text(string text, double size = 14, Role role = Role.Ink, bool wrap = true, bool strong = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Look.Brush(role),
    };

    /// <summary>Başlık: tasarımın başlık yazı tipi, rakamlar düz.</summary>
    public static TextBlock Heading(string text, double size) =>
        Look.Heading(new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Role.Ink) });

    /// <summary>Segoe Fluent Icons simgesi.</summary>
    public static TextBlock Icon(string glyph, double size = 16) => new()
    {
        Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = size, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>İşi tıklama bittikten sonra başlatır: iş sırasında liste yeniden kurulsa da iş kesilmez (Ek A B19). İstisna yutulmaz.</summary>
    public static void Later(Action work) => Dispatcher.CurrentDispatcher.BeginInvoke(work);

    public static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    public static T Margin<T>(T e, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        e.Margin = new Thickness(left, top, right, bottom);
        return e;
    }

    /// <summary>Sessiz düğme: zeminsiz, kenarsız (Fluent'in üzerine gelme zemini kalır).</summary>
    public static T Quiet<T>(T b) where T : Control
    {
        b.Background = Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
        b.Padding = new Thickness(8, 5, 8, 5);
        return b;
    }

    /// <summary>Kutu ve sayfa düğmesi: metinli (eylem tıklamada), birincil ya da ikincil.</summary>
    public static Button Button(string text, ButtonKind kind, Action onClick)
    {
        var b = Styled(new Button { Content = new TextBlock { Text = text }, MinHeight = 32, Padding = new Thickness(12, 5, 12, 6) }, kind);
        if (kind == ButtonKind.Quiet) Quiet(b);
        AutomationProperties.SetName(b, text);
        FocusRing.Attach(b);
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>
    /// Köprü metni (tıklayınca <paramref name="onClick"/>; Tab ile odak alır, Enter basar). Renk paletin bağlantı rolünden: Hashido
    /// tasarımında Windows'un vurgu metni rengi koyu temada zemine karışıyordu (kabul turu 2026-11-18, Ek A R3).
    /// </summary>
    public static TextBlock Link(string text, Action onClick, double size = 14)
    {
        var link = new Hyperlink(new Run(text)) { Foreground = Look.Brush(Role.Link), TextDecorations = null }; // WinUI'deki gibi alt çizgisiz
        link.Click += (_, _) => onClick();
        return new TextBlock(link) { FontSize = size, TextWrapping = TextWrapping.Wrap };
    }

    /// <summary>Adresi varsayılan tarayıcıda açan köprü metni.</summary>
    public static TextBlock Link(string text, string url, double size = 14) => Link(text, () => Open(url), size);

    /// <summary>Adresi varsayılan tarayıcıda açar (açılamazsa sessiz).</summary>
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

}

/// <summary>
/// Kutu düğmelerinin odak halkası (Fluent'in iki çizgili halkası ve renkleri, süslemeyle): klavye odağı düğmedeyken YALNIZ klavye
/// kullanılıyorsa görünür — son girdi bir tuşsa; fareyle tıklanınca kalkar. Kullanıcı (kabul turu 2026-11-21, yazma onayı fareyle
/// açılınca Vazgeç'in halkası): "her butona tıklayacağımda, seçili olan buton bu şekilde çerçeveli görünüyor. bu kötü bir görünüme
/// sebep oluyor" → önceki "her zaman görünür" kuralı (wpfPlan 2.7 denetçi 3) kalktı. Varsayılan düğme yine odakta: Enter/Esc
/// davranışı değişmez (Ek A S1), yalnız halka klavyeye geçilince (Tab, ok tuşları…) belirir.
/// </summary>
internal sealed class FocusRing : Adorner
{
    private FocusRing(UIElement element) : base(element) => IsHitTestVisible = false;

    /// <summary>Son girdi klavye mi (tuş basıldı; fare ya da dokunma basışı sıfırlar). Değişince görünür halkalar güncellenir.</summary>
    private static bool _keyboard;
    private static event Action? ModeChanged;
    private static bool _hooked;

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            bool? mode = e.StagingItem.Input switch
            {
                KeyEventArgs k when k.RoutedEvent == Keyboard.PreviewKeyDownEvent => true,
                MouseButtonEventArgs m when m.RoutedEvent == Mouse.PreviewMouseDownEvent => false,
                StylusDownEventArgs or TouchEventArgs => false,
                _ => null,
            };
            if (mode is { } m2 && m2 != _keyboard)
            {
                _keyboard = m2;
                ModeChanged?.Invoke();
            }
        };
    }

    public static void Attach(Control control)
    {
        Hook();
        control.FocusVisualStyle = null;
        FocusRing? ring = null;
        void Update()
        {
            if (AdornerLayer.GetAdornerLayer(control) is not { } layer) return;
            bool show = control.IsKeyboardFocused && _keyboard;
            if (show && ring is null) layer.Add(ring = new FocusRing(control));
            else if (!show && ring is not null) { layer.Remove(ring); ring = null; }
        }
        control.IsKeyboardFocusedChanged += (_, _) => Update();
        // Genel olaya yalnız düğme ağaçtayken bağlı (kutular kapanınca bırakılsın)
        control.Loaded += (_, _) => { ModeChanged -= Update; ModeChanged += Update; };
        control.Unloaded += (_, _) => ModeChanged -= Update;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var element = (FrameworkElement)AdornedElement;
        var outer = element.TryFindResource("FocusStrokeColorOuterBrush") as Brush ?? Brushes.Black;
        var inner = element.TryFindResource("FocusStrokeColorInnerBrush") as Brush ?? Brushes.White;
        var r = new Rect(element.RenderSize);
        dc.DrawRoundedRectangle(null, new Pen(outer, 2), Rect.Inflate(r, 2, 2), 6, 6);     // dışta 2 px (kenardan 1–3 px dışarıda)
        dc.DrawRoundedRectangle(null, new Pen(inner, 1), Rect.Inflate(r, 0.5, 0.5), 4.5, 4.5); // içte 1 px
    }
}

/// <summary>
/// Eylem düğmesi: tıklayınca iş tıklamadan SONRA başlar (<see cref="UiKit.Later"/>); meşgulken dönen halka + meşgul metni gösterir
/// ve tıklama yok sayılır (1.8.0'a kadar FriendlyButton.Busy). Simge düğmesinde araç ipucu = erişilebilir ad, meşgulken meşgul
/// metni (Ek A R8). Basma görünümü Windows'un kendisi (kullanıcı kararı 2026-11-18: Hashido'daki küçülme kalktı).
/// </summary>
internal sealed class ActionButton : Button
{
    private readonly string _text;
    private readonly object _idle;
    private readonly bool _iconOnly;
    private bool _busy;

    /// <param name="icon">Simge (Segoe Fluent Icons); <paramref name="iconOnly"/> ise metin yalnız araç ipucu ve erişilebilir ad.</param>
    public ActionButton(string text, ButtonKind kind = ButtonKind.Secondary, string? icon = null, bool iconOnly = false, double iconSize = 16)
    {
        _text = text;
        _iconOnly = iconOnly && icon is not null;
        UiKit.Styled(this, kind);
        if (kind == ButtonKind.Quiet) UiKit.Quiet(this);
        if (_iconOnly)
        {
            _idle = UiKit.Icon(icon!, iconSize);
            Width = 40;
            Height = 36;
            Padding = new Thickness(0);
        }
        else if (icon is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(UiKit.Margin(UiKit.Icon(icon, 15), 0, 1, 7, 0));
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            _idle = row;
        }
        else _idle = new TextBlock { Text = text };
        Content = _idle;
        SetLabel(text);
        Click += (_, _) =>
        {
            if (_busy) return;
            UiKit.Later(() => Invoked?.Invoke());
        };
    }

    /// <summary>Tıklandı (iş tıklamadan sonra, meşgul değilken).</summary>
    public event Action? Invoked;

    /// <summary>Meşgulken gösterilecek metin (yoksa düğmenin metni).</summary>
    public string? BusyText { get; set; }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            if (value)
            {
                if (_iconOnly) Content = new Spinner(16);
                else
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(UiKit.Margin(new Spinner(14), 0, 0, 8, 0));
                    row.Children.Add(new TextBlock { Text = BusyText ?? _text, VerticalAlignment = VerticalAlignment.Center });
                    Content = row;
                }
            }
            else Content = _idle;
            SetLabel(value ? BusyText ?? _text : _text);
        }
    }

    private void SetLabel(string name)
    {
        AutomationProperties.SetName(this, name);
        if (_iconOnly) ToolTip = name;
    }
}

/// <summary>Meşgul halkası: dönen yay, rengi içinde durduğu düğmenin metin rengi.</summary>
internal sealed class Spinner : Viewbox
{
    public Spinner(double size)
    {
        Width = size;
        Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        var spin = new RotateTransform(0, 8, 8);
        var arc = new WpfPath
        {
            Data = Geometry.Parse("M8,1.5 A6.5,6.5 0 1 1 1.5,8"), StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransform = spin,
        };
        arc.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
        });
        Child = new Canvas { Width = 16, Height = 16, Children = { arc } };
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        Loaded += (_, _) => spin.BeginAnimation(RotateTransform.AngleProperty, turn);
        Unloaded += (_, _) => spin.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}

/// <summary>Çizilecek işaret: birim durumları (<see cref="VolumeGlyph"/>) ve boş listenin büyük marka işareti.</summary>
internal enum GlyphKind { Open, Locked, Writable, Problem, Mark, MarkDashed }

/// <summary>
/// 橋戸 kapısı: birim satırındaki durum işareti ve boş listenin işareti (ölçüler marka taslağının SVG'leri; WinUI'deki GateGlyph.xaml
/// ile aynı). Durum işaretleri 24'lük ızgarada çizgi 1,6; marka işareti 48'lik ızgarada çizgi 3,2 (burada yarısı). Renk ROLDEN (canlı).
/// </summary>
internal sealed class GateGlyph : Viewbox
{
    public GateGlyph(GlyphKind kind, double size)
    {
        Width = size;
        Height = size;
        var role = kind switch
        {
            GlyphKind.Writable => Role.Writable,
            GlyphKind.Problem => Role.Alert,
            GlyphKind.Mark or GlyphKind.MarkDashed => Role.Hair,
            _ => Role.Ink,
        };
        var brush = Look.Brush(role);
        bool dashed = kind is GlyphKind.Problem or GlyphKind.MarkDashed;
        WpfPath P(string data, bool fill = false) => new()
        {
            Data = Geometry.Parse(data), Stroke = brush, StrokeThickness = 1.6, Fill = fill ? brush : null,
            StrokeDashArray = dashed ? [1.875, 1.5] : null,
        };
        var grid = new Grid { Width = 24, Height = 24 };
        if (kind is GlyphKind.Mark or GlyphKind.MarkDashed)
        {
            // 48'lik ızgaradaki güverte (5,11.5 Q24,5.5 43,11.5) ve kapı (12.5–35.5, kemer 26, ayak 42)
            grid.Children.Add(P("M2.5,5.75 Q12,2.75 21.5,5.75"));
            grid.Children.Add(P("M6.25,21 V13 A5.75,5.75 0 0 1 17.75,13 V21"));
        }
        else
        {
            grid.Children.Add(P("M2,4.6 Q12,2.2 22,4.6"));
            grid.Children.Add(kind == GlyphKind.Locked ? P("M6,21 V12.5 A6,6 0 0 1 18,12.5 V21 Z", fill: true) : P("M6,21 V12.5 A6,6 0 0 1 18,12.5 V21"));
            if (kind == GlyphKind.Writable) grid.Children.Add(new WpfPath { Data = Geometry.Parse("M9.5,16 H14.5 V21 H9.5 Z"), Fill = brush });
        }
        Child = grid;
        IsHitTestVisible = false;
        Focusable = false;
    }

    internal static GlyphKind Of(VolumeGlyph g) => g switch
    {
        VolumeGlyph.Writable => GlyphKind.Writable,
        VolumeGlyph.Locked => GlyphKind.Locked,
        VolumeGlyph.Problem => GlyphKind.Problem,
        _ => GlyphKind.Open,
    };
}
