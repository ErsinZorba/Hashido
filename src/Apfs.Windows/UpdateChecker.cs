using System.Net.Http.Headers;
using System.Text.Json;

namespace Apfs.Windows;

/// <summary>
/// Yayımlanmış daha yeni bir sürüm: numara, GitHub Release sayfası (yalnız https://github.com/ adresi kabul edilir) ve —
/// Release'te kurallara uyan bir setup dosyası varsa — onun adresi, boyutu ve SHA-256'sı (1.8.0; yoksa yalnız bağlantı).
/// </summary>
public sealed record UpdateInfo(Version Version, Uri ReleasePage, UpdateSetup? Setup = null);

/// <summary>Denetimin sonucu: sunucu geçerli yanıt verdi mi, verdiyse daha yeni sürüm (yoksa null = güncel).</summary>
public sealed record UpdateCheckResult(bool Answered, UpdateInfo? Update);

/// <summary>
/// Release'teki setup dosyası (1.8.0): <see cref="UpdateChecker"/> yalnız açık deponun o sürüm etiketindeki
/// <c>Hashido-Setup-&lt;sürüm&gt;.exe</c> dosyasını, GitHub'ın verdiği <c>digest</c> (sha256) ve boyutla kabul eder.
/// </summary>
public sealed record UpdateSetup(Uri Url, string FileName, long Size, byte[] Sha256)
{
    /// <summary>Kabul edilen boyut aralığı (bugünkü setup ~38 MB).</summary>
    public const long MinSize = 1L << 20, MaxSize = 200L << 20;

    public static string FileNameFor(Version v) => $"Hashido-Setup-{v.ToString(3)}.exe";
}

/// <summary>
/// "Yeni sürüm var" denetimi: GitHub'ın herkese açık API'sinde son Release (taslak ve ön sürüm hariç). Burada hiçbir şey
/// indirilmez (setup'ı <see cref="UpdateDownloader"/> indirir ve doğrular); istekte genel bir User-Agent dışında bilgi yoktur (e-posta, kimlik, cihaz bilgisi gönderilmez). Her hata "yeni sürüm yok"
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
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default) => (await QueryAsync(current, ct)).Update;

    /// <summary>
    /// <see cref="CheckAsync"/> gibi, ama "güncel" ile "denetlenemedi"yi ayırır (Ayarlar › Şimdi denetle, 1.8.0): sunucu
    /// geçerli bir Release yanıtı verdiyse <see cref="UpdateCheckResult.Answered"/> true.
    /// </summary>
    public async Task<UpdateCheckResult> QueryAsync(Version current, CancellationToken ct = default) =>
        await QueryCoreAsync(current, ct) is { } r ? r : new UpdateCheckResult(false, null);

    private async Task<UpdateCheckResult?> QueryCoreAsync(Version current, CancellationToken ct)
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
            return new UpdateCheckResult(true, Normalize(version) > Normalize(current) ? new UpdateInfo(version, page, FindSetup(r, version)) : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Açık deponun Release dosyalarının adres öneki (etiket ve dosya adı eklenir).</summary>
    public const string DownloadPrefix = "https://github.com/ErsinZorba/Hashido/releases/download/";

    /// <summary>
    /// Release'in <c>assets</c> listesinden bu sürümün setup'ı: ad <c>Hashido-Setup-&lt;sürüm&gt;.exe</c>, adres tam olarak
    /// <c>https://github.com/ErsinZorba/Hashido/releases/download/v&lt;sürüm&gt;/&lt;ad&gt;</c>, boyut 1–200 MB, <c>digest</c>
    /// <c>sha256:&lt;64 hex&gt;</c>. Biri tutmazsa null (o sürüm için yalnız Release bağlantısı gösterilir, kendiliğinden kurulmaz).
    /// Geliştirme ve testler: API adresi loopback ise (<c>APFS_UPDATE_URL=http://127.0.0.1:…</c>) dosya aynı kökenden olabilir.
    /// </summary>
    private UpdateSetup? FindSetup(JsonElement release, Version version)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        string name = UpdateSetup.FileNameFor(version);
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("name", out var n) || n.GetString() != name) continue;
            if (!a.TryGetProperty("browser_download_url", out var u) || !Uri.TryCreate(u.GetString(), UriKind.Absolute, out var url)
                || !AllowedSetupUrl(url, version, name)) return null;
            if (!a.TryGetProperty("size", out var sz) || !sz.TryGetInt64(out long size) || size < UpdateSetup.MinSize || size > UpdateSetup.MaxSize) return null;
            if (!a.TryGetProperty("digest", out var d) || d.GetString() is not { } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal)) return null;
            string hex = digest["sha256:".Length..];
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return null;
            return new UpdateSetup(url, name, size, Convert.FromHexString(hex));
        }
        return null;
    }

    private bool AllowedSetupUrl(Uri url, Version version, string name)
    {
        if (url.AbsoluteUri == $"{DownloadPrefix}v{version.ToString(3)}/{name}") return true;
        return IsLoopback(_api) && url.Scheme == _api.Scheme && url.Host == _api.Host && url.Port == _api.Port
            && url.AbsolutePath.EndsWith("/" + name, StringComparison.Ordinal);
    }

    /// <summary>Yerel deneme sunucusu (yalnız http://localhost / 127.0.0.1 / [::1]).</summary>
    internal static bool IsLoopback(Uri u) => u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;

    /// <summary>1.4 ile 1.4.0 eşit sayılsın (Revision yok sayılır).</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>Günde bir kez: son denemeden bu yana <see cref="CheckInterval"/> geçtiyse (saat geri alındıysa da) denenir.</summary>
    public static bool ShouldCheck(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc - last >= CheckInterval || last > nowUtc;

    /// <summary>Sürüm başına bir bildirim: bu sürüm daha önce bildirilmediyse.</summary>
    public static bool ShouldNotify(UpdateInfo info, string? notifiedVersion) =>
        !Version.TryParse(notifiedVersion, out var n) || Normalize(info.Version) > Normalize(n);

    public void Dispose() => _http.Dispose();
}
