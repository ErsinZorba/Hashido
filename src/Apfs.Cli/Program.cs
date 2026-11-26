using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Apfs;
using Apfs.Cli;
using Apfs.Container;
using Apfs.Volume;
using Apfs.Windows;
using Apfs.Windows.Localization;

Console.OutputEncoding = Encoding.UTF8;

// Çıktı dili: tepside seçilen dil (kayıt), yoksa Windows'un dili; çeviri yalnız İngilizce ve Türkçe, öbür dillerde İngilizce.
var language = LanguageSetting.Effective();
CultureInfo.DefaultThreadCurrentUICulture = language;
CultureInfo.CurrentUICulture = language;

try
{
    var opts = CliOptions.Parse(args);
    return opts.Command switch
    {
        "info" => Info(opts),
        "ls" => List(opts),
        "extract" => Extract(opts),
        "check" => CheckCommand.Run(opts),
        "dump" => Dump(opts),
        "mount" => MountCommand.Run(opts),
        "launcher-mount" => MountCommand.RunFromLauncher(opts),
        "register" => LauncherRegistration.Register(),
        "unregister" => LauncherRegistration.Unregister(),
        _ => Help(),
    };
}
catch (ApfsException e)
{
    Console.Error.WriteLine(WinText.T("Cli.Error", e.Message));
    return 1;
}
catch (CliUsageException e)
{
    Console.Error.WriteLine(WinText.T("Cli.Error", e.Message));
    Console.Error.WriteLine(WinText.T("Cli.HelpHint"));
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine(WinText.T("Cli.Unexpected", e.Message));
    return 3;
}

static int Help()
{
    Console.WriteLine(WinText.T("Cli.Help", Branding.ProductName));
    return 0;
}

static int Info(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, WinText.T("Cli.ArgSource")), o.Partition);
    var sb = c.Superblock;
    Console.WriteLine(WinText.T("Cli.InfoContainer", sb.Uuid));
    Console.WriteLine(WinText.T("Cli.InfoBlockSize", sb.BlockSize));
    Console.WriteLine(WinText.T("Cli.InfoSize", FormatSize((long)sb.BlockCount * sb.BlockSize), sb.BlockCount));
    Console.WriteLine(WinText.T("Cli.InfoCheckpoint", sb.Xid, sb.Paddr));
    long free = c.FreeBytes;
    Console.WriteLine(WinText.T("Cli.InfoUsedFree", FormatSize((long)sb.BlockCount * sb.BlockSize - free), FormatSize(free), c.FreeSpaceSource));
    foreach (var w in c.Warnings) Console.WriteLine(WinText.T("Cli.Warning", w));
    if (o.Has("--fingerprint"))
    {
        // Diskin değişmediğini göstermek için: blok 0 ve tüm checkpoint tanımlayıcı alanının SHA-256'sı.
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(c.Reader.ReadBlock(0));
        uint descBlocks = sb.XpDescBlocks & 0x7FFFFFFF;
        for (ulong i = 0; i < descBlocks && sb.XpDescBase + i < c.Reader.BlockCount; i++)
            sha.AppendData(c.Reader.ReadBlock(sb.XpDescBase + i));
        Console.WriteLine(WinText.T("Cli.InfoFingerprint", Convert.ToHexString(sha.GetHashAndReset()), descBlocks));
    }
    Console.WriteLine();
    foreach (var r in c.OpenVolumes())
    {
        using var opened = r.Volume;
        if (r.Volume is { } v)
        {
            Console.WriteLine(WinText.T("Cli.VolumeHeader", r.Index, v.Name, (v.RoleName.Length > 0 ? $"  [{v.RoleName}]" : "")));
            Console.WriteLine($"  UUID            {v.Uuid}");
            Console.WriteLine(WinText.T("Cli.InfoCaseSensitivity", (v.IsCaseInsensitive ? WinText.T("Cli.CaseInsensitive") : WinText.T("Cli.CaseSensitive"))));
            Console.WriteLine(WinText.T("Cli.InfoContents", v.NumFiles, v.NumDirectories, v.NumSymlinks));
            Console.WriteLine(WinText.T("Cli.InfoUsed", FormatSize((long)v.AllocatedBlocks * sb.BlockSize)));
        }
        else
        {
            Console.WriteLine(WinText.T("Cli.InfoVolumeFailed", r.Index, r.Error!.Message));
        }
        Console.WriteLine();
    }
    return 0;
}

static int Dump(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, WinText.T("Cli.ArgSource")), o.Partition);
    if (o.Positional.Count > 1)
    {
        // apfs dump <kaynak> <yol>: o yolun ham kayıtları (geliştirme)
        using var v = OpenVolume(c, o.Volume, Passwords.FromOptions(o));
        foreach (var line in Apfs.Diagnostics.RecordDump.Lines(v, o.Positional[1])) Console.WriteLine(line);
        return 0;
    }
    int problems = 0;
    string marker = CoreText.T("Dump.ProblemMarker");
    foreach (var line in Apfs.Diagnostics.ContainerDump.Lines(c))
    {
        Console.WriteLine(line);
        if (line.Contains(marker, StringComparison.Ordinal)) problems++;
    }
    return problems == 0 ? 0 : 1;
}

