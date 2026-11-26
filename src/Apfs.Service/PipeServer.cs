using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using Apfs.Windows.Reporting;

namespace Apfs.Service;

/// <summary>Tepsi uygulamasının bağlandığı adlandırılmış kanal sunucusu. Her bağlantı: tek istek, tek yanıt.</summary>
internal sealed class PipeServer(MountManager manager, ReportRecorder reports) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ReportLease _lease = new();
    private readonly RecentKeys _configLogged = new(); // reports-config günlük satırı: hesap başına 10 dk'da bir (kilit: kendisi)
    private Task? _loop;
    private int _devFaulted;

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>
    /// Kanalın ilk örneği <c>FirstPipeInstance</c> ile açılır (1.8.1): hizmet dururken adı kapan bir süreç varsa açılmaz,
    /// günlüğe bir kez <c>Svc.PipeNameTaken</c> yazılır ve 5 sn'de bir yeniden denenir (önceden sessizce onun ek örneği açılıyordu).
    /// Sonraki örnek, bağlanan örnek işlenmeye verilmeden ÖNCE açılır: ad hiç boş kalmaz.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        System.IO.Pipes.NamedPipeServerStream? listening = null;
        bool first = true, takenLogged = false;
        int served = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (listening is null)
                    {
                        listening = PipeProtocol.CreateServer(firstInstance: first);
                        first = false;
                    }
                    // Yalnız geliştirme (konsol) kipi: ilk bağlantıdan sonraki beklemede bir kez genel hata (genel yolu sınamak için)
                    if (served == 1 && ServiceLog.EchoToConsole && Environment.GetEnvironmentVariable("APFS_DEV_PIPE_FAULT_ONCE") == "1"
                        && Interlocked.Exchange(ref _devFaulted, 1) == 0)
                        throw new InvalidOperationException("APFS_DEV_PIPE_FAULT_ONCE");
                    try { await listening.WaitForConnectionAsync(ct); }
                    catch (IOException) when (!ct.IsCancellationRequested)
                    {
                        // Bağlanıp hemen kapanan istemci (1.8.2): bu örnek kullanılamaz. Yenisi beklemeden ve ESKİSİ atılmadan
                        // açılır (ad hiç boş kalmaz; önceden örnek atılıyor, 1 sn hiç dinleyen örnek kalmıyordu). Açılamazsa
                        // aşağıdaki genel yol (günlük + 1 sn) devralır.
                        var broken = listening;
                        listening = null;
                        try { listening = PipeProtocol.CreateServer(); }
                        finally { broken.Dispose(); }
                        continue;
                    }
                    var connected = listening;
                    listening = null;
                    served++;
                    try { listening = PipeProtocol.CreateServer(); }
                    finally { _ = Task.Run(() => HandleAsync(connected, ct), ct); }
                }
                catch (OperationCanceledException) { break; }
                catch (UnauthorizedAccessException) when (first)
                {
                    if (!takenLogged) ServiceLog.Write(WinText.T("Svc.PipeNameTaken", PipeProtocol.PipeName));
                    takenLogged = true;
                    await Task.Delay(5000, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                }
                catch (Exception e)
                {
                    ServiceLog.Write(WinText.T("Svc.PipeError", e.Message));
                    // Yeni örnek beklemeden ve eskisi atılmadan ÖNCE açılır (1.9.1): önceden örnek atılıyor, ad 1 sn boş kalıyordu
                    // (o arada adı başka bir süreç kapabilirdi). İlk örnek hiç açılmadıysa (ad başkasındaydı) ek örnek AÇILMAZ.
                    var broken = listening;
                    listening = null;
                    if (!first)
                        try { listening = PipeProtocol.CreateServer(); }
                        catch (Exception again) { ServiceLog.Write(WinText.T("Svc.PipeError", again.Message)); } // döngü hiç durmaz
                    broken?.Dispose();
                    if (listening is null) await Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
        }
        finally { listening?.Dispose(); }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                // 30 sn yalnız isteği okumaya (takılan istemci kanal örneğini tutmasın). İşlemin kendisi kesilmez: tepsi
                // set-writable için 5 dk bekler, büyük diskte tam denetim 30 sn'yi aşabilir; yanıt ayrı süreyle yazılır.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                var line = await PipeProtocol.ReadLineAsync(server, readTimeout.Token);
                if (line is null) return;
                var req = JsonSerializer.Deserialize<Request>(line, PipeProtocol.Json);
                // Bu istek isteyenin dili ve sayı biçimiyle işlenir; varsayılanı yalnız etkin konsol oturumu değiştirir
                ServiceLanguage.BeginRequest(req?.Lang, req?.Culture, FromConsoleSession(server));
                object response = req?.Op switch
                {
                    "status" => manager.Status(req.Since),
                    "unlock" when req.Disk is not null && req.Password is not null =>
                        await manager.UnlockAsync(req.Disk, req.Container, req.Volume, req.Password, CallerOf(server)),
                    "rescan" => await RescanAsync(ct),
                    "eject" when req.Disk is not null => await Task.Run(() => manager.Eject(req.Disk, req.Force), ct),
                    "set-writable" when req.Disk is not null => await Task.Run(() => manager.SetWritable(req.Disk, req.Container, req.Enable, req.Force), ct),
                    "reports-pull" => PullReports(req.ReportAfter, req.ReportLease),
                    "reports-ack" => Ack(req.ReportAfter, req.ReportLease),
                    "reports-config" => Configure(req.Enable, CallerOf(server)),
                    // Yalnız geliştirme modu (--image; testler): uyku/uyanış ve kimlik okuma hatası taklidi. Kurulu hizmette "bilinmeyen istek".
                    "dev-power" when manager.IsDevelopment =>
                        await Task.Run(() => { manager.DevPower(req.Enable); return new UnlockResponse(true, ""); }, ct),
                    "dev-identity-fail" when manager.IsDevelopment => DevIdentityFail(req.Volume),
                    _ => new UnlockResponse(false, WinText.T("Svc.UnknownRequest")),
                };
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await PipeProtocol.WriteLineAsync(server, response, writeTimeout.Token);
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or OperationCanceledException)
            {
                // Bozuk veya yarıda kalan istek: bağlantıyı kapat, hizmet çalışmaya devam eder.
            }
            catch (Exception e)
            {
                ServiceLog.Write(WinText.T("Svc.RequestFailed", e.GetType().Name, e.Message));
            }
        }
    }

    private UnlockResponse DevIdentityFail(int count)
    {
        manager.DevIdentityFail(count);
        return new UnlockResponse(true, "");
    }

    /// <summary>
    /// "Yeniden tara": ayrılmış diskler ve yanıtsız listesi unutulur, sonra istekten sonra başlayan birleşik tarama beklenir (1.9.1;
    /// <see cref="MountManager.RescanAsync"/>: eşzamanlı istekler tek taramayı paylaşır, havuzda iş parçacığı bekletilmez).
    /// </summary>
    private async Task<UnlockResponse> RescanAsync(CancellationToken ct)
    {
        manager.ForgetReleased();
        await manager.RescanAsync().WaitAsync(ct);
        return new UnlockResponse(true, WinText.T("Svc.Rescanned"));
    }

    /// <summary>
    /// İstek etkin konsol oturumundan mı (hizmetin varsayılan dilini o belirler). Konsol oturumu yoksa (oturum geçişi) ya da
    /// konsolda oturum açmış kimse yoksa (yalnız giriş ekranı; kullanıcı uzak masaüstüyle bağlı) her istek sayılır — önceki gibi;
    /// istemcinin oturumu öğrenilemezse sayılmaz. Geliştirme kipinde "konsol" hizmetin kendi oturumudur
    /// (<see cref="ServiceLanguage.ConsoleSessionOverride"/>; kullanıcı denetimi yok).
    /// </summary>
    private static bool FromConsoleSession(NamedPipeServerStream server)
    {
        uint console;
        if (ServiceLanguage.ConsoleSessionOverride is uint dev) console = dev;
        else
        {
            console = WTSGetActiveConsoleSessionId();
            if (console == NoConsoleSession || !HasUser(console)) return true;
        }
        return GetNamedPipeClientSessionId(server.SafePipeHandle, out uint session) && session == console;
    }

    /// <summary>Oturumda oturum açmış bir kullanıcı var mı (öğrenilemezse yok sayılır: önceki davranış, her istek).</summary>
    private static bool HasUser(uint session)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WtsUserName, out IntPtr buffer, out _)) return false;
        try { return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer)); }
        finally { WTSFreeMemory(buffer); }
    }

    private const int WtsUserName = 5;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>
    /// Kanaldaki istemcinin hesabı (SID): parola denemesi sınırının sayacı hesap başınadır, başka hesap sahibini bekletemez.
    /// Öğrenilemezse (istemci kimliğini vermeyen anonim düzeyle bağlandıysa) <see cref="MountManager.AnonymousCaller"/>: böyle
    /// istemcinin kilit açması ve rapor ayarı reddedilir/yok sayılır (1.9.1).
    /// </summary>
    private static string CallerOf(NamedPipeServerStream server)
    {
        string? sid = null;
        try
        {
            server.RunAsClient(() =>
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent(ifImpersonating: true);
                    sid = id?.User?.Value;
                    // Yalnız geliştirme (konsol) kipi: testler tek kullanıcıyla koşar; bürünme düzeyi veren istemci "başka hesap"
                    // sayılır (ürünün istemcileri Identification verir)
                    if (sid is not null && ServiceLog.EchoToConsole && id!.ImpersonationLevel == TokenImpersonationLevel.Impersonation
                        && Environment.GetEnvironmentVariable("APFS_DEV_CALLER_BY_LEVEL") == "1")
                        sid += "#dev-other";
                }
                catch (Exception) { } // anonim düzeyde belirteç açılamaz
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return sid ?? MountManager.AnonymousCaller;
    }

    private const uint NoConsoleSession = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientSessionId);

    /// <summary>
    /// Başka bir tepsi kuyruğu çekerken (kira) boş sayfa: olaylar iki kez gönderilmesin. Raporlama etkin değilken de boş sayfa
    /// (1.9.1): kapatma kuyruğu siler, arada kalan olay gönderilmez.
    /// </summary>
    private ReportsResponse PullReports(long after, string? lease)
    {
        if (!reports.Enabled) return new ReportsResponse([], false, false);
        if (!_lease.TryHold(lease)) return new ReportsResponse([], false, reports.Enabled);
        var (list, more) = reports.Queue.Pull(after, PipeProtocol.MaxReportPageBytes);
        return new ReportsResponse(list, more, reports.Enabled);
    }

    /// <summary>Onay her zaman uygulanır (o olaylar gönderildi); kirayı tutan tepside süreyi yeniler.</summary>
    private UnlockResponse Ack(long upto, string? lease)
    {
        _lease.TryHold(lease);
        reports.Queue.Ack(upto);
        return new UnlockResponse(true, "");
    }

    private UnlockResponse Configure(bool enabled, string caller)
    {
        if (caller == MountManager.AnonymousCaller) return new UnlockResponse(true, ""); // hesabı bilinmeyen seçim yok sayılır
        var (changed, _, after) = reports.SetEnabled(enabled, caller); // hesap başına seçim, "Hayır kazanır"
        // Günlük satırı yalnız bu hesabın seçimi değişince ve hesap başına 10 dk'da bir (1.9.1): önceden her açma/kapamada bir
        // satırdı; kanala erişen her hesap saniyeler içinde binlerce istekle günlüğü döndürüp eski kayıtları sildirebiliyordu
        bool log;
        lock (_configLogged) log = changed && _configLogged.TryMark(caller, TimeSpan.FromMinutes(10));
        if (log) ServiceLog.Write(after ? WinText.T("Svc.ReportsEnabled") : WinText.T("Svc.ReportsDisabled"));
        return new UnlockResponse(true, "");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
