using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;
using Apfs.Windows.Tray;
using static Apfs.Tray.Native;

namespace Apfs.Tray.Dev;

/// <summary>Görüntü düzeneğinin seçenekleri: <c>--shot &lt;png&gt; --view &lt;ad&gt; [--fake &lt;senaryo&gt;] [--lang &lt;dil&gt;] [--switch-theme]</c>.</summary>
/// <param name="Lang">Arayüz dili (tüm diller turu; kayıt defterine yazılmaz); null = kayıtlı seçim / Windows dili.</param>
/// <param name="SwitchTheme">Çekimden sonra aynı süreçte açık ↔ koyu değiştirilip ikinci çekim (<c>&lt;ad&gt;-switched.png</c>).</param>
internal sealed record ShotOptions(string Png, string View, string Fake, string? Lang, bool SwitchTheme)
{
    /// <summary>Komut satırında <c>--shot</c> yoksa null (Program yalnız geliştirme kanalında çağırır).</summary>
    public static ShotOptions? Parse(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        return Arg("--shot") is { } png
            ? new ShotOptions(png, Arg("--view") ?? "main", Arg("--fake") ?? "mixed", Arg("--lang"), args.Contains("--switch-theme"))
            : null;
    }
}

/// <summary>
/// Görüntü düzeneği (wpfPlan 2.1; WinUI'deki winuiPlan P2.7 düzeneğiyle aynı komut satırı; yalnız <c>--pipe</c> varsayılandan
/// farklıyken, kurulu programda etkisiz — Ek A S5): gerçek <see cref="AppController"/> sahte kanal ve sahte boş alanla kurulur,
/// istenen pencere/kutu EKRAN DIŞINDA ve etkinleşmeden gösterilir (sanal ekranın sağı, görev çubuğunda yok), <c>PrintWindow</c>
/// (PW_RENDERFULLCONTENT) ile PNG'ye çekilir, program çıkar. Tasarım/tema ortam değişkenleriyle (<c>APFS_DEV_DESIGN</c>,
/// <c>APFS_DEV_THEME</c>), dil <c>--lang</c> ya da <c>HKCU</c> seçimi / Windows diliyle. Odak kullanıcının penceresinde kalır (Guard).
/// <c>--switch-theme</c>: ilk çekimden sonra tema ortam değişkeni ters çevrilip tepsinin Windows teması değişiminde kullandığı yol
/// (<see cref="Look.Refresh"/>) çağrılır, aynı pencere yeniden çekilir (açık/koyu yeniden başlatmadan; Ek A R2 ⟳).
/// <para>Görünümler: main, settings, flyout, write, write-many, eject-busy, update, inform, details, about, unlock, unlock-wrong,
/// unlock-saved-invalid, hello, consent, sent. Senaryolar: mixed, multi, busy, empty, down, connecting, readme (README görselleri:
/// adlar ve sayılar dile göre; <c>localization-tool/v180/readme-shots.ps1</c>).</para>
/// </summary>
internal static class ShotRig
{
    private static IntPtr _user; // kullanıcının ön plandaki penceresi

