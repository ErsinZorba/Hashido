namespace Apfs.Windows;

/// <summary>
/// WinFsp Launcher kaydının çalıştıracağı programın klasörü (<c>apfs register</c> / <c>unregister</c>). SYSTEM yetkisiyle
/// çalışan Launcher yalnız yöneticilerin yazabildiği bir klasördeki dosyayı çalıştırmalı:
/// <list type="bullet">
/// <item>Program Files altında çalışan program (kurulum; 1.3.0'dan yükseltilen eski klasör ya da yeni ad) kendi klasörünü kullanır,
/// hiçbir şey kopyalanmaz.</item>
/// <item>Başka yerden (ör. geliştirme derlemesi) çalışan program <c>Program Files\&lt;ürün adı&gt;</c>'na kopyalanır ve oraya
/// <see cref="MarkerFileName"/> bırakılır. İşaretsiz dolu bir klasörün (kurulu sürüm) üzerine kopyalanmaz.</item>
/// <item><c>unregister</c> yalnız işaret dosyalı, çalışan programın kendisi olmayan klasörü siler: kurulum klasörü asla silinmez.</item>
/// </list>
/// </summary>
public static class LauncherInstallDir
{
    /// <summary><c>apfs register</c>'ın kopyaladığı klasörü gösteren işaret dosyası.</summary>
    public const string MarkerFileName = "apfs-register-copy.txt";

    /// <summary>Kayıtta kullanılacak klasör ve oraya kopyalama gerekip gerekmediği.</summary>
    public static (string Dir, bool Copy) Choose(string baseDir, string programFiles)
    {
        string self = Normalize(baseDir);
        return IsStrictlyUnder(self, programFiles) ? (self, false) : (Path.Combine(Normalize(programFiles), Branding.ProductName), true);
    }

    /// <summary>
    /// <paramref name="source"/>'u <paramref name="target"/>'a kopyalar ve işaret dosyasını yazar. Hedef işaretsiz ve boş değilse
    /// (kurulu bir sürüm) hiçbir şey yazmadan reddeder.
    /// </summary>
    public static void CopyWithMarker(string source, string target)
    {
        source = Normalize(source);
        target = Normalize(target);
        if (Directory.Exists(target) && !HasMarker(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new ApfsException(ApfsErrorCode.AccessDenied, WinText.T("Cli.InstallDirInUse", target));
        Directory.CreateDirectory(target);
        // İşaret önce: kopyalama yarıda kalırsa klasör yine bizim kopyamız olarak tanınır (unregister temizleyebilsin).
        File.WriteAllText(Path.Combine(target, MarkerFileName),
            "apfs register copied the program here for the WinFsp Launcher; apfs unregister deletes this folder.\r\n");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    /// <summary><c>unregister</c> bu klasörü silebilir mi: işaret dosyası var ve çalışan programın klasörü değil.</summary>
    public static bool MayDelete(string dir, string runningDir) =>
        Directory.Exists(dir) && HasMarker(dir) && !string.Equals(Normalize(dir), Normalize(runningDir), StringComparison.OrdinalIgnoreCase);

    public static bool HasMarker(string dir) => File.Exists(Path.Combine(dir, MarkerFileName));

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsStrictlyUnder(string path, string root)
    {
        string r = Normalize(root) + Path.DirectorySeparatorChar;
        return path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
