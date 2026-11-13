using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Apfs.Tray.Ui;

/// <summary>Renkler, yazı tipleri ve ortak çizim yardımcıları. Tek yerde tutulur ki pencere, panel ve parola penceresi aynı görünsün.</summary>
internal static class Theme
{
    // ---- 橋戸 marka paleti (1.5.0): kâğıt, mürekkep, tek vurgu zinober ----
    // Zinober (Shu) yalnız dikkat isteyen yerde: yazılabilir birim, dolmak üzere disk, sorun. Her şey yolundaysa yalnız mühür.

    /// <summary>Renk takımı; açık ve koyu tema aynı adları taşır.</summary>
    internal sealed record Palette(Color Paper, Color Washi, Color Ink, Color InkSoft, Color Hair, Color Track, Color Bar, Color Shu)
    {
        public static readonly Palette Light = new(Hex(0xFBFAF7), Hex(0xF5F2EC), Hex(0x1E1C19), Hex(0x6B665E), Hex(0xDCD6CB),
            Hex(0xE6E1D7), Hex(0x4A4640), Hex(0xC2412A));
        public static readonly Palette Dark = new(Hex(0x1B1A18), Hex(0x141312), Hex(0xECE7DF), Hex(0x9D978D), Hex(0x34312C),
            Hex(0x2E2B27), Hex(0xBDB6AA), Hex(0xE4664C));

        private static Color Hex(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));
    }

    /// <summary>
    /// Geçerli renk takımı: süreç başında Windows'un uygulama temasından seçilir (<see cref="SystemPalette"/>); çalışırken
    /// değişmez. Windows teması değişince tepsi boştayken yeniden başlar (<c>TrayApp</c>).
    /// </summary>
    public static Palette Colors { get; set; } = SystemPalette();

    /// <summary>Koyu tema seçili mi.</summary>
    public static bool IsDark => Colors == Palette.Dark;

    /// <summary>
    /// Windows'un uygulama teması: HKCU <c>…\Themes\Personalize\AppsUseLightTheme</c> = 0 → koyu; değer yoksa ya da
    /// okunamazsa açık. Geliştirme kancası <c>APFS_DEV_THEME=dark|light</c> kaydı geçersiz kılar (görüntü düzenekleri; Claude
    /// araçlarının HKCU'su sanal).
    /// </summary>
    public static Palette SystemPalette()
    {
        switch (Environment.GetEnvironmentVariable("APFS_DEV_THEME")?.Trim().ToLowerInvariant())
        {
            case "dark": return Palette.Dark;
            case "light": return Palette.Light;
        }
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0 ? Palette.Dark : Palette.Light;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Palette.Light;
        }
    }

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
    /// <summary>Zinober: mühür ve dikkat.</summary>
    public static Color Shu => Colors.Shu;
    /// <summary>Mürekkep dolgunun üstündeki metin.</summary>
    public static Color OnInk => Colors.Paper;

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
    /// Başlık yazı tipi: Sitka Heading (Windows'la gelir) → Georgia → gövde. Japonca, Çince ve Korecede Latin serifi o dilin
    /// karakterlerini taşımaz; orada gövdenin Asya yazı tipi (<see cref="PickFontName"/>) kullanılır.
    /// </summary>
    private static readonly string HeadingName = PickHeadingName();

    private static string PickHeadingName()
    {
        if (FontName is "Yu Gothic UI" or "Microsoft YaHei UI" or "Malgun Gothic") return FontName;
        var installed = new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet();
        return installed.Contains("Sitka Heading") ? "Sitka Heading" : installed.Contains("Georgia") ? "Georgia" : FontName;
    }

    private static readonly Dictionary<float, Font> Headings = [];

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
    // Açık temada hiçbirine dokunulmaz (Windows'un kendi görünüşü). TaskDialog ve MessageBox Windows'un, açık kalır (kabul).

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

    /// <summary>Onay kutusu: koyu temada düz çizim (kutu mürekkep çizgi, işaret mürekkep); Windows'un açık kutusu koyu zeminde parlıyordu.</summary>
    public static void Style(CheckBox check)
    {
        if (!IsDark) return;
        check.FlatStyle = FlatStyle.Flat;
        check.FlatAppearance.BorderColor = InkSoft;
        check.FlatAppearance.CheckedBackColor = Paper;
        check.FlatAppearance.MouseOverBackColor = Track;
        check.FlatAppearance.MouseDownBackColor = Track;
        check.BackColor = Paper;
        check.ForeColor = Ink;
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
