using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Apfs.Windows.Ipc;
using Apfs.Windows.Reporting;

namespace Apfs.Tests;

/// <summary>Sahte rapor sunucusu (yerel HttpListener): gelen gövdeleri saklar; yanıt kodu ve gecikme ayarlanır.</summary>
internal sealed class FakeReportServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<string> _bodies = [];
    private readonly CancellationTokenSource _cts = new();

    public FakeReportServer()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }
    public Uri Url => new($"http://localhost:{Port}/v1/events");
    public int Status { get; set; } = 200;
    public TimeSpan Delay { get; set; }
    /// <summary>Yanıt gövdesi (güncelleme denetimi testleri JSON verir).</summary>
    public string? ResponseBody { get; set; }
    /// <summary>Gelen isteklerin başlıkları (ad: değer), istek sırasıyla.</summary>
    public List<string> Headers { get { lock (_bodies) return [.. _headers]; } }
    private readonly List<string> _headers = [];

    public List<string> Bodies { get { lock (_bodies) return [.. _bodies]; } }

    public List<JsonObject> Events => [.. Bodies.SelectMany(b => JsonNode.Parse(b)!["events"]!.AsArray()).Select(e => e!.AsObject())];

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    string body = await reader.ReadToEndAsync();
                    if (Delay > TimeSpan.Zero) await Task.Delay(Delay, _cts.Token);
                    lock (_bodies)
                    {
                        _bodies.Add(body);
                        foreach (string? k in ctx.Request.Headers.AllKeys) _headers.Add($"{k}: {ctx.Request.Headers[k]}");
                    }
                    ctx.Response.StatusCode = Status;
                    if (ResponseBody is { } rb)
                    {
                        var bytes = Encoding.UTF8.GetBytes(rb);
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                    ctx.Response.Close();
                }
                catch (Exception) { try { ctx.Response.Abort(); } catch { } }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

/// <summary>
/// 1.4.0 P2.4: gönderici sahte sunucuya karşı (200 / 500 / zaman aşımı / çevrimdışı / kalıcı ret), dolu kuyruk birkaç çekişte
/// boşalır; uçtan uca: ayrı süreçteki hizmet (<see cref="ServiceWriteTests.DevService"/>) + fikstür → bağlama olayı sunucuya
/// ulaşır ve tanıtıcı bilgi taşımaz; onay yokken hiçbir şey kuyruğa girmez ve gitmez.
/// </summary>
[Collection("WinFsp")]
public class ReportUploadTests(LinuxFixtures fixtures) : IClassFixture<LinuxFixtures>
{
    private static string TempDir() => Directory.CreateTempSubdirectory("apfs-upload-").FullName;

    private static ReportQueue Filled(int n, int pad = 0)
    {
        var q = new ReportQueue(Path.Combine(TempDir(), "queue.jsonl"));
        for (int i = 1; i <= n; i++)
        {
            var e = ReportEvents.Create("eject", "ok", "service").WithCode("N" + i);
            if (pad > 0)
                e["crash"] = new JsonObject { ["exType"] = "System.IO.IOException", ["frames"] = new JsonArray([.. Enumerable.Range(0, pad).Select(k => JsonValue.Create($"Apfs.{new string('F', 150)}.M{k}+0x1"))]) };
            q.Enqueue(e);
        }
        return q;
    }

    [Fact]
    public async Task SuccessSendsEverythingInSmallValidBodiesAndAcks()
    {
        using var server = new FakeReportServer();
        var q = Filled(120, pad: 2);
        var sent = new SentLog(Path.Combine(TempDir(), "sent.jsonl"));
        using var up = new ReportUploader(server.Url, sent);
        var r = await up.DrainAsync(new LocalReportSource(q, pageBytes: 20_000));
        Assert.Equal(UploadOutcome.Sent, r.Outcome);
        Assert.Equal(120, r.Sent);
        Assert.True(r.Pulls > 3, $"{r.Pulls} çekiş");
        Assert.Equal(0, q.Count);
        Assert.True(server.Bodies.Count > 10);
        foreach (var b in server.Bodies)
        {
            Assert.True(Encoding.UTF8.GetByteCount(b) <= ReportUploader.MaxBodyBytes, b.Length.ToString());
            Assert.Empty(ReportSchema.ValidateEnvelope(JsonNode.Parse(b)));
        }
        Assert.Equal(Enumerable.Range(1, 120).Select(i => "N" + i), server.Events.Select(e => e["code"]!.GetValue<string>()));
        Assert.Equal(SentLog.Capacity, sent.Read().Count); // son 20 olay saklanır
        Assert.Contains("\"N120\"", sent.Read()[^1]);
    }

