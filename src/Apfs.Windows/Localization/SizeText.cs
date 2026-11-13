using System.Globalization;

namespace Apfs.Windows.Localization;

/// <summary>
/// Bayt sayısını Dosya Gezgini gibi yazar: ikili birim (1 KB = 1024 bayt), üç anlamlı basamak, kesilerek (yuvarlanmaz:
/// 1,999 TB "1,99 TB" olur, "2,00 TB" değil); 1000'e ulaşan değer bir üst birime geçer (1000 MB → "0,97 GB").
/// Sayı biçimi bölge ayarından (<see cref="CultureInfo.CurrentCulture"/>), birim adları arayüz dilinden (Fransızcada "Go").
/// </summary>
public static class SizeText
{
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string[] units = WinText.U("Card.Units").Split('|');
        if (units.Length != 5) units = ["bytes", "KB", "MB", "GB", "TB"];
        bytes = Math.Max(0, bytes);
        if (bytes < 1000) return $"{bytes.ToString(culture)} {units[0]}";
        double v = bytes;
        int unit = 0;
        while (v >= 1000 && unit < units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        int decimals = v < 10 ? 2 : v < 100 ? 1 : 0;
        double scale = Math.Pow(10, decimals);
        // Kayan nokta hatası (ör. 1,8199999…) kesmede bir basamak kaybettirmesin
        double truncated = Math.Floor(v * scale + 1e-9) / scale;
        return $"{truncated.ToString("F" + decimals, culture)} {units[unit]}";
    }
}
