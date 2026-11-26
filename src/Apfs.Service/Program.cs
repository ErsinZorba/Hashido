using Apfs;
using Apfs.Service;
using Apfs.Windows;
using Apfs.Windows.Reporting;

// Ürünün arka plan hizmeti (SCM adı APFSforWindows). Windows Hizmet Denetimi (SCM) tarafından LocalSystem olarak başlatılır.
// Geliştirme için: apfs-service --console --image a.img [--image b.img] [--pipe ad] [--lang tr] [--reports-dir klasör]
// (fiziksel diskler yerine görüntüler; yönetici gerekmez; --lang: tepsi bağlanmadan önceki dil — testler makinenin dilinden
// bağımsız olsun diye; --reports-dir: rapor kuyruğu ve onay yapılandırması, kurulu hizmetinkiyle karışmasın)
var images = new List<string>();
string? reportsDir = null;
ServiceLog.EchoToConsole = args.Contains("--console");
// Konsol kipinde çıktı yönlendirilmişse (testler, boru) UTF-8 yaz: varsayılan OEM kod sayfası (ör. İngilizce Windows'ta 437)
// Türkçe harfleri yazarken kaybeder. Etkileşimli terminalin kod sayfasına dokunulmaz.
if (ServiceLog.EchoToConsole)
{
    var utf8 = new System.Text.UTF8Encoding(false);
    if (Console.IsErrorRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    if (Console.IsOutputRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
}
// Kurulu hizmet (LocalSystem): günlük ve rapor klasörünü ilk yazmadan ÖNCE güvenceye al (1.8.1). Geliştirme kipinde dokunulmaz.
// Güvenceye alınamayan alan (günlük / raporlar) bu çalışmada SYSTEM'in kendi LOCALAPPDATA'sına yazılır (1.9.1).
bool reportsTrusted = true;
if (!ServiceLog.EchoToConsole)
{
    using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
    if (me.IsSystem)
    {
        var secured = DataDirSecurity.SecureServiceDataDirectory();
        reportsTrusted = secured.ReportsTrusted;
        if (!secured.LogTrusted) ServiceLog.UseFallbackOnly();
        if (secured.LinksRemoved > 0) ServiceLog.Write(WinText.T("Svc.DataDirLinksRemoved", secured.LinksRemoved));
        foreach (string aside in secured.MovedAside ?? []) ServiceLog.Write(WinText.T("Svc.DataDirMovedAside", aside));
        if (secured.FirstError is not null) ServiceLog.Write(WinText.T("Svc.DataDirSecureFailed", secured.FirstError));
        if (!secured.LogTrusted)
            ServiceLog.Write(WinText.T("Svc.DataDirUntrusted", Path.GetDirectoryName(ServiceLog.PathName)!, ServiceLog.FallbackPathName));
        if (!secured.ReportsTrusted)
            ServiceLog.Write(WinText.T("Svc.DataDirUntrusted", ReportRecorder.DefaultServiceDirectory, ReportRecorder.DefaultUserDirectory));
    }
}
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--image") images.Add(Path.GetFullPath(args[i + 1]));
    if (args[i] == "--pipe" && args.Contains("--console")) Apfs.Windows.Ipc.PipeProtocol.PipeName = args[i + 1];
    if (args[i] == "--lang" && args.Contains("--console")) Apfs.Service.ServiceLanguage.Apply(args[i + 1]);
    if (args[i] == "--reports-dir" && args.Contains("--console")) reportsDir = Path.GetFullPath(args[i + 1]);
}
// Geliştirme kipinde "etkin konsol oturumu" hizmetin kendi oturumudur (tepsi de orada); testler başka bir oturumu taklit edebilir
// (yalnız konsol oturumunun istekleri varsayılan dili değiştirir)
if (ServiceLog.EchoToConsole)
    Apfs.Service.ServiceLanguage.ConsoleSessionOverride =
        uint.TryParse(Environment.GetEnvironmentVariable("APFS_DEV_CONSOLE_SESSION"), out uint devConsole) ? devConsole
            : (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
// Konsol kipinde varsayılan kuyruk kanala özgü geçici klasör: kurulu hizmetin ProgramData'daki kuyruğuna dokunulmaz
reportsDir ??= ServiceLog.EchoToConsole
    ? Path.Combine(Path.GetTempPath(), "apfs-dev-reports", Apfs.Windows.Ipc.PipeProtocol.PipeName)
    : reportsTrusted ? ReportRecorder.DefaultServiceDirectory : ReportRecorder.DefaultUserDirectory;
var reports = new ReportRecorder(reportsDir);
// Çökme olayı (onay varsa) eşzamanlı kuyruğa yazılır; istisna yutulmaz, süreç yine çöker ve SCM yeniden başlatır
AppDomain.CurrentDomain.UnhandledException += (_, e) => reports.RecordCrash(e.ExceptionObject as Exception, "service");
return new ApfsService(images.Count > 0 ? images : null, reports).Run();

internal sealed class ApfsService(IReadOnlyList<string>? images, ReportRecorder reports) : Fsp.Service("APFSforWindows")
{
    private MountManager? _manager;
    private PipeServer? _pipe;
    private PowerEvents? _power;
    private DeviceNotifications.Registration? _diskWatch;
    private Timer? _timer;
    private int _scanning;

    protected override void OnStart(string[] args)
    {
        ServiceLog.Write(images is null ? WinText.T("Svc.Started") : WinText.T("Svc.StartedDev", images.Count));
        _manager = new MountManager(images, reports);
        _manager.ScanRequested += () => ScheduleScan(TimeSpan.FromSeconds(1));
        _pipe = new PipeServer(_manager, reports);
        _pipe.Start();
        // Uyku/uyanış (1.9.1): uykudan önce yazma oturumları kaydedilip bekletilir, uyanışta salt okunura döner
        var manager = _manager;
        _power = new PowerEvents(() => manager.PrepareForSleep(MountManager.SleepBudget), manager.ResumeFromSleep);
        // Disk takıldığında/çıkarıldığında hemen; ayrıca bildirim kaçarsa diye 15 saniyede bir tara.
        _diskWatch = DeviceNotifications.WatchDiskInterfaces(_ =>
        {
            RawDiskDevice.ForgetStalled(); // takılan/çıkarılan aygıt: okuması bırakılmış diskler yeniden denensin
            ScheduleScan(TimeSpan.FromSeconds(1));
        });
        _timer = new Timer(_ => ScheduleScan(TimeSpan.Zero), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
        // Kurulu hizmet (LocalSystem): setup'ın kapattığı öbür oturumların tepsileri yeniden açılır (1.9.0; kanal artık dinliyor)
        if (images is null && !ServiceLog.EchoToConsole) Task.Run(RestartTrays);
    }

    private static void RestartTrays()
    {
        try
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (me.IsSystem) TrayRestart.RunOnce(Path.Combine(AppContext.BaseDirectory, "apfs-tray.exe"), ServiceLog.Write);
        }
        catch (Exception e) // en iyi çabayla yapılan arka plan işi: hizmeti etkilemez, iz günlükte kalır
        {
            ServiceLog.Write(WinText.T("Svc.TrayRestartFailed", "?", e.Message));
        }
    }

    private void ScheduleScan(TimeSpan delay)
    {
        // Zamanlanmış tarama arka plan işidir: onu isteyen bir kanal isteğinin dili ve sayı biçimi taşınmaz
        if (ExecutionContext.IsFlowSuppressed()) { StartScanAfter(delay); return; }
        using (ExecutionContext.SuppressFlow()) StartScanAfter(delay);
    }

    private void StartScanAfter(TimeSpan delay)
    {
        Task.Delay(delay).ContinueWith(_ =>
        {
            if (Interlocked.Exchange(ref _scanning, 1) == 1) return; // tarama zaten sürüyor
            try { _manager?.Rescan(); }
            catch (Exception e) { ServiceLog.Write(WinText.T("Svc.ScanError", e.Message)); }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        }, TaskScheduler.Default);
    }

    protected override void OnStop()
    {
        _power?.Dispose();
        _timer?.Dispose();
        _diskWatch?.Dispose();
        _pipe?.Dispose();
        _manager?.Dispose();
        ServiceLog.Write(WinText.T("Svc.Stopped"));
    }
}
