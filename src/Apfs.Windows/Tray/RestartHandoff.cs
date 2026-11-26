using System.Diagnostics;
using System.Globalization;

namespace Apfs.Windows.Tray;

/// <summary>
/// Dil/tasarım yeniden başlatmasında yeni tepsi örneğinin tek örnek kilidini devralması (1.9.1). Eski örnek yenisini
/// <c>--restart --from &lt;PID&gt;</c> ile başlatıp kapanır; yeni örnek eskisinin çıkmasını en çok <see cref="OldExitLimit"/> bekler,
/// sonra kilidi alır. Önceden kilit en çok 10 sn bekleniyordu: ağır yükte eski örnek daha geç kapanınca yenisi sessizce çıkıyor,
/// tepsi tümden kayboluyordu. Eski örnek sınırdan sonra hâlâ yaşıyorsa yenisi çıkar (tepsi yine açık: eskisi).
/// </summary>
public static class RestartHandoff
{
    /// <summary>Eski örneğin kapanması için en uzun bekleme.</summary>
    public static readonly TimeSpan OldExitLimit = TimeSpan.FromSeconds(60);

    /// <summary>Eski örneğin süreç kimliğini taşıyan argüman.</summary>
    public const string FromArg = "--from";

    /// <summary>Eski örnek çıktıktan sonra kilidin bırakılması için bekleme (çıkan sürecin kilidi hemen serbest kalır).</summary>
    private static readonly TimeSpan AfterExit = TimeSpan.FromSeconds(5);

    /// <summary>PID yoksa (eski sürümün başlattığı örnek) kilit bu kadar beklenir (bugüne dek olduğu gibi).</summary>
    private static readonly TimeSpan WithoutPid = TimeSpan.FromSeconds(10);

    /// <summary><c>--from &lt;PID&gt;</c>; yoksa ya da sayı değilse null.</summary>
    public static int? OldPid(string[] args)
    {
        int i = Array.IndexOf(args, FromArg);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0
            ? pid : null;
    }

    /// <summary>
    /// Eski örneğin çıkmasını bekler (en çok <paramref name="limit"/>), sonra tek örnek kilidini alır. Kilit alındıysa true. PID başka
    /// bir sürece aitse (eski örnek çoktan çıkmış, numara yeniden kullanılmış: adı <paramref name="processName"/> değil) beklenmez.
    /// </summary>
    /// <param name="processName">Eski örneğin süreç adı; verilmezse bu sürecinki.</param>
    public static bool Acquire(Mutex mutex, int? oldPid, TimeSpan limit, string? processName = null)
    {
        TimeSpan wait = WithoutPid;
        if (oldPid is int pid)
        {
            wait = AfterExit;
            processName ??= Process.GetCurrentProcess().ProcessName;
            try
            {
                using var old = Process.GetProcessById(pid);
                if (string.Equals(old.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) old.WaitForExit(limit);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { } // çoktan çıktı
        }
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; } // eski örnek kilidi bırakmadan kapandı: kilit artık bizim
    }
}
