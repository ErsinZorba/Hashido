using System.Globalization;
using Apfs.Windows.Ipc;
using Apfs.Windows.Localization;

namespace Apfs.Windows.Tray;

/// <summary>Tepsi simgesinin sağ tık menüsündeki öğenin türü.</summary>
public enum TrayMenuKind { OpenMain, Separator, Info, Eject, Rescan, OpenLog, Exit }

/// <summary>Sağ tık menüsünün bir öğesi. <paramref name="Disk"/> yalnız <see cref="TrayMenuKind.Eject"/>'te.</summary>
public sealed record TrayMenuItem(TrayMenuKind Kind, string Text = "", bool Enabled = true, DiskStatus? Disk = null);

/// <summary>Ayarlar penceresinde Tamam'a basıldığındaki seçimler.</summary>
/// <param name="ReportsAtOpen">Rapor kutusunun pencere açılırkenki hâli (onay "evet" ise işaretli).</param>
/// <param name="Language">Seçilen arayüz dili; null = Windows dili.</param>
/// <param name="Notifications">Ayarlar › Bildirimler. Varsayılanı YOK (bilerek): her çağıran seçimi açıkça versin, yoksa Tamam
/// kullanıcının seçimini sessizce "hepsi"ne çevirirdi (WinUI kıyas kopyası kayıtlı değeri geçirir).</param>
public sealed record SettingsChoices(bool Reports, bool ReportsAtOpen, bool UpdateNotify, bool UpdateAutoInstall, AppDesign Design,
    ThemeMode ThemeMode, CultureInfo? Language, NotificationMode Notifications);

/// <summary>Ayarlar › Tamam'ın sonucu (1.9.1).</summary>
/// <param name="Restart">Program yeniden başlamalı (tasarım ya da dil değişti ve kaydedildi).</param>
/// <param name="SaveFailed">Bir seçim kayıt defterine yazılamadı: kullanıcıya bir kez söylenir.</param>
public sealed record SettingsResult(bool Restart, bool SaveFailed);

/// <summary>
/// Tepsi simgesinin ve ayarların arayüzden bağımsız kararları (winuiPlan P1; önceden <c>TrayApp</c> ve <c>MainForm</c>'un içindeydi).
/// </summary>
public static class TrayModel
{
    /// <summary>Simgenin ipucu: hizmet kapalı / bağlı birim yok / N birim bağlı; Windows'un sınırı yüzünden en çok 63 karakter.</summary>
    public static string Tooltip(bool serviceDown, int mountedVolumes)
    {
        string t = serviceDown ? WinText.U("Tray.TipServiceDown", Branding.ProductName)
            : mountedVolumes == 0 ? WinText.U("Tray.TipNoVolumes", Branding.ProductName)
            : WinText.U("Tray.TipMounted", Branding.ProductName, mountedVolumes);
        return t.Length > 63 ? t[..63] : t;
    }

    /// <summary>
    /// Sağ tık menüsü (her açılışta yeniden kurulur): Ana pencereyi aç (kalın) · ayıraç · hizmet kapalı ya da henüz yanıt yoksa
    /// pasif "hizmet çalışmıyor" / disk yoksa pasif "disk yok" / her disk için "«ad» çıkar  E: F:" (çıkarılırken pasif) · ayıraç ·
    /// Yeniden tara · Günlüğü aç · ayıraç · Çıkış.
    /// </summary>
    public static IReadOnlyList<TrayMenuItem> Menu(StatusResponse? status, bool serviceDown, Func<string, bool> isEjecting)
    {
        var items = new List<TrayMenuItem> { new(TrayMenuKind.OpenMain, WinText.U("Action.OpenMain")), new(TrayMenuKind.Separator) };
        if (serviceDown || status is null)
            items.Add(new(TrayMenuKind.Info, WinText.U("Tray.MenuServiceDown"), Enabled: false));
        else if (status.Disks.Count == 0)
            items.Add(new(TrayMenuKind.Info, WinText.U("Tray.MenuNoDisk"), Enabled: false));
        else
            foreach (var d in status.Disks)
            {
                string drives = string.Join(" ", d.Volumes.Where(v => v.State == VolumeStatus.Mounted).Select(v => v.MountPoint));
                items.Add(new(TrayMenuKind.Eject, WinText.U("Tray.MenuEject", d.Description, drives.Length > 0 ? "  " + drives : ""),
                    Enabled: !isEjecting(d.Id), Disk: d));
            }
        items.Add(new(TrayMenuKind.Separator));
        items.Add(new(TrayMenuKind.Rescan, WinText.U("Action.Rescan")));
        items.Add(new(TrayMenuKind.OpenLog, WinText.U("Action.OpenLog")));
        items.Add(new(TrayMenuKind.Separator));
        items.Add(new(TrayMenuKind.Exit, WinText.U("Action.Exit")));
        return items;
    }

