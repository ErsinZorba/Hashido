using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Apfs.Localization;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Localization;
using Apfs.Windows.Reporting;
using Apfs.Windows.Tray;
using AppThemeMode = Apfs.Windows.ThemeMode;

namespace Apfs.Tray;

/// <summary>
/// Ayarlar (Ek A N1–N11; 1.8.0'a kadar WinForms SettingsForm): dil, görünüm (tasarım + tema), bildirimler (açılır kart, üç
/// seçenek), otomatik raporlar (+ "Ne
/// gönderiliyor?", "Gönderilenleri göster"), hatırlanan parolalar (kayıt varsa), güncellemeler (bildirim, kendiliğinden indir ve
/// kur, "Şimdi denetle"), Hakkında (sürüm → Hakkında kutusu, günlüğü aç, sahip). Ana pencerenin içeriği olarak açılır (anında).
/// Seçimler "Tamam"la uygulanır (<see cref="TrayModel.ApplySettings"/>); "İptal", geri ve Esc hiçbir şeyi uygulamaz ("Unut" ve
/// "Şimdi denetle"nin yan etkileri hariç). Açıkken tepsi yeniden başlamaz (kalıcı pencere sayılır; S4).
/// </summary>
internal sealed class SettingsView
{
    private readonly MainWindow _main;
    private readonly TaskCompletionSource _open = new();
    private readonly List<CultureInfo?> _cultures = [];
    private readonly ComboBox _language, _design, _theme;
    private readonly CheckBox _reports, _updates, _autoInstall;
    private readonly RadioButton[] _notify;
    private readonly bool _reportsAtOpen;
    private readonly ActionButton _back;
    private readonly ScrollViewer _scroll;

