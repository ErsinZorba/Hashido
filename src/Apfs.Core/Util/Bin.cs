using System.Buffers.Binary;

namespace Apfs.Util;

/// <summary>Sınır kontrollü little-endian okuma yardımcıları. Sınır taşması bozuk veri demektir.</summary>
internal static class Bin
{
    public static ushort U16(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(s, off, 2));
    public static uint U32(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(s, off, 4));
    public static ulong U64(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(s, off, 8));
    public static int I32(ReadOnlySpan<byte> s, int off) => BinaryPrimitives.ReadInt32LittleEndian(Slice(s, off, 4));

    /// <summary>APFS uuid_t (RFC 4122 bayt sırası).</summary>
    public static Guid Uuid(ReadOnlySpan<byte> s, int off) => new(Slice(s, off, 16), bigEndian: true);

    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> s, int off, int len)
    {
        if (off < 0 || len < 0 || off > s.Length - len)
            throw ApfsException.Corrupt(CoreText.T("Bin.RecordBoundsExceeded", off, len, s.Length));
        return s.Slice(off, len);
    }

    /// <summary>NUL ile biten (veya bitmeyen) UTF-8 dizgesi.</summary>
    public static string Utf8Z(ReadOnlySpan<byte> s)
    {
        int nul = s.IndexOf((byte)0);
        if (nul >= 0) s = s[..nul];
        return System.Text.Encoding.UTF8.GetString(s);
    }

    /// <summary>Geçersiz UTF-8 baytlarının kaçış aralığının ilki (bayt 0x80 → U+F680 … 0xFF → U+F6FF; SFM'nin U+F001–U+F029'u dışında).</summary>
    public const char NameEscapeFirst = (char)0xF680, NameEscapeLast = (char)0xF6FF;

    /// <summary>
    /// Dosya adı (NUL ile biten ya da bitmeyen UTF-8). Geçerli UTF-8 <see cref="Utf8Z"/> ile aynı; geçersiz dizinin her baytı
    /// (hep ≥ 0x80) U+F600 + bayt olur. Önceden hepsi U+FFFD'ye çöküyor, aynı klasörde iki geçersiz ad aynı adı alıp biri
    /// ulaşılamıyordu (1.9.1). Bu aralıktan karakter taşıyan geçerli Mac adı da olabilir: ikisi aynı dizgeye düşebilir →
    /// aralık "tuhaf ad" (<see cref="Volume.WindowsNameMap.IsOdd"/>), tam denetim geçersiz UTF-8'i hata sayar (yazıcıya ulaşmaz).
    /// </summary>
    public static string Utf8NameZ(ReadOnlySpan<byte> s)
    {
        int nul = s.IndexOf((byte)0);
        if (nul >= 0) s = s[..nul];
        if (System.Text.Unicode.Utf8.IsValid(s)) return System.Text.Encoding.UTF8.GetString(s);
        var sb = new System.Text.StringBuilder(s.Length);
        Span<char> pair = stackalloc char[2];
        while (!s.IsEmpty)
        {
            if (System.Text.Rune.DecodeFromUtf8(s, out var rune, out int n) == System.Buffers.OperationStatus.Done)
                sb.Append(pair[..rune.EncodeToUtf16(pair)]);
            else
                for (int i = 0; i < n; i++) sb.Append((char)(0xF600 + s[i]));
            s = s[n..];
        }
        return sb.ToString();
    }
}