    public static void Run(ShotOptions o) => UiKit.Later(async () =>
    {
        _user = GetForegroundWindow();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.Png))!); // .fit.txt ve hata dosyası da oraya
        Look.Plain = true;
        DialogWindow.Offscreen = true;
        FlyoutWindow.Offscreen = true;
        Motion.Off = true;
        int code = 0;
        try { await ShootAsync(o); }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(o.Png + ".error.txt", e.ToString());
            code = 1;
        }
        Environment.ExitCode = code;
        Application.Current.Shutdown(code);
    });

    /// <summary>Görsel ağaçta en yakın <typeparamref name="T"/> türündeki üst öğe (yoksa null).</summary>
    private static T? Ancestor<T>(DependencyObject o) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(o); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    /// <summary>Görsel ağaçtaki bütün alt öğeler (önce derinlik).</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static async Task ShootAsync(ShotOptions o)
    {
        var channel = new FakeChannel(o.Fake);
        var services = new TrayServices(channel, new CredentialStore($"HashidoShot/{Guid.NewGuid():N}/"),
            () => Task.FromResult(HelloAvailability.Available), (_, _) => Task.FromResult(HelloResult.Canceled), _ => { },
            drive => channel.Space(drive));
        var host = new WpfUiHost();
        // Mantığın kendiliğinden açtığı parola penceresi (sahte kilitli birim) beklemesin: düzenekte hemen vazgeçilir
        var app = new AppController(new NoPrompts(host), SynchronizationContext.Current!, services);
        if (o.Fake != "connecting") await app.PollAsync();
        app.WatchSpace(host, true);
        await Task.Delay(300); // alan sorguları (arka planda) dönsün

        var dialogShown = new TaskCompletionSource<Window>();
        DialogWindow.Shown += w => dialogShown.TrySetResult(w);
        var disk = app.Status?.Disks.FirstOrDefault(d => d.Writable) ?? app.Status?.Disks.FirstOrDefault();
        var locked = app.Status?.Disks.SelectMany(d => d.Volumes).FirstOrDefault(v => v.State == VolumeStatus.NeedsPassword);
        Window target;
        switch (o.View)
        {
            case "probe": // hareket ölçümü: görüntü yok, metin (MotionProbe)
                var probed = new MainWindow(app, null);
                OffScreen(probed);
                await Task.Delay(800);
                Motion.Off = false;
                await MotionProbe.RunAsync(probed, o.Png);
                return;
            case "main":
            case "settings":
            case "settings-notify":
                var main = new MainWindow(app, null);
                if (o.Fake == "readme") main.Height = main.Height * 107 / 100; // README: üç disk kaydırma çubuğusuz sığsın
                OffScreen(main);
                await Task.Delay(800);
                if (o.View != "main")
                {
                    main.OpenSettings();
                    await Task.Delay(900);
                }
                if (o.View == "settings-notify") // Ayarlar › Bildirimler kartı açık, görünür yere kaydırılmış (bildirimAyarPlan)
                {
                    var expander = Descendants(main).OfType<Expander>().First();
                    expander.IsExpanded = true;
                    await Task.Delay(300);
                    expander.BringIntoView();
                    await Task.Delay(500);
                }
                target = main;
                break;
            case "flyout":
                var flyout = new FlyoutWindow(app, () => { });
                flyout.ShowForShot();
                await Task.Delay(900);
                target = flyout;
                break;
            case "hello":
                target = await HelloOwnerWindow.ShowAsync(WinText.U("Hello.Unlock", "Archive, Vault"));
                await Task.Delay(500);
                break;
            default:
                _ = Dialog(o.View, app, host, disk, locked);
                target = await dialogShown.Task;
                await Task.Delay(700);
                break;
        }
        await CaptureAsync(target, o.Png);
        if (o.SwitchTheme)
        {
            // Tepsinin Windows teması değişiminde izlediği yol: palet yeniden okunur, açık/koyu yerinde uygulanır
            Environment.SetEnvironmentVariable("APFS_DEV_THEME", Look.IsDark ? "light" : "dark");
            Look.Refresh();
            await Task.Delay(900);
            await CaptureAsync(target, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.Png))!, Path.GetFileNameWithoutExtension(o.Png) + "-switched.png"));
        }
    }

    private static async Task CaptureAsync(Window target, string png)
    {
        Guard();
        IntPtr h = OpenWindows.Handle(target);
        // Pencere hiçbir ekranla kesişmiyor mu (kullanıcının ekranına gelmedi; shots.ps1 "ekranla kesişen" sayar)
        GetWindowRect(h, out var r);
        bool onScreen = MonitorFromRect(ref r, 0 /* MONITOR_DEFAULTTONULL */) != IntPtr.Zero;
        await File.WriteAllTextAsync(png + ".fit.txt", Fit(target) + Environment.NewLine + $"screen: {(onScreen ? 1 : 0)}");
        // Pencere yeniden boylandıktan hemen sonra WPF yeni kareyi henüz sunmamış olabilir: çekim tek renkse kısa bekleyip yeniden
        // (2.4'te 104 çekimin 2'sinde panel boş beyazdı)
        for (int attempt = 0; attempt < 5 && !Save(h, png); attempt++) await Task.Delay(400);
    }

    /// <summary>
    /// Sığma denetimi (tüm diller turu; 1.6.0'daki <c>Fit</c>'in karşılığı): kırpılan (üç noktalı) ya da tek satırda yerine sığmayan
    /// her metin satır olarak yazılır; hiçbiri yoksa "fit: 0". Kaydırılan metin sığar sayılır.
    /// </summary>
    private static string Fit(Window w)
    {
        var problems = new List<string>();
        void Walk(DependencyObject o)
        {
            if (o is TextBlock t && t.IsVisible && t.ActualWidth > 0 && t.TextWrapping == TextWrapping.NoWrap
                && string.Join("", t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) is var text && (t.Text.Length > 0 || text.Length > 0))
            {
                string shown = t.Text.Length > 0 ? t.Text : text;
                var natural = new FormattedText(shown, CultureInfo.CurrentUICulture, t.FlowDirection,
                    new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(t).PixelsPerDip);
                if (natural.WidthIncludingTrailingWhitespace > t.ActualWidth + 1.5)
                    problems.Add($"{(t.TextTrimming != TextTrimming.None ? "trimmed" : "clipped")}: \"{shown}\" ({t.ActualWidth:0} px)");
            }
            // Düğme kaydırma alanının yanından taşıyor mu: taşan kısım kesilir, üstüne gelince çıkan yuvarlak zeminin o yandaki köşeleri
            // düz görünür (kullanıcı 2026-11-22: panelde "Yazmaya izin ver" Margin -8 ile sağdan taşıyordu)
            if (o is ButtonBase b && b.IsVisible && b.ActualWidth > 0 && Ancestor<ScrollContentPresenter>(b) is { } viewport)
            {
                var box = b.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
                double over = Math.Max(-box.Left, box.Right - viewport.ActualWidth);
                if (over > 0.5)
                    problems.Add($"overflow: \"{System.Windows.Automation.AutomationProperties.GetName(b)}\" {over:0.#} px outside the scroll area");
            }
            int n = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
        Walk(w);
        return problems.Count == 0 ? "fit: 0" : $"fit: {problems.Count}\n" + string.Join("\n", problems);
    }

    /// <summary>Kutuyu açar (sonucu beklenmez: görüntü alınınca program çıkar).</summary>
    private static Task Dialog(string view, AppController app, WpfUiHost host, DiskStatus? disk, VolumeStatus? locked) => view switch
    {
        "write" => host.ConfirmAsync(null, WinText.U("Write.ConfirmHeading"), WinText.U("Write.ConfirmText", disk?.Description ?? "Disk 2 (931 GB)"), WinText.U("Action.AllowWriting"), ConfirmTone.Caution),
        "write-many" => host.ConfirmAsync(null, WinText.U("Write.ConfirmHeading"), WinText.U("Write.ConfirmText", "Disk 3 (465 GB) · \"Projects\", \"Music\""), WinText.U("Action.AllowWriting"), ConfirmTone.Caution),
        "eject-busy" => host.ConfirmAsync(null, WinText.U("Eject.BusyHeading"), FakeChannel.BusyText, WinText.U("Action.EjectAnyway"), ConfirmTone.Alert),
        "update" => host.ConfirmAsync(null, WinText.U("Update.ConfirmHeading", Branding.ProductName, "1.8.1"), WinText.U("Update.ConfirmText"), WinText.U("Action.Install"), ConfirmTone.Caution),
        "inform" => host.InformAsync(null, null, WinText.U("Unlock.ReadOnlyFirst"), InformIcon.Info),
        "details" => app.ShowDetailsAsync(null, new VolumeStatus(1, "System data", VolumeStatus.Failed, null, null, FakeChannel.FailedText)),
        "about" => MessageWindow.AboutAsync(null, "1.8.0"),
        "unlock" => host.AskPasswordAsync(new PasswordRequest(locked?.Name ?? "Archive", locked?.Hint ?? "first holiday", null, false, true)),
        "unlock-wrong" => host.AskPasswordAsync(new PasswordRequest("Archive", "first holiday", WinText.U("Unlock.Failed"), true, true)),
        "unlock-saved-invalid" => host.AskPasswordAsync(new PasswordRequest("Archive", null, WinText.U("Password.SavedInvalid"), false, false)),
        "consent" => ConsentWindow.AskAsync(null),
        "sent" => SentWindow.ShowAsync(null, new Apfs.Windows.Reporting.SentLog(Path.Combine(Path.GetTempPath(), "hashido-shot-sent.jsonl"))),
        _ => throw new ArgumentException("unknown view: " + view),
    };

    /// <summary>Ana pencereyi ekran dışında, etkinleştirmeden gösterir.</summary>
    private static void OffScreen(Window w)
    {
        IntPtr h = new WindowInteropHelper(w).EnsureHandle();
        double s = OpenWindows.Scale(h);
        var off = OpenWindows.OffScreen;
        w.Left = off.X / s;
        w.Top = off.Y / s;
        w.ShowActivated = false;
        w.Show();
    }

    /// <summary>Pencerelerimizden biri ön plana geçtiyse odağı kullanıcının penceresine geri verir (kurallar/arayuz.md 🔴 düzenek).</summary>
    private static void Guard()
    {
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId && _user != IntPtr.Zero) SetForegroundWindow(_user);
    }

    /// <summary>
    /// Pencereyi (DWM içeriğiyle) PNG'ye çeker; görünmez boyutlandırma kenarı (PrintWindow'da siyah) kırpılır. Görüntü tek renkse
    /// (pencere henüz çizilmemiş) false döner; dosya yine yazılır.
    /// </summary>
    private static bool Save(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var r);
        int left = 0, top = 0;
        bool framed = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0;
        if (framed)
        {
            left = Math.Max(0, frame.Left - r.Left);
            top = Math.Max(0, frame.Top - r.Top);
        }
        int width = r.Width, height = r.Height;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr dc = CreateCompatibleDC(screen);
        var bi = new BITMAPINFO { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        IntPtr bmp = CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, bmp);
        PrintWindow(hwnd, dc, 2); // PW_RENDERFULLCONTENT
        var all = new byte[width * height * 4];
        Marshal.Copy(bits, all, 0, all.Length);
        int cw = width - left, ch = height - top;
        if (framed && frame.Width > 0 && frame.Height > 0)
        {
            cw = Math.Min(cw, frame.Width);
            ch = Math.Min(ch, frame.Height);
        }
        var pixels = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++) Buffer.BlockCopy(all, ((y + top) * width + left) * 4, pixels, y * cw * 4, cw * 4);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        bool drawn = false;
        for (int i = 4; i < pixels.Length && !drawn; i += 4)
            drawn = pixels[i] != pixels[0] || pixels[i + 1] != pixels[1] || pixels[i + 2] != pixels[2];
        SelectObject(dc, old);
        DeleteObject(bmp);
        DeleteDC(dc);
        ReleaseDC(IntPtr.Zero, screen);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var image = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgra32, null, pixels, cw * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        enc.Save(stream);
        return drawn;
    }
}

