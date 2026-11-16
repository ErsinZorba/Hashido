using System.Globalization;
using Apfs.Windows.Localization;
using Microsoft.Win32;

namespace Apfs.Windows;

/// <summary>
/// Tepsinin kullanıcı başına ayarları (dil ile aynı anahtar: <see cref="LanguageSetting.KeyPath"/>, HKCU). Kayıt okunamazsa
/// varsayılanlar geçerlidir; yazılamazsa sessizce geçilir (ayar bir sonraki açılışta yine sorulur/varsayılan kalır).
/// </summary>
public static class UserSettings
{
    private const string ReportsValue = "Reports";             // DWORD 1/0; yok = onay penceresi henüz cevaplanmadı
    private const string UpdateNotifyValue = "UpdateNotify";   // DWORD 1/0; yok = açık
    private const string UpdateLastCheckValue = "UpdateLastCheck"; // metin, ISO 8601 UTC
    private const string UpdateNotifiedValue = "UpdateNotified";   // metin, en son bildirilen sürüm ("1.4.0")
    private const string UpdateUrlValue = "UpdateUrl";             // metin, o sürümün Release sayfası

    /// <summary>Otomatik raporlama onayı: true/false, cevaplanmadıysa null (o hâlde hiçbir şey toplanmaz ve gönderilmez).</summary>
    public static bool? ReportsConsent
    {
        get => ReadInt(ReportsValue) is int v ? v != 0 : null;
        set => Write(ReportsValue, value is null ? null : value.Value ? 1 : 0);
    }

    /// <summary>"Yeni sürüm var" bildirimi (varsayılan açık).</summary>
    public static bool UpdateNotify
    {
        get => ReadInt(UpdateNotifyValue) is not 0;
        set => Write(UpdateNotifyValue, value ? 1 : 0);
    }

    public static DateTime? UpdateLastCheckUtc
    {
        get => ReadString(UpdateLastCheckValue) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        set => Write(UpdateLastCheckValue, value?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
    }

    public static string? UpdateNotifiedVersion
    {
        get => ReadString(UpdateNotifiedValue);
        set => Write(UpdateNotifiedValue, value);
    }

    public static string? UpdateUrl
    {
        get => ReadString(UpdateUrlValue);
        set => Write(UpdateUrlValue, value);
    }

    private const string TrayHintShownValue = "TrayHintShown"; // DWORD 1; yok = gösterilmedi

    /// <summary>
    /// "Program tepside çalışmaya devam ediyor" ipucu gösterildi mi (1.6.3): kullanıcı başına bir kez, kalıcı (önceden her
    /// oturumda ana pencerenin ilk kapatılışında çıkıyordu).
    /// </summary>
    public static bool TrayHintShown
    {
        get => ReadInt(TrayHintShownValue) is int v && v != 0;
        set => Write(TrayHintShownValue, value ? 1 : null);
    }

    /// <summary>
    /// Tepsinin tasarımı (1.6.0): sade Windows 11 (varsayılan, kullanıcı kararı 2026-11-15; 1.6.x'te marka) ya da 橋戸 marka.
    /// Yalnız "brand" markadır; değer yoksa ya da bilinmiyorsa → Windows 11.
    /// </summary>
    public static AppDesign Design
    {
        get => ParseDesign(ReadString(DesignValue));
        set => Write(DesignValue, value == AppDesign.Windows ? "windows" : "brand");
    }

    /// <summary>Tepsinin teması (1.6.0): Windows'a uy (varsayılan), açık ya da koyu. Bilinmeyen değer → Windows'a uy.</summary>
    public static ThemeMode ThemeMode
    {
        get => ParseTheme(ReadString(ThemeValue));
        set => Write(ThemeValue, value switch { ThemeMode.Light => "light", ThemeMode.Dark => "dark", _ => "system" });
    }

    /// <summary>
    /// <see cref="Design"/>'ı okur; false = kayıt OKUNAMADI (değer yoksa true + varsayılan). Çalışırken tema yeniden başlaması
    /// geçici bir okuma hatasını değişiklik saymasın diye (gidip gelen yeniden başlama).
    /// </summary>
    public static bool TryReadDesign(out AppDesign design)
    {
        bool ok = TryRead(DesignValue, out var v);
        design = ParseDesign(v as string);
        return ok;
    }

    /// <summary><see cref="ThemeMode"/>'u okur; false = kayıt okunamadı (bkz. <see cref="TryReadDesign"/>).</summary>
    public static bool TryReadThemeMode(out ThemeMode mode)
    {
        bool ok = TryRead(ThemeValue, out var v);
        mode = ParseTheme(v as string);
        return ok;
    }

    private const string DesignValue = "Design"; // metin: "brand" | "windows"
    private const string DesignDefaultValue = "DesignDefault"; // DWORD 2 = 1.6.2 varsayılan tasarım geçişi yapıldı

    /// <summary>
    /// 1.6.2 geçişi (kullanıcı kararı 2026-11-15), kullanıcı başına BİR KEZ: 1.6.0/1.6.1 Ayarlar'da tasarım ya da tema
    /// değişince ikisini birlikte yazıyordu, kayıtlı "brand" bilinçli seçimden ayırt edilemiyor → bir kez Windows 11 yapılır ve
    /// işaretlenir. Sonra Hashido'yu seçen markada kalır. Kayıt okunamazsa hiçbir şey yapılmaz (sonraki açılışta yeniden denenir).
    /// Tepsi paleti ilk kez okumadan önce çağrılmalı.
    /// </summary>
    public static void MigrateDesignDefault()
    {
        if (!TryRead(DesignDefaultValue, out var done) || done is int) return;
        if (!TryRead(DesignValue, out var design)) return;
        if (design is string s && string.Equals(s.Trim(), "brand", StringComparison.OrdinalIgnoreCase)) Write(DesignValue, "windows");
        Write(DesignDefaultValue, 2);
    }
    private const string ThemeValue = "Theme";   // metin: "system" | "light" | "dark"

    private static AppDesign ParseDesign(string? s) =>
        string.Equals(s?.Trim(), "brand", StringComparison.OrdinalIgnoreCase) ? AppDesign.Brand : AppDesign.Windows;

    private static ThemeMode ParseTheme(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    private static object? Read(string name) => TryRead(name, out var v) ? v : null;

    /// <summary>Değeri okur; false yalnız kayıt okunamadığında (yetki, G/Ç). Anahtar ya da değer yoksa true + null.</summary>
    private static bool TryRead(string name, out object? value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LanguageSetting.KeyPath);
            value = key?.GetValue(name);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            value = null;
            return false;
        }
    }

    private static int? ReadInt(string name) => Read(name) is int v ? v : null;

    private static string? ReadString(string name) => Read(name) as string;

    private static void Write(string name, object? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LanguageSetting.KeyPath);
            if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }
}

/// <summary>Tepsinin görünümü: 橋戸 marka ya da sade Windows 11 (renk, yazı tipi, düğme davranışı; yerleşim aynı).</summary>
public enum AppDesign { Brand, Windows }

/// <summary>Tepsinin teması: Windows'un uygulama modunu izle, ya da hep açık / hep koyu.</summary>
public enum ThemeMode { System, Light, Dark }
