using System.Text;

namespace Apfs.Util;

/// <summary>
/// Diskten ya da dışarıdan gelen metnin (birim/disk adı, dosya adı, komut satırı) günlüğe ve kullanıcıya güvenle yazılması (1.9.1).
/// Denetim karakterleri (C0, DEL, C1) ve yön biçimlendirme karakterleri (U+061C, U+200E/F, U+202A–202E, U+2066–2069) önceden
/// süzülmüyordu: adı satır sonu içeren bir birim günlüğe sahte satır ekleyebiliyor, U+202E metni bildirimde ters gösterebiliyordu.
/// </summary>
public static class TextSafety
{
    /// <summary>Kaçışlanan/değiştirilen karakter mi.</summary>
    public static bool IsUnsafe(char c) =>
        char.IsControl(c) || c == '\u061C' || c is '\u200E' or '\u200F' || c is >= '\u202A' and <= '\u202E' || c is >= '\u2066' and <= '\u2069';

    /// <summary>Günlük için: güvensiz karakterler <c>\uXXXX</c> olarak yazılır (satır hep tek satır, iz kaybolmaz).</summary>
    public static string ForLog(string text)
    {
        if (!text.Any(IsUnsafe)) return text;
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
            if (IsUnsafe(c)) sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            else sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Kullanıcıya gösterim için: güvensiz karakterler U+FFFD ile değiştirilir.</summary>
    public static string ForDisplay(string text)
    {
        if (!text.Any(IsUnsafe)) return text;
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (IsUnsafe(chars[i])) chars[i] = '\uFFFD';
        return new string(chars);
    }
}
