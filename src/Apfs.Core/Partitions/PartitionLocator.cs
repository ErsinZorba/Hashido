using System.Runtime.ExceptionServices;
using Apfs.Container;
using Apfs.IO;
using Apfs.Util;

namespace Apfs.Partitions;

/// <summary>Bir aygıtta bulunan APFS kapsayıcısının konumu.</summary>
/// <param name="OverlapsOther">GPT'de APFS DIŞI bir girdiyle örtüşüyor (1.9.1): bozuk tablo. Bölüm gizlenmez (kullanıcı
/// verisinden ayrılmasın), salt okunur açılır; yazmaya açılmaz (yazıcı öbür bölümün verisine yazabilirdi).</param>
public sealed record ApfsPartition(long Offset, long Length, int Index, string Description, bool OverlapsOther = false);

/// <summary>GPT bölüm tablosunu okuyup APFS bölümlerini bulur. Bölüm tablosu yoksa aygıtın tamamını kapsayıcı kabul eder.</summary>
public static class PartitionLocator
{
    public static readonly Guid ApfsPartitionType = Guid.Parse("7C3457EF-0000-11AA-AA11-00306543ECAC");

    private static readonly byte[] GptSignature = "EFI PART"u8.ToArray();
    /// <summary>
    /// Girdi tablosunun en büyük boyu: önceki sınırın (1024 girdi × 4096 bayt) en büyük tablosu. 1024'ten çok girdili geçerli
    /// GPT artık okunur (APFS olmayan böyle bir diske "okunamadı" denmesin); bunu aşan tablo bozuk sayılır.
    /// </summary>
    private const long MaxTableBytes = 4L << 20;
    private const int MaxApfsPartitions = 1024;

    public static IReadOnlyList<ApfsPartition> FindApfsPartitions(IBlockDevice device)
    {
        // Doğrudan bir APFS kapsayıcısı mı (bölüm tablosu olmayan görüntü)?
        if (LooksLikeContainer(device, 0))
            return [new ApfsPartition(0, device.Length, 0, CoreText.U("Partition.Unpartitioned"))];

        // GPT başlığı 1. LBA'dadır; sektör boyutu 512 veya 4096 olabilir.
        bool? protectiveMbr = null;
        Exception? readError = null;
        string? problem = null;
        foreach (int sector in new[] { device.SectorSize, 512, 4096 }.Distinct())
        {
            if (device.Length < sector * 2L) continue;
            var primary = new byte[sector];
            bool hasPrimary = TryRead(device, sector, primary, ref readError) && HasGptSignature(primary);
            // Birincil imza kayıpsa, MBR'ye çevrilmiş disklerde kalmış eski bir GPT yedeğini kullanma.
            if (!hasPrimary && !(protectiveMbr ??= HasProtectiveMbr(device, ref readError))) continue;

            var partitions = ParseGpt(device, hasPrimary ? primary : null, sector, ref readError, out string? candidateProblem);
            if (partitions is not null) return partitions;
            problem ??= candidateProblem;
        }
        if (readError is not null) ExceptionDispatchInfo.Throw(readError);
        if (problem is not null) throw ApfsException.Corrupt(problem);
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

    private static bool HasGptSignature(ReadOnlySpan<byte> header) => header[..8].SequenceEqual(GptSignature);

    private static bool HasProtectiveMbr(IBlockDevice device, ref Exception? readError)
    {
        Span<byte> mbr = stackalloc byte[512];
        if (!TryRead(device, 0, mbr, ref readError) || Bin.U16(mbr, 510) != 0xAA55) return false;
        for (int offset = 446; offset < 510; offset += 16)
            if (mbr[offset + 4] == 0xEE && Bin.U32(mbr, offset + 8) == 1) return true;
        return false;
    }

    private static bool TryRead(IBlockDevice device, long offset, Span<byte> buffer, ref Exception? readError)
    {
        try
        {
            device.Read(offset, buffer);
            return true;
        }
        catch (Exception e) when (e is IOException or ApfsException { Code: ApfsErrorCode.IoError })
        {
            readError ??= e;
            return false;
        }
    }

    /// <summary>
    /// GPT'yi (açık depo bildirimi #3, 1.8.0; PR #5) Windows/macOS gibi doğrular: başlık boyutu, başlık CRC'si, kendi konumu,
    /// kullanılabilir aralık (FirstUsable ≥ 2), girdi tablosunun yeri (birincilde FirstUsable'dan önce, yedekte LastUsable ile yedek
    /// başlık arasında) ve CRC'si. Birincil başlık geçerliyse yedeğe hiç bakılmaz (yedeğin yeri/CRC'si bugün açılan bir diski
    /// kapatmasın: ör. büyütülmüş görüntüde yedek sonda değildir). Birincil geçersiz, okunamıyor ya da imzası yoksa (bu son
    /// durumda yalnız koruyucu MBR varsa; çağıran denetler) yedek başlık (birincilin <c>AlternateLBA</c>'sı diskin içindeyse
    /// orası, değilse son sektör) kullanılır; ikisi de olmazsa <c>null</c>. <paramref name="problem"/> yalnız bir GPT imzası
    /// görüldüyse doludur: birincilin imzası yok ve yedekte de yoksa (koruyucu MBR'si kalmış, yarım silinmiş disk) bu bir GPT
    /// değildir, "APFS yok" sayılır (PR #5'ten önceki davranış). LastUsable'ın disk sonuyla ilişkisi BİLEREK
    /// denetlenmez (kesilmiş görüntü / olduğundan küçük görünen disk kapanmasın); diskin dışına taşan girdiyi
    /// <see cref="ParseEntries"/> atlar.
    /// </summary>
    private static List<ApfsPartition>? ParseGpt(IBlockDevice device, byte[]? primary, int sector,
        ref Exception? readError, out string? problem)
    {
        long sectors = device.Length / sector;
        problem = null;
        byte[] table;
        if (primary is not null)
        {
            if (TryReadTable(device, primary, sector, 1, ref readError, out table, out string primaryProblem))
                return ParseEntries(device, primary, table, sector);
            problem = primaryProblem;
        }

        ulong alt = primary is null ? 0 : Bin.U64(primary, 32);
        long backupLba = alt > 1 && alt < (ulong)sectors ? (long)alt : sectors - 1;
        foreach (long lba in new[] { backupLba, sectors - 1 }.Distinct())
        {
            if (lba <= 1) continue;
            var backup = new byte[sector];
            if (!TryRead(device, lba * sector, backup, ref readError) || !HasGptSignature(backup)) continue;
            if (TryReadTable(device, backup, sector, lba, ref readError, out table, out _))
                return ParseEntries(device, backup, table, sector);
            problem ??= CoreText.T("Partition.GptHeaderInvalid");
        }
        return null;
    }

    /// <summary>
    /// Başlığı doğrular ve girdi tablosunu okur; geçersizse <paramref name="problem"/> nedenin metnidir. Okuma hatası
    /// <paramref name="readError"/>'a (ilki) yazılır; hiçbir yol açılmazsa <see cref="FindApfsPartitions"/> onu yeniden atar.
    /// </summary>
    private static bool TryReadTable(IBlockDevice device, byte[] header, int sector, long expectedLba,
        ref Exception? readError, out byte[] table, out string problem)
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
        if (firstUsable < 2 || firstUsable > lastUsable) return false;

        ulong entriesLba = Bin.U64(header, 72);
        uint count = Bin.U32(header, 80);
        uint entrySize = Bin.U32(header, 84);
        if (entrySize < 128 || entrySize > 4096 || entrySize % 8 != 0 || count == 0 || (long)count * entrySize > MaxTableBytes)
            return false;

        long tableLength = (long)count * entrySize; // ≤ 4 MiB
        ulong tableSectors = (ulong)((tableLength + sector - 1) / sector);
        if (entriesLba < 2 || entriesLba >= (ulong)sectors || tableSectors > (ulong)sectors - entriesLba)
        {
            problem = CoreText.T("Partition.GptOutsideDisk");
            return false;
        }
        ulong tableEnd = entriesLba + tableSectors;
        if (expectedLba == 1 ? tableEnd > firstUsable : entriesLba <= lastUsable || tableEnd > (ulong)expectedLba)
            return false;

        var entries = new byte[tableLength];
        if (!TryRead(device, (long)entriesLba * sector, entries, ref readError)
            || GptCrc32.Compute(entries) != Bin.U32(header, 88)) return false;
        table = entries;
        return true;
    }

