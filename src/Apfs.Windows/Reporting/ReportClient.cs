using Apfs.Windows.Ipc;

namespace Apfs.Windows.Reporting;

/// <summary>
/// Tepsideki raporlama döngüsü (arka planda; arayüzü hiç bekletmez). Her turda: kullanıcının seçimini hizmete bildirir (son
/// seçen geçerli; hizmet kapalıyken kaçan bildirim sonraki turda gider), onay varsa önce hizmetin, sonra tepsinin kendi
/// kuyruğunu gönderir. Tur aralığı 15 dk; sunucuya ulaşılamazsa 1 dk'dan başlayıp 6 saate kadar ikiye katlanarak bekler.
/// </summary>
public sealed class ReportClient : IDisposable
{
    public const string DefaultEndpoint = "https://hashido-api.zorba.dev/v1/events";

    /// <summary>Gizlilik açıklaması: açık depodaki README "Privacy" bölümü.</summary>
    public const string PrivacyUrl = "https://github.com/ErsinZorba/Hashido#privacy";

    /// <summary>Sunucu adresi; <c>APFS_REPORTS_URL</c> ortam değişkeni geliştirme ve testler için değiştirir.</summary>
    public static Uri Endpoint => new(Environment.GetEnvironmentVariable("APFS_REPORTS_URL") is { Length: > 0 } u ? u : DefaultEndpoint);

    private readonly Func<bool?> _consent;
    private readonly ReportRecorder _local;
    private readonly IReportSource _service;
    private readonly ReportUploader _uploader;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public ReportClient(Func<bool?> consent, ReportRecorder local, IReportSource service, ReportUploader uploader)
    {
        _consent = consent;
        _local = local;
        _service = service;
        _uploader = uploader;
    }

    public static TimeSpan Interval { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Başarısız denemeden sonra bekleme: 1, 2, 4… dk, en çok 6 sa.</summary>
    public static TimeSpan Backoff(int failures) =>
        TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, Math.Clamp(failures - 1, 0, 10))));

    /// <summary>Bir tur. Onay yoksa (null) hiçbir şey yapılmaz; "hayır" ise hizmete bildirilir ve yerel kuyruk silinir.</summary>
    public async Task<UploadOutcome> RunOnceAsync(CancellationToken ct = default)
    {
        bool? consent = _consent();
        if (consent is null) return UploadOutcome.Nothing;
        try { await PipeProtocol.SendAsync<UnlockResponse>(new Request("reports-config", Enable: consent.Value), TimeSpan.FromSeconds(10), ct); }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { }
        if (_local.Enabled != consent.Value) _local.SetEnabled(consent.Value);
        if (consent != true) return UploadOutcome.Nothing;

        var a = await _uploader.DrainAsync(_service, ct);
        if (a.Outcome == UploadOutcome.Failed) return UploadOutcome.Failed;
        var b = await _uploader.DrainAsync(new LocalReportSource(_local.Queue), ct);
        return b.Outcome == UploadOutcome.Failed ? UploadOutcome.Failed
            : a.Outcome == UploadOutcome.Sent || b.Outcome == UploadOutcome.Sent ? UploadOutcome.Sent : UploadOutcome.Nothing;
    }

    /// <summary>Arka plan döngüsü (ilk tur <paramref name="firstDelay"/> sonra).</summary>
    public void Start(TimeSpan firstDelay) => _loop = Task.Run(() => LoopAsync(firstDelay, _cts.Token));

    /// <summary>Seçim değişti: bekleyen gecikmeyi kısa kesip hemen bir tur at.</summary>
    public void Nudge() => Interlocked.Exchange(ref _wake, new CancellationTokenSource()).Cancel();

    private CancellationTokenSource _wake = new();

    private async Task LoopAsync(TimeSpan delay, CancellationToken ct)
    {
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Volatile.Read(ref _wake).Token))
            {
                try { await Task.Delay(delay, linked.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // Nudge
                catch (OperationCanceledException) { return; }
            }
            UploadOutcome outcome;
            try { outcome = await RunOnceAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { outcome = UploadOutcome.Failed; } // raporlama tepsiyi asla düşürmez
            failures = outcome == UploadOutcome.Failed ? failures + 1 : 0;
            delay = failures > 0 ? Backoff(failures) : Interval;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
        _uploader.Dispose();
        _cts.Dispose();
    }
}
