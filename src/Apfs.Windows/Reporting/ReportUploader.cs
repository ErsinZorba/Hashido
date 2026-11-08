using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apfs.Windows.Ipc;

namespace Apfs.Windows.Reporting;

/// <summary>Gönderilecek olayların kaynağı: hizmetin kuyruğu (kanal) ya da tepsinin kendi kuyruğu.</summary>
public interface IReportSource
{
    /// <summary><paramref name="after"/>'dan sonraki olayların bir sayfası; kaynak yoksa/eskiyse boş.</summary>
    Task<ReportsResponse> PullAsync(long after, CancellationToken ct);

    /// <summary><paramref name="upto"/> dahil öncekiler gönderildi (ya da sunucu kalıcı olarak reddetti): silinebilir.</summary>
    Task AckAsync(long upto, CancellationToken ct);
}

/// <summary>Hizmetin kuyruğu, kanal üzerinden ("reports-pull" / "reports-ack").</summary>
public sealed class ServiceReportSource : IReportSource
{
    public async Task<ReportsResponse> PullAsync(long after, CancellationToken ct)
    {
        // Eski hizmet bu isteği tanımaz ve UnlockResponse döner: Reports null → boş yığın
        var r = await PipeProtocol.SendAsync<ReportsResponse>(new Request("reports-pull", ReportAfter: after), TimeSpan.FromSeconds(10), ct);
        return r is { Reports: not null } ? r : new ReportsResponse([], false, false);
    }

    public Task AckAsync(long upto, CancellationToken ct) =>
        PipeProtocol.SendAsync<UnlockResponse>(new Request("reports-ack", ReportAfter: upto), TimeSpan.FromSeconds(10), ct);
}

/// <summary>Bu sürecin kendi kuyruğu (tepsinin çökme olayları).</summary>
public sealed class LocalReportSource(ReportQueue queue, int pageBytes = PipeProtocol.MaxReportPageBytes) : IReportSource
{
    public Task<ReportsResponse> PullAsync(long after, CancellationToken ct)
    {
        var (list, more) = queue.Pull(after, pageBytes);
        return Task.FromResult(new ReportsResponse(list, more, true));
    }

    public Task AckAsync(long upto, CancellationToken ct)
    {
        queue.Ack(upto);
        return Task.CompletedTask;
    }
}

public enum UploadOutcome
{
    /// <summary>Gönderilecek bir şey yoktu.</summary>
    Nothing,
    /// <summary>Hepsi gönderildi (ya da sunucu kalıcı olarak reddetti ve silindi).</summary>
    Sent,
    /// <summary>Sunucuya ulaşılamadı / geçici hata (zaman aşımı, çevrimdışı, 5xx, 429): olaylar kuyrukta kaldı.</summary>
    Failed,
}

public sealed record UploadResult(UploadOutcome Outcome, int Sent, int Dropped, int Pulls);

/// <summary>
/// Olayları sunucuya gönderir: kaynaktan sayfa sayfa çeker, her olayı yeniden şemaya göre denetler, ≤ <see cref="MaxBodyBytes"/>
/// gövdeli yığınlar hâlinde <c>{"events":[…]}</c> POST eder; 2xx'te onaylar. 400/413/422 kalıcı ret sayılır (o yığın silinir,
/// yoksa kuyruk sonsuza dek takılır); zaman aşımı, bağlantı hatası, 429 ve 5xx geçicidir: kalan olaylar kuyrukta bekler.
/// Gönderilen son <see cref="SentLog.Capacity"/> olay "Gönderilenleri göster" için saklanır. Hiçbir hata dışarı sızmaz.
/// </summary>
public sealed class ReportUploader : IDisposable
{
    public const int MaxBodyBytes = 8 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly SentLog? _sent;

    public ReportUploader(Uri endpoint, SentLog? sent, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint;
        _sent = sent;
        // Vekil sunucu: kullanıcının sistem ayarı (tepsi kullanıcı oturumunda çalışır). Çerez, kimlik bilgisi gönderilmez.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, Credentials = null }) { Timeout = timeout ?? DefaultTimeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("apfs-report", "1"));
    }

