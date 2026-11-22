using System.Globalization;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;

namespace Apfs.Windows.Tray;

/// <summary>Disk listesinin ekranı: disk listesi ya da boş ekranlardan biri.</summary>
public enum ListScreen { Disks, ServiceDown, Connecting, NoDisk }

/// <summary>Birim satırının durum işareti (橋戸 kapısı).</summary>
public enum VolumeGlyph { Open, Writable, Locked, Problem }

/// <summary>Birim satırının tek düğmesi.</summary>
public enum VolumeAction { Open, Unlock, Details }

/// <summary>Metnin rengi ROLDEN: soluk mürekkep, yazılabilir işareti, sorun.</summary>
public enum TextRole { Soft, Writable, Alert }

/// <summary>Listenin bütünü. Boş ekranda <see cref="Heading"/>/<see cref="Body"/> dolu, <see cref="Disks"/> boş.</summary>
/// <param name="Dashed">Boş ekranın büyük işareti kesik çizgili (hizmet kapalı); öbürlerinde soluk düz işaret.</param>
public sealed record ListModel(ListScreen Screen, string Heading, string Body, bool Dashed, IReadOnlyList<DiskModel> Disks);

/// <summary>Bir diskin kaydı: başlık + "Güvenle çıkar", bölümler, alttaki göstergeler.</summary>
/// <param name="Placeholder">Görünen birimi yoksa tek satır ("Disk denetleniyor…" / "Birim yok"); varsa null.</param>
/// <param name="MultiPartition">Birden çok APFS bölümü: her bölümün ret nedeni ve yazma düğmesi o bölümün SON biriminin altında;
/// tek bölümde neden birimlerin altında, düğme altta sağda.</param>
/// <param name="Checking">Birimleri listeden düşmüş, yazma durumu değişen bölüm var: altta TEK meşgul "Denetleniyor…".</param>
public sealed record DiskModel(DiskStatus Disk, string Title, string EjectText, string EjectBusyText, bool Ejecting, string? Placeholder,
    bool MultiPartition, IReadOnlyList<PartitionModel> Partitions, bool Checking, string CheckingText);

/// <summary>Bir APFS bölümü (birimleri hizmetin sırasıyla).</summary>
/// <param name="RefusedText">Yazma kapısı bölümü reddettiyse "Yazmaya açılamadı: …"; yoksa null.</param>
/// <param name="Toggle">"Yazmaya izin ver" / "Salt okunura dön"; bölümde bağlı birim yoksa null.</param>
public sealed record PartitionModel(int Container, IReadOnlyList<VolumeModel> Volumes, string? RefusedText, WriteToggleModel? Toggle);

/// <summary>Birim satırı: işaret, ad + harf, durum, tek düğme.</summary>
/// <param name="Drive">Bağlı birimin harfi (doluluk çubuğu da buna göre); değilse null.</param>
/// <param name="Dotted">Üstünde ayırıcı çizgi (diskin ilk satırı hariç).</param>
public sealed record VolumeModel(VolumeStatus Volume, VolumeGlyph Glyph, string? Drive, string StatusText, TextRole StatusRole,
    VolumeAction Action, string ActionText, bool ActionEnabled, bool Dotted);

/// <summary>Bölümün yazma düğmesi.</summary>
/// <param name="Enable">Basınca yazmaya açar (true) ya da salt okunura döner (false).</param>
/// <param name="Caution">"Yazmaya izin ver" dikkatli eylem tonunda; "Salt okunura dön" nötr.</param>
/// <param name="Label">Onay penceresindeki ad: tek bölüm + tek bağlı birim → disk; çok bölüm ya da ≥2 bağlı birim → disk · "A", "B".</param>
public sealed record WriteToggleModel(int Container, bool Enable, string Text, string BusyText, bool Caution, bool Busy, bool Enabled, string Label);

/// <summary>
/// Disk listesinin sunum mantığı (winuiPlan P1; 1.7.0'a kadar <c>DiskListView</c>'in içindeydi): durum → işaret, metin, düğme,
/// bölüm grupları, yazma düğmesi ve ret nedeni. Saf: aynı girdi aynı modeli verir; ana pencere ve panel aynı modeli çizer
/// (yalnız alan metni <see cref="SpaceText"/>'te sıkışık/geniş ayrılır).
/// </summary>
public static class DiskViewModel
{
    public static ListModel From(AppController app) => Build(app.Status, app.ServiceDown, app.Ejecting, app.Switching);

    /// <param name="ejecting">Çıkarılmakta olan diskler.</param>
    /// <param name="switching">Yazma durumu değişen bölümler ("disk#bölüm").</param>
    public static ListModel Build(StatusResponse? status, bool serviceDown, IReadOnlySet<string> ejecting, IReadOnlySet<string> switching)
    {
        if (serviceDown) return new(ListScreen.ServiceDown, WinText.U("List.ServiceDownHeading"), WinText.U("List.ServiceDownBody"), true, []);
        if (status is null) return new(ListScreen.Connecting, WinText.U("List.Connecting"), "", false, []);
        if (status.Disks.Count == 0) return new(ListScreen.NoDisk, WinText.U("List.NoDiskHeading"), WinText.U("List.NoDiskBody"), false, []);
        return new(ListScreen.Disks, "", "", false, status.Disks.Select(d => Disk(d, ejecting, switching)).ToList());
    }

