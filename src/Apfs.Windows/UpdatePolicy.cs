using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Apfs.Windows;

/// <summary>Güncelleme denetimini başlatan an (1.8.0).</summary>
public enum UpdateTrigger
{
    /// <summary>Tepsi açıldıktan ~30 sn sonra.</summary>
    Startup,
    /// <summary>Ana pencere açıldı.</summary>
    MainWindow,
    /// <summary>6 saatlik zamanlayıcı (günde bir kuralı).</summary>
    Timer,
    /// <summary>Kullanıcı istedi (Ayarlar › Şimdi denetle, alt bağlantı): sınırsız, bildirim kapalıyken de.</summary>
    Manual,
}

/// <summary>Kendiliğinden kurulumun koşulları (hepsi o anki durum; <see cref="UpdatePolicy.ShouldAutoInstall"/>).</summary>
/// <param name="AutoInstall">Ayarlar: bildirim VE kendiliğinden kurulum açık.</param>
/// <param name="SetupReady">İndirilip doğrulanmış setup elde.</param>
/// <param name="DiskCount">Hizmetin bu an bildirdiği disk sayısı; hizmet yanıt vermediyse null (= kurma).</param>
/// <param name="IsAdministrator">Kullanıcı yönetici grubunda (bölünmüş jeton dahil).</param>
/// <param name="DeclinedRecently">Bu sürüm son 24 saatte reddedildi ya da uygulanamadı.</param>
/// <param name="SetupRunning">Bir Hashido setup'ı zaten çalışıyor (bu tepsiden ya da başka oturumdan).</param>
public readonly record struct UpdateInstallConditions(
    bool AutoInstall, bool SetupReady, int? DiskCount, bool IsAdministrator, bool DeclinedRecently, bool SetupRunning);

/// <summary>
/// Uygulama içi güncellemenin saf kararları (1.8.0, <c>guncellemePlan</c> 2.3; tablo testleri <c>UpdatePolicyTests</c>) ve
/// iki küçük sistem sorgusu (yönetici mi, setup çalışıyor mu). Kurulum anı: açılıştaki denetimin indirmesi bitince ve ana
/// pencere kapatılınca; yalnız takılı Mac diski yokken (hizmet yanıt vermediyse kurulmaz).
/// </summary>
public static class UpdatePolicy
{
    /// <summary>Ana pencere ve açılış denetimleri arasında en az bu kadar (GitHub'ın kimliksiz sınırı saatte 60 istek).</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(1);

    /// <summary>Reddedilen sürüm bu kadar süre kendiliğinden önerilmez.</summary>
    public static readonly TimeSpan DeclineHold = TimeSpan.FromHours(24);

    /// <summary>
    /// Denetim yapılsın mı. Elle her zaman; bildirim kapalıysa yalnız elle. Zamanlayıcı günde bir; ana pencere en az saatte bir;
    /// açılış en az saatte bir, ama bilinen daha yeni bir sürüm bekliyorsa (kurulumu için setup bilgisi gerekir) her açılışta.
    /// Saat geri alınmışsa denetlenir.
    /// </summary>
    public static bool ShouldCheck(UpdateTrigger trigger, bool notify, DateTime? lastCheckUtc, DateTime nowUtc, bool newerKnown)
    {
        if (trigger == UpdateTrigger.Manual) return true;
        if (!notify) return false;
        if (lastCheckUtc is not { } last || last > nowUtc) return true;
        var since = nowUtc - last;
        return trigger switch
        {
            UpdateTrigger.Timer => since >= UpdateChecker.CheckInterval,
            UpdateTrigger.Startup => newerKnown || since >= MinInterval,
            _ => since >= MinInterval,
        };
    }

    /// <summary>Bu sürüm son <see cref="DeclineHold"/> içinde reddedildi mi (saat geri alınmışsa: hayır).</summary>
    public static bool DeclinedRecently(Version version, string? declinedVersion, DateTime? declinedUtc, DateTime nowUtc) =>
        Version.TryParse(declinedVersion, out var d) && UpdateChecker.Normalize(d) == UpdateChecker.Normalize(version)
        && declinedUtc is { } t && t <= nowUtc && nowUtc - t < DeclineHold;

    /// <summary>Kendiliğinden kurulum: her koşul tutmalı (biri yanlışsa kurulmaz).</summary>
    public static bool ShouldAutoInstall(UpdateInstallConditions c) =>
        c.AutoInstall && c.SetupReady && c.DiskCount == 0 && c.IsAdministrator && !c.DeclinedRecently && !c.SetupRunning;

    /// <summary>
    /// "Hazır, tıklayıp Kur'a basabilirsiniz" balonu (1.9.0): kendiliğinden kurulumu YALNIZ takılı Mac diski engelliyor (öbür her koşul
    /// tutuyor). Disk hep takılı kalan yönetici yeni sürümü yoksa hiç duymazdı; kurulum anı değişmez, sürpriz izin penceresi yok.
    /// Sürüm başına bir kez (tepsi hatırlar).
    /// </summary>
    public static bool ShouldAnnounceReady(UpdateInstallConditions c) =>
        c.AutoInstall && c.SetupReady && c.DiskCount > 0 && c.IsAdministrator && !c.DeclinedRecently && !c.SetupRunning;