/// <summary>Düzenekte mantığın kendi parola isteklerini iptal eden arayüz (kutular gerçek).</summary>
internal sealed class NoPrompts(IUiHost inner) : IUiHost
{
    public Task<bool> ConfirmAsync(object? owner, string heading, string text, string proceed, ConfirmTone tone) => inner.ConfirmAsync(owner, heading, text, proceed, tone);
    public Task InformAsync(object? owner, string? heading, string text, InformIcon icon) => inner.InformAsync(owner, heading, text, icon);
    public Task<PasswordAnswer?> AskPasswordAsync(PasswordRequest request) => Task.FromResult<PasswordAnswer?>(null);
    public Task<T> RunWithHelloOwnerAsync<T>(string message, Func<IntPtr, Task<T>> verify) => verify(IntPtr.Zero);
    public void Notify(string text, NoticeLevel level) { }
}

/// <summary>Düzeneğin sahte hizmet kanalı: senaryoya göre durum; öbür istekler yanıtsız.</summary>
internal sealed class FakeChannel(string scenario) : ITrayChannel
{
    public const string BusyText = "A file on this disk is open or being copied. If you eject now, the copy stops; your Mac disk is not harmed.";
    public const string FailedText = "A block checksum on the disk does not match; this part may be damaged. Nothing was written to your disk.";

