using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Apfs.Windows.Reporting;

/// <summary>Kuyruktaki bir olay: yerel sıra numarası (yalnız onay için; gönderilmez) ve olayın kendisi.</summary>
public sealed record QueuedReport(long Id, JsonElement Event);

/// <summary>
/// Gönderilmeyi bekleyen olaylar: satır başına bir JSON (<c>{"id":N,"e":{…}}</c>). Sınır <see cref="MaxEvents"/> olay ve
/// <see cref="MaxBytes"/> bayt; aşılınca EN ESKİ olay düşer (çevrimdışı bilgisayar diski doldurmasın). Tek süreç içinde iş
/// parçacığı güvenlidir; dosyanın sahibi tek süreçtir (hizmet ya da tepsi). Okunamayan satır atlanır.
/// </summary>
public sealed class ReportQueue(string path)
{
    public const int MaxEvents = 200;
    public const int MaxBytes = 256 * 1024;

    private readonly Lock _gate = new();

    public string PathName { get; } = path;

    public void Enqueue(JsonObject e)
    {
        lock (_gate)
        {
            var lines = Load();
            long id = lines.Count == 0 ? 1 : lines[^1].Id + 1;
            if (lines.Count == 0) id = Math.Max(id, ReadIdFloor() + 1);
            lines.Add((id, new JsonObject { ["id"] = id, ["e"] = e.DeepClone() }.ToJsonString()));
            while (lines.Count > MaxEvents || (lines.Count > 1 && lines.Sum(l => Encoding.UTF8.GetByteCount(l.Line) + 1) > MaxBytes))
                lines.RemoveAt(0);
            Save(lines, id);
        }
    }

    /// <summary>
    /// <paramref name="afterId"/>'den sonraki olaylar, sırayla, toplam JSON boyu <paramref name="maxBytes"/>'ı aşmayacak kadar
    /// (en az bir olay). <c>More</c>: sınır yüzünden kalanlar var.
    /// </summary>
    public (List<QueuedReport> Reports, bool More) Pull(long afterId, int maxBytes)
    {
        lock (_gate)
        {
            var result = new List<QueuedReport>();
            int size = 0;
            foreach (var (id, line) in Load())
            {
                if (id <= afterId) continue;
                int n = Encoding.UTF8.GetByteCount(line);
                if (result.Count > 0 && size + n > maxBytes) return (result, true);
                using var doc = JsonDocument.Parse(line);
                result.Add(new QueuedReport(id, doc.RootElement.GetProperty("e").Clone()));
                size += n;
            }
            return (result, false);
        }
    }

    /// <summary><paramref name="uptoId"/> dahil ondan önceki olayları siler (gönderildi ya da sunucu kalıcı olarak reddetti).</summary>
    public void Ack(long uptoId)
    {
        lock (_gate)
        {
            var lines = Load();
            int before = lines.Count;
            lines.RemoveAll(l => l.Id <= uptoId);
            if (lines.Count != before) Save(lines, Math.Max(uptoId, lines.Count > 0 ? lines[^1].Id : 0));
        }
    }

    public int Count
    {
        get { lock (_gate) return Load().Count; }
    }

    /// <summary>Kuyruğu siler (raporlama kapatıldı).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            TryDelete(PathName);
            TryDelete(PathName + ".id");
        }
    }

    private List<(long Id, string Line)> Load()
    {
        var list = new List<(long, string)>();
        string[] lines;
        try { lines = File.ReadAllLines(PathName, Encoding.UTF8); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return list; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return list; }
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out long n)
                    && doc.RootElement.TryGetProperty("e", out var e) && e.ValueKind == JsonValueKind.Object)
                    list.Add((n, line));
            }
            catch (JsonException) { } // yarım yazılmış satır
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    private void Save(List<(long Id, string Line)> lines, long lastId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            string tmp = PathName + ".tmp";
            File.WriteAllText(tmp, lines.Count == 0 ? "" : string.Join("\n", lines.Select(l => l.Line)) + "\n", new UTF8Encoding(false));
            File.Move(tmp, PathName, overwrite: true);
            // Kuyruk boşalsa da sıra numarası geri gitmesin (tepsi eski numarayla onay gönderirse yeni olay silinmesin)
            File.WriteAllText(PathName + ".id", lastId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // raporlama asıl işi engellemez
    }

    private long ReadIdFloor()
    {
        try { return long.TryParse(File.ReadAllText(PathName + ".id"), out long n) ? n : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
