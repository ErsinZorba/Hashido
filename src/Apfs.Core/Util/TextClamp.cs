namespace Apfs.Util;

/// <summary>
/// Metni karakter (UTF-16 birimi) sınırına kısaltır; vekil çiftini (emoji gibi BMP dışı karakter) ortadan BÖLMEZ (1.9.1). Önceden
/// bazı kesimler (tepsi balonu, Gezgin'deki birim etiketi) yarım yüksek vekil bırakıyordu: balonda kutucuk, etikette geçersiz
/// UTF-16. Bütün kesimler buradan geçer (kanalın bildirim ve ipucu sınırları dahil).
/// </summary>
public static class TextClamp
{
    /// <summary>En çok <paramref name="maxChars"/> birimlik baş (sınırdaki vekil çifti bütünüyle dışarıda kalır).</summary>
    public static string Prefix(string text, int maxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        if (text.Length <= maxChars) return text;
        int n = maxChars;
        if (n > 0 && char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n];
    }

    /// <summary>
    /// Sığmıyorsa başı + <paramref name="ellipsis"/>; sonucun toplam boyu en çok <paramref name="maxTotal"/> (ek dahil).
    /// </summary>
    public static string WithEllipsis(string text, int maxTotal, string ellipsis = "…")
    {
        if (text.Length <= maxTotal) return text;
        return Prefix(text, Math.Max(0, maxTotal - ellipsis.Length)) + ellipsis;
    }
}
