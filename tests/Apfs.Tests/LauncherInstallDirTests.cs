using Apfs.Windows;

namespace Apfs.Tests;

/// <summary>
/// <c>apfs register/unregister</c> klasör kuralı (<see cref="LauncherInstallDir"/>), sahte bir "Program Files" köküyle:
/// kurulum klasöründen çalışan program kopyalanmaz ve klasörü asla silinmez; başka yerden çalışan program
/// Program Files\&lt;ürün adı&gt;'na işaretle kopyalanır ve yalnız o kopya silinebilir. Gerçek kayıt (yönetici) P4'te denenir.
/// </summary>
public sealed class LauncherInstallDirTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"apfs-launcher-{Guid.NewGuid():N}");
    private string ProgramFiles => Path.Combine(_root, "Program Files");

    public LauncherInstallDirTests() => Directory.CreateDirectory(ProgramFiles);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string MakeProgram(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "tr"));
        File.WriteAllText(Path.Combine(dir, "apfs.exe"), "exe");
        File.WriteAllText(Path.Combine(dir, "tr", "Apfs.Windows.resources.dll"), "dil");
        return dir;
    }

    [Theory]
    [InlineData("APFS for Windows")]   // 1.3.0'dan yükseltilen eski klasör
    [InlineData("Hashido")]            // 1.4.0 temiz kurulum
    [InlineData("Başka Ad\\alt")]
    public void InstalledProgramUsesItsOwnFolderAndItIsNeverDeleted(string folder)
    {
        string installed = MakeProgram(Path.Combine(ProgramFiles, folder));
        var (dir, copy) = LauncherInstallDir.Choose(installed + "\\", ProgramFiles);
        Assert.Equal(installed, dir);
        Assert.False(copy);
        Assert.False(LauncherInstallDir.HasMarker(installed));

        // unregister ister kendi klasöründen ister başka yerden (ör. geliştirme derlemesi) çalışsın: işaretsiz klasör kalır
        Assert.False(LauncherInstallDir.MayDelete(installed, installed));
        Assert.False(LauncherInstallDir.MayDelete(installed, Path.Combine(_root, "repo", "bin")));
        Assert.True(File.Exists(Path.Combine(installed, "apfs.exe")));
    }

    [Fact]
    public void ProgramOutsideProgramFilesIsCopiedWithMarkerAndOnlyThatCopyIsDeletable()
    {
        string dev = MakeProgram(Path.Combine(_root, "repo", "bin", "Debug"));
        var (dir, copy) = LauncherInstallDir.Choose(dev, ProgramFiles);
        Assert.True(copy);
        Assert.Equal(Path.Combine(ProgramFiles, Branding.ProductName), dir);

        LauncherInstallDir.CopyWithMarker(dev, dir);
        Assert.True(LauncherInstallDir.HasMarker(dir));
        Assert.Equal("exe", File.ReadAllText(Path.Combine(dir, "apfs.exe")));
        Assert.Equal("dil", File.ReadAllText(Path.Combine(dir, "tr", "Apfs.Windows.resources.dll")));

        LauncherInstallDir.CopyWithMarker(dev, dir); // yeniden register: kendi kopyasının üzerine yazar
        Assert.True(LauncherInstallDir.MayDelete(dir, dev));
        Assert.False(LauncherInstallDir.MayDelete(dir, dir)); // kopyadaki apfs.exe kendi klasörünü silmez
        Assert.False(LauncherInstallDir.MayDelete(dev, dev));
    }

    [Fact]
    public void CopyRefusesToOverwriteAnInstalledProgram()
    {
        string target = Path.Combine(ProgramFiles, Branding.ProductName);
        MakeProgram(target);
        File.WriteAllText(Path.Combine(target, "apfs.exe"), "kurulu");
        string dev = MakeProgram(Path.Combine(_root, "repo", "bin"));

        var e = Assert.Throws<ApfsException>(() => LauncherInstallDir.CopyWithMarker(dev, target));
        Assert.Equal(ApfsErrorCode.AccessDenied, e.Code);
        Assert.Equal("kurulu", File.ReadAllText(Path.Combine(target, "apfs.exe")));
        Assert.False(LauncherInstallDir.HasMarker(target));
        Assert.False(LauncherInstallDir.MayDelete(target, dev));
    }

    [Fact]
    public void ProgramFilesRootAndLookalikePrefixAreNotInside()
    {
        Assert.True(LauncherInstallDir.Choose(ProgramFiles, ProgramFiles).Copy);
        Assert.True(LauncherInstallDir.Choose(ProgramFiles + " (eski)\\APFS", ProgramFiles).Copy);
        Assert.False(LauncherInstallDir.Choose(Path.Combine(ProgramFiles.ToUpperInvariant(), "X"), ProgramFiles).Copy);
    }
}
