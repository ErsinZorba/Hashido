using Apfs.Container;
using Apfs.IO;
using Apfs.Util;

namespace Apfs.Partitions;

/// <summary>Bir aygıtta bulunan APFS kapsayıcısının konumu.</summary>
public sealed record ApfsPartition(long Offset, long Length, int Index, string Description);

/// <summary>GPT bölüm tablosunu okuyup APFS bölümlerini bulur. Bölüm tablosu yoksa aygıtın tamamını kapsayıcı kabul eder.</summary>
public static class PartitionLocator
{
    public static readonly Guid ApfsPartitionType = Guid.Parse("7C3457EF-0000-11AA-AA11-00306543ECAC");

    private static readonly byte[] GptSignature = "EFI PART"u8.ToArray();
    private const int MaxPartitionEntries = 1024;

    public static IReadOnlyList<ApfsPartition> FindApfsPartitions(IBlockDevice device)
    {
        // Doğrudan bir APFS kapsayıcısı mı (bölüm tablosu olmayan görüntü)?
        if (LooksLikeContainer(device, 0))
            return [new ApfsPartition(0, device.Length, 0, CoreText.U("Partition.Unpartitioned"))];

        // GPT başlığı 1. LBA'dadır; sektör boyutu 512 veya 4096 olabilir.
        foreach (int sector in new[] { device.SectorSize, 512, 4096 }.Distinct())
        {
            if (device.Length < sector * 2L) continue;
            var header = new byte[sector];
            device.Read(sector, header);
            if (!header.AsSpan(0, 8).SequenceEqual(GptSignature)) continue;
            return ParseGpt(device, header, sector);
        }
        return [];
    }