    private static DiskModel Disk(DiskStatus d, IReadOnlySet<string> ejecting, IReadOnlySet<string> switching)
    {
        bool ejectingDisk = ejecting.Contains(d.Id);
        bool switchingDisk = AppController.SwitchingDisk(switching, d.Id);
        var visible = d.Volumes.Where(v => v.State != VolumeStatus.Hidden).ToList();
        // Yazma durumu değişirken bölümün birimleri kısa süre listede olmaz (denetlenip yeniden bağlanır)
        string? placeholder = visible.Count == 0 ? (switchingDisk ? WinText.U("Card.Checking") : WinText.U("Card.NoVolumes")) : null;
        // Yazma bölüm başınadır: birimler bölüm sırasıyla gruplanır (bölüm içinde hizmetin sırası)
        var groups = visible.GroupBy(v => v.Container).OrderBy(g => g.Key).ToList();
        bool multi = groups.Count > 1;
        bool first = true;
        var partitions = new List<PartitionModel>();
        foreach (var g in groups)
        {
            var volumes = new List<VolumeModel>();
            foreach (var v in g)
            {
                volumes.Add(Volume(v, ejectingDisk, switchingDisk, dotted: !first));
                first = false;
            }
            partitions.Add(new PartitionModel(g.Key, volumes, RefusedText(d, g, multi), Toggle(d, g, multi, ejectingDisk, switching)));
        }
        // Denetlenen bölümün birimleri o sırada listede olmaz: düğmesi meşgul olarak dursun (aynı anda birden çoğu için de tek gösterge)
        bool checking = AppController.SwitchingContainersOf(switching, d.Id).Any(c => groups.All(g => g.Key != c));
        return new DiskModel(d, d.Description, WinText.U("Action.SafelyEject"), WinText.U("Action.Ejecting"), ejectingDisk, placeholder,
            multi, partitions, checking, WinText.U("Action.Checking"));
    }

    private static VolumeModel Volume(VolumeStatus v, bool ejectingDisk, bool switchingDisk, bool dotted) => v.State switch
    {
        // "Aç" o disk çıkarılırken ya da (herhangi bir bölümünün) yazma durumu değişirken pasif; öbür düğmeler tıklanabilir
        VolumeStatus.Mounted => new(v, v.Writable ? VolumeGlyph.Writable : VolumeGlyph.Open, v.MountPoint,
            Capitalize(v.Writable ? WinText.U("Card.Writable") : WinText.U("Card.ReadOnly")), v.Writable ? TextRole.Writable : TextRole.Soft,
            VolumeAction.Open, WinText.U("Action.Open"), !ejectingDisk && !switchingDisk, dotted),
        VolumeStatus.NeedsPassword => new(v, VolumeGlyph.Locked, null, WinText.U("Card.NeedsPassword"), TextRole.Soft,
            VolumeAction.Unlock, WinText.U("Action.Unlock"), true, dotted),
        _ => new(v, VolumeGlyph.Problem, null, WinText.U("Card.Failed"), TextRole.Alert, VolumeAction.Details, WinText.U("Action.Details"), true, dotted),
    };

    /// <summary>Bölümün "Yazmaya izin ver" / "Salt okunura dön" düğmesi; bağlı (açılabilmiş) birimi yoksa null.</summary>
    private static WriteToggleModel? Toggle(DiskStatus d, IGrouping<int, VolumeStatus> g, bool multi, bool ejectingDisk, IReadOnlySet<string> switching)
    {
        if (!g.Any(v => v.State == VolumeStatus.Mounted)) return null;
        bool writable = g.Any(v => v.Writable);
        // Onayda bölümde yazılacak (bağlı) birimler sayılır: kilitli birim dokunulmadan kalır (1.7.0)
        var names = g.Where(v => v.State == VolumeStatus.Mounted).Select(v => $"\"{v.Name}\"").ToList();
        string label = multi || names.Count > 1 ? $"{d.Description} · {string.Join(", ", names)}" : d.Description;
        return new WriteToggleModel(g.Key, !writable,
            writable ? WinText.U("Action.BackToReadOnly") : WinText.U("Action.AllowWriting"),
            writable ? WinText.U("Action.Saving") : WinText.U("Action.Checking"),
            Caution: !writable, // 1.6.0: "Yazmaya izin ver" dikkatli eylem; "Salt okunura dön" nötr
            Busy: switching.Contains($"{d.Id}#{g.Key}"), Enabled: !ejectingDisk, label);
    }

    /// <summary>Yazma kapısı bu bölümü reddettiyse nedeni (sade Türkçe); yoksa null.</summary>
    private static string? RefusedText(DiskStatus d, IGrouping<int, VolumeStatus> g, bool multi)
    {
        if (g.Any(v => v.Writable)) return null;
        // Eski hizmet bölüm başına neden göndermez: tek bölümlüde disk düzeyindeki neden
        string? reason = g.Select(v => v.WriteBlockReason).FirstOrDefault(r => r is { Length: > 0 }) ?? (multi ? null : d.WriteBlockReason);
        return reason is { Length: > 0 } ? WinText.U("Card.WriteRefused", reason) : null;
    }

    /// <summary>"salt okunur" → "Salt okunur" (satırın başında; arayüz dilinin büyük harf kuralıyla).</summary>
    public static string Capitalize(string s) =>
        s.Length == 0 ? s : CultureInfo.CurrentUICulture.TextInfo.ToUpper(s[0]) + s[1..];

    /// <summary>Bağlı birimin alan yazısı: ana pencerede "465 GB içinde 153 GB boş", panelde (sıkışık) "153 GB boş".</summary>
    public static string SpaceText(DriveSpace s, bool compact)
    {
        string free = SizeText.Format(s.Free);
        return compact ? WinText.U("Card.Free", free) : WinText.U("Card.Space", SizeText.Format(s.Total), free);
    }

    /// <summary>Alan yazısının ve çubuğun rolü: boş alan %10'dan azsa sorun rengi.</summary>
    public static TextRole SpaceRole(DriveSpace s) => s.Low ? TextRole.Alert : TextRole.Soft;
}
