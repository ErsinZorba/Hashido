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

    private static object? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LanguageSetting.KeyPath);
            return key?.GetValue(name);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
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
