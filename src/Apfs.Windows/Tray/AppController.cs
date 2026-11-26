using System.Diagnostics;
using System.Text.Json;
using Apfs.Windows.Ipc;

namespace Apfs.Windows.Tray;

/// <summary>
/// Tepsi, panel ve ana pencerenin ortak beyni: hizmetin durumunu 2 sn'de bir sorar, şifreli birimler için parola ister,
/// çıkarma/tarama isteklerini gönderir. Tüm iş hizmette yapılır; burası yalnızca arayüz mantığıdır. winuiPlan P1'den beri
/// arayüz kütüphanesinden bağımsız: kutular, parola penceresi, Hello'nun sahibi ve balonlar <see cref="IUiHost"/>'tan;
/// zamanlayıcılar arayüz bağlamına (<see cref="SynchronizationContext"/>) tıklar; dış dünya <see cref="TrayServices"/>'tan.
/// Durum yalnız arayüz iş parçacığında değişir (await'ler arayüz bağlamına döner).
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly IUiHost _ui;
    private readonly TrayServices _services;
    private readonly TimeProvider _time;
    private readonly UiTimer _timer;
    private readonly HashSet<string> _asked = [];     // parolası bir kez sorulmuş birimler (iptal edilirse tekrar sorulmaz)
    private readonly HashSet<string> _ejecting = [];  // çıkarılmakta olan diskler (aynı disk için ikinci istek gönderilmez)
    private readonly HashSet<string> _unlocking = []; // parola penceresi açık olan birimler (aynı birim için ikinci pencere açılmaz)
    private readonly HashSet<string> _switching = []; // yazma durumu değiştirilen bölümler ("disk#bölüm")
    private long _lastNotice = -1;
    private bool _busy;
    private string _signature = "";

    /// <param name="ui">Kutular, parola penceresi, Hello'nun sahibi, balonlar.</param>
    /// <param name="sync">Arayüz iş parçacığının bağlamı: zamanlayıcılar buraya tıklar.</param>
    /// <param name="services">Dış dünya (testte sahte); null = gerçekleri (<see cref="TrayServices.Default"/>).</param>
    /// <param name="time">Zaman (testte elle ilerletilen); null = sistem saati.</param>
    public AppController(IUiHost ui, SynchronizationContext sync, TrayServices? services = null, TimeProvider? time = null)
    {
        _ui = ui;
        _services = services ?? TrayServices.Default;
        _time = time ?? TimeProvider.System;
        _timer = new UiTimer(sync, TimeSpan.FromSeconds(2), _time);
        _spaceTimer = new UiTimer(sync, TimeSpan.FromSeconds(10), _time);
        // async void (WinForms Tick'indeki gibi): beklenmeyen istisna yutulmaz, arayüz bağlamında süreci düşürür (A9)
        _timer.Tick += async () => await PollAsync();
        _spaceTimer.Tick += RefreshSpace;
    }

    public StatusResponse? Status { get; private set; }
    public bool ServiceDown { get; private set; }
    public bool Rescanning { get; private set; }

    /// <summary>Görünen durum değişti (yalnızca gerçekten değişince; her sorguda değil).</summary>
    public event Action? Changed;

    public bool IsEjecting(string diskId) => _ejecting.Contains(diskId);
    /// <summary>Diskin herhangi bir bölümünün yazma durumu değişiyor mu.</summary>
    public bool IsSwitching(string diskId) => SwitchingDisk(_switching, diskId);
    public bool IsSwitching(string diskId, int container) => _switching.Contains($"{diskId}#{container}");
    /// <summary>Diskin yazma durumu değişen bölümleri.</summary>
    public IEnumerable<int> SwitchingContainers(string diskId) => SwitchingContainersOf(_switching, diskId);

    /// <summary>Çıkarılmakta olan diskler (görünüm modeli için; yalnız okunur).</summary>
    public IReadOnlySet<string> Ejecting => _ejecting;
    /// <summary>Yazma durumu değişen bölümler, "disk#bölüm" (görünüm modeli için; yalnız okunur).</summary>
    public IReadOnlySet<string> Switching => _switching;

    internal static bool SwitchingDisk(IEnumerable<string> switching, string diskId) =>
        switching.Any(k => k.StartsWith(diskId + "#", StringComparison.Ordinal));

    internal static IEnumerable<int> SwitchingContainersOf(IEnumerable<string> switching, string diskId) =>
        switching.Where(k => k.StartsWith(diskId + "#", StringComparison.Ordinal)).Select(k => int.Parse(k.AsSpan(diskId.Length + 1)));

    // ---------------- Boşta mı (1.5.0 P3) ----------------
    // Windows teması değişince tepsi yeniden başlar; açık bir pencereyi ya da süren bir işi kesmesin diye yalnız boştayken.

    private static int _openDialogs; // yalnız arayüz iş parçacığında değişir

    /// <summary>Kalıcı (modal) pencere gösterir (ShowDialog, MessageDialog); açıkken tepsi yeniden başlamaz.</summary>
    public static T Modal<T>(Func<T> show)
    {
        _openDialogs++;
        try { return show(); }
        finally { _openDialogs--; }
    }

    /// <summary>Kalıcı pencere, beklenerek (winuiPlan P1): sayaç pencere kapanana dek (await boyunca) artık kalır.</summary>
    public static async Task<T> ModalAsync<T>(Func<Task<T>> show)
    {
        _openDialogs++;
        try { return await show(); }
        finally { _openDialogs--; }
    }

    /// <inheritdoc cref="ModalAsync{T}(Func{Task{T}})"/>
    public static async Task ModalAsync(Func<Task> show)
    {
        _openDialogs++;
        try { await show(); }
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
        // Sorgu başlarken yazma durumu değişen bölümler: yanıt geldiğinde geçiş bitmiş olsa da bu anlık görüntüde o bölümün
        // birimleri eksik olabilir (AskPasswordsAsync onların "soruldu" kaydını silmesin)
        var switchingAtStart = _switching.ToHashSet();
        StatusResponse? asked = null;
        try
        {
            var s = await _services.Channel.SendAsync<StatusResponse>(new Request("status", Since: Math.Max(0, _lastNotice)), TimeSpan.FromSeconds(3));
            if (s is null) return;
            if (ServiceRestarted(s))
            {
                // Hizmet yeniden başladı (1.8.2): bildirim kimlikleri 1'den başladı, eski imleçle sorulan yanıt yeni
                // hizmetin ilk bildirimlerini ("E: bağlandı") içermiyordu. Yeni hizmetin bütün bildirimleri bu tepsi için yeni.
                s = await _services.Channel.SendAsync<StatusResponse>(new Request("status", Since: 0), TimeSpan.FromSeconds(3));
                if (s is null) return;
            }
            _serviceInstance = s.Instance;
            ServiceDown = false;
            Status = s;
            if (_lastNotice >= 0) ShowNotices(s.Notices); // ilk açılışta eski bildirimleri gösterme
            _lastNotice = s.LastNoticeId;
            RaiseIfChanged();
            asked = s;
        }
        // Aşırı uzun ya da bozuk yanıt (InvalidData/Json) da "hizmete ulaşılamadı": zamanlayıcı async void, dışarı çıkan istisna
        // tepsiyi düşürüyordu
        catch (Exception e) when (PipeProtocol.IsChannelFailure(e))
        {
            ServiceDown = true;
            RaiseIfChanged();
        }
        finally
        {
            _busy = false;
        }
        // Parola/Hello penceresi açıkken yoklama sürer (1.9.0): durum tazelenir, yeni bildirimler balona çıkar. Aynı birime ikinci
        // pencere açılmaz (_asked, _unlocking).
        if (asked is not null) await AskPasswordsAsync(asked, switchingAtStart);
    }

    /// <summary>Son başarılı yoklamadaki hizmet çalışmasının kimliği (eski hizmette null).</summary>
    private string? _serviceInstance;

    /// <summary>
    /// Yanıt, son yoklamadakinden başka bir hizmet çalışmasından mı (ilk yoklamada hayır): kimlik değişti (eski hizmetten yeniye
    /// geçiş dahil) ya da hizmetin son bildirim kimliği imlecin gerisinde (eski hizmet de; kimlik yokken tek ipucu).
    /// </summary>
    private bool ServiceRestarted(StatusResponse s) =>
        _lastNotice >= 0 && ((s.Instance is not null && s.Instance != _serviceInstance) || s.LastNoticeId < _lastNotice);

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
    private readonly UiTimer _spaceTimer;
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
            var query = Task.Run(() => _services.ReadSpace(mountPoint));
            var value = await Task.WhenAny(query, Task.Delay(SpaceTimeout, _time)) == query ? query.Result : null;
            StoreSpace(mountPoint, value);
            await query; // zaman aşımında da: iş parçacığı dönene kadar bu harf yeniden sorulmaz
        }
        finally { _spaceQuerying.Remove(mountPoint); }
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

    /// <summary>
    /// Hizmetin bildirimleri: Ayarlar › Bildirimler'e göre süzülür (<see cref="TrayModel.ShowsNotice"/>); süzülenler de "görüldü"
    /// sayılır (imleç <see cref="PollAsync"/>'ta koşulsuz ilerler), seçim gevşetilince birikmiş eskiler gelmez.
    /// </summary>
    private void ShowNotices(List<Notice> notices)
    {
        var mode = _services.NotificationMode;
        var shown = notices.Where(n => TrayModel.ShowsNotice(mode, NoticeLevelOf([n]), n.Requested)).ToList();
        if (shown.Count == 0) return;
        _ui.Notify(NoticeText(shown), NoticeLevelOf(shown));
    }

    /// <summary>Tepsinin kendi balonu (kilit açma sonucu), Ayarlar › Bildirimler'e göre.</summary>
    private void Notify(string text, NoticeLevel level, bool requested)
    {
        if (TrayModel.ShowsNotice(_services.NotificationMode, level, requested)) _ui.Notify(text, level);
    }

    /// <summary>Balon düzeyi: biri "hata" ise hata, biri "uyarı" ise uyarı, yoksa bilgi.</summary>
    internal static NoticeLevel NoticeLevelOf(IReadOnlyCollection<Notice> notices) =>
        notices.Any(n => n.Level == "hata") ? NoticeLevel.Error : notices.Any(n => n.Level == "uyarı") ? NoticeLevel.Warning : NoticeLevel.Info;

    /// <summary>Balon metni: son 3 bildirim alt alta; 250 karakterden uzunsa "…" ile kesilir.</summary>
    internal static string NoticeText(IEnumerable<Notice> notices)
    {
        string text = string.Join("\n", notices.TakeLast(3).Select(n => n.Text));
        return text.Length > 250 ? Apfs.Util.TextClamp.Prefix(text, 250) + "…" : text; // vekil çifti bölünmez
    }

    // ---------------- Parola ----------------

    private async Task AskPasswordsAsync(StatusResponse s, HashSet<string> switchingAtStart)
    {
        // Artık parola beklemeyen birimleri unut: disk çıkarılıp yeniden takılınca parola yine kendiliğinden sorulsun.
        // Vazgeçilen pencere, birim parola beklemeye devam ettikçe yeniden açılmaz (anahtar biçimi tek yerden: AskKey).
        // Yazma durumu değişen bölümün birimleri o sırada listede olmaz: anahtarları silinmez, yoksa geçişten sonra (yazmaya açınca
        // ya da Salt okunura dönünce) kilitli birimin parola penceresi yeniden açılırdı (1.7.0).
        var waiting = s.Disks.SelectMany(d => d.Volumes.Where(v => v.State == VolumeStatus.NeedsPassword).Select(v => AskKey(d, v))).ToHashSet();
        _asked.RemoveWhere(k => !waiting.Contains(k) && !_switching.Contains(k[..k.LastIndexOf('#')]) && !switchingAtStart.Contains(k[..k.LastIndexOf('#')]));
        // Aynı anda en çok bir kendiliğinden akış (H18): sürerken yeni kilitli birimler "soruldu" sayılmaz, akış bitince sonraki
        // yoklama onları alır (yoklamanın kendisi artık durmaz)
        if (_autoUnlocking) return;
        var due = new List<(DiskStatus D, VolumeStatus V)>();
        foreach (var d in s.Disks)
            foreach (var v in d.Volumes.Where(v => v.State == VolumeStatus.NeedsPassword))
            {
                // Yazılabilir bölümdeki kilitli birim dokunulmadan kalır, kilidi açılamaz: parola kendiliğinden sorulmaz,
                // kayıtlı parola da kullanılmaz (RememberPolicy: None)
                if (PartitionWritable(d, v)) continue;
                if (!_asked.Add(AskKey(d, v))) continue;
                due.Add((d, v));
            }
        // Taramada bulunan bütün kilitli birimler birlikte: kayıtlı parolalar için tek Hello onayı (parolaPlan 2.2)
        if (due.Count == 0) return;
        _autoUnlocking = true;
        try { await UnlockVolumesAsync(due); }
        finally { _autoUnlocking = false; }
    }

    /// <summary>Taramanın başlattığı kilit açma akışı (Hello + o taramadaki pencereler) sürüyor.</summary>
    private bool _autoUnlocking;

    /// <summary>
    /// Kayıtlı parolası hizmetçe "yanlış" denen birimlerin UUID'leri (1.9.0'da disk takılı kaldıkça; 1.9.1'den beri tepsi ömrü
    /// boyunca): kayıt silinmez ama o UUID'ye bir daha kendiliğinden gönderilmez (elle "Kilidi aç" doğrudan parola penceresi açar).
    /// Önceden disk çıkarılınca unutuluyordu: bayat kayıt her takışta hizmetin yanlış parola sayacını bir artırıyor, birkaç takıştan
    /// sonra elle girilen doğru parola da bekliyordu. O UUID'nin kilidi açılınca ya da kaydı yeniden yazılınca/silinince unutulur.
    /// </summary>
    private readonly HashSet<string> _savedRefused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Birimin parola penceresi anahtarı: disk + APFS bölüm sırası + birim sırası.</summary>
    internal static string AskKey(DiskStatus d, VolumeStatus v) => $"{d.Id}#{v.Container}#{v.Index}";

    /// <summary>Birimin APFS bölümü yazmaya açık mı (bölümün herhangi bir birimi yazılabilir bağlıysa).</summary>
    internal static bool PartitionWritable(DiskStatus d, VolumeStatus v) => d.Volumes.Any(x => x.Container == v.Container && x.Writable);

    /// <summary>"Kilidi aç" (elle): taramadaki yolla aynı — kayıtlı parola varsa Hello/kendiliğinden, yoksa parola penceresi.</summary>
    /// <param name="owner">Düğmenin penceresi (yalnız "önce Salt okunura dön" kutusunun sahibi; parola penceresi her zaman sahipsiz).</param>
    public async Task UnlockAsync(DiskStatus d, VolumeStatus v, object? owner = null)
    {
        if (PartitionWritable(d, v))
        {
            // Hizmet de reddeder; parola penceresi boşuna açılmasın (1.7.0)
            await InformAsync(owner, null, WinText.U("Unlock.ReadOnlyFirst"), InformIcon.Info);
            return;
        }
        _asked.Add(AskKey(d, v));
        await UnlockVolumesAsync([(d, v)]);
    }

    /// <summary>
    /// Kilitli birimleri açar ("Bu bilgisayarda hatırla", parolaPlan 2.2). Kayıtlı parolası olan birimler için karar BİR kez
    /// verilir (<see cref="RememberPolicy"/>): Hello varsa tek onay penceresi (ileti birimleri sayar), Hello kesin yoksa
    /// doğrudan (yalnız etkin oturumda); Hello bilinmiyorsa (uzak oturum, hata), onay verilmezse ya da kayıt yoksa parola
    /// penceresi. Kayıtlı parola tutmazsa (hizmet <c>WrongPassword</c> derse) kayıt KALIR (aynı UUID'li başka bir disk gerçek
    /// diskin kaydını silmesin; 1.9.0), pencere "Kayıtlı parola artık geçerli değil" ile kutu işaretli açılır ve kayıtlı parola
    /// o UUID'ye tepsi açık kaldıkça (kilidi açılana ya da kaydı değişene dek; çıkar-tak dahil) bir daha gönderilmez (1.9.1).
    /// Birimler sırayla açılır.
    /// </summary>
    private async Task UnlockVolumesAsync(List<(DiskStatus D, VolumeStatus V)> items)
    {
        // Penceresi zaten açık (ya da kendiliğinden açılmakta) olan birim için ikinci kez başlanmaz
        var mine = items.Where(x => _unlocking.Add(AskKey(x.D, x.V))).ToList();
        try
        {
            var saved = new Dictionary<string, string>(); // AskKey → kayıtlı parola (yalnız bu yöntemin ömrü boyunca bellekte)
            var recorded = new HashSet<string>();         // kaydı olan birimler (kutu işaretli açılır)
            foreach (var (d, v) in mine)
            {
                var state = ReadSaved(v, out var p);
                // Okuma hatası (1.9.1): kayıt var sayılır (kutu işaretli; elle açış onu silmesin), ama kayıtlı parola denenmez
                if (state != CredentialRead.Missing) recorded.Add(AskKey(d, v));
                if (state == CredentialRead.Found && !_savedRefused.Contains(v.Uuid!)) saved[AskKey(d, v)] = p!; // kayıt yalnız UUID'li birimde
            }
            HelloAvailability? hello = null;
            bool useSaved = false, approved = false; // approved: kullanıcı Hello'da onayladı (yoksa kendiliğinden açılış)
            if (saved.Count > 0)
            {
                hello = await _services.HelloAvailability();
                var action = RememberPolicy.Decide(hasSaved: true, hello.Value, partitionWritable: false, sessionActive: _services.IsSessionActive);
                if (action == RememberAction.VerifyWithHello)
                {
                    string names = string.Join(", ", mine.Where(x => saved.ContainsKey(AskKey(x.D, x.V))).Select(x => x.V.Name));
                    action = RememberPolicy.AfterHello(await VerifyAsync(WinText.U("Hello.Unlock", names)));
                    approved = action == RememberAction.Unlock;
                }
                useSaved = action == RememberAction.Unlock;
            }
            foreach (var (d, v) in mine)
            {
                string? error = null;
                bool hasSaved = recorded.Contains(AskKey(d, v));
                saved.TryGetValue(AskKey(d, v), out var password);
                if (useSaved && password is not null)
                {
                    var r = await SendUnlockAsync(d, v, password);
                    switch (RememberPolicy.AfterSavedAttempt(reached: r is not null, ok: r is { Ok: true }, code: r?.Code))
                    {
                        case SavedAttemptOutcome.Unlocked:
                            _services.Log(WinText.T("Password.SavedUnlocked", v.Name));
                            RenameIfNeeded(v, password); // Mac'te yeniden adlandırılmışsa Ayarlar listesi yeni adı göstersin
                            Notify(r!.Message, NoticeLevel.Info, requested: approved); // Hello'suz kendiliğinden açılış "önemli" değil
                            await PollAsync();
                            continue;
                        case SavedAttemptOutcome.WrongAndAsk:
                            // Mac'te parola değişmiş ya da aynı UUID'li başka disk: kayıt kalır, bir kez sorulur (döngü yok)
                            _savedRefused.Add(v.Uuid!);
                            _services.Log(WinText.T("Password.SavedRefused", v.Name));
                            error = WinText.U("Password.SavedInvalid");
                            break;
                        default:
                            error = r is null ? WinText.U("Error.ServiceUnreachable") : r.Message;
                            break;
                    }
                }
                // Hello sürerken ya da önceki birimler açılırken disk çekilmiş, birim başka yoldan açılmış ya da bölüm yazmaya
                // açılmış olabilir: artık kilitli beklemeyen birim için pencere açılmaz
                if (!RememberPolicy.StillLocked(await FreshStatusAsync(), d.Id, v.Container, v.Index, v.Uuid)) continue;
                // Kutunun altındaki satır için (yalnız kutu gösterilecekse sorulur)
                if (v.Uuid is not null) hello ??= await _services.HelloAvailability();
                await UnlockLoopAsync(d, v, error, v.Uuid is null ? null : hasSaved, hello == HelloAvailability.Available);
            }
        }
        finally
        {
            foreach (var (d, v) in mine) _unlocking.Remove(AskKey(d, v));
        }
    }

    private async Task UnlockLoopAsync(DiskStatus d, VolumeStatus v, string? error, bool? remember, bool hello)
    {
        while (true)
        {
            var answer = await ModalAsync(() => _ui.AskPasswordAsync(new PasswordRequest(v.Name, v.Hint, error, remember, hello)));
            if (answer is null) return;
            remember = answer.Remember; // yanlış parolada pencere, kutunun seçimi korunarak yeniden açılır
            var r = await SendUnlockAsync(d, v, answer.Password);
            if (r is { Ok: true })
            {
                if (v.Uuid is { } opened) _savedRefused.Remove(opened); // kilit açıldı: kayıtlı parola sonraki takışta yine bir kez denenir
                // Yalnız tutan parola kaydedilir; kutu boş bırakıldıysa varsa eski kayıt silinir
                bool remembered = true;
                switch (RememberPolicy.AfterManualUnlock(remember))
                {
                    case RememberStore.Save: remembered = SaveOrLog(v, answer.Password); break;
                    case RememberStore.Delete: ForgetSaved(v); break;
                }
                // parola penceresi: kullanıcının işi; "hatırla" kasaya yazılamadıysa aynı balonda tek satırla söylenir (1.9.0)
                if (remembered) Notify(r.Message, NoticeLevel.Info, requested: true);
                else Notify(r.Message + "\n" + WinText.U("Password.NotRemembered"), NoticeLevel.Warning, requested: true);
                await PollAsync();
                return;
            }
            error = r is null ? WinText.U("Error.ServiceUnreachable") : r.Message;
            // Pencere açıkken disk çekilmiş ya da birim başka yoldan açılmış olabilir ("artık parola beklemiyor"): kilitli beklemeyen
            // birime pencere yeniden açılmaz (durum alınamazsa açılır)
            if (!RememberPolicy.StillLocked(await FreshStatusAsync(), d.Id, v.Container, v.Index, v.Uuid)) return;
        }
    }

    /// <summary>
    /// Hizmete taze sorulan disk sayısı (1.9.1; kendiliğinden ve elle güncellemenin "takılı Mac diski yok" koşulu): yoklamanın yan
    /// etkisi yok (bildirim gösterilmez, parola penceresi açılmaz ve BEKLENMEZ, durum değişmez). Hizmet yanıt vermezse null.
    /// Önceden <see cref="PollAsync"/> kullanılıyordu: açık bir parola penceresi kurulum kararını kullanıcı cevaplayana dek bekletiyordu.
    /// </summary>
    public async Task<int?> FreshDiskCountAsync() => (await FreshStatusAsync())?.Disks.Count;

    /// <summary>Kilit açma akışının arasında taze durum (bildirim imleci ilerletilmez; alınamazsa null).</summary>
    private async Task<StatusResponse?> FreshStatusAsync()
    {
        var (_, s) = await RequestAsync<StatusResponse>(new Request("status", Since: Math.Max(0, _lastNotice)), TimeSpan.FromSeconds(3));
        return s;
    }

    /// <summary>
    /// Hizmete tek istek; kanalın her hatası (<see cref="PipeProtocol.IsChannelFailure"/>: bozuk/aşırı uzun yanıt, erişim reddi
    /// dahil) "ulaşılamadı" (Reached false) olur, istisna dışarı çıkmaz (1.8.2).
    /// </summary>
    private async Task<(bool Reached, T? Value)> RequestAsync<T>(Request request, TimeSpan timeout)
    {
        try { return (true, await _services.Channel.SendAsync<T>(request, timeout)); }
        catch (Exception e) when (PipeProtocol.IsChannelFailure(e)) { return (false, default); }
    }

    /// <summary>Hizmete "unlock"; ulaşılamazsa null.</summary>
    private async Task<UnlockResponse?> SendUnlockAsync(DiskStatus d, VolumeStatus v, string password)
    {
        var (reached, r) = await RequestAsync<UnlockResponse>(new Request("unlock", Disk: d.Id, Volume: v.Index, Password: password, Container: v.Container), TimeSpan.FromSeconds(60));
        return reached ? r ?? new UnlockResponse(false, WinText.U("Unlock.Failed")) : null;
    }

    // Kasa erişimi kilit açmayı hiçbir zaman bozmaz: geçersiz UUID (ArgumentException) ve kasa hatası yutulur; kaydetme hatası
    // günlüğe parolasız tek satır.
    private CredentialRead ReadSaved(VolumeStatus v, out string? password)
    {
        password = null;
        if (v.Uuid is not { } uuid) return CredentialRead.Missing;
        try
        {
            var state = _services.Credentials.Read(uuid, out password, out int error);
            if (state == CredentialRead.Failed) _services.Log(WinText.T("Password.ReadFailed", v.Name, error));
            return state;
        }
        catch (ArgumentException) { return CredentialRead.Missing; }
    }

    private void ForgetSaved(VolumeStatus v)
    {
        if (v.Uuid is not { } uuid) return;
        _savedRefused.Remove(uuid);
        try { _services.Credentials.Delete(uuid); }
        catch (ArgumentException) { }
    }

    private void RenameIfNeeded(VolumeStatus v, string password)
    {
        if (v.Uuid is not { } uuid) return;
        string? stored;
        try { stored = _services.Credentials.NameOf(uuid); }
        catch (ArgumentException) { return; }
        if (RememberPolicy.NeedsRename(stored, v.Name)) SaveOrLog(v, password);
    }

    /// <returns>Kaydedildi (UUID yoksa da true: kutu gösterilmemişti).</returns>
    private bool SaveOrLog(VolumeStatus v, string password)
    {
        if (v.Uuid is not { } uuid) return true;
        try
        {
            _services.Credentials.Save(uuid, v.Name, password);
            _savedRefused.Remove(uuid);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException)
        {
            _services.Log(WinText.T("Password.SaveFailed", v.Name, e.Message));
            return false;
        }
    }

    /// <summary>
    /// Hello onay penceresi. Sahibi her zaman önde açılan küçük bir Hashido penceresi (<see cref="IUiHost.RunWithHelloOwnerAsync{T}"/>):
    /// gizli pencereye bağlanan istek ekrana gelmedi (2.7 f); elle "Kilidi aç"ta da panel odağı kaybedince gizlenir. Sonuç
    /// gelince kapanır. Kalıcı pencere sayılmaz (açık-pencere sayacına girmez; boşta sayılmamayı süren kilit açma sağlar).
    /// </summary>
    private Task<HelloResult> VerifyAsync(string message) =>
        _ui.RunWithHelloOwnerAsync(message, owner => _services.HelloVerify(owner, message));

    // ---------------- Çıkarma ----------------

    /// <summary>
    /// "Güvenle çıkar". Diskte açık dosya veya süren kopyalama varsa hizmet hiçbir şey yapmadan "meşgul" der; kullanıcıya sorulur,
    /// onaylarsa zorla çıkarılır. Başarıda hizmetin kendi bildirimi balon olarak gelir; burada yalnızca hata gösterilir.
    /// </summary>
    /// <param name="owner">Kutuların sahibi (düğmenin penceresi); null = sahipsiz (panel, tepsi menüsü).</param>
    /// <returns>Disk çıkarıldı (1.8.0: güncelleme, başarısız ya da vazgeçilen çıkarmada durur). Aynı disk zaten çıkarılıyorsa false.</returns>
    public async Task<bool> EjectAsync(DiskStatus d, object? owner)
    {
        if (!_ejecting.Add(d.Id)) return false;
        RaiseIfChanged();
        try
        {
            var r = await SendEjectAsync(d.Id, force: false);
            if (r is { Busy: true })
            {
                if (!await ConfirmAsync(owner, WinText.U("Eject.BusyHeading"), r.Message, WinText.U("Action.EjectAnyway"), ConfirmTone.Alert)) return false;
                r = await SendEjectAsync(d.Id, force: true);
            }
            if (r is { Ok: true }) return true;
            await InformAsync(owner, null, r?.Message ?? WinText.U("Eject.Failed"), InformIcon.Warning);
            return false;
        }
        finally
        {
            _ejecting.Remove(d.Id);
            await PollAsync();
            RaiseIfChanged();
        }
    }

    private async Task<UnlockResponse?> SendEjectAsync(string disk, bool force)
    {
        var (reached, r) = await RequestAsync<UnlockResponse>(new Request("eject", Disk: disk, Force: force), TimeSpan.FromSeconds(60));
        return reached ? r : new UnlockResponse(false, WinText.U("Error.ServiceUnreachable"));
    }

    // ---------------- Yazma (deneysel) ----------------

    /// <summary>
    /// "Yazmaya izin ver" / "Salt okunura dön". İzin vermeden önce kullanıcıya deneysel olduğu ve yedek alması söylenir.
    /// Diskte açık dosya varsa hizmet "meşgul" der; kullanıcı onaylarsa zorla yapılır. Ret nedenini hizmet karta da yazar.
    /// </summary>
    /// <param name="container">APFS bölüm sırası (yazma bölüm başınadır).</param>
    /// <param name="label">Onay penceresinde gösterilecek ad (tek birimli tek bölümde disk; çok bölümlüde ya da çok birimli bölümde
    /// disk · bölümde yazılacak birimler).</param>
    /// <param name="owner">Kutuların sahibi (düğmenin penceresi); null = sahipsiz.</param>
    public async Task SetWritableAsync(DiskStatus d, int container, string label, bool enable, object? owner)
    {
        if (enable && !await ConfirmAsync(owner, WinText.U("Write.ConfirmHeading"), WinText.U("Write.ConfirmText", label), WinText.U("Action.AllowWriting"), ConfirmTone.Caution)) return;
        string key = $"{d.Id}#{container}";
        if (!_switching.Add(key)) return;
        RaiseIfChanged();
        try
        {
            var r = await SendSetWritableAsync(d.Id, container, enable, force: false);
            if (r is { Busy: true })
            {
                if (!await ConfirmAsync(owner, WinText.U("Write.BusyHeading"), r.Message, WinText.U("Action.ContinueAnyway"), ConfirmTone.Caution)) return;
                r = await SendSetWritableAsync(d.Id, container, enable, force: true);
            }
            if (r is not { Ok: true })
                await InformAsync(owner, enable ? WinText.U("Write.EnableFailedTitle") : null, r?.Message ?? WinText.U("Error.OperationFailed"), InformIcon.Info);
        }
        finally
        {
            _switching.Remove(key);
            await PollAsync();
            RaiseIfChanged();
        }
    }

    private async Task<UnlockResponse?> SendSetWritableAsync(string disk, int container, bool enable, bool force)
    {
        // Kapı tam denetim yapar; büyük diskte birkaç saniye sürebilir
        var (reached, r) = await RequestAsync<UnlockResponse>(new Request("set-writable", Disk: disk, Enable: enable, Force: force, Container: container), TimeSpan.FromMinutes(5));
        return reached ? r : new UnlockResponse(false, WinText.U("Error.ServiceUnreachable"));
    }

    // ---------------- Kutular (açık-pencere sayacıyla) ----------------

    private Task<bool> ConfirmAsync(object? owner, string heading, string text, string proceed, ConfirmTone tone) =>
        ModalAsync(() => _ui.ConfirmAsync(owner, heading, text, proceed, tone));

    private Task InformAsync(object? owner, string? heading, string text, InformIcon icon) =>
        ModalAsync(() => _ui.InformAsync(owner, heading, text, icon));

    // ---------------- Diğer ----------------

    public async Task RescanAsync()
    {
        if (Rescanning) return;
        Rescanning = true;
        RaiseIfChanged();
        try { await RequestAsync<UnlockResponse>(new Request("rescan"), TimeSpan.FromSeconds(30)); }
        finally
        {
            Rescanning = false;
            await PollAsync();
            RaiseIfChanged();
        }
    }

    public static void OpenDrive(string mountPoint) => OpenDrive(mountPoint, Process.Start, ServiceLog.Write);

    /// <summary>
    /// Birimin "Aç"ı: harf bu oturumda kullanıcının bir eşlemesine (ağ sürücüsü, subst) bağlıysa Gezgin Mac diskini değil o sürücüyü
    /// açardı → açmak yerine açıklama (1.9.0; hizmet harfi bütün oturumlar için seçer, oturumun kendi eşlemesini göremez).
    /// </summary>
    public Task OpenVolumeAsync(object? owner, VolumeStatus v) => OpenVolumeAsync(owner, v, Process.Start);

    internal async Task OpenVolumeAsync(object? owner, VolumeStatus v, Func<ProcessStartInfo, Process?> start)
    {
        if (v.MountPoint is not { Length: > 0 } mountPoint) return;
        if (await Task.Run(() => _services.IsDriveShadowed(mountPoint)))
        {
            string letter = DriveMapping.Letter(mountPoint) ?? mountPoint;
            await InformAsync(owner, null, WinText.U("Drive.Shadowed", letter, v.Name), InformIcon.Warning);
            return;
        }
        OpenDrive(mountPoint, start, _services.Log);
    }

    internal static bool OpenDrive(string mountPoint, Func<ProcessStartInfo, Process?> start, Action<string> log) =>
        TryStart(new ProcessStartInfo("explorer.exe", mountPoint.EndsWith('\\') ? mountPoint : mountPoint + "\\") { UseShellExecute = true }, start, log);

    /// <summary>
    /// Programı başlatır; başlatılamazsa (Not Defteri kaldırılmış, ilke engeli…) tepsi düşmez, günlüğe tek satır (1.9.0: çağıranlar
    /// async void düğme işleyicileri, dışarı çıkan istisna tepsiyi kapatıyordu).
    /// </summary>
    internal static bool TryStart(ProcessStartInfo info, Func<ProcessStartInfo, Process?> start, Action<string> log)
    {
        try
        {
            start(info)?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            log(WinText.T("Tray.StartFailed", info.FileName, e.Message));
            return false;
        }
    }

    /// <summary>Günlüğü aç: hizmet günlüğü, yoksa yedek yol; hiçbiri yoksa "henüz günlük yok" kutusu (HER ZAMAN sahipsiz).</summary>
    public Task OpenLogAsync() => OpenLogAsync(_ui);

    /// <inheritdoc cref="OpenLogAsync()"/>
    public static Task OpenLogAsync(IUiHost ui) => OpenLogAsync(ui, LogPath(), Process.Start, ServiceLog.Write);

    internal static async Task OpenLogAsync(IUiHost ui, string? path, Func<ProcessStartInfo, Process?> start, Action<string> log)
    {
        if (path is not null) TryStart(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }, start, log);
        else await ModalAsync(() => ui.InformAsync(null, null, WinText.U("Log.NoneYet"), InformIcon.Info));
    }

    /// <summary>Açılacak günlük dosyası: hizmetinki, yoksa yedek yol; ikisi de yoksa null.</summary>
    internal static string? LogPath()
    {
        string path = File.Exists(ServiceLog.PathName) ? ServiceLog.PathName : ServiceLog.FallbackPathName;
        return File.Exists(path) ? path : null;
    }

    /// <summary>"Ayrıntı": başlık birim adı, metin birimin iletisi (yoksa "bilinmeyen hata"), uyarı ikonu.</summary>
    public Task ShowDetailsAsync(object? owner, VolumeStatus v) =>
        InformAsync(owner, v.Name, v.Message ?? WinText.U("Error.Unknown"), InformIcon.Warning);

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
public readonly record struct DriveSpace(long Total, long Free)
{
    /// <summary>Kullanılan oran (0–1).</summary>
    public double Used => Total <= 0 ? 0 : (double)(Total - Free) / Total;

    /// <summary>Boş alan %10'dan az: zinober (dikkat).</summary>
    public bool Low => Free < Total / 10;
}
