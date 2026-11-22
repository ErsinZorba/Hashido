using System.IO;
using System.Windows;
using Apfs.Windows.Reporting;

namespace Apfs.Tray;

/// <summary>Açılış seçenekleri (Program'dan: komut satırı, tek örnek "göster" olayı, rapor kuyruğu).</summary>
/// <param name="ShowEvent">Başka bir örnek (Başlat menüsü kısayolu) "pencereyi göster" dediğinde işaretlenir.</param>
/// <param name="Show">Açılışta ana pencere (<c>--show</c>).</param>
/// <param name="Updated">Setup güncelleme kipinin sonunda açtı (<c>--updated</c>, 1.8.0).</param>
/// <param name="Shot">Görüntü düzeneği (yalnız geliştirme kanalında; <see cref="Dev.ShotRig"/>); programda null.</param>
internal sealed record LaunchOptions(EventWaitHandle? ShowEvent, bool Show, ReportRecorder Reports, bool Updated, Dev.ShotOptions? Shot);

/// <summary>
/// WPF uygulaması (XAML'siz; Main <see cref="Program"/>'da). Pencereler kapansa da süreç sürer (<see cref="ShutdownMode.OnExplicitShutdown"/>;
/// Ek A A12): programdan çıkış yalnız tepsi menüsündeki "Çıkış"tan (A11). Görünüm <see cref="Look"/>'ta (Fluent teması + palet).
/// </summary>
internal sealed class App : Application
{
    private readonly LaunchOptions _options;

    internal App(LaunchOptions options)
    {
        _options = options;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (options.Shot is { } shot)
        {
            // Düzenek: hata görüntünün yanına yazılır (programda yok)
            void Log(object? e) => File.AppendAllText(shot.Png + ".error.txt", e + Environment.NewLine);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject);
            DispatcherUnhandledException += (_, e) => Log(e.Exception);
        }
        // Arayüz iş parçacığındaki istisna: rapor yazılır, istisna YUTULMAZ (Handled false → süreç çöker; A9)
        DispatcherUnhandledException += (_, e) => Crash.Record(e.Exception);
        // Dağıtıcı Program.Run'da kendi döngüsüyle çalışır (Application.Run değil): Shutdown pencereleri kapatıp Exit'i verir,
        // döngüyü bitirmek bize kalır
        Exit += (_, _) => Dispatcher.InvokeShutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Look.Install(this); // hiçbir pencereden önce: Fluent teması, palet, rol fırçaları
        if (_options.Shot is { } shot)
        {
            Dev.ShotRig.Run(shot);
            return;
        }
        _ = new TrayHost(_options);
    }
}

/// <summary>Çökme raporu (yalnız onay verildiyse; tepsinin kendi kuyruğuna, eşzamanlı). Bir istisna bir kez yazılır.</summary>
internal static class Crash
{
    private static ReportRecorder? _reports;
    private static int _recorded;

    public static void Install(ReportRecorder reports)
    {
        _reports = reports;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record(e.ExceptionObject as Exception);
    }

    public static void Record(Exception? e)
    {
        if (Interlocked.Exchange(ref _recorded, 1) == 1) return; // WPF ve AppDomain olayları aynı çöküşü iki kez vermesin
        _reports?.RecordCrash(e, "tray");
    }
}