    public Task<T?> SendAsync<T>(Request request, TimeSpan timeout)
    {
        if (scenario == "down") throw new TimeoutException();
        if (request.Op != "status") return Task.FromResult<T?>(default);
        object status = new StatusResponse(Disks(), [], 0);
        return Task.FromResult((T?)status);
    }

    private List<DiskStatus> Disks() => scenario switch
    {
        "empty" => [],
        "multi" =>
        [
            new("3", @"\\.\PhysicalDrive3", "Disk 3 (298 GB)",
            [
                new(0, "C1-Plain", VolumeStatus.Mounted, "F:", null, null, Writable: true, Container: 0),
                new(1, "C3-Sensitive", VolumeStatus.Mounted, "G:", null, null, Writable: true, Container: 0),
                new(2, "C2-Vault", VolumeStatus.NeedsPassword, null, "cat's name", null, Container: 0, Uuid: Guid.Empty.ToString()),
                new(0, "Backup", VolumeStatus.Mounted, "H:", null, null, Container: 1),
            ], WriteBlockReason: null),
        ],
        "readme" => Readme(),
        "busy" =>
        [
            new("2", @"\\.\PhysicalDrive2", "Disk 2 (931 GB)", [new(0, "Photos", VolumeStatus.Mounted, "E:", null, null)]),
        ],
        _ =>
        [
            new("2", @"\\.\PhysicalDrive2", "Disk 2 (931 GB)", [new(0, "Photos", VolumeStatus.Mounted, "E:", null, null)]),
            new("4", @"\\.\PhysicalDrive4", "Disk 4 (1.81 TB)", [new(0, "Archive", VolumeStatus.NeedsPassword, null, "first holiday", null, Uuid: Guid.Empty.ToString())]),
            new("6", @"\\.\PhysicalDrive6", "Disk 6 (250 GB)",
            [
                new(0, "Old Mac", VolumeStatus.Mounted, "K:", null, null, WriteBlockReason: "This volume has snapshots; only volumes without snapshots can be opened for writing."),
                new(1, "System data", VolumeStatus.Failed, null, null, FailedText),
            ]),
        ],
    };

