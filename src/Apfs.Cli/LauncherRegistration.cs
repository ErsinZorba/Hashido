using System.Security.Principal;
using Apfs.Windows;
using Microsoft.Win32;

namespace Apfs.Cli;

/// <summary>
/// Fiziksel diskleri yönetici olmayan kullanıcıların da bağlayabilmesi için WinFsp Launcher kaydı.
/// Launcher'ın çalıştırdığı program yalnızca yöneticilerin yazabildiği Program Files altında olmalı; SYSTEM yetkisiyle çalışan
/// Launcher kullanıcı tarafından değiştirilebilen bir dosyayı çalıştırmasın. Klasör kuralı: <see cref="LauncherInstallDir"/>.
/// </summary>
internal static class LauncherRegistration
{
    private const string ServiceName = "apfs";
    private const string ServicesKey = @"SOFTWARE\WinFsp\Services\" + ServiceName; // 32 bit görünümde: WOW6432Node altında
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static int Register()
    {
        RequireAdmin();
        string? winfsp = WinFspInstallDir() ?? throw new ApfsException(ApfsErrorCode.Unsupported, WinText.T("Cli.WinFspMissing"));

        // Program Files altında çalışıyorsa (kurulum) kendi klasörü; değilse Program Files\<ürün adı>'na kopya + işaret dosyası.
        var (installDir, copy) = LauncherInstallDir.Choose(AppContext.BaseDirectory, ProgramFiles);
        if (copy) LauncherInstallDir.CopyWithMarker(AppContext.BaseDirectory, installDir);

        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = hklm32.CreateSubKey(ServicesKey, writable: true);
        key.SetValue("Executable", Path.Combine(installDir, "apfs.exe"), RegistryValueKind.String);
        key.SetValue("CommandLine", "launcher-mount %1 %2", RegistryValueKind.String);
        // Herkes örnek başlatıp durdurabilir; komut satırı yalnızca APFS içeren fiziksel diskleri kabul eder.
        key.SetValue("Security", "D:P(A;;RPWPLC;;;WD)", RegistryValueKind.String);
        key.SetValue("JobControl", 1, RegistryValueKind.DWord);

        string launchctl = Path.Combine(winfsp, "bin", "launchctl-x64.exe");
        Console.WriteLine(WinText.T("Cli.Registered", installDir));
        Console.WriteLine(WinText.T("Cli.RegisterMountHint"));
        Console.WriteLine($"  \"{launchctl}\" start {ServiceName} disk2 PhysicalDrive2 X:");
        Console.WriteLine(WinText.T("Cli.RegisterUnmountHint"));
        Console.WriteLine($"  \"{launchctl}\" stop {ServiceName} disk2");
        return 0;
    }

    public static int Unregister()
    {
        RequireAdmin();
        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        // Kaydın gösterdiği klasör (sabit bir yol değil): yalnız register'ın kopyalayıp işaretlediği klasör silinir.
        string? exe;
        using (var key = hklm32.OpenSubKey(ServicesKey)) exe = key?.GetValue("Executable") as string;
        hklm32.DeleteSubKeyTree(ServicesKey, throwOnMissingSubKey: false);
        Console.WriteLine(WinText.T("Cli.Unregistered"));
        string? dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        if (dir is not null && LauncherInstallDir.MayDelete(dir, AppContext.BaseDirectory))
        {
            Directory.Delete(dir, recursive: true);
            Console.WriteLine(WinText.T("Cli.Deleted", dir));
        }
        return 0;
    }

    internal static string? WinFspInstallDir()
    {
        using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var key = hklm32.OpenSubKey(@"SOFTWARE\WinFsp");
        return key?.GetValue("InstallDir") as string;
    }

    private static void RequireAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            throw new ApfsException(ApfsErrorCode.AccessDenied, WinText.T("Cli.AdminRequired"));
    }
}
