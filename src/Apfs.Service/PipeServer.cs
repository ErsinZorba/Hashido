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
    private Task? _loop;

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
                    listening?.Dispose();
                    listening = null;
                    await Task.Delay(1000, ct).ContinueWith(_ => { }, TaskScheduler.Default);
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
                    "rescan" => await Task.Run(() => { manager.ForgetReleased(); manager.Rescan(); return new UnlockResponse(true, WinText.T("Svc.Rescanned")); }, ct),
                    "eject" when req.Disk is not null => await Task.Run(() => manager.Eject(req.Disk, req.Force), ct),
                    "set-writable" when req.Disk is not null => await Task.Run(() => manager.SetWritable(req.Disk, req.Container, req.Enable, req.Force), ct),
                    "reports-pull" => PullReports(req.ReportAfter),
                    "reports-ack" => Ack(req.ReportAfter),
                    "reports-config" => Configure(req.Enable),
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
    /// Öğrenilemezse (istemci kimliğini vermeyen anonim düzeyle bağlandıysa) "?" — bu istemciler ortak sayaçtadır.
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
                }
                catch (Exception) { } // anonim düzeyde belirteç açılamaz
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return sid ?? "?";
    }

    private const uint NoConsoleSession = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientSessionId);

    private ReportsResponse PullReports(long after)
    {
        var (list, more) = reports.Queue.Pull(after, PipeProtocol.MaxReportPageBytes);
        return new ReportsResponse(list, more, reports.Enabled);
    }

    private UnlockResponse Ack(long upto)
    {
        reports.Queue.Ack(upto);
        return new UnlockResponse(true, "");
    }

    private UnlockResponse Configure(bool enabled)
    {
        if (enabled != reports.Enabled) ServiceLog.Write(enabled ? WinText.T("Svc.ReportsEnabled") : WinText.T("Svc.ReportsDisabled"));
        reports.SetEnabled(enabled);
        return new UnlockResponse(true, "");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
