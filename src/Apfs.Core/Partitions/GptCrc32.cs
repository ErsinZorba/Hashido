namespace Apfs.Partitions;

/// <summary>
/// CRC-32 (IEEE 802.3, yansıtılmış 0xEDB88320; başlangıç ve son XOR 0xFFFFFFFF): GPT başlığının ve bölüm tablosunun sağlaması.
/// APFS'in ad karmasındaki CRC-32C (Castagnoli) ile KARIŞTIRMA. Bağımsız kanıt: test GPT'leri ImageBuilder'ın ayrı
/// <c>Crc32</c>'siyle yazılır, Mac'in yazdığı gerçek GPT de bu sağlamayla açılır.
/// </summary>
internal static class GptCrc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
