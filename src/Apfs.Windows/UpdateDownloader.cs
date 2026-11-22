using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Apfs.Windows.Localization;

namespace Apfs.Windows;

/// <summary>
/// Doğrulanmış setup dosyası (1.8.0). Dosya, doğrulamadan setup başlayana dek yazmaya ve silmeye kapalı bir tutamakla
/// (<see cref="FileShare.Read"/>) açık tutulur: arada içeriği değiştirilemez. Setup başlatılınca (ya da vazgeçilince) bırakılır.
/// </summary>
public sealed class DownloadedSetup : IDisposable
{
    private readonly FileStream _handle;

    internal DownloadedSetup(Version version, string path, FileStream handle)
    {
        Version = version;
        Path = path;
        _handle = handle;
    }

    public Version Version { get; }
    public string Path { get; }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Setup indirilemedi ya da doğrulanamadı; ileti teknik (günlük) metnidir.</summary>
public sealed class UpdateDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Release'teki setup'ı indirir ve doğrular (1.8.0, <c>guncellemePlan</c> Risk 1): kendi HttpClient'ı (genel süre yok; 60 sn
/// ilerlemesiz kalırsa iptal), hedef <see cref="DefaultDirectory"/><c>\&lt;ad&gt;.part</c> → yeniden adlandır →
/// yazmaya kapalı tutamakla aç → boyut + SHA-256 (API <c>digest</c>) + dosyanın ProductVersion'ı (etiketle aynı). Biri
/// tutmazsa dosya silinir ve <see cref="UpdateDownloadException"/>. Önceden indirilmiş ve hâlâ doğrulanan dosya yeniden indirilmez.
/// </summary>
public sealed class UpdateDownloader : IDisposable
{
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Tepsinin indirme klasörü: yerel uygulama verisindeki teknik klasörün <c>updates</c> alt klasörü (kullanıcı başına;
    /// kaldırıcı siler). Teknik klasör adı tek yerden (<see cref="ServiceLog.FallbackPathName"/>), raporlar klasörü gibi.
    /// </summary>
    public static string DefaultDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ServiceLog.FallbackPathName)!, "updates");

    private readonly string _directory;
    private readonly TimeSpan _stall;
    private readonly HttpClient _http;

    public UpdateDownloader(string directory, TimeSpan? stallTimeout = null, HttpMessageHandler? handler = null)
    {
        _directory = directory;
        _stall = stallTimeout ?? DefaultStallTimeout;
        // Yönlendirme izlenir (GitHub dosyayı başka bir https sunucusundan verir); .NET https → http yönlendirmesini izlemez.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("apfs-update-check", "1"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
    }

    /// <summary>
    /// Setup'ı indirir (ya da önceden indirilmişi kullanır) ve doğrular. İlerleme yüzde olarak bildirilir.
    /// </summary>
    /// <exception cref="UpdateDownloadException">İndirme ya da doğrulama başarısız (dosya silindi).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> iptal edildi.</exception>
    public async Task<DownloadedSetup> DownloadAsync(UpdateInfo info, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var setup = info.Setup ?? throw new UpdateDownloadException(WinText.T("Update.NoSetup", info.Version.ToString(3)));
        string final;
        try
        {
            Directory.CreateDirectory(_directory);
            final = System.IO.Path.Combine(_directory, setup.FileName);
            if (File.Exists(final))
            {
                try { return Verify(info, final); }
                catch (UpdateDownloadException) { } // eski/bozuk kopya: Verify sildi, yeniden indirilir
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateDownloadException(WinText.T("Update.DownloadFailed", e.Message), e);
        }
        string part = final + ".part";
        try
        {
            await DownloadToAsync(setup, part, progress, ct).ConfigureAwait(false);
            File.Move(part, final, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(part);
            throw new UpdateDownloadException(WinText.T("Update.DownloadFailed", e.Message), e);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
        return Verify(info, final);
    }

    private async Task DownloadToAsync(UpdateSetup setup, string part, IProgress<int>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_stall);
        try
        {
            using var resp = await _http.GetAsync(setup.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new UpdateDownloadException(WinText.T("Update.HttpStatus", (int)resp.StatusCode));
            if (resp.Content.Headers.ContentLength is long announced && announced != setup.Size)
                throw new UpdateDownloadException(WinText.T("Update.BadSize", announced, setup.Size));
            await using var source = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long total = 0;
            int shown = -1;
            while (true)
            {
                stall.CancelAfter(_stall); // ilerledikçe süre yeniden başlar
                int n = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                if (total > setup.Size) throw new UpdateDownloadException(WinText.T("Update.BadSize", total, setup.Size));
                await target.WriteAsync(buffer.AsMemory(0, n), stall.Token).ConfigureAwait(false);
                int percent = (int)(total * 100 / setup.Size);
                if (percent != shown) progress?.Report(shown = percent);
            }
            if (total != setup.Size) throw new UpdateDownloadException(WinText.T("Update.BadSize", total, setup.Size));
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new UpdateDownloadException(WinText.T("Update.Stalled", (int)_stall.TotalSeconds), e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateDownloadException(WinText.T("Update.DownloadFailed", e.Message), e);
        }
    }

    /// <summary>Dosyayı yazmaya kapalı açar ve denetler; tutmazsa siler ve fırlatır.</summary>
    private static DownloadedSetup Verify(UpdateInfo info, string path)
    {
        var setup = info.Setup!;
        FileStream? handle = null;
        try
        {
            handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (handle.Length != setup.Size) throw new UpdateDownloadException(WinText.T("Update.BadSize", handle.Length, setup.Size));
            byte[] hash = SHA256.HashData(handle);
            if (!CryptographicOperations.FixedTimeEquals(hash, setup.Sha256)) throw new UpdateDownloadException(WinText.T("Update.BadHash"));
            string? product = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (ParseVersion(product) is not { } v || UpdateChecker.Normalize(v) != UpdateChecker.Normalize(info.Version))
                throw new UpdateDownloadException(WinText.T("Update.BadVersion", product ?? "-", info.Version.ToString(3)));
            handle.Position = 0;
            return new DownloadedSetup(info.Version, path, handle);
        }
        catch (Exception e) when (e is UpdateDownloadException or IOException or UnauthorizedAccessException)
        {
            handle?.Dispose();
            TryDelete(path);
            throw e as UpdateDownloadException ?? new UpdateDownloadException(WinText.T("Update.DownloadFailed", e.Message), e);
        }
    }

    /// <summary>"1.8.1" ya da "1.8.1+sha" → 1.8.1.</summary>
    internal static Version? ParseVersion(string? s) =>
        s?.Split('+')[0].Trim() is { Length: > 0 } t && Version.TryParse(t, out var v) && v.Build >= 0 ? v : null;

    private static readonly Regex SetupName = new(@"^Hashido-Setup-(\d+\.\d+\.\d+)\.exe(\.part)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Kurulu sürümden eski ya da ona eşit setup'ları ve yarım indirmeleri (<c>.part</c>) siler (tepsi açılırken). Klasörde başka
    /// dosyaya dokunulmaz.
    /// </summary>
    public static void CleanUp(string directory, Version installed)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (string f in Directory.EnumerateFiles(directory))
            {
                var m = SetupName.Match(System.IO.Path.GetFileName(f));
                if (!m.Success) continue;
                if (m.Groups[2].Success || UpdateChecker.Normalize(Version.Parse(m.Groups[1].Value)) <= UpdateChecker.Normalize(installed)) TryDelete(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _http.Dispose();
}
