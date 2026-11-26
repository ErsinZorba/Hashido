using System.Globalization;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;
using Apfs.Windows.Reporting;
using System.IO;

namespace Apfs.Tray;

// apfs-tray [--show] [--pipe ad] [--restart]
//   --show: ana pencereyi aç (Başlat menüsü kısayolu). Program zaten çalışıyorsa açık örneğin penceresi öne gelir.
//   --pipe: yalnızca geliştirme; `apfs-service --console --pipe ad` ile çalışan hizmete bağlanır.
//   --restart [--from PID]: dil ya da tasarım değişince kendini yeniden başlatan örnek; eskisinin (PID) çıkmasını en çok 60 sn,
//     PID'siz (eski sürüm başlattıysa) tek örnek kilidini en çok 10 sn bekler (RestartHandoff).
//   --updated: setup güncelleme kipinin sonunda açtı (1.8.0): "güncellendi" balonu (kurulu sürüm indirilene ulaştıysa).
//   --forget-passwords: kaldırıcı (parolaPlan 2.4): bu kullanıcının "Bu bilgisayarda hatırla" kayıtlarını siler ve çıkar. Tek
//     örnek kilidinden ve arayüzden ÖNCE işlenir: açık bir tepsi örneği varken de bekleme olmadan çalışır.
//   --shot <png> --view <ad> [--fake <senaryo>] [--lang <dil>] [--switch-theme]: görüntü düzeneği (wpfPlan 2.1); YALNIZ --pipe
//     varsayılandan farklıyken.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--forget-passwords"))
        {
            CredentialStore.Default.DeleteAll();
            return;
        }
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--pipe") PipeProtocol.PipeName = args[i + 1];
        bool show = args.Contains("--show");
        bool dev = PipeProtocol.PipeName != PipeProtocol.DefaultPipeName;
        string suffix = dev ? "." + PipeProtocol.PipeName : ""; // geliştirme örneği kurulu olanla çakışmasın

        // Arayüz dili: kayıtlı seçim, yoksa Windows'un dili (süreç kendi dilini ayarlamadan önce okunmalı). Hizmete her istekte gider.
        var shotLang = dev ? Dev.ShotOptions.Parse(args)?.Lang : null; // yalnız düzenek (geliştirme kanalı)
        var language = shotLang is { Length: > 0 } ? CultureInfo.GetCultureInfo(shotLang) : LanguageSetting.Effective();
        CultureInfo.DefaultThreadCurrentUICulture = language;
        CultureInfo.CurrentUICulture = language;
        PipeProtocol.ClientLanguage = language.Name;
        if (shotLang is { Length: > 0 }) // düzenek: sayılar da görüntünün dilinde (README görselleri; 1,28 / 1.28)
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.CreateSpecificCulture(shotLang);
        // Bölge ayarı da her istekte (1.8.2): hizmetin yazdığı disk başlığı birim satırıyla aynı sayı biçiminde olsun ("1,81 TB")
        PipeProtocol.ClientCulture = CultureInfo.CurrentCulture.Name;

        // Görüntü düzeneği: kurulu programda (varsayılan kanal) etkisiz; tek örnek kilidi ve tepsi simgesi yok (Ek A S5)
        var shot = dev ? Dev.ShotOptions.Parse(args) : null;
        if (shot is not null)
        {
            Run(new LaunchOptions(null, false, new ReportRecorder(Path.Combine(Path.GetTempPath(), "hashido-shot-reports")), false, shot));
            return;
        }

        // Oturum başına tek örnek
        using var mutex = new Mutex(initiallyOwned: true, @"Local\APFSforWindows.Tray" + suffix, out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\APFSforWindows.Tray.Show" + suffix);
        // Yeniden başlatma: eski örneğin (--from PID) çıkması en çok 60 sn beklenir (1.9.1; önceden kilit 10 sn, yükte sessiz çıkış)
        if (!first && args.Contains("--restart"))
            first = Apfs.Windows.Tray.RestartHandoff.Acquire(mutex, Apfs.Windows.Tray.RestartHandoff.OldPid(args), Apfs.Windows.Tray.RestartHandoff.OldExitLimit);
        if (!first)
        {
            if (show) showEvent.Set();
            return;
        }

        // 1.6.2: varsayılan tasarım Windows 11; eski sürümün yazdığı "brand" bir kez Windows 11'e çevrilir (palet okunmadan önce)
        UserSettings.MigrateDesignDefault();

        // Çökme raporu (yalnız onay verildiyse; tepsinin kendi kuyruğuna, eşzamanlı). İstisna YUTULMAZ: süreç çöker.
        var localReports = new ReportRecorder(ReportRecorder.DefaultUserDirectory);
        Crash.Install(localReports);

        Run(new LaunchOptions(showEvent, show, localReports, args.Contains("--updated"), null));
        mutex.ReleaseMutex(); // yeniden başlatılan örnek beklemesin (A10)
    }

    /// <summary>Düzenek: açılış hatası görüntünün yanına yazılır (istisna yine yükselir).</summary>
    private static bool Log(string png, Exception e)
    {
        File.AppendAllText(png + ".error.txt", e + Environment.NewLine);
        return false;
    }

    /// <summary>
    /// WPF'i bu (STA) iş parçacığında başlatır; uygulama çıkınca döner. <c>Application.Run</c> DEĞİL, dağıtıcının kendi döngüsü:
    /// <c>Application.Run</c> gizli bir pencereyle Windows'un oturum sonu sorusunu (WM_QUERYENDSESSION) yakalar ve iptal edilmemişse
    /// programı hemen kapatır — oturum kapatma ya da kapanış sonradan iptal edilse de (başka bir program engelleyince) tepsi gitmiş
    /// olurdu (bağımsız denetçi, wpfPlan 2.7). Böylece WinForms/WinUI'deki gibi oturum sonu iletilerine dokunulmaz: Windows'u
    /// engellemez (Ek A A17). Not (kabul turu 2026-11-22): başka bir program engelleyip oturum kapatma İPTAL edilse de Windows o
    /// ana dek engellemeyen programları kapatıyor — tepsi (WPF ve WinUI kopyası) Spotify, Claude, NVIDIA ile birlikte kapandı;
    /// Hashido'ya özgü değil, sonraki oturumda kendiliğinden açılır. <see cref="Application.OnStartup"/>'ı kurucunun kuyruğa koyduğu iş çağırır;
    /// çıkışta dağıtıcıyı <see cref="App"/> kapatır.
    /// </summary>
    private static void Run(LaunchOptions options)
    {
        try
        {
            _ = new App(options);
            System.Windows.Threading.Dispatcher.Run();
        }
        catch (Exception e) when (options.Shot is { } shot && Log(shot.Png, e)) { }
    }
}