    /// <summary>
    /// Zamanlayıcı (6 sa) denetiminin indirdiği sürüm için "hazır" haberi (1.9.1): zamanlayıcı kurmaz, o yüzden disk sayısına
    /// bakılmaz; öbür koşullar <see cref="ShouldAnnounceReady"/> ile aynı. Sürüm başına bir kez (tepsi hatırlar).
    /// </summary>
    public static bool ShouldAnnounceTimerReady(UpdateInstallConditions c) =>
        c.AutoInstall && c.SetupReady && c.IsAdministrator && !c.DeclinedRecently && !c.SetupRunning;

    /// <summary>Setup kendiliğinden indirilsin mi (kurulamayacaksa indirilmez: yönetici olmayan kullanıcı, setup bilgisi yok).</summary>
    public static bool ShouldDownload(bool autoInstall, bool hasSetup, bool isAdministrator) => autoInstall && hasSetup && isAdministrator;

    /// <summary>
    /// "Yeni sürüm çıktı" balonu: kendiliğinden kurulum bu sürümü üstlenemiyorsa (kapalı, setup bilgisi yok, yönetici değil).
    /// Üstleniyorsa yalnız "kuruluyor" ve "güncellendi" balonları çıkar (bildirim ilkesi: gerektiği kadar).
    /// </summary>
    public static bool ShouldAnnounceFound(bool autoInstall, bool hasSetup, bool isAdministrator) =>
        !ShouldDownload(autoInstall, hasSetup, isAdministrator);

    /// <summary>
    /// Kullanıcı Yöneticiler grubunda mı: UAC'li bölünmüş jetonda (sınırlı) da true. Jeton türü "varsayılan" ise (UAC kapalı ya
    /// da standart kullanıcı) grup üyeliğine bakılır.
    /// </summary>
    public static bool UserIsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            if (GetTokenInformation(id.Token, TokenElevationType, out int type, sizeof(int), out _))
                if (type is ElevationFull or ElevationLimited) return true;
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Bir Hashido setup'ı çalışıyor mu (1.9.1): Inno'nun <c>Global\HashidoSetup</c> mutex'i (bütün oturumlar; <c>.iss</c>
    /// <c>SetupMutex</c>) ya da BU oturumda <c>Hashido-Setup…</c> adlı süreç. Önceden her oturumdaki süreç adına bakılıyordu: başka bir
    /// hesap o adla bir süreç çalıştırarak güncellemeleri süresiz bekletebiliyordu. Kendi oturumundaki süreç yine sayılır: Inno mutex'i
    /// ancak yükseltilmiş süreç başladıktan birkaç saniye sonra açıyor, elle açılan setup'ın UAC penceresi açıkken mutex YOK (ölçüldü,
    /// surum191Plan P6.7).
    /// </summary>
    public static bool AnySetupRunning()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        var names = new List<(string, int)>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { all = []; } // liste yok: yalnız mutex
        foreach (var p in all)
            using (p)
            {
                try { names.Add((p.ProcessName, p.SessionId)); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } // süreç bitti
            }
        return SetupRunning(SetupMutexExists(), names, session);
    }

    /// <summary><see cref="AnySetupRunning"/>'in saf kararı: mutex varsa ya da bu oturumda setup adlı süreç varsa.</summary>
    internal static bool SetupRunning(bool mutexExists, IEnumerable<(string Name, int Session)> processes, int session) =>
        mutexExists || processes.Any(p => p.Session == session && p.Name.StartsWith("Hashido-Setup", StringComparison.OrdinalIgnoreCase));

    /// <summary>Setup'ın tek uçuş mutex'i (<c>.iss</c> <c>SetupMutex=HashidoSetup,Global\HashidoSetup</c>).</summary>
    internal const string SetupMutexName = @"Global\HashidoSetup";

    /// <summary>Mutex var mı (yükseltilmiş setup'ınkine erişim reddi de "var").</summary>
    internal static bool SetupMutexExists(string name = SetupMutexName)
    {
        try
        {
            if (!System.Threading.Mutex.TryOpenExisting(name, out var m)) return false;
            m.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// Güncelleme klasörü ağ yolunda mı (UNC; 1.9.1): öyleyse kendiliğinden indirme ve kurulum yapılmaz, yalnız haber verilir ve "Kur"
    /// Release sayfasını açar (setup ağ paylaşımından yükseltilmiş çalışmasın; doğrulamadan sonra paylaşımın sahibi dosyayı
    /// değiştirebilir). Yol çözülemezse de öyle sayılır (güvenli yön).
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        try { return new Uri(System.IO.Path.GetFullPath(path)).IsUnc; }
        catch (Exception e) when (e is ArgumentException or UriFormatException or NotSupportedException or System.IO.PathTooLongException) { return true; }
    }

    private const int TokenElevationType = 18;
    private const int ElevationFull = 2, ElevationLimited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