    /// <summary>
    /// Diskteki APFS kapsayıcılarının kimliği: bölüm konumu/boyu + bölüm başındaki ilk 4 KiB'ın (blok 0) SHA-256'sı.
    /// Takılıyken blok 0'ı yalnızca yazma oturumunun Close'u yazar (macOS bağlı değildir); hizmet onu diski bıraktıktan sonra
    /// çağırır ve yeniden bağlarken kimliği tazeler. Başka bir program diski biçimlendirir ya da üzerine bir görüntü yazarsa
    /// (aynı UUID'li bir yedek bile) değişir. APFS yoksa boş metin.
    /// </summary>
    public static string Fingerprint(IBlockDevice device)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in FindApfsPartitions(device))
        {
            var head = new byte[Math.Min(ApfsConstants.MinBlockSize, p.Length)];
            device.Read(p.Offset, head);
            sb.Append($"{p.Offset}+{p.Length}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(head))};");
        }
        return sb.ToString();
    }

    /// <summary>Verilen konumda NXSB süper bloğu var mı?</summary>
    public static bool LooksLikeContainer(IBlockDevice device, long offset)
    {
        if (device.Length < offset + ApfsConstants.MinBlockSize) return false;
        Span<byte> head = stackalloc byte[40];
        device.Read(offset, head);
        return Bin.U32(head, 32) == ApfsConstants.NxMagic;
    }

    /// <summary>
    /// GPT'yi (açık depo bildirimi #3, 1.8.0) Windows/macOS gibi doğrular: başlık boyutu, başlık CRC'si, kendi konumu, kullanılabilir
    /// aralık, girdi tablosunun yeri ve CRC'si. Birincil başlık geçerliyse yedeğe hiç bakılmaz (yedeğin yeri/CRC'si bugün açılan bir
    /// diski kapatmasın: ör. büyütülmüş görüntüde yedek sonda değildir). Birincil geçersizse yedek başlık (birincilin
    /// <c>AlternateLBA</c>'sı diskin içindeyse orası, değilse son sektör) kullanılır; ikisi de geçersizse ret. Girdilerde taşmasız
    /// aritmetik: diskin dışına taşan ya da tersine dönmüş bölüm atlanır.
    /// </summary>
    private static List<ApfsPartition> ParseGpt(IBlockDevice device, byte[] primary, int sector)
    {
        long sectors = device.Length / sector;
        if (TryReadTable(device, primary, sector, 1, out var table, out string? problem))
            return ParseEntries(device, primary, table, sector);

        ulong alt = Bin.U64(primary, 32);
        long backupLba = alt > 1 && alt < (ulong)sectors ? (long)alt : sectors - 1;
        foreach (long lba in new[] { backupLba, sectors - 1 }.Distinct())
        {
            if (lba <= 1) continue;
            var backup = new byte[sector];
            device.Read(lba * sector, backup);
            if (backup.AsSpan(0, 8).SequenceEqual(GptSignature) && TryReadTable(device, backup, sector, lba, out table, out _))
                return ParseEntries(device, backup, table, sector);
        }
        throw ApfsException.Corrupt(problem!);
    }

    /// <summary>Başlığı doğrular ve girdi tablosunu okur; geçersizse <paramref name="problem"/> nedenin metnidir.</summary>
    private static bool TryReadTable(IBlockDevice device, byte[] header, int sector, long expectedLba, out byte[] table, out string? problem)
    {
        table = [];
        problem = CoreText.T("Partition.GptHeaderInvalid");
        long sectors = device.Length / sector;
        uint headerSize = Bin.U32(header, 12);
        if (headerSize < 92 || headerSize > sector) return false;
        var copy = header.AsSpan(0, (int)headerSize).ToArray();
        uint headerCrc = Bin.U32(copy, 16);
        copy.AsSpan(16, 4).Clear();
        if (GptCrc32.Compute(copy) != headerCrc) return false;
        if (Bin.U64(header, 24) != (ulong)expectedLba) return false;
        ulong firstUsable = Bin.U64(header, 40), lastUsable = Bin.U64(header, 48);
        if (firstUsable > lastUsable) return false;

        ulong entriesLba = Bin.U64(header, 72);
        uint count = Bin.U32(header, 80);
        uint entrySize = Bin.U32(header, 84);
        if (entrySize < 128 || entrySize > 4096 || entrySize % 8 != 0 || count == 0 || count > MaxPartitionEntries) return false;

        long tableLength = (long)count * entrySize; // ≤ 4 MiB
        if (entriesLba < 2 || entriesLba >= (ulong)sectors || (long)entriesLba * sector > device.Length - tableLength)
        {
            problem = CoreText.T("Partition.GptOutsideDisk");
            return false;
        }
        table = new byte[tableLength];
        device.Read((long)entriesLba * sector, table);
        if (GptCrc32.Compute(table) != Bin.U32(header, 88))
        {
            table = [];
            return false;
        }
        problem = null;
        return true;
    }

    private static List<ApfsPartition> ParseEntries(IBlockDevice device, byte[] header, byte[] table, int sector)
    {
        uint count = Bin.U32(header, 80);
        uint entrySize = Bin.U32(header, 84);
        ulong sectors = (ulong)(device.Length / sector);
        var result = new List<ApfsPartition>();
        for (int i = 0; i < count; i++)
        {
            var entry = table.AsSpan((int)(i * entrySize), (int)entrySize);
            var type = new Guid(entry[..16]);
            if (type != ApfsPartitionType) continue;
            ulong first = Bin.U64(entry, 32);
            ulong last = Bin.U64(entry, 40);
            // Taşmasız: önce sektör sayısıyla karşılaştırılır, sonra çarpılır (diskin dışına taşan ya da ters bölüm: atla)
            if (last < first || last >= sectors) continue;
            long offset = (long)first * sector;
            long length = (long)(last - first + 1) * sector;
            string name = System.Text.Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0');
            result.Add(new ApfsPartition(offset, length, i + 1, string.IsNullOrEmpty(name) ? CoreText.U("Partition.Numbered", i + 1) : name));
        }
        return result;
    }
}
