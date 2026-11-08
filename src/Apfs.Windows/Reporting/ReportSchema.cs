using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Apfs.Windows.Reporting;

/// <summary>
/// Raporlama izin listesi (<c>worker/schema.json</c>, derlemeye gömülü; Worker aynı dosyayı uygular). Bir olay ancak şemanın
/// tanımladığı alanlardan oluşuyorsa kuyruğa girer ve gönderilir: tanımsız alan, kalıba uymayan metin ya da sınır dışı sayı
/// olayı düşürür. JSON Schema'nın küçük bir alt kümesi uygulanır (anahtar sözcükler dosyanın $comment'inde).
/// </summary>
public static class ReportSchema
{
    private static readonly Lazy<JsonObject> Root = new(() =>
    {
        using var s = typeof(ReportSchema).Assembly.GetManifestResourceStream("report-schema.json")
            ?? throw new InvalidOperationException("report-schema.json");
        return JsonNode.Parse(s)!.AsObject();
    });

    private static readonly Dictionary<string, Regex> Patterns = [];

    /// <summary>Şemanın üst düzey tanımları (envelope, event, disk).</summary>
    internal static JsonObject Definitions => Root.Value;

    /// <summary>Olayı denetler; sorun yoksa boş liste. Sorunlar yalnız alan yolu ve kural adıdır (değer içermez).</summary>
    public static List<string> ValidateEvent(JsonNode? e)
    {
        var problems = new List<string>();
        Validate(e, Def("event"), "event", problems);
        return problems;
    }

    /// <summary>Gönderim zarfını (<c>{"events":[…]}</c>) denetler.</summary>
    public static List<string> ValidateEnvelope(JsonNode? body)
    {
        var problems = new List<string>();
        Validate(body, Def("envelope"), "envelope", problems);
        return problems;
    }

    private static JsonObject Def(string name) => Root.Value[name]?.AsObject() ?? throw new InvalidOperationException(name);

    private static void Validate(JsonNode? node, JsonObject schema, string path, List<string> problems)
    {
        if (schema["$ref"] is JsonValue r) { Validate(node, Def(r.GetValue<string>()), path, problems); return; }
        if (node is null) { problems.Add($"{path}: null"); return; }

        if (schema["const"] is { } c && !JsonNode.DeepEquals(c, node)) problems.Add($"{path}: const");
        if (schema["enum"] is JsonArray en && !en.Any(x => JsonNode.DeepEquals(x, node))) problems.Add($"{path}: enum");

        string? type = schema["type"]?.GetValue<string>();
        switch (type)
        {
            case "object":
                if (node is not JsonObject o) { problems.Add($"{path}: type"); return; }
                var props = schema["properties"]?.AsObject();
                foreach (var req in schema["required"]?.AsArray() ?? [])
                    if (!o.ContainsKey(req!.GetValue<string>())) problems.Add($"{path}.{req}: required");
                foreach (var (k, v) in o)
                {
                    if (props?[k] is JsonObject sub) Validate(v, sub, $"{path}.{k}", problems);
                    else problems.Add($"{path}.{k}: unknown");
                }
                break;
            case "array":
                if (node is not JsonArray a) { problems.Add($"{path}: type"); return; }
                if (schema["maxItems"] is { } mi && a.Count > mi.GetValue<int>()) problems.Add($"{path}: maxItems");
                if (schema["items"] is JsonObject items)
                    for (int i = 0; i < a.Count; i++) Validate(a[i], items, $"{path}[{i}]", problems);
                break;
            case "string":
                if (node is not JsonValue sv || sv.GetValueKind() != JsonValueKind.String) { problems.Add($"{path}: type"); return; }
                string s = sv.GetValue<string>();
                if (schema["maxLength"] is { } ml && s.Length > ml.GetValue<int>()) problems.Add($"{path}: maxLength");
                // .NET'te $ sondaki satır sonundan önce de eşleşir (JS'te eşleşmez): satır sonu baştan reddedilir
                if (schema["pattern"] is { } p && (s.Contains('\n') || !Pattern(p.GetValue<string>()).IsMatch(s))) problems.Add($"{path}: pattern");
                break;
            case "integer":
                if (node is not JsonValue iv || iv.GetValueKind() != JsonValueKind.Number
                    || !decimal.TryParse(iv.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d))
                { problems.Add($"{path}: type"); return; }
                if (schema["minimum"] is { } min && d < min.GetValue<decimal>()) problems.Add($"{path}: minimum");
                if (schema["maximum"] is { } max && d > max.GetValue<decimal>()) problems.Add($"{path}: maximum");
                break;
            case "boolean":
                if (node is not JsonValue bv || bv.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) problems.Add($"{path}: type");
                break;
            case null:
                if (schema["const"] is null && schema["enum"] is null) problems.Add($"{path}: schema");
                break;
            default:
                problems.Add($"{path}: schema");
                break;
        }
    }

    private static Regex Pattern(string p)
    {
        lock (Patterns)
        {
            if (!Patterns.TryGetValue(p, out var r))
                Patterns[p] = r = new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            return r;
        }
    }
}
