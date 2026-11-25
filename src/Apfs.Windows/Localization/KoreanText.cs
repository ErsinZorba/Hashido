using System.Text;

namespace Apfs.Windows.Localization;

/// <summary>
/// Korece satır kırma (1.9.0): WPF Hangul'u Çince/Japonca gibi her hecenin arasından kırar; Korecede satır sözcük arasından
/// (boşluktan) kırılır. Aynı sözcükteki iki hece arasına görünmez SÖZCÜK BİRLEŞTİRİCİ (U+2060) konur: WPF orada kırmaz (ölçüldü:
/// 150 DIP genişlikte "별도/의" yerine "별도의"), satır yalnız boşluktan kırılır; sığmayan tek sözcük yine de bölünür (metin kesilmez).
/// Yalnız tepsinin kendi çizdiği metne uygulanır (ekran okuyucu adlarına ve kaynak metne değil).
/// </summary>
public static class KoreanText
{
    public const char WordJoiner = '⁠';

    /// <summary>Hangul hece bloğu (가–힣).</summary>
    public static bool IsSyllable(char c) => c is >= '가' and <= '힣';

    /// <summary>Ardışık iki Hangul hecesinin arasına <see cref="WordJoiner"/>; zaten varsa ikinci kez konmaz (değişmeyen metin aynı nesne).</summary>
    public static string KeepWords(string text)
    {
        int i = 1;
        for (; i < text.Length; i++)
            if (IsSyllable(text[i - 1]) && IsSyllable(text[i])) break;
        if (i >= text.Length) return text;
        var sb = new StringBuilder(text.Length + text.Length / 2);
        sb.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            if (IsSyllable(text[i - 1]) && IsSyllable(text[i])) sb.Append(WordJoiner);
            sb.Append(text[i]);
        }
        return sb.ToString();
    }
}
