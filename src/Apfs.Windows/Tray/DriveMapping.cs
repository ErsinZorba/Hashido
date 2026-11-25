using System.Runtime.InteropServices;
using System.Text;

namespace Apfs.Windows.Tray;

/// <summary>
/// Hizmet diskleri bütün oturumlara açık (genel) sürücü harfine bağlar; kullanıcının KENDİ oturumundaki bir eşleme (ağ sürücüsü,
/// <c>subst</c>) aynı harfi o oturumda gölgeler: Gezgin o harfte Mac diskini değil eşlenen sürücüyü gösterir, doluluk da onunkidir
/// (1.9.0). Tepsi harfi kendi oturumunda sorar; başka bir aygıta bağlıysa açmak yerine açıklar.
/// </summary>
public static class DriveMapping
{
    private const uint DriveRemote = 4; // GetDriveType: DRIVE_REMOTE

    /// <summary>
    /// Hedef başka bir aygıt mı: ağ sürücüsü (<c>GetDriveType</c> uzak; yönlendiricinin <c>\Device\…\;X:…</c> hedefi) ya da
    /// <c>subst</c> (<c>\??\C:\klasör</c>). Hedef okunamadıysa (null) yalnız sürücü türüne bakılır; tanınmayan hedef bizimki sayılır
    /// (yanlış uyarı yerine bugünkü davranış).
    /// </summary>
    public static bool IsForeign(string? target, uint driveType) =>
        driveType == DriveRemote
        || target is not null && (target.StartsWith(@"\??\", StringComparison.Ordinal) || target.Contains(@"\;", StringComparison.Ordinal));

    /// <summary>Bağlama noktasının harfi ("E:", "E:\" → "E:"); harf değilse null.</summary>
    public static string? Letter(string mountPoint) =>
        mountPoint.Length >= 2 && char.IsAsciiLetter(mountPoint[0]) && mountPoint[1] == ':' && mountPoint.TrimEnd('\\').Length == 2
            ? char.ToUpperInvariant(mountPoint[0]) + ":" : null;

    /// <summary>Bu oturumda harf başka bir aygıta (ağ, subst) bağlı mı. Harf değilse ya da sorgu başarısızsa false.</summary>
    public static bool IsShadowed(string mountPoint)
    {
        if (Letter(mountPoint) is not { } letter) return false;
        var buffer = new StringBuilder(1024);
        string? target = QueryDosDeviceW(letter, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
        return IsForeign(target, GetDriveTypeW(letter + "\\"));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string deviceName, StringBuilder targetPath, int max);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPath);
}
