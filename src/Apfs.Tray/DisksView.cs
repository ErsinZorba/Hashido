using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Apfs.Localization;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Localization;
using Apfs.Windows.Tray;

namespace Apfs.Tray;

/// <summary>
/// Ana pencerenin Diskler içeriği: üstte "Diskler" + yeniden tara / ayarlar (simge düğmeleri; metinleri araç ipucu), disk listesi,
/// yeni sürüm satırı (yalnız bilinen daha yeni sürüm ve bildirim açıkken), altta dil ve 橋戸 imzası (Ek A C2–C5).
/// </summary>
internal sealed class DisksView
{
    private readonly MainWindow _main;
    private readonly DiskList _list;
    private readonly ActionButton _rescan;
    private readonly TextBlock _updateLine;
    private readonly Hyperlink _update;
    private readonly Run _updateText = new();

    public DisksView(MainWindow main)
    {
        _main = main;
        var app = main.Controller;
        var root = new Grid { Margin = new Thickness(24, 8, 20, 12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        View = root;

        // Üst: "Diskler" + simge düğmeleri
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _rescan = new ActionButton(WinText.U("Action.Rescan"), ButtonKind.Quiet, "", iconOnly: true, iconSize: 17) { BusyText = WinText.U("Action.Rescanning") };
        _rescan.Invoked += async () => await app.RescanAsync();
        var settings = new ActionButton(WinText.U("Action.Settings"), ButtonKind.Quiet, "", iconOnly: true, iconSize: 17);
        settings.Invoked += main.OpenSettings;
        buttons.Children.Add(_rescan);
        buttons.Children.Add(settings);
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        var heading = UiKit.Heading(WinText.U("Main.Disks"), 30);
        heading.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(heading);
        root.Children.Add(head);

        // Liste
        _list = new DiskList(app, compact: false, () => _main);
        _list.View.Margin = new Thickness(0, 0, 4, 0);
        var scroll = new ScrollViewer { Content = _list.View, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0), Focusable = false };
        Motion.SmoothWheel(scroll);
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Yeni sürüm: sakin bilgi satırı; tıklayınca "şimdi güncelle" akışı (C4, C5)
        _update = new Hyperlink(_updateText) { Foreground = Look.Brush(Role.Link), TextDecorations = null };
        _update.Click += (_, _) => UiKit.Later(async () => { if (main.Features is { } f) await f.UpdateNowAsync(app, _main); });
        _updateLine = new TextBlock(_update) { Visibility = Visibility.Collapsed, Margin = new Thickness(4, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(_updateLine, 2);
        root.Children.Add(_updateLine);

        // Alt: geçerli dilin kendi dilindeki adı (tıklayınca seçim menüsü), sağda küçük 橋戸 imzası
        var foot = new DockPanel { Margin = new Thickness(0, 8, 4, 0) };
        var signature = new TextBlock
        {
            Text = "橋戸", FontFamily = new FontFamily("Yu Gothic UI"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Look.Brush(Role.InkSoft), Margin = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(signature, Dock.Right);
        foot.Children.Add(signature);
        string current = Languages.Supported.FirstOrDefault(l => l.Name == CultureInfo.CurrentUICulture.Name).NativeName ?? "English";
        var language = UiKit.Quiet(new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 0, 0) });
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock { Text = current, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(UiKit.Margin(UiKit.Icon("", 10), 8, 2, 0, 0));
        language.Content = label;
        System.Windows.Automation.AutomationProperties.SetName(language, current);
        language.Foreground = Look.Brush(Role.Ink);
        var menu = LanguageMenu(language);
        Motion.PressOnlyCloses(language, menu);
        language.Click += (_, _) =>
        {
            FillLanguages(menu); // her açılışta kayıtlı seçimle
            Motion.PrepareMenuReveal(menu);
            menu.IsOpen = true;
        };
        foot.Children.Add(language);
        Grid.SetRow(foot, 3);
        root.Children.Add(foot);
        foot.Loaded += (_, _) =>
        {
            foot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            main.EnsureMinimumWidth(foot.DesiredSize.Width + root.Margin.Left + root.Margin.Right);
        };

        app.Changed += OnChanged;
        if (main.Features is { } features) features.UpdateChanged += RefreshUpdateLink;
        OnChanged();
        RefreshUpdateLink();
    }

    public FrameworkElement View { get; }

    /// <summary>Ayarlar'dan dönüş: bildirim seçimi değişmiş olabilir.</summary>
    public void Returned() => RefreshUpdateLink();

    private void OnChanged()
    {
        _rescan.Busy = _main.Controller.Rescanning;
        _list.Rebuild();
    }

    private void RefreshUpdateLink()
    {
        if (_main.Features is not { } f) return;
        var u = f.AvailableUpdate;
        _updateLine.Visibility = UiKit.Vis(u is not null);
        if (u is null) return;
        _updateText.Text = f.DownloadPercent is { } p
            ? WinText.U("Update.Downloading", u.Version.ToString(3), p)
            : WinText.U("Update.Link", u.Version.ToString(3));
        _update.IsEnabled = !f.Updating && !f.SetupRunning;
        _update.Foreground = Look.Brush(_update.IsEnabled ? Role.Link : Role.InkSoft);
    }

    /// <summary>"Windows dili" + desteklenen diller (kendi dillerindeki adlarıyla); FARKLI dil seçilince kaydedilir, program HEMEN yeniden açılır (C3).</summary>
    private static ContextMenu LanguageMenu(Button button)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = button, Placement = PlacementMode.Top, FontFamily = Look.BodyFont, FontSize = 14,
            Language = XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag),
        };
        Motion.AttachMenuReveal(menu); // WinUI MenuFlyout gibi düğmeden yukarı açılır
        return menu;
    }

    private void FillLanguages(ContextMenu menu)
    {
        var saved = LanguageSetting.Load();
        menu.Items.Clear();
        menu.Items.Add(Item(WinText.U("Language.Windows"), saved is null, null));
        menu.Items.Add(new Separator());
        foreach (var (name, native) in Languages.Supported)
            menu.Items.Add(Item(native, saved?.Name == name, CultureInfo.GetCultureInfo(name)));
    }

    private MenuItem Item(string text, bool on, CultureInfo? culture)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text }, IsCheckable = false, IsChecked = on };
        System.Windows.Automation.AutomationProperties.SetName(item, text);
        item.Click += (_, _) =>
        {
            if (TrayModel.ChooseLanguage(culture)) _main.RequestRestart();
        };
        return item;
    }
}
