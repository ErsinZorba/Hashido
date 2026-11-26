using System.Globalization;
using System.Text.Json.Nodes;

namespace Apfs.Windows.Reporting;

/// <summary>
/// Olayları yerel kuyruğa yazan taraf (hizmet; tepside yalnız çökme). **Varsayılan KAPALI:** klasörde "açık" diyen yapılandırma
/// (<c>config.json</c>) yoksa hiçbir olay toplanmaz. Açma/kapama tepsiden gelir (kullanıcının onayı HKCU'dadır); kapatınca
/// kuyruk silinir. Her olay kuyruğa girmeden önce ortak şemaya (<see cref="ReportSchema"/>) göre denetlenir; uymayan düşer.
/// Raporlama hiçbir zaman asıl işi engellemez: buradaki her hata yutulur ve yalnız günlüğe tek satır düşer.
/// </summary>
public sealed class ReportRecorder
{
    /// <summary>Bir hesabın seçimi bu süre içinde bildirildiyse sayılır ("Hayır kazanır" penceresi, 1.9.1).</summary>
    public static readonly TimeSpan AccountWindow = TimeSpan.FromDays(30);

    /// <summary>Yapılandırmada tutulan en çok hesap (en eski bildirilen düşer).</summary>
    private const int MaxAccounts = 256;

    private readonly Lock _gate = new();
    private readonly string _configPath;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (bool Enabled, DateTimeOffset Seen)> _accounts = new(StringComparer.Ordinal);
    private bool _legacyEnabled;
    private bool _enabled;

    /// <param name="directory">Kuyruk ve yapılandırma klasörü (hizmette <see cref="DefaultServiceDirectory"/>).</param>
    /// <param name="time">Hesap seçimlerinin tarihi için saat (testler; verilmezse sistem saati).</param>
    public ReportRecorder(string directory, TimeProvider? time = null)
    {
        Directory = directory;
        _configPath = Path.Combine(directory, "config.json");
        _time = time ?? TimeProvider.System;
        Queue = new ReportQueue(Path.Combine(directory, "queue.jsonl"));
        ReadConfig();
        _enabled = Effective();
    }

    /// <summary>ProgramData'daki günlük klasörünün altında "reports" (teknik klasör; ürün adıyla değişmez).</summary>
    public static string DefaultServiceDirectory => Path.Combine(Path.GetDirectoryName(ServiceLog.PathName)!, "reports");

    /// <summary>Tepsinin kendi kuyruğu: LOCALAPPDATA'daki yedek günlük klasörünün altında "reports".</summary>
    public static string DefaultUserDirectory => Path.Combine(Path.GetDirectoryName(ServiceLog.FallbackPathName)!, "reports");

    public string Directory { get; }

    public ReportQueue Queue { get; }