static int List(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, WinText.T("Cli.ArgSource")), o.Partition);
    using var v = OpenVolume(c, o.Volume, Passwords.FromOptions(o));
    string path = o.Positional.Count > 1 ? o.Positional[1] : "/";
    var start = v.Lookup(path);
    if (!start.IsDirectory)
    {
        PrintEntry(v, path.Trim('/'), start, o.Has("--hash"));
        return 0;
    }
    // klasörler de "görüldü" kümesinde (APFS'te klasörün sert bağı yok: ikinci kez görülen klasör bozuk veridir, içine
    // inilmez) ve derinlik `check` ile aynı sınırda (yığın taşmasın); ikisinde de liste sürer, çıkış 1
    var seen = new HashSet<ulong> { start.Id };
    int problems = 0;
    void Walk(Inode dir, string prefix, int depth)
    {
        foreach (var e in v.ListDirectory(dir.Id).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var ino = v.GetInode(e.FileId);
            string p = prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}";
            PrintEntry(v, p, ino, o.Has("--hash"));
            if (!ino.IsDirectory || !o.Has("-r")) continue;
            if (!seen.Add(ino.Id)) { Problem(WinText.T("Cli.DirectoryLoop", p)); continue; }
            if (depth + 1 > CheckCommand.MaxDepth) { Problem(WinText.T("Cli.DepthExceeded", p)); continue; }
            Walk(ino, p, depth + 1);
        }
    }
    void Problem(string text)
    {
        problems++;
        Console.Error.WriteLine(WinText.T("Cli.Warning", text));
    }
    Walk(start, path.Trim('/', '\\').Replace('\\', '/'), 0);
    return problems == 0 ? 0 : 1;
}

static void PrintEntry(ApfsVolume v, string path, Inode ino, bool hash)
{
    if (ino.IsDirectory)
        Console.WriteLine($"{path}\td\t-\t-");
    else if (ino.IsSymlink)
        Console.WriteLine($"{path}\tl\t-\t->{v.ReadSymlinkTarget(ino.Id)}");
    else
    {
        string h = "-";
        if (hash)
        {
            try { h = HashFile(v, ino); }
            catch (ApfsException e) { h = WinText.T("Cli.HashError", e.Message); }
        }
        Console.WriteLine($"{path}\tf\t{v.GetFileSize(ino)}\t{h}");
    }
}

static string HashFile(ApfsVolume v, Inode ino)
{
    var reader = v.OpenFile(ino);
    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var buf = new byte[1 << 20];
    ulong pos = 0;
    while (pos < reader.Length)
    {
        int n = reader.Read(pos, buf);
        sha.AppendData(buf, 0, n);
        pos += (ulong)n;
    }
    return Convert.ToHexString(sha.GetHashAndReset());
}

static int Extract(CliOptions o)
{
    using var c = SourceOpener.OpenContainer(o.Require(0, WinText.T("Cli.ArgSource")), o.Partition);
    using var v = OpenVolume(c, o.Volume, Passwords.FromOptions(o));
    string source = o.Require(1, WinText.T("Cli.ArgPath"));
    var ino = v.Lookup(source);
    string target = o.Require(2, WinText.T("Cli.ArgTarget"));
    // yalnız sıradan dosya (sembolik bağ ve özel dosya 0 baytlık "başarı" yazıyordu); okuma yarıda kalırsa yarım dosya silinir
    if (!ino.IsRegularFile)
    {
        string kind = ino.IsDirectory ? WinText.T("Cli.KindDirectory")
            : ino.IsSymlink ? WinText.T("Cli.KindSymlink") : WinText.T("Cli.KindSpecial");
        throw new ApfsException(ApfsErrorCode.InvalidArgument, WinText.T("Cli.ExtractNotFile", source, kind));
    }
    var reader = v.OpenFile(ino);
    var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
    try
    {
        var buf = new byte[1 << 20];
        ulong pos = 0;
        while (pos < reader.Length)
        {
            int n = reader.Read(pos, buf);
            fs.Write(buf, 0, n);
            pos += (ulong)n;
        }
        fs.Dispose();
    }
    catch
    {
        fs.Dispose();
        try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        throw;
    }
    Console.WriteLine(WinText.T("Cli.Copied", target, FormatSize((long)reader.Length)));
    return 0;
}

static ApfsVolume OpenVolume(ApfsContainer c, int index, Func<VolumeLockInfo, string?>? passwords)
{
    var all = c.OpenVolumes(passwords);
    var r = all.FirstOrDefault(x => x.Index == index);
    foreach (var other in all) if (other != r) other.Volume?.Dispose(); // seçilmeyenlerin şifreleyicisi bellekte kalmasın
    if (r is null) throw new ApfsException(ApfsErrorCode.InvalidArgument, WinText.T("Cli.NoSuchVolumeCount", index, all.Count));
    return r.Volume ?? throw r.Error!;
}

static string FormatSize(long bytes) => bytes switch
{
    >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
    >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
    >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
    >= 1L << 10 => $"{bytes / (double)(1L << 10):0.##} KB",
    _ => WinText.T("Cli.Bytes", bytes),
};