    /// <summary>README görselleri (eski <c>readme/readme-shots.cs</c>'in örnek verisi): salt okunur, kilitli şifreli (ipuçlu),
    /// yazılabilir; disk boyutu hizmetin biçimleyicisiyle (<see cref="Apfs.Windows.Localization.SizeText"/>). Birim adları ve ipucu
    /// README'nin dilinde, düzenek betiğinden gelir (<c>APFS_DEV_README_NAMES</c> = "ad1|ad2|ad3|ipucu"; örnek veri kodda değil,
    /// <c>ResourceTests.NoHardcodedTurkishText</c>); yoksa İngilizce.</summary>
    private static List<DiskStatus> Readme()
    {
        var names = (Environment.GetEnvironmentVariable("APFS_DEV_README_NAMES") ?? "Photos|Archive|Projects|our first trip").Split('|');
        string D(int n, long bytes) => WinText.U("Disk.PhysicalName", n, Apfs.Windows.Localization.SizeText.Format(bytes, System.Globalization.CultureInfo.CurrentCulture));
        return
        [
            new("2", @"\\.\PhysicalDrive2", D(2, 1_000_204_886_016L), [new(1, names[0], VolumeStatus.Mounted, "E:", null, null)]),
            new("4", @"\\.\PhysicalDrive4", D(4, 2_000_398_934_016L),
                [new(1, names[1], VolumeStatus.NeedsPassword, null, names[3], null, Uuid: Guid.Empty.ToString())]),
            new("3", @"\\.\PhysicalDrive3", D(3, 500_107_862_016L), [new(1, names[2], VolumeStatus.Mounted, "F:", null, null, Writable: true)], Writable: true),
        ];
    }

    /// <summary>Sahte boş alan: E: rahat, K: %10'dan az boş.</summary>
    public DriveSpace? Space(string drive) => scenario == "readme" ? ReadmeSpace(drive) : drive.ToUpperInvariant() switch
    {
        "E:" => new DriveSpace(999_653_638_144, 285_600_000_000),
        "K:" => new DriveSpace(250_685_575_168, 11_811_160_064),
        "F:" or "G:" => new DriveSpace(320_072_933_376, 172_872_933_376),
        "H:" => new DriveSpace(120_000_000_000, 80_000_000_000),
        _ => null,
    };

    private static DriveSpace? ReadmeSpace(string drive) => drive.ToUpperInvariant() switch
    {
        "E:" => new DriveSpace(1000_204_000_000L, 286_400_000_000L),
        "F:" => new DriveSpace(500_107_000_000L, 173_200_000_000L),
        _ => null,
    };
}