    public async Task<UploadResult> DrainAsync(IReportSource source, CancellationToken ct = default)
    {
        int sent = 0, dropped = 0, pulls = 0;
        long after = 0;
        while (true)
        {
            ReportsResponse page;
            try { page = await source.PullAsync(after, ct); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
            {
                return new UploadResult(sent + dropped == 0 ? UploadOutcome.Nothing : UploadOutcome.Sent, sent, dropped, pulls);
            }
            pulls++;
            var reports = page.Reports ?? [];
            if (reports.Count == 0) break;

            foreach (var batch in Batches(reports))
            {
                if (batch.Events.Count > 0)
                {
                    bool? ok = await PostAsync(batch.Events, ct);
                    if (ok is null) return new UploadResult(UploadOutcome.Failed, sent, dropped, pulls); // geçici: kuyrukta kalsın
                    if (ok == true) { sent += batch.Events.Count; _sent?.Add(batch.Events); }
                    else dropped += batch.Events.Count;
                }
                dropped += batch.Invalid;
                try { await source.AckAsync(batch.LastId, ct); }
                catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
                {
                    return new UploadResult(UploadOutcome.Failed, sent, dropped, pulls);
                }
            }
            after = reports[^1].Id;
            if (!page.More) break;
        }
        return new UploadResult(sent + dropped == 0 ? UploadOutcome.Nothing : UploadOutcome.Sent, sent, dropped, pulls);
    }

    private sealed record Batch(List<JsonNode> Events, int Invalid, long LastId);

    /// <summary>Olayları gövdesi sınırı aşmayan yığınlara böler; şemaya uymayan ya da tek başına sınırı aşan olay atlanır (onaylanır).</summary>
    private static IEnumerable<Batch> Batches(List<QueuedReport> reports)
    {
        var events = new List<JsonNode>();
        int invalid = 0, size = Encoding.UTF8.GetByteCount("{\"events\":[]}");
        long last = 0;
        foreach (var r in reports)
        {
            var node = JsonNode.Parse(r.Event.GetRawText());
            int n = node is null ? 0 : Encoding.UTF8.GetByteCount(node.ToJsonString()) + 1;
            if (node is null || ReportSchema.ValidateEvent(node).Count > 0 || n + 16 > MaxBodyBytes)
            {
                invalid++;
                last = r.Id;
                continue;
            }
            if (events.Count > 0 && (size + n > MaxBodyBytes || events.Count >= 50))
            {
                yield return new Batch(events, invalid, last);
                events = [];
                invalid = 0;
                size = Encoding.UTF8.GetByteCount("{\"events\":[]}");
            }
            events.Add(node);
            size += n;
            last = r.Id;
        }
        if (events.Count > 0 || invalid > 0) yield return new Batch(events, invalid, last);
    }

    /// <returns>true: kabul edildi; false: kalıcı ret (silinsin); null: geçici hata (kalsın).</returns>
    private async Task<bool?> PostAsync(List<JsonNode> events, CancellationToken ct)
    {
        var body = new JsonObject { ["events"] = new JsonArray([.. events.Select(e => e.DeepClone())]) }.ToJsonString();
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(_endpoint, content, ct);
            if (resp.IsSuccessStatusCode) return true;
            return resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity
                ? false : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return null; // çevrimdışı, ad çözülemedi, zaman aşımı
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Gönderilen son olaylar (kullanıcı "Gönderilenleri göster" ile görür): satır başına bir olay, en yenisi sonda.</summary>
public sealed class SentLog(string path)
{
    public const int Capacity = 20;
    private readonly Lock _gate = new();

    public string PathName { get; } = path;

    public void Add(IEnumerable<JsonNode> events)
    {
        lock (_gate)
        {
            var lines = Read();
            lines.AddRange(events.Select(e => e.ToJsonString()));
            if (lines.Count > Capacity) lines.RemoveRange(0, lines.Count - Capacity);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                File.WriteAllLines(PathName, lines, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Saklanan olaylar, en yenisi sonda.</summary>
    public List<string> Read()
    {
        lock (_gate)
        {
            try { return [.. File.ReadAllLines(PathName, Encoding.UTF8).Where(l => l.Length > 0)]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { File.Delete(PathName); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
