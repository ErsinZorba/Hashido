namespace Apfs.Windows;

/// <summary>
/// Arka planda (hizmet veya WinFsp Launcher altında) çalışırken mesajların kaybolmaması için basit günlük:
/// %ProgramData%\APFS for Windows\log.txt. Bu dosyaya yazılamıyorsa (ör. hizmet değil normal kullanıcıyken)
/// %LOCALAPPDATA%\APFS for Windows\log.txt kullanılır. Parola veya dosya içeriği asla yazılmaz.
/// </summary>
public static class ServiceLog
{
    private const long MaxBytes = 1 << 20;
    private static readonly Lock Gate = new();

    public static string PathName { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "APFS for Windows", "log.txt");

    public static string FallbackPathName { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APFS for Windows", "log.txt");

    /// <summary>Geliştirme (konsol) modunda satırlar ayrıca standart hataya yazılır.</summary>
    public static bool EchoToConsole { get; set; }

    private static volatile bool _fallbackOnly;

    /// <summary>
    /// Bu süreçte günlük yalnız <see cref="FallbackPathName"/>'e yazılır (1.9.1): hizmet veri klasörünün günlük alanını güvenceye
    /// alamadıysa (<see cref="DataDirSecurity.Result.LogTrusted"/>) o çalışmada ProgramData'daki günlüğe hiç yazmaz.
    /// </summary>
    public static void UseFallbackOnly() => _fallbackOnly = true;

    /// <summary>Bu süreçte satırların yazıldığı ilk yol.</summary>
    public static string CurrentPathName => _fallbackOnly ? FallbackPathName : PathName;

    public static void Write(string message)
    {
        string line = FormatLine(DateTime.Now, message);
        if (EchoToConsole) Console.Error.WriteLine(line);
        lock (Gate)
        {
            if (_fallbackOnly || !TryAppend(PathName, line, createDirectory: false)) TryAppend(FallbackPathName, line, createDirectory: true);
        }
    }

    /// <summary>
    /// Günlük satırı (1.9.1): tarih her zaman Gregoryen takvimle (InvariantCulture; önceden süreç kültürünün takvimiyle: th-TH'de
    /// 2569, ar-SA'da Hicri yıl), ileti <see cref="Apfs.Util.TextSafety.ForLog"/>'dan geçer (diskten gelen ad sahte satır ekleyemez).
    /// </summary>
    internal static string FormatLine(DateTime when, string message) =>
        when.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + Apfs.Util.TextSafety.ForLog(message);

    /// <param name="createDirectory">Klasör yoksa açılsın mı. ProgramData'daki klasörü YALNIZ hizmet, son izinleriyle açar (1.9.1):
    /// önceden SYSTEM olarak koşan başka bir süreç (Launcher) onu ProgramData'nın mirasıyla, kullanıcıların yazabileceği biçimde açabiliyordu.</param>
    internal static bool TryAppend(string path, string line, bool createDirectory)
    {
        try
        {
            if (createDirectory) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            else if (!Directory.Exists(Path.GetDirectoryName(path)!)) return false;
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine);
            return true;
        }
        catch (IOException) { return false; } // günlük yazılamazsa asıl işi engellememeli
        catch (UnauthorizedAccessException) { return false; }
    }
}
