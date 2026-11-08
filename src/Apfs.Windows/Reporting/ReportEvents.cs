using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Apfs.Diagnostics;
using Apfs.Localization;

namespace Apfs.Windows.Reporting;

/// <summary>Diskin aygıt düzeyi bilgileri (hizmet takarken bir kez toplar). Kişiyi ya da diski tanımlamaz.</summary>
/// <param name="Bus">Bağlantı türü simgesi (<see cref="ReportEvents.BusToken"/>).</param>
/// <param name="Scheme">"gpt" ya da "none" (bölüm tablosu yok, doğrudan kapsayıcı).</param>
public sealed record DiskFacts(string Bus, int SectorSize, string Scheme, int ApfsPartitions);

/// <summary>Yazma oturumunun sayaçları (yazılabilir sürücüden; ham sayılar, rapora aralık olarak geçer).</summary>
public sealed record WriteCounters(long Commits, long Bytes, long Operations);

/// <summary>
/// Rapor olaylarını <c>worker/schema.json</c> biçiminde kurar. Buraya yalnız sayılar, sabit simgeler ve anahtarlar girer: ad,
/// yol, UUID, istisna iletisi ya da tam boyut geçen bir parametre YOKTUR (gizlilik testleri bunu fikstürlerle denetler).
/// </summary>
public static partial class ReportEvents
{
    /// <summary>0 → 0, aksi hâlde ikili basamak sayısı (1 → 1, 2–3 → 2, 4–7 → 3…): tam değer yerine iki kuvveti aralığı.</summary>
    public static int Log2(ulong v) => v == 0 ? 0 : 64 - BitOperations.LeadingZeroCount(v);

    public static int Log2(long v) => v <= 0 ? 0 : Log2((ulong)v);

    /// <summary>Süre aralığı: milisaniyenin <see cref="Log2(ulong)"/>'si.</summary>
    public static int DurationLog2(TimeSpan t) => Log2((long)t.TotalMilliseconds);

    /// <summary>Gün (UTC); saat gönderilmez.</summary>
    public static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Uygulama sürümü ve derleme sha'sı (InformationalVersion "1.4.0+abc…").</summary>
    internal static (string Version, string? Sha) AppVersion()
    {
        string info = typeof(ReportEvents).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        int plus = info.IndexOf('+');
        string version = (plus < 0 ? info : info[..plus]).Split('-')[0];
        string? sha = plus < 0 ? null : info[(plus + 1)..].ToLowerInvariant();
        if (sha is not null && (sha.Length is < 7 or > 40 || !sha.All(Uri.IsHexDigit))) sha = null;
        return (version, sha);
    }

