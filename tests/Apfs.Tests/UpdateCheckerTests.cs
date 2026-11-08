using Apfs.Windows;

namespace Apfs.Tests;

/// <summary>1.4.0 P2.5: "yeni sürüm var" denetimi sahte sunucuya karşı; günde bir denetim, sürüm başına bir bildirim.</summary>
public class UpdateCheckerTests
{
    private static string Release(string tag, bool prerelease = false, bool draft = false, string url = "https://github.com/ErsinZorba/Hashido/releases/tag/") =>
        $$"""{"tag_name":"{{tag}}","name":"x","draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},"html_url":"{{url}}{{tag}}","assets":[]}""";

    private static async Task<UpdateInfo?> Check(FakeReportServer server, string body, string current, int status = 200)
    {
        server.ResponseBody = body;
        server.Status = status;
        using var c = new UpdateChecker(server.Url);
        return await c.CheckAsync(Version.Parse(current));
    }

    [Fact]
    public async Task ComparesVersionsAndIgnoresDraftsPrereleasesAndBadAnswers()
    {
        using var server = new FakeReportServer();
        var newer = await Check(server, Release("v1.4.0"), "1.3.0");
        Assert.NotNull(newer);
        Assert.Equal(new Version(1, 4, 0), newer.Version);
        Assert.Equal("https://github.com/ErsinZorba/Hashido/releases/tag/v1.4.0", newer.ReleasePage.ToString());
        Assert.NotNull(await Check(server, Release("v1.10.0"), "1.9.3")); // sayısal karşılaştırma, metin değil
        Assert.Null(await Check(server, Release("v1.3.0"), "1.3.0"));
        Assert.Null(await Check(server, Release("v1.2.1"), "1.3.0"));
        Assert.Null(await Check(server, Release("v2.0.0", prerelease: true), "1.3.0"));
        Assert.Null(await Check(server, Release("v2.0.0", draft: true), "1.3.0"));
        Assert.Null(await Check(server, Release("v2.0.0", url: "https://kotu.example/"), "1.3.0")); // yalnız github.com açılır
        Assert.Null(await Check(server, Release("son-surum"), "1.3.0"));
        Assert.Null(await Check(server, "{bozuk", "1.3.0"));
        Assert.Null(await Check(server, "[]", "1.3.0"));
        Assert.Null(await Check(server, Release("v9.9.9"), "1.3.0", status: 404));
        Assert.Null(await Check(server, Release("v9.9.9"), "1.3.0", status: 500));

        // İstekte yalnız genel bir User-Agent: e-posta, kullanıcı ya da makine adı yok
        var ua = Assert.Single(server.Headers.Where(h => h.StartsWith("User-Agent:", StringComparison.Ordinal)).Distinct());
        Assert.Equal("User-Agent: apfs-update-check/1", ua);
        Assert.DoesNotContain(server.Headers, h => h.Contains('@') || h.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)
            || h.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OfflineAndTimeoutMeanNoUpdate()
    {
        using (var c = new UpdateChecker(new Uri($"http://localhost:{FakeReportServer.FreePort()}/latest")))
            Assert.Null(await c.CheckAsync(new Version(1, 0, 0)));
        using var server = new FakeReportServer { ResponseBody = Release("v9.0.0"), Delay = TimeSpan.FromSeconds(5) };
        using var slow = new UpdateChecker(server.Url, timeout: TimeSpan.FromSeconds(1));
        Assert.Null(await slow.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public void OncePerDayAndOncePerVersion()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateChecker.ShouldCheck(null, now));
        Assert.False(UpdateChecker.ShouldCheck(now.AddHours(-23), now));
        Assert.True(UpdateChecker.ShouldCheck(now.AddHours(-24), now));
        Assert.True(UpdateChecker.ShouldCheck(now.AddDays(3), now)); // saat geri alınmış

        var info = new UpdateInfo(new Version(1, 4, 0), new Uri("https://github.com/x/y/releases/tag/v1.4.0"));
        Assert.True(UpdateChecker.ShouldNotify(info, null));
        Assert.True(UpdateChecker.ShouldNotify(info, "1.3.0"));
        Assert.False(UpdateChecker.ShouldNotify(info, "1.4.0"));
        Assert.False(UpdateChecker.ShouldNotify(info, "1.5.0"));
    }

    /// <summary>Gerçek GitHub API'si (ağ ister; yalnız APFS_NET=1 ile): 1.2.0 diyen sürüme yayımlanmış daha yenisi görünür.</summary>
    [Fact]
    public async Task RealApiShowsANewerReleaseToAnOldVersion()
    {
        if (Environment.GetEnvironmentVariable("APFS_NET") != "1") return;
        using var c = new UpdateChecker(new Uri(UpdateChecker.DefaultApi));
        var info = await c.CheckAsync(new Version(1, 2, 0));
        Assert.NotNull(info);
        Assert.True(info.Version >= new Version(1, 3, 0), info.Version.ToString());
        Console.WriteLine($"gerçek API: {info.Version} {info.ReleasePage}");
    }
}
