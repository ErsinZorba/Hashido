using System.Drawing;

namespace Apfs.Windows.Tray;

// ---- Paletler (1.6.0: iki tasarım × açık/koyu; winuiPlan P1'de Apfs.Tray.Ui.Theme'den taşındı, saf kısım) ----
// 橋戸 marka (1.5.0): kâğıt, mürekkep, tek vurgu zinober. Zinober (Shu) yalnız dikkat isteyen yerde: yazılabilir birim,
// dolmak üzere disk, sorun, "Güvenle çıkar"; "Yazmaya izin ver" kincha (金茶). Windows 11: Fluent nötrleri + Windows'un
// vurgu rengi (AccentPalette). Denetimler rengi ROLDEN alır (Alert, Caution, Focus, Accent…), sabit renk yok.

/// <summary>
/// Renk takımı; dört palet aynı adları taşır. Roller: <see cref="Alert"/> sorun/uyarı (az boş alan, parola hatası,
/// Güvenle çıkar), <see cref="WritableMark"/> yazılabilir birim, <see cref="Caution"/> dikkatli eylem (Yazmaya izin ver),
/// <see cref="Focus"/> odak halkası, <see cref="Accent"/>/<see cref="OnAccent"/> birincil düğme ve işaretli kutu dolgusu
/// ve üstündeki işaret/metin, <see cref="Link"/> bağlantı metni. <see cref="Light"/>/<see cref="Dark"/> markadır.
/// </summary>
public sealed record Palette(Color Paper, Color Washi, Color Ink, Color InkSoft, Color Hair, Color Track, Color Bar, Color Shu,
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

    public static Color Hex(int rgb) => Color.FromArgb(255, Color.FromArgb(rgb));

    /// <summary>
    /// Saf palet seçimi: tasarım = <c>APFS_DEV_DESIGN</c> &gt; kayıtlı tasarım; koyu mu = <c>APFS_DEV_THEME</c> &gt; kayıtlı tema
    /// (açık/koyu) &gt; Windows'un uygulama modu; Windows tasarımında vurgu = AccentPalette. Okunamayan girdi (null): açılışta
    /// (<paramref name="current"/> null) varsayılan, çalışırken GEÇERLİ paletin değeri — geçici bir okuma hatası paleti
    /// değiştirip tepsiyi yeniden başlatmasın (sonraki okuma tutunca geri başlatırdı).
    /// </summary>
    public static Palette Resolve(PaletteInputs i, Palette? current)
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
        if (design == AppDesign.Brand) return dark ? Dark : Light;
        AccentColors accent = i.Accent ?? (current is { Design: AppDesign.Windows } c && c.IsDark == dark
            ? (dark ? AccentColors.Default with { Light2 = c.Accent, Light3 = c.Link } : AccentColors.Default with { Dark1 = c.Accent, Dark2 = c.Link })
            : AccentColors.Default);
        return Windows(dark, accent);
    }

    /// <summary>Girdileri okuyup paleti seçer (<see cref="Resolve"/>). <paramref name="current"/>: çalışırken geçerli palet.</summary>
    public static Palette Effective(Palette? current = null) => Resolve(ReadInputs(), current);

    public static PaletteInputs ReadInputs() => new(
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
}

/// <summary>
/// Windows'un vurgu renginin tonları (HKCU <c>…\Explorer\Accent\AccentPalette</c>, 8 × RGBA: [0] Light3, [1] Light2,
/// [4] Dark1, [5] Dark2). Fluent: açık temada dolgu Dark1, bağlantı Dark2; koyuda dolgu Light2, bağlantı Light3.
/// </summary>
public readonly record struct AccentColors(Color Light3, Color Light2, Color Dark1, Color Dark2)
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
public sealed record PaletteInputs(string? DevTheme, string? DevDesign, ThemeMode? Mode, AppDesign? Design, bool? SystemDark,
    AccentColors? Accent);
