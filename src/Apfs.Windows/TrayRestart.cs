using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Apfs.Windows;

/// <summary>
/// Setup'ın kapattığı ÖBÜR oturumların tepsileri (1.9.0). Setup dosyalar kilitli olmasın diye bütün oturumlardaki tepsileri kapatır;
/// kurulumu yapanın tepsisini kendisi yeniden açar (sihirbazın son sayfası ya da güncellemede <c>--updated</c>), öbür oturumlarınki
/// (hızlı kullanıcı değiştirmede arka plandaki hesaplar) ise o hesap yeniden oturum açana dek yoktu. Setup tepsileri kapatmadan önce
/// tepsisi açık olan öbür oturumları HKLM'ye yazar (<see cref="ValueName"/>; ProgramData'ya değil: o klasörü hizmet güvenceye alır,
/// setup oraya yazmaz); kurulu hizmet açılışta değeri okur, siler ve yalnız hâlâ oturum açık olup tepsisi olmayan kayıtlı oturumlarda
/// tepsiyi o oturumun kullanıcısı olarak (yükseltilmemiş) bir kez başlatır. Tepsisini kendi kapatan kullanıcının oturumu kayıtlı
/// olmadığından ona dokunulmaz.
/// </summary>
public static class TrayRestart
{
    /// <summary>HKLM altındaki anahtar (teknik kimlik, eski adla; .iss <c>DataDirName</c>).</summary>
    public const string KeyPath = @"SOFTWARE\APFS for Windows";

    /// <summary>Değer: yeniden açılacak oturum numaraları, virgülle ("1,3"). Yazıldığı an anahtarın son yazma zamanıdır (UTC, sistem tutar).</summary>
    public const string ValueName = "RestartTraySessions";

    /// <summary>Bundan eski kayıt yok sayılır (yarıda kalmış bir kurulumun değeri sonraki bir açılışta tepsi açtırmasın).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Değerden yeniden açılacak oturumlar: kayıt <see cref="MaxAge"/>'den eskiyse ya da ileri tarihliyse (saat farkı payı 2 dk) boş;
    /// oturum 0 (hizmetler) ve okunamayan numaralar atlanır, tekrarlar bir kez. Zaman anahtarın UTC yazma zamanından: yerel saat
    /// damgası yaz saati geçişinde kaydı yanlışlıkla eski gösterirdi (bağımsız inceleme, 1.9.0).
    /// </summary>
    public static IReadOnlyList<uint> Parse(string? value, DateTime writtenUtc, DateTime nowUtc)
    {
        var age = nowUtc - writtenUtc;
        if (string.IsNullOrWhiteSpace(value) || age > MaxAge || age < TimeSpan.FromMinutes(-2)) return [];
        var sessions = new List<uint>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint s) && s != 0 && !sessions.Contains(s))
                sessions.Add(s);
        return sessions;
    }

    /// <summary>Kayıtlı oturumlardan tepsisi hâlâ (ya da yeniden) açık olmayanlar.</summary>
    public static IReadOnlyList<uint> Missing(IEnumerable<uint> recorded, IEnumerable<int> traySessions)
    {
        var running = traySessions.Select(s => (uint)s).ToHashSet();
        return [.. recorded.Where(s => !running.Contains(s))];
    }

    /// <summary>
    /// Kurulu hizmetin açılışında (LocalSystem): değeri okur ve HEMEN siler (bir kez), eksik tepsileri <paramref name="trayPath"/>
    /// ile başlatır. Hata tepsiyi açmamaktan öteye gitmez; her başlatma, başarısızlık ve kullanılmayan kayıt günlüğe tek satır.
    /// </summary>
    public static void RunOnce(string trayPath, Action<string> log) =>
        Restart(TakeRecord, trayPath, File.Exists, TraySessions, StartInSession, DateTime.UtcNow, log);

    /// <summary><see cref="RunOnce"/>'ın sırası (testte dış dünya sahte): kaydı al (alınırken silinir) → seç → eksikleri başlat.</summary>
    internal static void Restart(Func<(string? Value, DateTime WrittenUtc)?> take, string trayPath, Func<string, bool> exists,
        Func<string, IEnumerable<int>> traySessions, Func<string, uint, int> start, DateTime nowUtc, Action<string> log)
    {
        if (take() is not { } record) return;
        var sessions = Parse(record.Value, record.WrittenUtc, nowUtc);
        if (sessions.Count == 0 || !exists(trayPath))
        {
            log(WinText.T("Svc.TrayRestartSkipped", record.Value ?? "", record.WrittenUtc.ToString("u", CultureInfo.InvariantCulture)));
            return;
        }
        foreach (uint session in Missing(sessions, traySessions(Path.GetFileNameWithoutExtension(trayPath))))
        {
            int error = start(trayPath, session);
            log(error == 0 ? WinText.T("Svc.TrayRestarted", session) : WinText.T("Svc.TrayRestartFailed", session, error));
        }
    }

    /// <summary>Değeri ve anahtarın son yazma zamanını (UTC) okur, değeri siler; değer yoksa null.</summary>
    private static (string? Value, DateTime WrittenUtc)? TakeRecord()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(KeyPath, writable: true);
        if (key?.GetValue(ValueName) is not { } raw) return null;
        long written = RegQueryInfoKeyW(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long fileTime) == 0 ? fileTime : 0;
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return (raw as string, written > 0 ? DateTime.FromFileTimeUtc(written) : DateTime.MinValue);
    }

    /// <summary>Adı verilen süreçlerin oturumları (okunamayan süreç atlanır).</summary>
    private static List<int> TraySessions(string processName)
    {
        var sessions = new List<int>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                try { sessions.Add(p.SessionId); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return sessions;
    }

    /// <summary>
    /// Programı oturumun kullanıcısı olarak, o kullanıcının ortamıyla ve etkileşimli masaüstünde başlatır. <c>WTSQueryUserToken</c>
    /// kullanıcının SINIRLI belirtecini verir (UAC'li yönetici de yükseltilmeden açılır; oturum açılışındaki HKLM Run gibi). Yalnız
    /// LocalSystem'de çalışır. 0 = başladı; değilse Win32 hata kodu (oturumda kullanıcı yoksa 1008 ERROR_NO_TOKEN).
    /// </summary>
    private static int StartInSession(string path, uint session)
    {
        if (!WTSQueryUserToken(session, out IntPtr token)) return Marshal.GetLastWin32Error();
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false)) return Marshal.GetLastWin32Error();
            var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var commandLine = new StringBuilder($"\"{path}\"");
            if (!CreateProcessAsUserW(token, path, commandLine, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment, environment,
                    Path.GetDirectoryName(path), ref startup, out var info))
                return Marshal.GetLastWin32Error();
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            return 0;
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            CloseHandle(token);
        }
    }

    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    // LocalSystem sürecinde yerel kütüphaneler yalnız System32'den (program klasöründen değil; bağımsız inceleme, 1.9.0)
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryInfoKeyW(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, IntPtr className, IntPtr classLength,
        IntPtr reserved, IntPtr subKeys, IntPtr maxSubKeyLength, IntPtr maxClassLength, IntPtr values, IntPtr maxValueNameLength,
        IntPtr maxValueLength, IntPtr securityDescriptor, out long lastWriteTime);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(IntPtr handle);
}