    public SettingsView(MainWindow main)
    {
        _main = main;
        var features = main.Features;
        _ = AppController.ModalAsync(() => _open.Task);

        var root = new Grid { Margin = new Thickness(24, 8, 20, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        View = root;

        // Üst: geri (= İptal) + başlık
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 0, 0, 10) };
        _back = new ActionButton(WinText.U("Action.CancelDialog"), ButtonKind.Quiet, "", iconOnly: true, iconSize: 17);
        _back.Invoked += Cancel;
        header.Children.Add(_back);
        var heading = UiKit.Heading(WinText.U("Settings.Title", Branding.ProductName), 30);
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.Margin = new Thickness(6, 0, 0, 0);
        header.Children.Add(heading);
        root.Children.Add(header);

        var body = new StackPanel { MaxWidth = 720, Margin = new Thickness(0, 0, 4, 16) };
        _scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0), FocusVisualStyle = null,
        };
        Motion.SmoothWheel(_scroll);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        // Dil
        body.Children.Add(Section(WinText.U("Settings.Language"), 4));
        var saved = LanguageSetting.Load();
        var names = new List<string> { WinText.U("Language.Windows") };
        _cultures.Add(null);
        foreach (var (name, native) in Languages.Supported)
        {
            _cultures.Add(CultureInfo.GetCultureInfo(name));
            names.Add(native);
        }
        _language = Combo(names, Math.Max(0, _cultures.FindIndex(c => c?.Name == saved?.Name)), WinText.U("Settings.Language"));
        _language.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(Card(_language));
        body.Children.Add(Note(WinText.U("Settings.LanguageNote")));

        // Görünüm: tasarım (Hashido / Windows 11) ve tema (Windows'a uy / Açık / Koyu)
        body.Children.Add(Section(WinText.U("Settings.AppearanceHeading")));
        _design = Combo([WinText.U("Design.Brand"), WinText.U("Design.Windows")], UserSettings.Design == AppDesign.Windows ? 1 : 0, WinText.U("Settings.Design"));
        _theme = Combo([WinText.U("Theme.System"), WinText.U("Theme.Light"), WinText.U("Theme.Dark")], (int)UserSettings.ThemeMode, WinText.U("Settings.Theme"));
        body.Children.Add(Card(Row(WinText.U("Settings.Design"), _design), Row(WinText.U("Settings.Theme"), _theme)));
        body.Children.Add(Note(WinText.U("Settings.AppearanceNote")));

        // Bildirimler (1.8.0, kullanıcı 2026-11-22: "tam kontrol"): kapalı duran açılır kart, başlığında geçerli seçim (radyo değişince
        // hemen); içinde hepsi / yalnız önemliler / hiçbiri (TrayModel.ShowsNotice). Tamam'la kaydedilir, yeniden başlatmaz.
        body.Children.Add(Section(WinText.U("Settings.NotificationsHeading")));
        string[] notifyLabels = [WinText.U("Settings.NotifyAll"), WinText.U("Settings.NotifyImportant"), WinText.U("Settings.NotifyOff")];
        var summary = UiKit.Text(notifyLabels[(int)UserSettings.Notifications]);
        _notify =
        [
            Radio(notifyLabels[0], null),
            Radio(notifyLabels[1], WinText.U("Settings.NotifyImportantNote")),
            Radio(notifyLabels[2], WinText.U("Settings.NotifyOffNote")),
        ];
        var options = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < _notify.Length; i++)
        {
            int index = i;
            _notify[i].IsChecked = i == (int)UserSettings.Notifications;
            _notify[i].Checked += (_, _) => summary.Text = notifyLabels[index];
            if (i > 0) _notify[i].Margin = new Thickness(0, 8, 0, 0);
            options.Children.Add(_notify[i]);
        }
        var notifications = new Expander { Header = summary, Content = options, IsExpanded = false };
        System.Windows.Automation.AutomationProperties.SetName(notifications, WinText.U("Settings.NotificationsHeading"));
        body.Children.Add(notifications); // Fluent Expander kendi kartını çizer (bizim Card içinde iç içe çerçeve oluyordu)

        // Raporlar (onay "evet" ise işaretli; Tamam yalnız açılıştaki hâlinden farklıysa yazar: N11)
        body.Children.Add(Section(WinText.U("Settings.ReportsHeading")));
        _reports = Check(WinText.U("Settings.Reports"), UserSettings.ReportsConsent == true);
        _reportsAtOpen = _reports.IsChecked == true;
        var links = new WrapPanel();
        links.Children.Add(UiKit.Margin(ConsentWindow.PrivacyLink(WinText.U("Settings.WhatIsSent")), 0, 0, 16, 0));
        var sent = features?.Sent ?? new SentLog(System.IO.Path.Combine(ReportRecorder.DefaultUserDirectory, "sent.jsonl"));
        links.Children.Add(UiKit.Link(WinText.U("Settings.ShowSent"), () => UiKit.Later(async () => await SentWindow.ShowAsync(_main, sent))));
        body.Children.Add(Card(_reports, links));

        // Hatırlanan parolalar (yalnız kayıt varsa; parola gösterilmez): "Unut" kaydı HEMEN siler (Vazgeç geri almaz), satır
        // kalkar; son satırla bölüm de kalkar. Kayıt bu arada Windows'tan silinmişse de satır kalkar (N6).
        var remembered = CredentialStore.Default.List();
        if (remembered.Count > 0)
        {
            var section = Section(WinText.U("Settings.PasswordsHeading"));
            var list = new StackPanel();
            var card = Card(list);
            var note = Note(WinText.U("Settings.PasswordsNote"));
            foreach (var volume in remembered)
            {
                var forget = new ActionButton(WinText.U("Action.Forget"));
                var row = Row(volume.Name, forget);
                if (list.Children.Count > 0) row.Margin = new Thickness(0, 10, 0, 0);
                forget.Invoked += () =>
                {
                    if (!(CredentialStore.Default.Delete(volume.Uuid) || CredentialStore.Default.TryRead(volume.Uuid) is null)) return;
                    list.Children.Remove(row);
                    if (list.Children.Count > 0) ((FrameworkElement)list.Children[0]).Margin = new Thickness(0);
                    else section.Visibility = card.Visibility = note.Visibility = Visibility.Collapsed;
                };
                list.Children.Add(row);
            }
            body.Children.Add(section);
            body.Children.Add(card);
            body.Children.Add(note);
        }

        // Güncellemeler: bildirim; kendiliğinden indir ve kur (yalnız bildirim işaretliyken etkin; kayıtlı seçim korunur);
        // "Şimdi denetle" (meşgul "Denetleniyor…") → sonuç düz metin (N7)
        body.Children.Add(Section(WinText.U("Settings.UpdatesHeading")));
        _updates = Check(WinText.U("Settings.UpdateNotify"), UserSettings.UpdateNotify);
        _autoInstall = Check(WinText.U("Settings.UpdateAutoInstall"), UserSettings.UpdateAutoInstall);
        _autoInstall.IsEnabled = _updates.IsChecked == true;
        _updates.Checked += (_, _) => _autoInstall.IsEnabled = true;
        _updates.Unchecked += (_, _) => _autoInstall.IsEnabled = false;
        var checkNow = new ActionButton(WinText.U("Settings.CheckNow")) { BusyText = WinText.U("Action.Checking") };
        var result = UiKit.Text("", 13, Role.InkSoft);
        result.Visibility = Visibility.Collapsed;
        result.VerticalAlignment = VerticalAlignment.Center;
        result.Margin = new Thickness(12, 0, 0, 0);
        checkNow.Invoked += async () =>
        {
            if (checkNow.Busy) return;
            checkNow.Busy = true;
            try
            {
                var r = features is null ? new UpdateCheckResult(false, null) : await features.CheckNowAsync();
                if (_open.Task.IsCompleted) return; // Ayarlar kapandı: sonuç atılır
                result.Text = !r.Answered ? WinText.U("Settings.CheckFailed")
                    : r.Update is { } u ? WinText.U("Update.Link", u.Version.ToString(3))
                    : WinText.U("Settings.UpToDate", Branding.ProductName);
                result.Visibility = Visibility.Visible;
            }
            finally { checkNow.Busy = false; }
        };
        var checkRow = new Grid();
        checkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        checkRow.ColumnDefinitions.Add(new ColumnDefinition());
        checkRow.Children.Add(checkNow);
        Grid.SetColumn(result, 1);
        checkRow.Children.Add(result);
        body.Children.Add(Card(_updates, _autoInstall, checkRow));

        // Hakkında: sürüm (Hakkında kutusu; WinFsp notu orada), günlük, sahip
        body.Children.Add(Section(WinText.U("Settings.AboutHeading")));
        string version = Version;
        var aboutRow = new WrapPanel();
        var versionButton = new ActionButton(WinText.U("Main.Version", version)) { Margin = new Thickness(0, 0, 8, 0) };
        versionButton.Invoked += async () => await AppController.ModalAsync(() => MessageWindow.AboutAsync(_main, version));
        var log = new ActionButton(WinText.U("Action.OpenLog"));
        log.Invoked += async () => await AppController.OpenLogAsync(new WpfUiHost()); // "henüz günlük yok" kutusu her zaman sahipsiz
        aboutRow.Children.Add(versionButton);
        aboutRow.Children.Add(log);
        var aboutCard = new List<FrameworkElement> { aboutRow };
        if (OwnerLine(typeof(SettingsView).Assembly) is { } owner) aboutCard.Add(UiKit.Text(owner, 12, Role.InkSoft));
        body.Children.Add(Card([.. aboutCard]));

        // Düğmeler: İptal (Esc) ve Tamam (birincil)
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 4, 20) };
        var cancel = UiKit.Button(WinText.U("Action.CancelDialog"), ButtonKind.Secondary, Cancel);
        cancel.MinWidth = 120;
        var ok = UiKit.Button(WinText.U("Action.OK"), ButtonKind.Accent, Apply);
        ok.MinWidth = 120;
        ok.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
    }

    public FrameworkElement View { get; }

    /// <summary>
    /// Açılışta klavye odağı kaydırma alanında: tıklanan çark düğmesi içerikle birlikte gittiği için odak pencerede kalsın; oklar
    /// sayfayı kaydırır, Esc İptal, Enter Tamam (odak düğmede değil). Bir ayarda DEĞİL: odaktaki kapalı açılır liste ok tuşuyla
    /// seçimini değiştirir, ardından Enter dili değiştirip programı yeniden açardı (bağımsız denetçi, wpfPlan 2.7).
    /// </summary>
    public void Focus() => View.Dispatcher.BeginInvoke(() => _scroll.Focus());

    /// <summary>Ayarlar kapandı (Diskler'e dönüldü): açık-pencere sayacı düşer, "Şimdi denetle"nin geç sonucu atılır.</summary>
    public void Closed() => _open.TrySetResult();

    private static TextBlock Section(string text, double top = 20)
    {
        var t = UiKit.Heading(text, 16);
        t.Margin = new Thickness(2, top, 0, 6);
        return t;
    }

    private static TextBlock Note(string text)
    {
        var t = UiKit.Text(text, 12, Role.InkSoft);
        t.Margin = new Thickness(2, 4, 0, 0);
        return t;
    }

    private static Border Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        foreach (var r in rows)
        {
            if (stack.Children.Count > 0 && r.Margin == default) r.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(r);
        }
        var card = new Border { Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return card;
    }

    /// <summary>Etiket solda, denetim sağda.</summary>
    private static Grid Row(string label, FrameworkElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = UiKit.Text(label);
        t.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(t);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static ComboBox Combo(IEnumerable<string> items, int selected, string name)
    {
        var c = new ComboBox { MinWidth = 200 };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = selected;
        System.Windows.Automation.AutomationProperties.SetName(c, name);
        Motion.PressOnlyCloses(c);
        Motion.WinUiDropDown(c);
        return c;
    }

    /// <summary>Bildirim seçeneği (aynı grupta tek seçim): ad ve varsa altında soluk açıklama, ikisi de kaydırılır.</summary>
    private static RadioButton Radio(string text, string? note)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (note is not null)
        {
            var n = UiKit.Text(note, 12, Role.InkSoft);
            n.Margin = new Thickness(0, 2, 0, 0);
            content.Children.Add(n);
        }
        var r = new RadioButton { Content = content, GroupName = "Notifications" };
        System.Windows.Automation.AutomationProperties.SetName(r, text);
        return r;
    }

    /// <summary>Onay kutusu (UIA'da onay kutusu; R8), metni kaydırılır (uzun diller).</summary>
    private static CheckBox Check(string text, bool on) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on,
    };

    /// <summary>Tamam'daki seçimlerin tümü.</summary>
    private SettingsChoices Choices => new(
        _reports.IsChecked == true, _reportsAtOpen, _updates.IsChecked == true, _autoInstall.IsChecked == true,
        _design.SelectedIndex == 1 ? AppDesign.Windows : AppDesign.Brand,
        (AppThemeMode)Math.Clamp(_theme.SelectedIndex, 0, 2), _cultures[Math.Max(0, _language.SelectedIndex)],
        (NotificationMode)Math.Max(0, Array.FindIndex(_notify, r => r.IsChecked == true)));

    /// <summary>
    /// Tamam: seçimler kaydedilir; tema hemen uygulanır (R2 ⟳); tasarım ya da dil değiştiyse program TEK kez ve HEMEN yeniden
    /// açılır (A18).
    /// </summary>
    private void Apply()
    {
        bool restart = TrayModel.ApplySettings(Choices, () => _main.Features?.ConsentChanged());
        Look.Refresh();
        _main.Back();
        if (restart) _main.RequestRestart();
    }

    private void Cancel() => _main.Back();

    /// <summary>Esc = İptal; Enter odaktaki düğmeyi basar, odak düğmede değilse Tamam (N10). Açılır liste açıkken liste işler.</summary>
    public void OnKey(KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        if (KeyRouting.InsideOpenList(focused)) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (OpenWindows.EnterGoesToDefault(focused)) Apply();
            else OpenWindows.PressFocused(focused);
        }
    }

    /// <summary>Programın sürümü ("+hash"sız).</summary>
    private static string Version =>
        (typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+')[0];

    /// <summary>"Copyright (c) 2026 Ersin Zorba" → "© 2026 Ersin Zorba" (derlemenin telif özniteliğinden; çevrilmez); yoksa null.</summary>
    internal static string? OwnerLine(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright is { Length: > 0 } c
            ? Regex.Replace(c, @"^\s*Copyright\s*(\(c\)|©)?\s*", "© ", RegexOptions.IgnoreCase)
            : null;
}
