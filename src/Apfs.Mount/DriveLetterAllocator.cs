using System.Runtime.InteropServices;
using Fsp;

namespace Apfs.Mount;

/// <summary>
/// Bağlama noktası verilmediğinde sürücü harfini seçer (açık depo bildirimi #4, 1.8.0). Hizmet LocalSystem olarak çalışır ve
/// kullanıcının ağ sürücülerini görmez; WinFsp'nin kendi seçimi (Z'den geriye) kullanıcının sonradan bağlanan ağ sürücüleriyle
/// (genelde Z, Y, X…) çakışıyordu. Sıra: T'den F'ye geriye, hiç boş yoksa son çare U–Z. Biçim düz "X:" (Bağlama Yöneticisi'ne
/// kaydedilmez → Geri Dönüşüm Kutusu kurulmaz; <c>\\.\X:</c> KULLANMA, kurallar/yazma.md "Sürücü harfi").
/// </summary>
public static class DriveLetterAllocator
{
    /// <summary>Harf arada başka biri tarafından kapılırsa en çok bu kadar harf denenir.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>Denenecek harfler, sırasıyla.</summary>
    public static IReadOnlyList<char> Order { get; } = [.. Enumerable.Range(0, 'T' - 'F' + 1).Select(i => (char)('T' - i)),
        .. Enumerable.Range('U', 'Z' - 'U' + 1).Select(i => (char)i)];

    /// <summary>
    /// <paramref name="usedMask"/> (bit 0 = A:, <c>GetLogicalDrives</c> biçimi) dışındaki ilk harf ("T:" biçiminde); yoksa null.
    /// </summary>
    public static string? Pick(uint usedMask)
    {
        foreach (char c in Order)
            if ((usedMask & (1u << (c - 'A'))) == 0) return c + ":";
        return null;
    }

    /// <summary>
    /// <paramref name="fs"/>'i bağlar: <paramref name="mountPoint"/> verilmişse oraya; null ise seçilen harfe (harf arada
    /// kapılırsa sıradaki, en çok <see cref="MaxAttempts"/> deneme; her denemede YENİ host, başarısız host bırakılır). Başarısızsa
    /// <see cref="ApfsException"/> (<c>Fs.MountFailed</c> ya da <c>Fs.NoFreeDriveLetter</c>); WinFsp yüklenemezse onun istisnası
    /// olduğu gibi geçer (çağıran anlaşılır iletiye çevirir).
    /// </summary>
    internal static FileSystemHost MountHost(FileSystemBase fs, string? mountPoint)
    {
        if (mountPoint is not null)
        {
            int st = TryMount(fs, mountPoint, out var h);
            return h ?? throw new ApfsException(ApfsErrorCode.IoError, CoreText.T("Fs.MountFailed", st));
        }
        uint tried = 0;
        int status = 0;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string? letter = Pick(GetLogicalDrives() | tried);
            if (letter is null) break;
            status = TryMount(fs, letter, out var host);
            if (host is not null) return host;
            tried |= 1u << (letter[0] - 'A');
        }
        if (tried == 0) throw new ApfsException(ApfsErrorCode.IoError, CoreText.T("Fs.NoFreeDriveLetter"));
        throw new ApfsException(ApfsErrorCode.IoError, CoreText.T("Fs.MountFailed", status));
    }

    private static int TryMount(FileSystemBase fs, string mountPoint, out FileSystemHost? host)
    {
        var h = new FileSystemHost(fs);
        int status;
        try
        {
            status = h.Mount(mountPoint, null, false, 0);
        }
        catch
        {
            h.Dispose();
            throw;
        }
        if (status == 0)
        {
            host = h;
            return 0;
        }
        h.Dispose();
        host = null;
        return status;
    }

    [DllImport("kernel32.dll")] private static extern uint GetLogicalDrives();
}
