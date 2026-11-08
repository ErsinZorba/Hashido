using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Apfs.Windows;

namespace Apfs.Tests;

/// <summary>
/// Ürün adı tek kaynaktan (<see cref="Branding.ProductName"/>) gelir. Kod dışı iki kopya (Directory.Build.props Product,
/// .iss AppName) aynı olmalı; kodda ve metin kaynaklarında ad düz metin olarak yalnız teknik kimliklerde kalır.
/// </summary>
public partial class BrandingTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "APFS-for-Windows.sln"))) return d.FullName;
        throw new InvalidOperationException("depo kökü bulunamadı");
    }

    [Fact]
    public void PropsInstallerAndAssembliesCarryTheSameName()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Build.props"));
        Assert.Equal(Branding.ProductName, props.Descendants("Product").Single().Value);

        string iss = File.ReadAllText(Path.Combine(Root, "installer", "APFS-for-Windows.iss"));
        var m = IssAppName().Match(iss);
        Assert.True(m.Success, ".iss içinde #define AppName yok");
        Assert.Equal(Branding.ProductName, m.Groups[1].Value);

        Assert.Equal(Branding.ProductName, typeof(Branding).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }

    [GeneratedRegex("^#define AppName \"([^\"]*)\"", RegexOptions.Multiline)]
    private static partial Regex IssAppName();

    /// <summary>Adın düz metin olarak kalabileceği yerler: teknik kimlikler (değişmez) ve adın kaynağı.</summary>
    private static readonly string[] TechnicalFiles =
    [
        "src/Apfs.Windows/Branding.cs",                   // kaynak
        "src/Apfs.Windows/ServiceLog.cs",                 // %ProgramData%\APFS for Windows (günlük klasörü)
        "src/Apfs.Windows/Localization/LanguageSetting.cs", // HKCU\Software\APFS for Windows
    ];

    [Fact]
    public void NameAppearsInCodeOnlyAsTechnicalIdentifier()
    {
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
            if (!rel.EndsWith(".cs", StringComparison.Ordinal) && !rel.EndsWith(".resx", StringComparison.Ordinal)) continue;
            if (TechnicalFiles.Contains(rel)) continue;
            if (File.ReadAllText(file).Contains("APFS for Windows", StringComparison.Ordinal)) problems.Add(rel);
        }
        Assert.True(problems.Count == 0, "ürün adı düz metin (Branding.ProductName ya da {0} kullanın): " + string.Join(", ", problems));
    }
}