    private static List<ApfsPartition> ParseEntries(IBlockDevice device, byte[] header, byte[] table, int sector)
    {
        uint count = Bin.U32(header, 80);
        uint entrySize = Bin.U32(header, 84);
        ulong firstUsable = Bin.U64(header, 40), lastUsable = Bin.U64(header, 48);
        ulong sectors = (ulong)(device.Length / sector);
        var result = new List<ApfsPartition>();
        var taken = new List<(ulong First, ulong Last)>();
        // APFS dışı girdilerin aralıkları (boş tür ve ters aralık hariç; kullanılabilir alanın dışındakiler de: bozuk tablo)
        var others = new List<(ulong First, ulong Last)>();
        for (int i = 0; i < count; i++)
        {
            var entry = table.AsSpan((int)(i * entrySize), (int)entrySize);
            var type = new Guid(entry[..16]);
            if (type == Guid.Empty || type == ApfsPartitionType) continue;
            ulong f = Bin.U64(entry, 32), l = Bin.U64(entry, 40);
            if (f <= l) others.Add((f, l));
        }
        // En çok 1024 APFS bölümü (önceki tablo sınırının izin verdiği kadar): çok girdili tablo örtüşme denetimini (n²)
        // ve hizmetin bölüm başına işini büyütmesin; gerçek disklerde birkaç APFS bölümü olur
        for (int i = 0; i < count && result.Count < MaxApfsPartitions; i++)
        {
            var entry = table.AsSpan((int)(i * entrySize), (int)entrySize);
            var type = new Guid(entry[..16]);
            if (type != ApfsPartitionType) continue;
            ulong first = Bin.U64(entry, 32);
            ulong last = Bin.U64(entry, 40);
            // Taşmasız: önce kullanılabilir aralık ve sektör sayısıyla karşılaştırılır, sonra çarpılır (aralık dışı, diskin
            // dışına taşan ya da ters bölüm: atla; yazıcı GPT yapılarını ezemesin)
            if (first < firstUsable || last < first || last > lastUsable || last >= sectors) continue;
            // Önceki bir APFS girdisiyle örtüşen (kopya ya da kısmen üst üste) girdi atlanır, yalnız ilki alınır: aynı kapsayıcı iki
            // bölüm olup iki ayrı yazma oturumuyla aynı alan yöneticisinden blok ayırmasın (1.8.1: sessiz veri kaybı)
            if (taken.Exists(t => first <= t.Last && t.First <= last)) continue;
            taken.Add((first, last));
            long offset = (long)first * sector;
            long length = (long)(last - first + 1) * sector;
            string name = System.Text.Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0');
            bool overlaps = others.Exists(o => first <= o.Last && o.First <= last);
            result.Add(new ApfsPartition(offset, length, i + 1, string.IsNullOrEmpty(name) ? CoreText.U("Partition.Numbered", i + 1) : name, overlaps));
        }
        return result;
    }
}