    [Fact]
    public async Task ServerErrorTimeoutAndOfflineKeepTheQueue()
    {
        using var server = new FakeReportServer { Status = 500 };
        var q = Filled(5);
        using (var up = new ReportUploader(server.Url, null))
            Assert.Equal(UploadOutcome.Failed, (await up.DrainAsync(new LocalReportSource(q))).Outcome);
        Assert.Equal(5, q.Count);

        server.Status = 429;
        using (var up = new ReportUploader(server.Url, null))
            Assert.Equal(UploadOutcome.Failed, (await up.DrainAsync(new LocalReportSource(q))).Outcome);
        Assert.Equal(5, q.Count);

        server.Status = 200;
        server.Delay = TimeSpan.FromSeconds(5);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using (var up = new ReportUploader(server.Url, null, timeout: TimeSpan.FromSeconds(1)))
            Assert.Equal(UploadOutcome.Failed, (await up.DrainAsync(new LocalReportSource(q))).Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), watch.Elapsed.ToString());
        Assert.Equal(5, q.Count);

        var offline = new Uri($"http://localhost:{FakeReportServer.FreePort()}/v1/events"); // dinleyen yok
        using (var up = new ReportUploader(offline, null))
            Assert.Equal(UploadOutcome.Failed, (await up.DrainAsync(new LocalReportSource(q))).Outcome);
        Assert.Equal(5, q.Count);

        server.Delay = TimeSpan.Zero;
        using (var up = new ReportUploader(server.Url, null))
            Assert.Equal(UploadOutcome.Sent, (await up.DrainAsync(new LocalReportSource(q))).Outcome);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task PermanentRejectionAndInvalidEventsAreDroppedNotRetriedForever()
    {
        using var server = new FakeReportServer { Status = 400 };
        var q = Filled(3);
        var bad = ReportEvents.Create("eject", "ok", "service");
        bad["machine"] = "PC"; // şemada yok: gönderilmez
        q.Enqueue(bad);
        using var up = new ReportUploader(server.Url, null);
        var r = await up.DrainAsync(new LocalReportSource(q));
        Assert.Equal(UploadOutcome.Sent, r.Outcome);
        Assert.Equal(0, r.Sent);
        Assert.Equal(4, r.Dropped);
        Assert.Equal(0, q.Count);
        Assert.DoesNotContain(server.Bodies, b => b.Contains("machine", StringComparison.Ordinal));
    }

