using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Apfs.Windows;
using Apfs.Windows.Tray;
using static Apfs.Tray.Native;
using WpfColor = System.Windows.Media.Color;
using WpfThemeMode = System.Windows.ThemeMode;

namespace Apfs.Tray;

/// <summary>Metnin, ikonun ya da düğmenin rengi ROLDEN (<see cref="Palette"/>; sabit renk yok).</summary>
internal enum Role { Ink, InkSoft, Hair, Alert, Writable, Caution, Link }

/// <summary>
/// Görünüm (wpfPlan 2.1; prototip <c>Look</c>'un yaklaşımı): WPF'in .NET 10 Fluent teması (<see cref="Application.ThemeMode"/>).
/// Windows 11 tasarımında Fluent olduğu gibi (Mica, Windows'un vurgu rengi); Hashido tasarımında Fluent'in kaynak anahtarları
/// kâğıt, mürekkep ve zinoberle ezilir (denetimler aynı, renk ve başlık yazı tipi değişir). Tasarım süreç başında seçilir (değişince
/// program yeniden açılır), açık/koyu çalışırken değişir (Ek A R2 ⟳: Windows'tan da Ayarlar › Tema'dan da yeniden başlatmadan).
/// Rol fırçaları paylaşılan, değişebilir fırçalardır: açık/koyu değişince rengi yerinde değişir, kodla verilen öğeler kendiliğinden
/// güncellenir. Asya dillerinde gövde ve başlık o dilin Windows yazı tipi, pencerenin dili arayüz dili (R4).
/// </summary>
internal static class Look
{
    /// <summary>Geçerli palet (tasarım + açık/koyu).</summary>
    public static Palette Colors { get; private set; } = Palette.Light;

    public static bool IsWindows => Colors.Design == AppDesign.Windows;
    public static bool IsDark => Colors.IsDark;

    /// <summary>Açık/koyu değişti (pencereler başlık çubuğunu günceller; rol fırçaları zaten güncel).</summary>
    public static event Action? ThemeChanged;

    /// <summary>Görüntü düzeneği: malzeme (Mica) yerine düz zemin (ekran dışında Windows'un malzemesi çizilmez).</summary>
    public static bool Plain { get; set; }

    private static Application? _app;
    private static readonly Dictionary<Role, SolidColorBrush> Roles = Enum.GetValues<Role>().ToDictionary(r => r, _ => new SolidColorBrush());
    private static readonly SolidColorBrush Paper = new(); // Hashido pencere zemini
    private static readonly List<object> Overrides = [];
    private static ImageSource? _icon;

    /// <summary>Paleti okur ve uygulamaya işler; hiçbir pencere kurulmadan ÖNCE (OnStartup başında) çağrılır.</summary>
    public static void Install(Application app)
    {
        _app = app;
        Colors = Palette.Resolve(Palette.ReadInputs(), null);
        Apply();
    }