    /// <summary>Olaylar toplanıyor mu (hizmette hesap başına seçimlerden türeyen etkin değer).</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    /// <summary>Tek hesaplı seçim (tepsinin kendi çökme kuyruğu: sahibi tek hesap). Kapatınca kuyruk silinir.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = _legacyEnabled = enabled;
            _accounts.Clear();
            WriteConfig(Directory, enabled);
            if (!enabled) Queue.Clear();
        }
    }

    /// <summary>
    /// Bir hesabın seçimi (hizmet; her tepsi turunda bildirilir). <b>Hayır kazanır</b> (kullanıcı kararı 2026-11-25): son
    /// <see cref="AccountWindow"/> içinde seçimini bildiren hesaplardan en az biri Evet ve hiçbiri Hayır demediyse olaylar toplanır.
    /// Önceden "son seçen geçerli"ydi: çok hesaplı bilgisayarda onay her turda el değiştiriyor, Hayır diyen hesabın olayları
    /// öbürünün onayıyla gidiyordu. Etkin değer kapalıysa kuyruk silinir. Dönüş: bu hesabın seçimi değişti mi; etkin değer
    /// öncesi ve sonrası.
    /// </summary>
    public (bool AccountChanged, bool Before, bool After) SetEnabled(bool enabled, string account)
    {
        lock (_gate)
        {
            bool changed = !_accounts.TryGetValue(account, out var previous) || previous.Enabled != enabled;
            bool before = _enabled;
            _legacyEnabled = false; // hesap biçimi kullanılınca eski tek değer bir daha sayılmaz
            _accounts[account] = (enabled, _time.GetUtcNow());
            Prune();
            _enabled = Effective();
            SaveConfig();
            if (!_enabled) Queue.Clear();
            return (changed, before, _enabled);
        }
    }

    /// <summary>
    /// Etkin değer: pencere içindeki hesap seçimlerinden; hesap biçimi hiç kullanılmadıysa (1.9.0'ın yapılandırması) eski tek
    /// değerden (kilit altında). Bütün kayıtlar pencereden düşünce kapalı (güvenli yön; sonraki tepsi turu yeniden bildirir).
    /// </summary>
    private bool Effective()
    {
        if (_accounts.Count == 0) return _legacyEnabled;
        var since = _time.GetUtcNow() - AccountWindow;
        var recent = _accounts.Values.Where(a => a.Seen >= since).ToList();
        return recent.Any(a => a.Enabled) && !recent.Any(a => !a.Enabled);
    }

    /// <summary>Penceresi geçmiş kayıtlar ve sınırı aşan en eskiler atılır (kilit altında).</summary>
    private void Prune()
    {
        var since = _time.GetUtcNow() - AccountWindow;
        foreach (var stale in _accounts.Where(a => a.Value.Seen < since).Select(a => a.Key).ToList()) _accounts.Remove(stale);
        while (_accounts.Count > MaxAccounts) _accounts.Remove(_accounts.MinBy(a => a.Value.Seen).Key);
    }

    /// <summary>Tek değerli (eski biçim) yapılandırmayı yazar; testler hizmeti başlatmadan önce de kullanır.</summary>
    public static void WriteConfig(string directory, bool enabled)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "config.json"), new JsonObject { ["enabled"] = enabled }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Hesap başına biçim: <c>{"enabled": etkin, "accounts": {"SID": {"enabled": …, "seen": "UTC"}}}</c>. Üstteki <c>enabled</c>
    /// etkin değerdir: 1.9.0'a geri dönen hizmet yalnız onu okur. Kalıcı tarih, hizmette "süre yalnız TickCount64" kuralının
    /// bilinçli istisnası (süreç içi süre değil, yeniden başlatmalar boyunca süren pencere).
    /// </summary>
    private void SaveConfig()
    {
        var accounts = new JsonObject();
        foreach (var (sid, a) in _accounts.OrderBy(a => a.Key, StringComparer.Ordinal))
            accounts[sid] = new JsonObject
            {
                ["enabled"] = a.Enabled,
                ["seen"] = a.Seen.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(_configPath, new JsonObject { ["enabled"] = _enabled, ["accounts"] = accounts }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void ReadConfig()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_configPath));
            _legacyEnabled = root?["enabled"]?.GetValue<bool>() == true;
            if (root?["accounts"] is JsonObject accounts)
            {
                _legacyEnabled = false; // üstteki "enabled" yalnız eski okuyucular için
                foreach (var (sid, node) in accounts)
                {
                    if (node is not JsonObject a || a["enabled"]?.GetValue<bool>() is not bool enabled) continue;
                    if (!DateTimeOffset.TryParse(a["seen"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var seen)) continue;
                    if (seen > _time.GetUtcNow() + TimeSpan.FromDays(1)) continue; // gelecekteki tarih hiç düşmezdi: yok say
                    _accounts[sid] = (enabled, seen);
                }
            }
            Prune();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                      or InvalidOperationException or FormatException)
        {
            _legacyEnabled = false;
            _accounts.Clear();
        }
    }

    /// <summary>Raporlama açıksa olayı şemaya göre denetleyip kuyruğa ekler; kapalıysa hiçbir şey yapmaz.</summary>
    /// <remarks>
    /// Olay kilit dışında kurulur (kurucu başka kilitler alabilir); etkinliğin yeniden denetimi ve kuyruğa ekleme AYNI kilit
    /// altında (1.9.1): önceden kapatma, denetimle ekleme arasında kuyruğu silip olay ardından yine kuyruğa girebiliyordu.
    /// </remarks>
    public void Record(Func<JsonObject> build)
    {
        if (!Enabled) return;
        try
        {
            var e = build();
            var problems = ReportSchema.ValidateEvent(e);
            if (problems.Count > 0)
            {
                ServiceLog.Write(WinText.T("Svc.ReportDropped", e["type"]?.ToString() ?? "?", string.Join(", ", problems.Take(5))));
                return;
            }
            lock (_gate)
            {
                if (!_enabled) return;
                Queue.Enqueue(e);
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Write(WinText.T("Svc.ReportDropped", "?", ex.GetType().Name));
        }
    }

    /// <summary>
    /// İşlenmeyen istisna kancası: çökme olayını EŞZAMANLI yazar. İstisnayı yutmaz; süreç yine çöker (çağıran kanca döner).
    /// </summary>
    public void RecordCrash(Exception? ex, string process)
    {
        if (ex is null) return;
        Record(() => ReportEvents.Crash(ex, process));
    }
}