    [Fact]
    public void BackoffDoublesUpToSixHours()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), ReportClient.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(2), ReportClient.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(64), ReportClient.Backoff(7));
        Assert.Equal(TimeSpan.FromHours(6), ReportClient.Backoff(50));
    }

    // ---------------- Uçtan uca: ayrı süreçteki hizmet ----------------

    [Fact]
    public async Task EndToEndMountEventReachesServerWithoutIdentifyingData()
    {
        string image = fixtures.Copy("pop-ci-64m");
        string dir = TempDir();
        ReportRecorder.WriteConfig(dir, true); // kullanıcı daha önce "evet" demiş; hizmet açılışta okur
        using var server = new FakeReportServer();
        using (var svc = new ServiceWriteTests.DevService(image, extraArgs: $"--reports-dir \"{dir}\""))
        {
            await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
            using var up = new ReportUploader(server.Url, null);
            var r = await up.DrainAsync(new ServiceReportSource());
            Assert.Equal(UploadOutcome.Sent, r.Outcome);
            var page = await svc.Send<ReportsResponse>(new Request("reports-pull"));
            Assert.True(page.Enabled);
            Assert.Empty(page.Reports!); // onaylananlar hizmet kuyruğundan silindi
        }
        var mount = Assert.Single(server.Events, e => e["type"]!.GetValue<string>() == "mount");
        Assert.Equal("ok", mount["result"]!.GetValue<string>());
        Assert.Equal("service", mount["process"]!.GetValue<string>());
        Assert.Equal("image", mount["disk"]!["bus"]!.GetValue<string>());
        Assert.Equal("tr", mount["lang"]!.GetValue<string>());
        Assert.Equal(1, mount["volumes"]!["mounted"]!.GetValue<int>());
        using var c = TestImages.Open(image);
        var identifying = ReportingTests.Identifying(c);
        identifying.AddRange([image, dir]);
        foreach (var e in server.Events) ReportingTests.AssertPrivate(e, identifying);
    }

    [Fact]
    public async Task WithoutConsentNothingIsQueuedOrSentAndTurningOffDeletesTheQueue()
    {
        string image = fixtures.Copy("pop-ci-64m");
        string dir = TempDir(); // yapılandırma yok = varsayılan KAPALI
        using var server = new FakeReportServer();
        using var svc = new ServiceWriteTests.DevService(image, extraArgs: $"--reports-dir \"{dir}\"");
        var d = await svc.WaitDisk(x => x.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        var off = await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true)); // olay üretecek işlemler
        Assert.True(off.Ok, off.Message);
        await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: false, Force: true));
        var page = await svc.Send<ReportsResponse>(new Request("reports-pull"));
        Assert.False(page.Enabled);
        Assert.Empty(page.Reports!);
        Assert.False(File.Exists(Path.Combine(dir, "queue.jsonl")));
        using (var up = new ReportUploader(server.Url, null))
            Assert.Equal(UploadOutcome.Nothing, (await up.DrainAsync(new ServiceReportSource())).Outcome);
        Assert.Empty(server.Bodies);

        // Açınca olaylar birikir (yazmaya açma + oturum), kapatınca kuyruk silinir ve yeni olay toplanmaz
        await svc.Send<UnlockResponse>(new Request("reports-config", Enable: true));
        Assert.True((await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true))).Ok);
        Assert.True((await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: false, Force: true))).Ok);
        page = await svc.Send<ReportsResponse>(new Request("reports-pull"));
        Assert.Contains(page.Reports!, r => r.Event.GetProperty("type").GetString() == "write-enable");
        var session = Assert.Single(page.Reports!, r => r.Event.GetProperty("type").GetString() == "write-session").Event;
        Assert.Equal("read-only", session.GetProperty("write").GetProperty("close").GetString());
        await svc.Send<UnlockResponse>(new Request("reports-config", Enable: false));
        Assert.False(File.Exists(Path.Combine(dir, "queue.jsonl")));
        Assert.True((await svc.Send<UnlockResponse>(new Request("set-writable", Disk: d.Id, Enable: true))).Ok);
        Assert.Empty((await svc.Send<ReportsResponse>(new Request("reports-pull"))).Reports!);
    }

    [Fact]
    public async Task FullServiceQueueDrainsOverSeveralPipePages()
    {
        string image = fixtures.Copy("mk-ci-64m");
        string dir = TempDir();
        ReportRecorder.WriteConfig(dir, true);
        var q = new ReportQueue(Path.Combine(dir, "queue.jsonl"));
        for (int i = 0; i < ReportQueue.MaxEvents; i++)
        {
            var e = ReportEvents.Crash(new IOException(), "service");
            e["crash"]!["frames"] = new JsonArray([.. Enumerable.Range(0, 6).Select(k => JsonValue.Create($"Apfs.{new string('G', 150)}.M{k}+0x{i:x}"))]);
            q.Enqueue(e);
        }
        Assert.True(new FileInfo(q.PathName).Length > 2 * PipeProtocol.MaxReportPageBytes, "kuyruk birden çok sayfa olmalı");
        int queued = q.Count;
        using var server = new FakeReportServer();
        using var svc = new ServiceWriteTests.DevService(image, extraArgs: $"--reports-dir \"{dir}\"");
        await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        using var up = new ReportUploader(server.Url, null);
        var r = await up.DrainAsync(new ServiceReportSource());
        Assert.Equal(UploadOutcome.Sent, r.Outcome);
        Assert.True(r.Pulls >= 3, $"{r.Pulls} çekiş");
        Assert.True(r.Sent >= queued, $"{r.Sent} / {queued}");
        Assert.Empty((await svc.Send<ReportsResponse>(new Request("reports-pull"))).Reports!);
    }

    [Fact]
    public async Task ClientRoundTellsTheServiceTheChoiceAndSendsBothQueues()
    {
        string image = fixtures.Copy("mk-ci-64m");
        string dir = TempDir(); // hizmette yapılandırma yok (kapalı)
        var local = new ReportRecorder(TempDir());
        using var server = new FakeReportServer();
        using var svc = new ServiceWriteTests.DevService(image, extraArgs: $"--reports-dir \"{dir}\"");
        await svc.WaitDisk(d => d.Volumes.Any(v => v.State == VolumeStatus.Mounted));
        bool? consent = null;
        using var client = new ReportClient(() => consent, local, new ServiceReportSource(), new ReportUploader(server.Url, null));

        // Cevaplanmadı: hizmete bir şey söylenmez, hiçbir şey gitmez
        Assert.Equal(UploadOutcome.Nothing, await client.RunOnceAsync());
        Assert.False((await svc.Send<ReportsResponse>(new Request("reports-pull"))).Enabled);

        // Evet: hizmet açılır; tepsinin çökme kaydı da gönderilir (ileti ve yol olmadan)
        consent = true;
        Assert.Equal(UploadOutcome.Nothing, await client.RunOnceAsync());
        Assert.True((await svc.Send<ReportsResponse>(new Request("reports-pull"))).Enabled);
        Assert.True(local.Enabled);
        try { throw new InvalidOperationException(ReportingTests.FakeProfile + " gizli"); }
        catch (InvalidOperationException e) { local.RecordCrash(e, "tray"); }
        Assert.Equal(1, local.Queue.Count);
        Assert.Equal(UploadOutcome.Sent, await client.RunOnceAsync());
        var crash = Assert.Single(server.Events);
        Assert.Equal("tray", crash["process"]!.GetValue<string>());
        Assert.DoesNotContain("ayşe", crash.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, local.Queue.Count);

        // Hayır: hizmet kapanır (kuyruğunu siler), tepsi de artık kaydetmez
        consent = false;
        Assert.Equal(UploadOutcome.Nothing, await client.RunOnceAsync());
        Assert.False((await svc.Send<ReportsResponse>(new Request("reports-pull"))).Enabled);
        Assert.False(local.Enabled);
        local.RecordCrash(new IOException(), "tray");
        Assert.Equal(0, local.Queue.Count);
    }

    [Fact]
    public async Task OldServiceUnknownRequestIsAnEmptyBatch()
    {
        // Eski hizmet "reports-pull"ı tanımaz → UnlockResponse; gönderici boş yığın sayar ve hiçbir şey göndermez
        string pipe = "APFSold" + Guid.NewGuid().ToString("N")[..8];
        PipeProtocol.PipeName = pipe;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var s = PipeProtocol.CreateServer();
                await s.WaitForConnectionAsync(cts.Token);
                await PipeProtocol.ReadLineAsync(s, cts.Token);
                await PipeProtocol.WriteLineAsync(s, new UnlockResponse(false, "unknown request"), cts.Token);
            }
        });
        using var server = new FakeReportServer();
        using var up = new ReportUploader(server.Url, null);
        var r = await up.DrainAsync(new ServiceReportSource());
        Assert.Equal(UploadOutcome.Nothing, r.Outcome);
        Assert.Empty(server.Bodies);
        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }
}
