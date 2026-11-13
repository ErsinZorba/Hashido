using System.Diagnostics;
using System.Text.Json;
using Apfs.Windows;
using Apfs.Windows.Ipc;

namespace Apfs.Tray;

/// <summary>
/// Tepsi, panel ve ana pencerenin ortak beyni: hizmetin durumunu 2 sn'de bir sorar, şifreli birimler için parola ister,
/// çıkarma/tarama isteklerini gönderir. Tüm iş hizmette yapılır; burası yalnızca arayüz mantığıdır.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly HashSet<string> _asked = [];     // parolası bir kez sorulmuş birimler (iptal edilirse tekrar sorulmaz)
    private readonly HashSet<string> _ejecting = [];  // çıkarılmakta olan diskler (aynı disk için ikinci istek gönderilmez)
    private readonly HashSet<string> _unlocking = []; // parola penceresi açık olan birimler (aynı birim için ikinci pencere açılmaz)
    private readonly HashSet<string> _switching = []; // yazma durumu değiştirilen bölümler ("disk#bölüm")
    private long _lastNotice = -1;
    private bool _busy;
    private string _signature = "";

    public AppController()
    {
        _timer.Tick += async (_, _) => await PollAsync();
        _spaceTimer.Tick += (_, _) => RefreshSpace();
    }

    public StatusResponse? Status { get; private set; }
    public bool ServiceDown { get; private set; }
    public bool Rescanning { get; private set; }

    /// <summary>Görünen durum değişti (yalnızca gerçekten değişince; her sorguda değil).</summary>
    public event Action? Changed;

    /// <summary>Kullanıcıya balon olarak gösterilecek bildirim: başlık, metin, düzey.</summary>
    public event Action<string, ToolTipIcon>? Notify;

    public bool IsEjecting(string diskId) => _ejecting.Contains(diskId);
    /// <summary>Diskin herhangi bir bölümünün yazma durumu değişiyor mu.</summary>
    public bool IsSwitching(string diskId) => _switching.Any(k => k.StartsWith(diskId + "#", StringComparison.Ordinal));
    public bool IsSwitching(string diskId, int container) => _switching.Contains($"{diskId}#{container}");
    /// <summary>Diskin yazma durumu değişen bölümleri.</summary>
    public IEnumerable<int> SwitchingContainers(string diskId) =>
        _switching.Where(k => k.StartsWith(diskId + "#", StringComparison.Ordinal)).Select(k => int.Parse(k.AsSpan(diskId.Length + 1)));

    // ---------------- Boşta mı (1.5.0 P3) ----------------
    // Windows teması değişince tepsi yeniden başlar; açık bir pencereyi ya da süren bir işi kesmesin diye yalnız boştayken.

    private static int _openDialogs; // yalnız arayüz iş parçacığında değişir

    /// <summary>Kalıcı (modal) pencere gösterir (ShowDialog, TaskDialog, MessageBox); açıkken tepsi yeniden başlamaz.</summary>
    public static T Modal<T>(Func<T> show)
    {
        _openDialogs++;
        try { return show(); }
        finally { _openDialogs--; }
    }

    /// <summary>Açık kalıcı pencere yok; çıkarma, yazma durumu değişimi, kilit açma, yeniden tarama sürmüyor.</summary>
    public bool Idle => _openDialogs == 0 && _ejecting.Count == 0 && _switching.Count == 0 && _unlocking.Count == 0 && !Rescanning;

    public IEnumerable<VolumeStatus> MountedVolumes =>
        Status?.Disks.SelectMany(d => d.Volumes).Where(v => v.State == VolumeStatus.Mounted) ?? [];

    public void Start()
    {
        _timer.Start();
        _ = PollAsync();
    }

    public async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = await PipeProtocol.SendAsync<StatusResponse>(new Request("status", Since: Math.Max(0, _lastNotice)), TimeSpan.FromSeconds(3));
            if (s is null) return;
            ServiceDown = false;
            Status = s;
            if (_lastNotice >= 0) ShowNotices(s.Notices); // ilk açılışta eski bildirimleri gösterme
            _lastNotice = s.LastNoticeId;
            RaiseIfChanged();
            await AskPasswordsAsync(s);
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            ServiceDown = true;
            RaiseIfChanged();
        }
        finally
        {
            _busy = false;
        }
    }

    private void RaiseIfChanged()
    {
        string sig = JsonSerializer.Serialize(new { ServiceDown, Rescanning, Disks = ServiceDown ? null : Status?.Disks, Ejecting = _ejecting.Order(), Switching = _switching.Order() });
        if (sig == _signature) return;
        _signature = sig;
        // Artık bağlı olmayan harflerin alanını unut (aynı harfe başka birim bağlanırsa eski değer görünmesin)
        var mounted = MountedVolumes.Select(v => v.MountPoint).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _space.Keys.Where(k => !mounted.Contains(k)).ToList()) _space.Remove(gone);
        Changed?.Invoke();
        RefreshSpace(); // yeni bağlanan birimin alanı 10 sn beklemeden gelsin
    }

    // ---------------- Boş alan (1.5.0) ----------------
    // Hizmet ve protokol değişmez: bağlı birimin sürücü harfi tepsinin kendi sürecinde DriveInfo ile okunur. Alan durum imzasına
    // girmez (her kopyalamada liste yeniden kurulmasın); değişince ayrı SpaceChanged olayı çubuğu ve yazıyı yerinde günceller.

    private static readonly TimeSpan SpaceTimeout = TimeSpan.FromSeconds(2);
    private readonly System.Windows.Forms.Timer _spaceTimer = new() { Interval = 10000 };
    private readonly Dictionary<string, DriveSpace> _space = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _spaceQuerying = new(StringComparer.OrdinalIgnoreCase); // sorgusu dönmemiş harfler
    private readonly HashSet<object> _spaceWatchers = [];

    /// <summary>Bir birimin alanı değişti, geldi ya da (hata, zaman aşımı) gitti.</summary>
    public event Action? SpaceChanged;

    /// <summary>Sürücü harfinin bilinen son alanı; bilinmiyorsa (henüz sorulmadı, hata, zaman aşımı) null.</summary>
    public DriveSpace? Space(string? mountPoint) =>
        mountPoint is not null && _space.TryGetValue(mountPoint, out var s) ? s : null;

    /// <summary>
    /// Alanı gösteren pencere görünür oldu / gizlendi. Sorgu yalnız en az biri (ana pencere ya da panel) açıkken 10 sn'de bir
    /// yapılır; açılınca hemen.
    /// </summary>
    public void WatchSpace(object watcher, bool visible)
    {
        if (_disposed || (visible ? !_spaceWatchers.Add(watcher) : !_spaceWatchers.Remove(watcher))) return;
        _spaceTimer.Enabled = _spaceWatchers.Count > 0;
        if (visible) RefreshSpace();
    }

    private void RefreshSpace()
    {
        if (_spaceWatchers.Count == 0 || ServiceDown) return;
        foreach (var v in MountedVolumes)
            if (v.MountPoint is { Length: > 0 } mp) _ = QuerySpaceAsync(mp);
    }

    /// <summary>
    /// Harfin alanını arka planda okur, en çok 2 sn bekler. Önceki sorgu dönmeden aynı harf için yenisi başlamaz: asılı bir
    /// sürücüde (ör. WinFsp yanıt vermiyor) en çok bir iş parçacığı takılır, arayüz hiç beklemez. Bayrak ve sözlük yalnız
    /// arayüz iş parçacığında değişir (await arayüz bağlamına döner).
    /// </summary>
    private async Task QuerySpaceAsync(string mountPoint)
    {
        if (!_spaceQuerying.Add(mountPoint)) return;
        try
        {
            var query = Task.Run(() => ReadSpace(mountPoint));
            var value = await Task.WhenAny(query, Task.Delay(SpaceTimeout)) == query ? query.Result : null;
            StoreSpace(mountPoint, value);
            await query; // zaman aşımında da: iş parçacığı dönene kadar bu harf yeniden sorulmaz
        }
        finally { _spaceQuerying.Remove(mountPoint); }
    }

    private static DriveSpace? ReadSpace(string mountPoint)
    {
        try
        {
            var d = new DriveInfo(mountPoint);
            long total = d.TotalSize;
            return total > 0 ? new DriveSpace(total, Math.Clamp(d.AvailableFreeSpace, 0, total)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void StoreSpace(string mountPoint, DriveSpace? value)
    {
        // Sorgu sürerken birim ayrıldıysa yazma
        if (value is not null && !MountedVolumes.Any(m => string.Equals(m.MountPoint, mountPoint, StringComparison.OrdinalIgnoreCase))) return;
        if (Space(mountPoint) == value) return;
        if (value is { } s) _space[mountPoint] = s;
        else _space.Remove(mountPoint);
        SpaceChanged?.Invoke();
    }

    private void ShowNotices(List<Notice> notices)
    {
        if (notices.Count == 0) return;
        var level = notices.Any(n => n.Level == "hata") ? ToolTipIcon.Error : notices.Any(n => n.Level == "uyarı") ? ToolTipIcon.Warning : ToolTipIcon.Info;
        string text = string.Join("\n", notices.TakeLast(3).Select(n => n.Text));
        Notify?.Invoke(text.Length > 250 ? text[..250] + "…" : text, level);
    }

    // ---------------- Parola ----------------

    private async Task AskPasswordsAsync(StatusResponse s)
    {
        // Artık parola beklemeyen birimleri unut: disk çıkarılıp yeniden takılınca parola yine kendiliğinden sorulsun.
        // Vazgeçilen pencere, birim parola beklemeye devam ettikçe yeniden açılmaz (anahtar biçimi tek yerden: AskKey).
        var waiting = s.Disks.SelectMany(d => d.Volumes.Where(v => v.State == VolumeStatus.NeedsPassword).Select(v => AskKey(d, v))).ToHashSet();
        _asked.RemoveWhere(k => !waiting.Contains(k));
        foreach (var d in s.Disks)
            foreach (var v in d.Volumes.Where(v => v.State == VolumeStatus.NeedsPassword))
            {
                if (!_asked.Add(AskKey(d, v))) continue;
                await UnlockAsync(d, v);
            }
    }

    /// <summary>Birimin parola penceresi anahtarı: disk + APFS bölüm sırası + birim sırası.</summary>
    internal static string AskKey(DiskStatus d, VolumeStatus v) => $"{d.Id}#{v.Container}#{v.Index}";

    public async Task UnlockAsync(DiskStatus d, VolumeStatus v)
    {
        string key = AskKey(d, v);
        _asked.Add(key);
        if (!_unlocking.Add(key)) return;
        try { await UnlockLoopAsync(d, v); }
        finally { _unlocking.Remove(key); }
    }

    private async Task UnlockLoopAsync(DiskStatus d, VolumeStatus v)
    {
        string? error = null;
        while (true)
        {
            using var form = new PasswordForm(v.Name, v.Hint, error);
            if (Modal(form.ShowDialog) != DialogResult.OK) return;
            UnlockResponse? r;
            try
            {
                r = await PipeProtocol.SendAsync<UnlockResponse>(new Request("unlock", Disk: d.Id, Volume: v.Index, Password: form.Password, Container: v.Container), TimeSpan.FromSeconds(60));
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
            {
                error = WinText.U("Error.ServiceUnreachable");
                continue;
            }
            if (r is { Ok: true })
            {
                Notify?.Invoke(r.Message, ToolTipIcon.Info);
                await PollAsync();
                return;
            }
            error = r?.Message ?? WinText.U("Unlock.Failed");
        }
    }

    // ---------------- Çıkarma ----------------

    /// <summary>
    /// "Güvenle çıkar". Diskte açık dosya veya süren kopyalama varsa hizmet hiçbir şey yapmadan "meşgul" der; kullanıcıya sorulur,
    /// onaylarsa zorla çıkarılır. Başarıda hizmetin kendi bildirimi balon olarak gelir; burada yalnızca hata gösterilir.
    /// </summary>
    public async Task EjectAsync(DiskStatus d, IWin32Window? owner)
    {
        if (!_ejecting.Add(d.Id)) return;
        RaiseIfChanged();
        try
        {
            var r = await SendEjectAsync(d.Id, force: false);
            if (r is { Busy: true })
            {
                if (!ConfirmBusyEject(owner, r.Message)) return;
                r = await SendEjectAsync(d.Id, force: true);
            }
            if (r is not { Ok: true })
                Modal(() => MessageBox.Show(owner, r?.Message ?? WinText.U("Eject.Failed"), Branding.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }
        finally
        {
            _ejecting.Remove(d.Id);
            await PollAsync();
            RaiseIfChanged();
        }
    }

    private static async Task<UnlockResponse?> SendEjectAsync(string disk, bool force)
    {
        try
        {
            return await PipeProtocol.SendAsync<UnlockResponse>(new Request("eject", Disk: disk, Force: force), TimeSpan.FromSeconds(60));
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
        {
            return new UnlockResponse(false, WinText.U("Error.ServiceUnreachable"));
        }
    }

    private static bool ConfirmBusyEject(IWin32Window? owner, string message)
    {
        var eject = new TaskDialogButton(WinText.U("Action.EjectAnyway"));
        var cancel = new TaskDialogButton(WinText.U("Action.Cancel"));
        var page = new TaskDialogPage
        {
            Caption = Branding.ProductName,
            Heading = WinText.U("Eject.BusyHeading"),
            Text = message,
            Icon = TaskDialogIcon.Warning,
            Buttons = { eject, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        var result = Modal(() => owner is null ? TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) : TaskDialog.ShowDialog(owner, page));
        return result == eject;
    }

    // ---------------- Yazma (deneysel) ----------------

    /// <summary>
    /// "Yazmaya izin ver" / "Salt okunura dön". İzin vermeden önce kullanıcıya deneysel olduğu ve yedek alması söylenir.
    /// Diskte açık dosya varsa hizmet "meşgul" der; kullanıcı onaylarsa zorla yapılır. Ret nedenini hizmet karta da yazar.
    /// </summary>
    /// <param name="container">APFS bölüm sırası (yazma bölüm başınadır).</param>
    /// <param name="label">Onay penceresinde gösterilecek ad (tek bölümlü diskte disk, çok bölümlüde bölümün birimi).</param>
    public async Task SetWritableAsync(DiskStatus d, int container, string label, bool enable, IWin32Window? owner)
    {
        if (enable && !ConfirmEnableWrite(owner, label)) return;
        string key = $"{d.Id}#{container}";
        if (!_switching.Add(key)) return;
        RaiseIfChanged();
        try
        {
            var r = await SendSetWritableAsync(d.Id, container, enable, force: false);
            if (r is { Busy: true })
            {
                if (!ConfirmBusy(owner, WinText.U("Write.BusyHeading"), r.Message, WinText.U("Action.ContinueAnyway"))) return;
                r = await SendSetWritableAsync(d.Id, container, enable, force: true);
            }
            if (r is not { Ok: true })
                Modal(() => MessageBox.Show(owner, r?.Message ?? WinText.U("Error.OperationFailed"), enable ? WinText.U("Write.EnableFailedTitle") : Branding.ProductName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
        finally
        {
            _switching.Remove(key);
            await PollAsync();
            RaiseIfChanged();
        }
    }

    private static async Task<UnlockResponse?> SendSetWritableAsync(string disk, int container, bool enable, bool force)
    {
        try
        {
            // Kapı tam denetim yapar; büyük diskte birkaç saniye sürebilir
            return await PipeProtocol.SendAsync<UnlockResponse>(new Request("set-writable", Disk: disk, Enable: enable, Force: force, Container: container), TimeSpan.FromMinutes(5));
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
        {
            return new UnlockResponse(false, WinText.U("Error.ServiceUnreachable"));
        }
    }

    private static bool ConfirmEnableWrite(IWin32Window? owner, string disk)
    {
        var allow = new TaskDialogButton(WinText.U("Action.AllowWriting"));
        var cancel = new TaskDialogButton(WinText.U("Action.Cancel"));
        var page = new TaskDialogPage
        {
            Caption = Branding.ProductName,
            Heading = WinText.U("Write.ConfirmHeading"),
            Text = WinText.U("Write.ConfirmText", disk),
            Icon = TaskDialogIcon.ShieldWarningYellowBar,
            Buttons = { allow, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        var result = Modal(() => owner is null ? TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) : TaskDialog.ShowDialog(owner, page));
        return result == allow;
    }

    private static bool ConfirmBusy(IWin32Window? owner, string heading, string message, string proceed)
    {
        var go = new TaskDialogButton(proceed);
        var cancel = new TaskDialogButton(WinText.U("Action.Cancel"));
        var page = new TaskDialogPage
        {
            Caption = Branding.ProductName,
            Heading = heading,
            Text = message,
            Icon = TaskDialogIcon.Warning,
            Buttons = { go, cancel },
            DefaultButton = cancel,
            AllowCancel = true,
        };
        var result = Modal(() => owner is null ? TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen) : TaskDialog.ShowDialog(owner, page));
        return result == go;
    }

    // ---------------- Diğer ----------------

    public async Task RescanAsync()
    {
        if (Rescanning) return;
        Rescanning = true;
        RaiseIfChanged();
        try { await PipeProtocol.SendAsync<UnlockResponse>(new Request("rescan"), TimeSpan.FromSeconds(30)); }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException) { }
        finally
        {
            Rescanning = false;
            await PollAsync();
            RaiseIfChanged();
        }
    }

    public static void OpenDrive(string mountPoint) =>
        Process.Start(new ProcessStartInfo("explorer.exe", mountPoint.EndsWith('\\') ? mountPoint : mountPoint + "\\") { UseShellExecute = true });

    public static void OpenLog()
    {
        string path = File.Exists(ServiceLog.PathName) ? ServiceLog.PathName : ServiceLog.FallbackPathName;
        if (File.Exists(path)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        else Modal(() => MessageBox.Show(WinText.U("Log.NoneYet"), Branding.ProductName));
    }

    public static void ShowDetails(IWin32Window? owner, VolumeStatus v) =>
        Modal(() => MessageBox.Show(owner, v.Message ?? WinText.U("Error.Unknown"), $"{Branding.ProductName}: {v.Name}", MessageBoxButtons.OK, MessageBoxIcon.Warning));

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        _spaceTimer.Stop();
        _spaceTimer.Dispose();
    }
}

/// <summary>Birimin toplam ve (bu kullanıcı için) boş alanı, bayt.</summary>
internal readonly record struct DriveSpace(long Total, long Free)
{
    /// <summary>Kullanılan oran (0–1).</summary>
    public double Used => Total <= 0 ? 0 : (double)(Total - Free) / Total;

    /// <summary>Boş alan %10'dan az: zinober (dikkat).</summary>
    public bool Low => Free < Total / 10;
}