    private static string Arch(Architecture a) => a switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "other",
    };

    /// <summary>Arayüz dili (desteklenen dillerden biri: "tr-TR" → "tr"); başka dil "other".</summary>
    private static string LanguageToken() => Languages.Resolve(CultureInfo.CurrentUICulture)?.Name ?? "other";

    /// <summary>Her olayın ortak alanları: şema sürümü, uygulama/Windows sürümü, mimariler, arayüz dili, tür, sonuç, gün.</summary>
    public static JsonObject Create(string type, string result, string process, DateTime? nowUtc = null)
    {
        var (version, sha) = AppVersion();
        var os = Environment.OSVersion.Version;
        var e = new JsonObject
        {
            ["v"] = 1,
            ["app"] = version,
            ["os"] = $"{os.Major}.{os.Minor}.{os.Build}",
            ["osArch"] = Arch(RuntimeInformation.OSArchitecture),
            ["procArch"] = Arch(RuntimeInformation.ProcessArchitecture),
            ["lang"] = LanguageToken(),
            ["process"] = process,
            ["type"] = type,
            ["result"] = result,
            ["day"] = Day(nowUtc ?? DateTime.UtcNow),
        };
        if (sha is not null) e["sha"] = sha;
        return e;
    }

    /// <summary>Hata kodu: <see cref="ApfsErrorCode"/> adı, kapı/hizmet anahtarı ya da istisna TÜRÜ adı (iletisi asla).</summary>
    public static JsonObject WithCode(this JsonObject e, string? code)
    {
        if (!string.IsNullOrEmpty(code)) e["code"] = code.Length > 48 ? code[..48] : code;
        return e;
    }

    public static JsonObject WithDuration(this JsonObject e, TimeSpan t)
    {
        e["durLog2"] = DurationLog2(t);
        return e;
    }

    public static JsonObject WithGate(this JsonObject e, IEnumerable<string> keys)
    {
        var a = new JsonArray();
        foreach (var k in keys.Distinct(StringComparer.Ordinal).Take(20)) a.Add(k);
        if (a.Count > 0) e["gate"] = a;
        return e;
    }

    public static JsonObject WithVolumeCounts(this JsonObject e, int mounted, int locked, int hidden, int failed)
    {
        e["volumes"] = new JsonObject
        {
            ["mounted"] = Math.Min(mounted, 100), ["locked"] = Math.Min(locked, 100),
            ["hidden"] = Math.Min(hidden, 100), ["failed"] = Math.Min(failed, 100),
        };
        return e;
    }

    public static JsonObject WithWrite(this JsonObject e, string close, WriteCounters? counters)
    {
        var w = new JsonObject { ["close"] = close };
        if (counters is not null)
        {
            w["commitsLog2"] = Log2(counters.Commits);
            w["bytesLog2"] = Log2(counters.Bytes);
            w["opsLog2"] = Log2(counters.Operations);
        }
        e["write"] = w;
        return e;
    }

    /// <summary>Disk ve (biliniyorsa) kapsayıcı yapı bilgileri.</summary>
    public static JsonObject WithDisk(this JsonObject e, DiskFacts? disk, ContainerFacts? container)
    {
        if (disk is null && container is null) return e;
        var d = new JsonObject();
        if (disk is not null)
        {
            d["bus"] = disk.Bus;
            d["sector"] = disk.SectorSize;
            d["scheme"] = disk.Scheme;
            d["apfsParts"] = Math.Min(disk.ApfsPartitions, 64);
        }
        if (container is not null)
        {
            d["blockSize"] = container.BlockSize;
            d["sizeLog2"] = Log2(container.ByteSize);
            d["nxFeatures"] = Low32(container.Features);
            d["nxIncompat"] = Low32(container.Incompatible);
            d["nxRoCompat"] = Low32(container.ReadOnlyCompatible);
            d["swCrypto"] = container.SoftwareCrypto;
            if (container.UnreadableVolumes > 0) d["unreadableVolumes"] = Math.Min(container.UnreadableVolumes, 100);
            var vols = new JsonArray();
            foreach (var v in container.Volumes.Take(16))
            {
                var o = new JsonObject
                {
                    ["role"] = v.Role, ["enc"] = v.Encryption, ["caseSensitive"] = v.CaseSensitive, ["snapshots"] = v.HasSnapshots,
                    ["filesLog2"] = Log2(v.FileCount),
                    ["features"] = Low32(v.Features), ["incompat"] = Low32(v.Incompatible), ["roCompat"] = Low32(v.ReadOnlyCompatible),
                };
                if (v.LastWriter is not null) o["lastWriter"] = v.LastWriter;
                vols.Add(o);
            }
            d["volumes"] = vols;
        }
        e["disk"] = d;
        return e;
    }

    private static uint Low32(ulong v) => (uint)(v & 0xFFFFFFFF);

    /// <summary>Windows STORAGE_BUS_TYPE → şemadaki simge.</summary>
    public static string BusToken(int busType) => busType switch
    {
        0x1 => "scsi",
        0x3 => "ata",
        0x7 => "usb",
        0x8 => "raid",
        0xA => "sas",
        0xB => "sata",
        0xC => "sd",
        0xD => "mmc",
        0xE => "virtual",
        0xF => "vhd",
        0x11 => "nvme",
        _ => "other",
    };

    /// <summary>
    /// Çökme olayı: istisna TÜRÜ ve yalnız kendi derlemelerimizin (Apfs.*, apfs*) yığın çerçeveleri "Tür.Yöntem+0xIL" biçiminde.
    /// İleti, dosya yolu, satır numarası ve başka derlemelerin çerçeveleri ALINMAZ.
    /// </summary>
    public static JsonObject Crash(Exception ex, string process, DateTime? nowUtc = null)
    {
        var e = Create("crash", "fail", process, nowUtc);
        var c = new JsonObject { ["exType"] = TypeName(ex.GetType()) };
        if (ex.InnerException is { } inner) c["innerType"] = TypeName(inner.GetType());
        var frames = new JsonArray();
        foreach (var f in new StackTrace(ex, fNeedFileInfo: false).GetFrames())
        {
            if (frames.Count >= 20) break;
            var m = f.GetMethod();
            var t = m?.DeclaringType;
            if (m is null || t is null || !IsOwnAssembly(t.Assembly)) continue;
            int il = f.GetILOffset();
            string frame = $"{TypeName(t)}.{m.Name}+0x{(il < 0 ? 0 : il):x}";
            if (FramePattern().IsMatch(frame)) frames.Add(frame); // kalıba uymayan çerçeve (ör. olağandışı ad) olayı düşürmesin
        }
        if (frames.Count > 0) c["frames"] = frames;
        e["crash"] = c;
        return e;
    }

    /// <summary>Şemadaki çerçeve kalıbıyla aynı.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^Apfs\.[A-Za-z0-9_.`<>$|+,]{1,180}\+0x[0-9a-f]{1,6}$")]
    private static partial System.Text.RegularExpressions.Regex FramePattern();

    /// <summary>Tür adı; genel türde tanım adı (tür bağımsız değişkenleri derleme adı taşır).</summary>
    private static string TypeName(Type t)
    {
        if (t.IsGenericType && !t.IsGenericTypeDefinition) t = t.GetGenericTypeDefinition();
        string n = t.FullName ?? t.Name;
        return n.Length > 120 ? n[..120] : n;
    }

    private static bool IsOwnAssembly(Assembly a)
    {
        string n = a.GetName().Name ?? "";
        return n.StartsWith("Apfs.", StringComparison.Ordinal) || n.StartsWith("apfs", StringComparison.Ordinal);
    }
}
