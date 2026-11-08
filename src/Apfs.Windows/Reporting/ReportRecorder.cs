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
    private readonly Lock _gate = new();
    private readonly string _configPath;
    private bool _enabled;

    /// <param name="directory">Kuyruk ve yapılandırma klasörü (hizmette <see cref="DefaultServiceDirectory"/>).</param>
    public ReportRecorder(string directory)
    {
        Directory = directory;
        _configPath = Path.Combine(directory, "config.json");
        Queue = new ReportQueue(Path.Combine(directory, "queue.jsonl"));
        _enabled = ReadEnabled(_configPath);
    }

    /// <summary>ProgramData'daki günlük klasörünün altında "reports" (teknik klasör; ürün adıyla değişmez).</summary>
    public static string DefaultServiceDirectory => Path.Combine(Path.GetDirectoryName(ServiceLog.PathName)!, "reports");

    /// <summary>Tepsinin kendi kuyruğu: LOCALAPPDATA'daki yedek günlük klasörünün altında "reports".</summary>
    public static string DefaultUserDirectory => Path.Combine(Path.GetDirectoryName(ServiceLog.FallbackPathName)!, "reports");

    public string Directory { get; }

    public ReportQueue Queue { get; }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    /// <summary>Kullanıcının seçimi (son seçen geçerli). Kapatınca kuyruk silinir.</summary>
    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            WriteConfig(Directory, enabled);
            if (!enabled) Queue.Clear();
        }
    }

    /// <summary>Yapılandırmayı yazar (testler hizmeti başlatmadan önce de kullanır).</summary>
    public static void WriteConfig(string directory, bool enabled)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "config.json"), new JsonObject { ["enabled"] = enabled }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool ReadEnabled(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?["enabled"]?.GetValue<bool>() == true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return false; }
    }

    /// <summary>Raporlama açıksa olayı şemaya göre denetleyip kuyruğa ekler; kapalıysa hiçbir şey yapmaz.</summary>
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
            Queue.Enqueue(e);
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