    /// <summary>
    /// Bu balon gösterilsin mi (Ayarlar › Bildirimler, 1.8.0; kullanıcı kararı 2026-11-22): hepsi; yalnız önemliler = uyarı/hata ya da
    /// kullanıcının başlattığı işin sonucu (<paramref name="requested"/>: Güvenle çıkar, yazmaya aç/kapat, parola penceresi/Hello
    /// onayıyla kilit açma, yeni sürüm ("haber ver" açıkken), güncelleme kuruldu); kapalı = hiçbiri. Pencereler (kutular, parola)
    /// bildirim değildir, buraya gelmez; "güncelleme kuruluyor" balonu da gelmez (Windows izin penceresinin habercisi, her zaman;
    /// <see cref="TrayFeatures"/>). Olan her şey yine günlüğe yazılır.
    /// </summary>
    public static bool ShowsNotice(NotificationMode mode, NoticeLevel level, bool requested) => mode switch
    {
        NotificationMode.Off => false,
        NotificationMode.Important => requested || level != NoticeLevel.Info,
        _ => true,
    };

    /// <summary>
    /// Ayarlar › Tamam: değişen seçimleri kaydeder. Rapor seçimi YALNIZ kutu açılıştaki hâlinden farklıysa yazılır ve hemen bir tur
    /// (<paramref name="consentChanged"/>): onay hiç cevaplanmamışken kutuya dokunulmadıysa cevapsız kalır, onay penceresi sonraki
    /// açılışta yine sorulur (kullanıcı kararı 2026-11-18, winuiPlan Ek A N11; önceden "Hayır" yazılıyordu). Güncelleme bildirimi,
    /// kendiliğinden kurulum ve Bildirimler seçimi her zaman yazılır (Bildirimler yeniden başlatmaz, hemen geçerli). Tasarım,
    /// tema ya da dil değiştiyse kaydedilir. Tema (açık/koyu) yeniden başlatmaz:
    /// çağıran hemen uygular (winuiPlan P2.2, Ek A R2 ⟳; WinForms'ta tema da yeniden başlatıyordu).
    /// </summary>
    /// <returns>Program yeniden başlamalı: tasarım ya da dil değişti (ikisi birlikte de bir kez; süren iş yoksa hemen, varsa bitince: <see cref="DeferredRestart"/>).</returns>
    public static bool ApplySettings(SettingsChoices c, Action consentChanged) => ApplySettingsChecked(c, consentChanged).Restart;

    /// <summary>
    /// <see cref="ApplySettings"/>, kaydedilemeyen seçimi de söyler (1.9.1): kayıt defteri yazılamazsa Ayarlar bir kez
    /// <c>Settings.SaveFailed</c> gösterir (önceden seçim uygulanmış gibi görünüyordu). Rapor onayı yazılamasa da bu çalışmada
    /// seçilen değerle uygulanır (<see cref="UserSettings.ReportsConsent"/>); tasarım ya da dil kaydedilemediyse yeniden başlanmaz
    /// (yeni süreç eskisini açardı).
    /// </summary>
    public static SettingsResult ApplySettingsChecked(SettingsChoices c, Action consentChanged)
    {
        bool saved = true;
        if (c.Reports != c.ReportsAtOpen)
        {
            saved &= UserSettings.SetReportsConsent(c.Reports);
            consentChanged();
        }
        saved &= UserSettings.SetUpdateNotify(c.UpdateNotify);
        saved &= UserSettings.SetUpdateAutoInstall(c.UpdateAutoInstall);
        saved &= UserSettings.SetNotifications(c.Notifications);
        bool restart = false;
        if (c.ThemeMode != UserSettings.ThemeMode) saved &= UserSettings.SetThemeMode(c.ThemeMode);
        if (c.Design != UserSettings.Design)
        {
            if (UserSettings.SetDesign(c.Design)) restart = true;
            else saved = false;
        }
        if (LanguageSetting.Load()?.Name != c.Language?.Name)
        {
            if (LanguageSetting.Save(c.Language)) restart = true;
            else saved = false;
        }
        return new SettingsResult(restart, !saved);
    }

    /// <summary>
    /// Dil/tasarım yeniden başlaması şimdi süren bir işi keser mi (1.9.1; <see cref="DeferredRestart"/>): güncelleme "Kur" akışı
    /// (<see cref="TrayFeatures.Updating"/>), bu tepsinin başlattığı setup (<see cref="TrayFeatures.SetupRunning"/>) ya da tepsi
    /// boşta değil (<see cref="AppController.Idle"/>: açık kutu, çıkarma, yazma geçişi, kilit açma, tarama).
    /// </summary>
    public static bool RestartMustWait(bool updating, bool setupRunning, bool idle) => updating || setupRunning || !idle;

    /// <summary>Alt çubuktaki dil menüsünden seçim: farklıysa kaydedilir ve true (program yeniden başlar; süren iş varsa bitince, <see cref="DeferredRestart"/>); aynıysa hiçbir şey.</summary>
    public static bool ChooseLanguage(CultureInfo? language)
    {
        // Kaydedilemediyse yeniden başlamak eski dili açardı
        return LanguageSetting.Load()?.Name != language?.Name && LanguageSetting.Save(language);
    }
}