    /// <summary>Fluent temasını (açık/koyu), rol renklerini ve Hashido ezmelerini geçerli palete göre kurar.</summary>
    private static void Apply()
    {
        var app = _app!;
        var p = Colors;
        app.ThemeMode = p.IsDark ? WpfThemeMode.Dark : WpfThemeMode.Light;
        Roles[Role.Ink].Color = C(p.Ink.ToArgb());
        Roles[Role.InkSoft].Color = C(p.InkSoft.ToArgb());
        Roles[Role.Hair].Color = C(p.Hair.ToArgb());
        Roles[Role.Alert].Color = C(p.Alert.ToArgb());
        Roles[Role.Writable].Color = C(p.WritableMark.ToArgb());
        Roles[Role.Caution].Color = C(p.Caution.ToArgb());
        Roles[Role.Link].Color = C(p.Link.ToArgb());
        Paper.Color = C((p.IsDark ? p.Paper : p.Washi).ToArgb());

        // Ezmeler yerinde değiştirilir; yalnız artık kullanılmayan anahtar silinir (her kaynak değişimi bütün pencereleri dolaşır:
        // silip yeniden eklemek açık/koyu geçişinde işi ikiye katlıyordu)
        var r = app.Resources;
        var previous = new HashSet<object>(Overrides);
        Overrides.Clear();
        void V(string key, object value)
        {
            r[key] = value;
            Overrides.Add(key);
            previous.Remove(key);
        }
        void B(string key, WpfColor c) => V(key, new SolidColorBrush(c));
        if (!IsWindows)
        {
            // Hashido: Windows 11 denetimlerinin zemin, kart, metin, vurgu ve uyarı anahtarları kâğıt/mürekkep/zinober (prototip)
            bool dark = p.IsDark;
            WpfColor ink = C(p.Ink.ToArgb()), paper = C(p.Paper.ToArgb());
            B("TextFillColorPrimaryBrush", ink);
            B("TextFillColorSecondaryBrush", C(p.InkSoft.ToArgb()));
            B("CardBackgroundFillColorDefaultBrush", dark ? Hex(0x24221F) : paper);
            B("CardBackgroundFillColorSecondaryBrush", dark ? Hex(0x2B2925) : Hex(0xFFFFFF));
            B("CardStrokeColorDefaultBrush", C(p.Hair.ToArgb()));
            B("SolidBackgroundFillColorBaseBrush", dark ? paper : C(p.Washi.ToArgb()));
            B("ControlFillColorDefaultBrush", dark ? Hex(0x2B2925) : Hex(0xFFFFFF));
            B("ControlFillColorSecondaryBrush", dark ? Hex(0x33302C) : Hex(0xF7F5F0));
            B("ControlFillColorTertiaryBrush", dark ? Hex(0x24221F) : Hex(0xF3F0EA));
            // Vurgu = mürekkep (açıkta koyu, koyuda açık): birincil düğme, onay kutusu, ilerleme çubuğu mürekkep olur
            B("AccentFillColorDefaultBrush", ink);
            B("AccentFillColorSecondaryBrush", Alpha(ink, 0xE6));
            B("AccentFillColorTertiaryBrush", Alpha(ink, 0xCC));
            B("TextOnAccentFillColorPrimaryBrush", C(p.OnAccent.ToArgb()));
            B("AccentTextFillColorPrimaryBrush", ink);
            // WPF Fluent'te denetim anahtarları sözlük yüklenirken çözülmüş fırçalar: ayrıca ezilir (şablonlar DynamicResource ile okur)
            foreach (var s in new[] { "", "PointerOver", "Pressed" })
            {
                B("AccentButtonBackground" + s, s == "Pressed" ? Alpha(ink, 0xCC) : s == "PointerOver" ? Alpha(ink, 0xE6) : ink);
                B("AccentButtonForeground" + s, C(p.OnAccent.ToArgb()));
                B("AccentButtonBorderBrush" + s, ink);
            }
            // Fluent'in vurgu renginde çözülmüş öbür anahtarlar (kullandığımız denetimler): onay kutusu, açılır listenin odak çizgisi ve
            // seçim çubuğu, metin/parola alanının odak çizgisi — Hashido'da mürekkep (WinUI'de SystemAccentColor ezmesi bunları da boyuyordu)
            foreach (var state in new[] { "Checked", "Indeterminate" })
                foreach (var part in new[] { "CheckBoxCheckBackgroundFill", "CheckBoxCheckBackgroundStroke" })
                {
                    B(part + state, ink);
                    B(part + state + "PointerOver", Alpha(ink, 0xE6));
                    B(part + state + "Pressed", Alpha(ink, 0xCC));
                }
            B("CheckBoxCheckGlyphForeground", C(p.OnAccent.ToArgb()));
            B("CheckBoxCheckGlyphForegroundPressed", C(p.OnAccent.ToArgb()));
            B("ComboBoxBorderBrushFocused", ink);
            B("ComboBoxItemPillFillBrush", ink);
            B("TextControlFocusedBorderBrush", ink);
            // Odaktaki metin/parola alanının kenarı: altta 2 px vurgu çizgisi, gerisi ince kenar (Fluent'in degradesi, mürekkeple)
            foreach (var key in new[] { "TextControlBorderBrushFocused", "TextControlElevationBorderFocusedBrush" })
                V(key, new LinearGradientBrush
                {
                    MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, 2),
                    RelativeTransform = new ScaleTransform(1, -1, 0, 0.5),
                    GradientStops = { new GradientStop(ink, 1.0), new GradientStop(C(p.Hair.ToArgb()), 1.0) },
                });
            B("ProgressBarForeground", C(p.Bar.ToArgb()));
            B("ProgressBarBackground", C(p.Hair.ToArgb()));
            B("SystemFillColorCautionBrush", C(p.Caution.ToArgb()));
            B("SystemFillColorCriticalBrush", C(p.Alert.ToArgb()));
            B("SystemFillColorCriticalBackgroundBrush", dark ? WpfColor.FromArgb(0x33, 0xE4, 0x66, 0x4C) : WpfColor.FromArgb(0x22, 0xC2, 0x41, 0x2A));
            V("SystemAccentColor", C(p.Bar.ToArgb()));
            V("SystemAccentColorLight1", C(p.Bar.ToArgb()));
            V("SystemAccentColorLight2", ink);
            V("SystemAccentColorLight3", ink);
            V("SystemAccentColorDark1", ink);
            V("SystemAccentColorDark2", ink);
            V("SystemAccentColorDark3", C(p.Bar.ToArgb()));
        }
        if (AsianFont is { } asian) V("ContentControlThemeFontFamily", new FontFamily(asian));
        foreach (var k in previous) r.Remove(k);
    }

    /// <summary>
    /// Windows'un teması ya da Ayarlar › Tema değişti: palet yeniden okunur. Açık/koyu değiştiyse pencereler yeniden başlamadan
    /// güncellenir (R2 ⟳). Tasarım bu süreçte değişmez (değişince program yeniden açılır: Ayarlar). Yalnız vurgu değiştiyse bir şey
    /// olmaz, yeni vurgu sonraki açılışta (1.6.1). Okunamayan girdi geçerli değeri korur (<see cref="Palette.Resolve"/>).
    /// </summary>
    public static void Refresh()
    {
        var next = Palette.Resolve(Palette.ReadInputs() with { DevDesign = IsWindows ? "windows" : "brand" }, Colors);
        bool darkChanged = next.IsDark != Colors.IsDark;
        Colors = next;
        if (!darkChanged || _app is null) return;
        Apply();
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Rolün fırçası (paylaşılan; açık/koyu değişince rengi yerinde değişir). Kaynak sözlüğüne KONMAZ: uygulama sözlüğü fırçaları
    /// dondurur (renk değişemezdi); sözlüğe girecek yerde <see cref="Bound"/>.
    /// </summary>
    public static Brush Brush(Role role) => Roles[role];

    /// <summary>Rol rengine bağlı yeni fırça (bağlı olduğu için dondurulamaz: kaynak sözlüğüne konabilir, yine canlı kalır).</summary>
    public static Brush Bound(Role role)
    {
        var b = new SolidColorBrush();
        System.Windows.Data.BindingOperations.SetBinding(b, SolidColorBrush.ColorProperty,
            new System.Windows.Data.Binding(nameof(SolidColorBrush.Color)) { Source = Roles[role] });
        return b;
    }

    /// <summary>Düğmenin metni rol renginde (üzerine gelince ve basınca da: Fluent şablonunun okuduğu anahtarlar düğmede ezilir).</summary>
    public static T Tone<T>(T button, Role role) where T : Control
    {
        var b = Bound(role);
        button.Foreground = b;
        button.Resources["ButtonForegroundPointerOver"] = b;
        button.Resources["ButtonForegroundPressed"] = b;
        return button;
    }

    // ---- Pencere ----

    /// <summary>
    /// Pencerenin görünümü: yazı tipi ve dil, zemin, başlık çubuğu (açık/koyu değişince yenilenir). Zemin Windows 11 tasarımında
    /// Fluent'in kendisi (Windows 11'de Mica), Hashido'da kâğıt; görüntü düzeneğinde (<see cref="Plain"/>) düz.
    /// </summary>
    public static void Frame(Window w)
    {
        w.FontFamily = BodyFont;
        w.FontSize = 14; // Fluent gövde boyu (WPF'in varsayılanı 12; açılır liste, onay kutusu ve menü metni bunu alır)
        w.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag);
        if (!IsWindows) w.Background = Paper;
        else if (Plain) w.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        void Paint() => Caption(w);
        w.SourceInitialized += (_, _) => Paint();
        ThemeChanged += Paint;
        w.Closed += (_, _) => ThemeChanged -= Paint;
    }

    /// <summary>
    /// Başlık çubuğu (R6): koyu temada koyu (DWM); Hashido'da Windows'un kendi başlık çubuğu kâğıt ve mürekkep renginde
    /// (DWMWA_CAPTION_COLOR / TEXT_COLOR; Windows 11), Windows 11 tasarımında Windows'un rengi.
    /// </summary>
    private static void Caption(Window w)
    {
        IntPtr h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var p = Colors;
        int dark = p.IsDark ? 1 : 0;
        int caption = IsWindows ? unchecked((int)0xFFFFFFFF) : Ref((p.IsDark ? p.Paper : p.Washi).ToArgb()); // DWMWA_COLOR_DEFAULT
        int text = IsWindows ? unchecked((int)0xFFFFFFFF) : Ref(p.Ink.ToArgb());
        _ = DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }

    /// <summary>Uygulama simgesi (pencere simgesi; Windows uygun boyu seçer).</summary>
    public static ImageSource AppIcon => _icon ??= BitmapFrame.Create(new Uri(IconPath));

    private static string IconPath => System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");

    /// <summary>Mühür: simgenin istenen boya en yakın (ondan küçük olmayan) karesi (ekran ölçeğiyle).</summary>
    public static System.Windows.Controls.Image Seal(double size)
    {
        var frames = BitmapDecoder.Create(new Uri(IconPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
        int want = (int)Math.Ceiling(size * 2); // %200'e kadar ölçekte keskin
        var frame = frames.Where(f => f.PixelWidth >= want).OrderBy(f => f.PixelWidth).FirstOrDefault() ?? frames.OrderByDescending(f => f.PixelWidth).First();
        var image = new System.Windows.Controls.Image { Source = frame, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    // ---- Yazı tipleri ----

    /// <summary>
    /// Arayüz dili Asya diliyse o dilin Windows arayüz yazı tipi: Segoe UI'ın yedeği Windows'un diline göre seçilir, Türkçe
    /// Windows'ta Çince seçilince Japonca ve Çince biçimli karakterler karışıyordu (1.3.0).
    /// </summary>
    public static string? AsianFont => CultureInfo.CurrentUICulture.Name switch
    {
        "ja" => "Yu Gothic UI",
        "zh-Hans" => "Microsoft YaHei UI",
        "ko" => "Malgun Gothic",
        _ => null,
    };

    /// <summary>Gövde yazı tipi (Asya dillerinde o dilin yazı tipi).</summary>
    public static FontFamily BodyFont => new(AsianFont ?? "Segoe UI Variable Text, Segoe UI");

    /// <summary>Başlık yazı tipi: Hashido'da Sitka Heading (yoksa Georgia), Windows 11'de Segoe UI Variable Display; Asya dillerinde o dilin yazı tipi.</summary>
    public static FontFamily HeadingFont => new(AsianFont ?? (IsWindows ? "Segoe UI Variable Display, Segoe UI" : "Sitka Heading, Georgia"));

    /// <summary>Başlık metni: tasarımın yazı tipi (Hashido'da ince), rakamlar düz (Sitka'nın eski üslup rakamları sarkmasın; R5).</summary>
    public static TextBlock Heading(TextBlock t)
    {
        t.FontFamily = HeadingFont;
        t.FontWeight = !IsWindows && AsianFont is null ? FontWeights.Normal : FontWeights.SemiBold;
        Typography.SetNumeralStyle(t, FontNumeralStyle.Lining);
        return t;
    }

    // ---- Renk ----

    /// <summary>Paletin rengi (ARGB) → WPF rengi.</summary>
    private static WpfColor C(int argb) => WpfColor.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>Paletin rengi (ARGB) → DWM'nin COLORREF'i (0x00BBGGRR).</summary>
    private static int Ref(int argb) => ((argb >> 16) & 0xFF) | (argb & 0xFF00) | ((argb & 0xFF) << 16);

    private static WpfColor Alpha(WpfColor c, byte a) => WpfColor.FromArgb(a, c.R, c.G, c.B);

    private static WpfColor Hex(int rgb) => WpfColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
