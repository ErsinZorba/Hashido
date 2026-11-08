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
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--image") images.Add(Path.GetFullPath(args[i + 1]));
    if (args[i] == "--pipe" && args.Contains("--console")) Apfs.Windows.Ipc.PipeProtocol.PipeName = args[i + 1];
    if (args[i] == "--lang" && args.Contains("--console")) Apfs.Service.ServiceLanguage.Apply(args[i + 1]);
    if (args[i] == "--reports-dir" && args.Contains("--console")) reportsDir = Path.GetFullPath(args[i + 1]);
}
// Konsol kipinde varsayılan kuyruk kanala özgü geçici klasör: kurulu hizmetin ProgramData'daki kuyruğuna dokunulmaz
reportsDir ??= ServiceLog.EchoToConsole
    ? Path.Combine(Path.GetTempPath(), "apfs-dev-reports", Apfs.Windows.Ipc.PipeProtocol.PipeName)
    : ReportRecorder.DefaultServiceDirectory;
var reports = new ReportRecorder(reportsDir);
// Çökme olayı (onay varsa) eşzamanlı kuyruğa yazılır; istisna yutulmaz, süreç yine çöker ve SCM yeniden başlatır
AppDomain.CurrentDomain.UnhandledException += (_, e) => reports.RecordCrash(e.ExceptionObject as Exception, "service");
return new ApfsService(images.Count > 0 ? images : null, reports).Run();

internal sealed class ApfsService(IReadOnlyList<string>? images, ReportRecorder reports) : Fsp.Service("APFSforWindows")
{
    private MountManager? _manager;
    private PipeServer? _pipe;
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
        // Disk takıldığında/çıkarıldığında hemen; ayrıca bildirim kaçarsa diye 15 saniyede bir tara.
        _diskWatch = DeviceNotifications.WatchDiskInterfaces(_ => ScheduleScan(TimeSpan.FromSeconds(1)));
        _timer = new Timer(_ => ScheduleScan(TimeSpan.Zero), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    private void ScheduleScan(TimeSpan delay)
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
        _timer?.Dispose();
        _diskWatch?.Dispose();
        _pipe?.Dispose();
        _manager?.Dispose();
        ServiceLog.Write(WinText.T("Svc.Stopped"));
    }
}
