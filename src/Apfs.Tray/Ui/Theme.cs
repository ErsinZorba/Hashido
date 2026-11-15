using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Apfs.Windows;

namespace Apfs.Tray.Ui;

/// <summary>Renkler, yazı tipleri ve ortak çizim yardımcıları. Tek yerde tutulur ki pencere, panel ve parola penceresi aynı görünsün.</summary>
internal static class Theme
{
    // ---- Paletler (1.6.0: iki tasarım × açık/koyu) ----
    // 橋戸 marka (1.5.0): kâğıt, mürekkep, tek vurgu zinober. Zinober (Shu) yalnız dikkat isteyen yerde: yazılabilir birim,
    // dolmak üzere disk, sorun, "Güvenle çıkar"; "Yazmaya izin ver" kincha (金茶). Windows 11: Fluent nötrleri + Windows'un
    // vurgu rengi (AccentPalette). Denetimler rengi ROLDEN alır (Alert, Caution, Focus, Accent…), sabit renk yok.

    /// <summary>
    /// Renk takımı; dört palet aynı adları taşır. Roller: <see cref="Alert"/> sorun/uyarı (az boş alan, parola hatası,
    /// Güvenle çıkar), <see cref="WritableMark"/> yazılabilir birim, <see cref="Caution"/> dikkatli eylem (Yazmaya izin ver),
    /// <see cref="Focus"/> odak halkası, <see cref="Accent"/>/<see cref="OnAccent"/> birincil düğme ve işaretli kutu dolgusu
    /// ve üstündeki işaret/metin, <see cref="Link"/> bağlantı metni. <see cref="Light"/>/<see cref="Dark"/> markadır (adları
    /// düzenekler yansımayla okur).
    /// </summary>
    internal sealed record Palette(Color Paper, Color Washi, Color Ink, Color InkSoft, Color Hair, Color Track, Color Bar, Color Shu,
        Color Alert, Color WritableMark, Color Caution, Color Focus, Color Accent, Color OnAccent, Color Link, bool IsDark, AppDesign Design)
    {
        public static readonly Palette Light = Brand(false);
        public static readonly Palette Dark = Brand(true);

        private static Palette Brand(bool dark)
        {
            if (!dark)
            {
                Color paper = Hex(0xFBFAF7), ink = Hex(0x1E1C19), shu = Hex(0xC2412A);
                return new(paper, Hex(0xF5F2EC), ink, Hex(0x6B665E), Hex(0xDCD6CB), Hex(0xE6E1D7), Hex(0x4A4640), shu,
                    Alert: shu, WritableMark: shu, Caution: Hex(0x9A6200), Focus: shu, Accent: ink, OnAccent: paper, Link: ink,
                    IsDark: false, Design: AppDesign.Brand);
            }
            Color dPaper = Hex(0x1B1A18), dInk = Hex(0xECE7DF), dShu = Hex(0xE4664C);
            return new(dPaper, Hex(0x141312), dInk, Hex(0x9D978D), Hex(0x34312C), Hex(0x2E2B27), Hex(0xBDB6AA), dShu,
                Alert: dShu, WritableMark: dShu, Caution: Hex(0xD9A44A), Focus: dShu, Accent: dInk, OnAccent: dPaper, Link: dInk,
                IsDark: true, Design: AppDesign.Brand);
        }

        /// <summary>Windows 11 (Fluent) paleti: nötrler sabit, vurgu Windows'un vurgu renginden.</summary>
        public static Palette Windows(bool dark, AccentColors accent)
        {
            if (!dark)
            {
                Color caution = Hex(0x9D5D00);
                return new(Hex(0xF3F3F3), Hex(0xFBFBFB), Hex(0x1A1A1A), Hex(0x5D5D5D), Hex(0xE5E5E5), Hex(0xEAEAEA), accent.Dark1,
                    Hex(0xC2412A), Alert: Hex(0xC42B1C), WritableMark: caution, Caution: caution, Focus: Hex(0x1A1A1A),
                    Accent: accent.Dark1, OnAccent: Hex(0xFFFFFF), Link: accent.Dark2, IsDark: false, Design: AppDesign.Windows);
            }
            Color dCaution = Hex(0xFCE100);
            return new(Hex(0x202020), Hex(0x2B2B2B), Hex(0xFFFFFF), Hex(0xC5C5C5), Hex(0x333333), Hex(0x2D2D2D), accent.Light2,
                Hex(0xE4664C), Alert: Hex(0xFF99A4), WritableMark: dCaution, Caution: dCaution, Focus: Hex(0xFFFFFF),
                Accent: accent.Light2, OnAccent: Hex(0x000000), Link: accent.Light3, IsDark: true, Design: AppDesign.Windows);
        }

        internal static Color Hex(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));
    }

    /// <summary>
    /// Windows'un vurgu renginin tonları (HKCU <c>…\Explorer\Accent\AccentPalette</c>, 8 × RGBA: [0] Light3, [1] Light2,
    /// [4] Dark1, [5] Dark2). Fluent: açık temada dolgu Dark1, bağlantı Dark2; koyuda dolgu Light2, bağlantı Light3.
    /// </summary>
    internal readonly record struct AccentColors(Color Light3, Color Light2, Color Dark1, Color Dark2)
    {
        /// <summary>Değer yoksa ya da bozuksa: Windows'un varsayılan mavisi.</summary>
        public static readonly AccentColors Default = new(Palette.Hex(0x99EBFF), Palette.Hex(0x4CC2FF), Palette.Hex(0x0067C0), Palette.Hex(0x003E92));

        public static AccentColors FromBytes(byte[]? b)
        {
            if (b is not { Length: >= 32 }) return Default;
            Color At(int i) => Color.FromArgb(255, b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
            return new(At(0), At(1), At(4), At(5));
        }
    }

    /// <summary>
    /// Paletin girdileri; null = o girdi OKUNAMADI (yetki/G/Ç hatası — değer yoksa varsayılan verilir, null değil).
    /// <paramref name="DevTheme"/>/<paramref name="DevDesign"/> geliştirme kancaları (<c>APFS_DEV_THEME</c>, <c>APFS_DEV_DESIGN</c>).
    /// </summary>
    internal sealed record PaletteInputs(string? DevTheme, string? DevDesign, ThemeMode? Mode, AppDesign? Design, bool? SystemDark,
        AccentColors? Accent);

    /// <summary>
    /// Saf palet seçimi: tasarım = <c>APFS_DEV_DESIGN</c> &gt; kayıtlı tasarım; koyu mu = <c>APFS_DEV_THEME</c> &gt; kayıtlı tema
    /// (açık/koyu) &gt; Windows'un uygulama modu; Windows tasarımında vurgu = AccentPalette. Okunamayan girdi (null): açılışta
    /// (<paramref name="current"/> null) varsayılan, çalışırken GEÇERLİ paletin değeri — geçici bir okuma hatası paleti
    /// değiştirip tepsiyi yeniden başlatmasın (sonraki okuma tutunca geri başlatırdı).
    /// </summary>
    internal static Palette Resolve(PaletteInputs i, Palette? current)
    {
        AppDesign design = i.DevDesign?.Trim().ToLowerInvariant() switch
        {
            "windows" => AppDesign.Windows,
            "brand" => AppDesign.Brand,
            _ => i.Design ?? current?.Design ?? AppDesign.Windows, // varsayılan Windows 11 (kullanıcı kararı 2026-11-15)
        };
        bool dark = i.DevTheme?.Trim().ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => i.Mode switch
            {
                ThemeMode.Light => false,
                ThemeMode.Dark => true,
                ThemeMode.System => i.SystemDark ?? current?.IsDark ?? false,
                // Tema ayarı okunamadı: çalışırken geçerli palet; açılışta varsayılan "Windows'a uy" (1.6.1: önceden açık)
                _ => current?.IsDark ?? i.SystemDark ?? false,
            },
        };
        if (design == AppDesign.Brand) return dark ? Palette.Dark : Palette.Light;
        AccentColors accent = i.Accent ?? (current is { Design: AppDesign.Windows } c && c.IsDark == dark
            ? (dark ? AccentColors.Default with { Light2 = c.Accent, Light3 = c.Link } : AccentColors.Default with { Dark1 = c.Accent, Dark2 = c.Link })
            : AccentColors.Default);
        return Palette.Windows(dark, accent);
    }

    /// <summary>
    /// Çalışan tepsi yeni palet için yeniden başlamalı mı: yalnız tasarım ya da açık/koyu değiştiyse. Yalnız Windows'un vurgu
    /// rengi değiştiyse HAYIR (1.6.1, kullanıcı kararı): "vurguyu arka plandan seç" + duvar kâğıdı slayt gösterisi her kâğıt
    /// değişiminde tepsiyi yeniden başlatıyordu; yeni vurgu program bir sonraki açılışında gelir.
    /// </summary>
    internal static bool NeedsRestart(Palette next, Palette current) => next.Design != current.Design || next.IsDark != current.IsDark;

    /// <summary>Girdileri okuyup paleti seçer (<see cref="Resolve"/>). <paramref name="current"/>: çalışırken geçerli palet.</summary>
    public static Palette EffectivePalette(Palette? current = null) => Resolve(ReadInputs(), current);

    internal static PaletteInputs ReadInputs() => new(
        Environment.GetEnvironmentVariable("APFS_DEV_THEME"),
        Environment.GetEnvironmentVariable("APFS_DEV_DESIGN"),
        UserSettings.TryReadThemeMode(out var mode) ? mode : null,
        UserSettings.TryReadDesign(out var design) ? design : null,
        ReadHkcu(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", out var light)
            ? light is int v && v == 0 : null,
        ReadHkcu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", out var accent)
            ? AccentColors.FromBytes(accent as byte[]) : null);

    /// <summary>HKCU değeri; false yalnız okunamadığında (anahtar/değer yoksa true + null).</summary>
    private static bool ReadHkcu(string path, string name, out object? value)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            value = key?.GetValue(name);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Geçerli renk takımı: süreç başında seçilir (<see cref="EffectivePalette"/>); çalışırken değişmez. Seçim değişince
    /// (Ayarlar, Windows teması, vurgu rengi) tepsi boştayken yeniden başlar (<c>TrayApp</c>).
    /// </summary>
    public static Palette Colors { get; set; } = EffectivePalette();

    /// <summary>Koyu tema seçili mi.</summary>
    public static bool IsDark => Colors.IsDark;

    /// <summary>Windows 11 tasarımı mı (değilse 橋戸 marka).</summary>
    public static bool IsWindows => Colors.Design == AppDesign.Windows;

    /// <summary>Pencere zemini (kâğıt).</summary>
    public static Color Paper => Colors.Paper;
    /// <summary>Kâğıttan bir ton koyu zemin (metin kutuları, salt okunur alanlar).</summary>
    public static Color Washi => Colors.Washi;
    /// <summary>Mürekkep: metin, çizgi, birincil düğme dolgusu.</summary>
    public static Color Ink => Colors.Ink;
    /// <summary>Seyreltilmiş mürekkep: ikincil metin.</summary>
    public static Color InkSoft => Colors.InkSoft;
    /// <summary>İnce ayırıcı çizgi.</summary>
    public static Color Hair => Colors.Hair;
    /// <summary>Doluluk çubuğunun yolu; simge düğmesinin üzerine gelme zemini.</summary>
    public static Color Track => Colors.Track;
    /// <summary>Doluluk çubuğu.</summary>
    public static Color Bar => Colors.Bar;
    /// <summary>Zinober (marka rengi; denetimler rolleri kullanır).</summary>
    public static Color Shu => Colors.Shu;
    /// <summary>Mürekkep dolgunun üstündeki metin.</summary>
    public static Color OnInk => Colors.Paper;
    /// <summary>Sorun ve geri alınmaz eylem: sorunlu birim, az boş alan, parola hatası, Güvenle çıkar.</summary>
    public static Color Alert => Colors.Alert;
    /// <summary>Yazılabilir birimin işareti ve durum yazısı.</summary>
    public static Color WritableMark => Colors.WritableMark;
    /// <summary>Dikkatli eylem: Yazmaya izin ver.</summary>
    public static Color Caution => Colors.Caution;
    /// <summary>Klavye odak halkası.</summary>
    public static Color Focus => Colors.Focus;
    /// <summary>Birincil düğme ve işaretli onay kutusu dolgusu.</summary>
    public static Color Accent => Colors.Accent;
    /// <summary>Vurgu dolgusunun üstündeki metin/işaret.</summary>
    public static Color OnAccent => Colors.OnAccent;
    /// <summary>Bağlantı metni.</summary>
    public static Color Link => Colors.Link;

    /// <summary>
    /// Arayüz dili Asya diliyse o dilin Windows arayüz yazı tipi: Segoe UI'ın yedek yazı tipi Windows'un diline göre seçilir,
    /// Türkçe Windows'ta Çince seçilince Japonca ve Çince biçimli karakterler karışıyordu. Dil, formlardan önce ayarlanır.
    /// </summary>
    private static readonly string FontName = PickFontName();

    private static string PickFontName()
    {
        var installed = new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet();
        string? asian = System.Globalization.CultureInfo.CurrentUICulture.Name switch
        {
            "ja" => "Yu Gothic UI",
            "zh-Hans" => "Microsoft YaHei UI",
            "ko" => "Malgun Gothic",
            _ => null,
        };
        if (asian is not null && installed.Contains(asian)) return asian;
        return installed.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    }

    private static readonly Dictionary<(float, FontStyle), Font> Fonts = [];

    /// <summary>Paylaşılan yazı tipi (önbellekten; elden çıkarılmamalı).</summary>
    public static Font Font(float size = 9.75f, FontStyle style = FontStyle.Regular)
    {
        if (!Fonts.TryGetValue((size, style), out var f))
            Fonts[(size, style)] = f = new Font(FontName, size, style);
        return f;
    }

    public static Font Body => Font();
    public static Font Bold => Font(9.75f, FontStyle.Bold);
    public static Font Small => Font(8.75f);

    /// <summary>
    /// Başlık yazı tipi. Marka: Sitka Heading (Windows'la gelir) → Georgia → gövde. Windows 11 tasarımı (1.6.0): Segoe UI Variable
    /// Display Semibold (GDI aile adı kesik: "…Semib") → Segoe UI Semibold → gövde. Japonca, Çince ve Korecede Latin yazı tipleri
    /// o dilin karakterlerini taşımaz; orada gövdenin Asya yazı tipi (<see cref="PickFontName"/>) kullanılır. Punto iki
    /// tasarımda aynı (yerleşim kaymasın).
    /// </summary>
    private static string HeadingName => _headingName ??= PickHeadingName();
    private static string? _headingName;

    private static string PickHeadingName()
    {
        if (FontName is "Yu Gothic UI" or "Microsoft YaHei UI" or "Malgun Gothic") return FontName;
        var installed = new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet();
        string[] order = IsWindows
            ? ["Segoe UI Variable Display Semib", "Segoe UI Variable Display Semibold", "Segoe UI Semibold"]
            : ["Sitka Heading", "Georgia"];
        return order.FirstOrDefault(installed.Contains) ?? FontName;
    }

    // ---- Windows 11 tasarımının denetim dolguları (Fluent ControlFill*: yarı saydam beyazın kâğıt üstündeki karşılığı) ----

    /// <summary>İkincil düğmenin dolgusu: 0 bekleme, 1 üzerine gelme, 2 basılı (Fluent Default / Secondary / Tertiary).</summary>
    public static Color ControlFill(int state) => (IsDark, state) switch
    {
        (false, 0) => Palette.Hex(0xFDFDFD),
        (false, 1) => Palette.Hex(0xF9F9F9),
        (false, _) => Palette.Hex(0xF5F5F5),
        (true, 0) => Palette.Hex(0x2D2D2D),
        (true, 1) => Palette.Hex(0x323232),
        (true, _) => Palette.Hex(0x272727),
    };

    /// <summary>İkincil düğmenin çerçevesi ve "yükselti" kenarı (açıkta alt kenar bir ton koyu, koyuda üst kenar bir ton açık).</summary>
    public static (Color Border, Color Edge) ControlStroke => IsDark
        ? (Palette.Hex(0x353535), Palette.Hex(0x3C3C3C))
        : (Palette.Hex(0xE5E5E5), Palette.Hex(0xCCCCCC));

    private static readonly Dictionary<float, Font> Headings = [];

    /// <summary>Yazı tipi <see cref="Heading"/>'den mi (başlık etiketi seçimi için; önbellek nesnesiyle karşılaştırılır).</summary>
    public static bool IsHeading(Font font) => Headings.ContainsValue(font);

    /// <summary>Paylaşılan başlık yazı tipi (önbellekten; elden çıkarılmamalı).</summary>
    public static Font Heading(float size)
    {
        if (!Headings.TryGetValue(size, out var f))
            Headings[size] = f = new Font(HeadingName, size, FontStyle.Regular);
        return f;
    }

    /// <summary>Alt çubuktaki küçük 橋戸 imzası için Japonca yazı tipi (Latin yazı tiplerinde bu karakterler yok).</summary>
    public static Font Signature => _signature ??= new Font(
        new InstalledFontCollection().Families.Any(f => f.Name == "Yu Gothic UI") ? "Yu Gothic UI" : FontName, 8.75f);
    private static Font? _signature;

    /// <summary>96 DPI'a göre verilen pikseli denetimin ekranına ölçekler.</summary>
    public static int S(this Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96f);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // ---- Koyu tema (1.5.0 P3): pencere çerçevesi, kaydırma çubukları, Windows denetimleri, menüler ----
    // Açık temada hiçbirine dokunulmaz (Windows'un kendi görünüşü). İletişim kutuları 1.6.0'dan beri temalı (Ui/MessageDialog);
    // araç ipuçları Windows'un, açık kalır (kabul).

    /// <summary>Koyu temada başlık çubuğu ve pencere kenarlığı koyu (DWMWA_USE_IMMERSIVE_DARK_MODE). Tutamaç kurulunca uygulanır.</summary>
    public static void ApplyFrame(Form form) => WhenCreated(form, h =>
    {
        int dark = 1;
        _ = DwmSetWindowAttribute(h, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    });

    /// <summary>Koyu temada denetimin kaydırma çubukları koyu (Gezgin'in koyu teması).</summary>
    public static void DarkScrollbars(Control c) => WhenCreated(c, h => _ = SetWindowTheme(h, "DarkMode_Explorer", null));

    /// <summary>Açılır liste: koyu temada paletle (seçili satır ve liste Windows'un koyu açılır kutu temasıyla).</summary>
    public static void Style(ComboBox combo)
    {
        if (!IsDark) return;
        combo.BackColor = Paper;
        combo.ForeColor = Ink;
        WhenCreated(combo, h =>
        {
            _ = SetWindowTheme(h, "DarkMode_CFD", null);
            var info = new ComboBoxInfo { Size = Marshal.SizeOf<ComboBoxInfo>() };
            if (GetComboBoxInfo(h, ref info) && info.List != IntPtr.Zero) _ = SetWindowTheme(info.List, "DarkMode_Explorer", null);
        });
    }

    /// <summary>Sağ tık ve dil menüleri: kâğıt zemin, mürekkep yazı, seçili satır Track (iki temada da).</summary>
    public static ToolStripRenderer MenuRenderer => new PaletteMenuRenderer();

    private static void WhenCreated(Control c, Action<IntPtr> apply)
    {
        if (!IsDark) return;
        if (c.IsHandleCreated) apply(c.Handle);
        c.HandleCreated += (_, _) => apply(c.Handle); // tutamaç yeniden kurulursa yine
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string appName, string? idList);

    [StructLayout(LayoutKind.Sequential)]
    private struct ComboBoxInfo
    {
        public int Size;
        public int ItemLeft, ItemTop, ItemRight, ItemBottom;         // RECT rcItem (okunmuyor)
        public int ButtonLeft, ButtonTop, ButtonRight, ButtonBottom; // RECT rcButton (okunmuyor)
        public int ButtonState;
        public IntPtr Combo, Edit, List;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetComboBoxInfo(IntPtr hwnd, ref ComboBoxInfo info);

    private sealed class PaletteMenuRenderer() : ToolStripProfessionalRenderer(new PaletteMenuColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ink : InkSoft;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Ink;
            base.OnRenderArrow(e);
        }

        /// <summary>Seçim işareti: Windows'un siyah işaret resmi koyu zeminde görünmüyordu; mürekkep çizgiyle çizilir.</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            if (r.Width <= 0) return;
            var g = e.Graphics;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Math.Min(r.Width, r.Height) / 16f;
            using var pen = new Pen(Ink, 1.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [
                new PointF(r.X + 3.5f * k, r.Y + 8.5f * k), new PointF(r.X + 6.5f * k, r.Y + 11.5f * k), new PointF(r.X + 12.5f * k, r.Y + 4.5f * k),
            ]);
            g.SmoothingMode = old;
        }
    }

    private sealed class PaletteMenuColors : ProfessionalColorTable
    {
        public PaletteMenuColors() => UseSystemColors = false;
        public override Color ToolStripDropDownBackground => Paper;
        public override Color ImageMarginGradientBegin => Paper;
        public override Color ImageMarginGradientMiddle => Paper;
        public override Color ImageMarginGradientEnd => Paper;
        public override Color MenuBorder => Hair;
        public override Color MenuItemBorder => Track;
        public override Color MenuItemSelected => Track;
        public override Color MenuItemSelectedGradientBegin => Track;
        public override Color MenuItemSelectedGradientEnd => Track;
        public override Color MenuItemPressedGradientBegin => Track;
        public override Color MenuItemPressedGradientEnd => Track;
        public override Color SeparatorDark => Hair;
        public override Color SeparatorLight => Paper;
        public override Color CheckBackground => Paper;
        public override Color CheckSelectedBackground => Track;
        public override Color CheckPressedBackground => Track;
    }

    private static readonly Dictionary<int, Icon> Icons = [];

    /// <summary>Uygulama simgesi (gömülü app.ico) istenen boyutta.</summary>
    public static Icon AppIcon(int size)
    {
        if (Icons.TryGetValue(size, out var icon)) return icon;
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("app.ico")!;
        return Icons[size] = new Icon(s, size, size);
    }
}
