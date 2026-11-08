using System.Net.Http.Headers;
using System.Text.Json;

namespace Apfs.Windows;

/// <summary>Yayımlanmış daha yeni bir sürüm: numara ve GitHub Release sayfası (yalnız https://github.com/ adresi kabul edilir).</summary>
public sealed record UpdateInfo(Version Version, Uri ReleasePage);

/// <summary>
/// "Yeni sürüm var" denetimi: GitHub'ın herkese açık API'sinde son Release (taslak ve ön sürüm hariç). Hiçbir şey indirilmez;
/// istekte genel bir User-Agent dışında bilgi yoktur (e-posta, kimlik, cihaz bilgisi gönderilmez). Her hata "yeni sürüm yok"
/// sayılır. Günde bir kez ve sürüm başına bir bildirim kuralı <see cref="ShouldCheck"/> / <see cref="ShouldNotify"/>'da.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>Açık depo (1.3.0 ve öncesi eski adı APFS-For-Windows kullanır; GitHub yönlendirir).</summary>
    public const string DefaultApi = "https://api.github.com/repos/ErsinZorba/Hashido/releases/latest";

    /// <summary>Adres; <c>APFS_UPDATE_URL</c> ortam değişkeni geliştirme ve testler için değiştirir.</summary>
    public static Uri Api => new(Environment.GetEnvironmentVariable("APFS_UPDATE_URL") is { Length: > 0 } u ? u : DefaultApi);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly Uri _api;
    private readonly HttpClient _http;

    public UpdateChecker(Uri api, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _api = api;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false }) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("apfs-update-check", "1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Bu programın sürümü (InformationalVersion'ın "+sha" öncesi).</summary>
    public static Version CurrentVersion()
    {
        var (v, _) = Reporting.ReportEvents.AppVersion();
        return Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0, 0);
    }

    /// <summary>Son Release <paramref name="current"/>'tan yeniyse onu, değilse (ya da herhangi bir hatada) null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(_api, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            if (!r.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } t) return null;
            if (!Version.TryParse(t.TrimStart('v', 'V'), out var version) || version.Build < 0) return null;
            if (!r.TryGetProperty("html_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var page)
                || page.Scheme != Uri.UriSchemeHttps || page.Host != "github.com") return null;
            return Normalize(version) > Normalize(current) ? new UpdateInfo(version, page) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>1.4 ile 1.4.0 eşit sayılsın (Revision yok sayılır).</summary>
    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>Günde bir kez: son denemeden bu yana <see cref="CheckInterval"/> geçtiyse (saat geri alındıysa da) denenir.</summary>
    public static bool ShouldCheck(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= CheckInterval || last > nowUtc;

    /// <summary>Sürüm başına bir bildirim: bu sürüm daha önce bildirilmediyse.</summary>
    public static bool ShouldNotify(UpdateInfo info, string? notifiedVersion) =>
        !Version.TryParse(notifiedVersion, out var n) || Normalize(info.Version) > Normalize(n);

    public void Dispose() => _http.Dispose();
}
