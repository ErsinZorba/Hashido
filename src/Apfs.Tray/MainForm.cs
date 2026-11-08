using System.Diagnostics;
using System.Globalization;
using Apfs.Localization;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Localization;

namespace Apfs.Tray;

/// <summary>
/// Ana pencere: bağlı Mac diskleri kart olarak, altta "Yeniden tara" ve "Günlüğü aç". Kapatınca tepsiye gizlenir;
/// programdan çıkış tepsi menüsündendir (diskler arka planda bağlı kalır, bunu hizmet yapar).
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppController _app;
    private readonly TrayFeatures? _features;
    private readonly DiskListView _list;
    private readonly LinkLabel _update;
    private readonly FriendlyButton _rescan;
    private readonly ContextMenuStrip _languageMenu = new();
    private bool _hintShown;

    /// <summary>Pencere ilk kez tepsiye gizlendiğinde (kullanıcıya bir kez bilgi vermek için).</summary>
    public event Action? HiddenToTray;

    /// <summary>Kullanıcı başka bir arayüz dili seçti (kaydedildi); metinler yeniden başlatınca değişir.</summary>
    public event Action? RestartRequested;

    /// <param name="features">Raporlama ve güncelleme (Ayarlar düğmesi ve "yeni sürüm" bağlantısı); düzenekte null olabilir.</param>
    public MainForm(AppController app, TrayFeatures? features = null)
    {
        _app = app;
        _features = features;
        Text = Branding.ProductName;
        Icon = Theme.AppIcon(32);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(this.S(480), this.S(600));
        MinimumSize = new Size(this.S(380), this.S(360));

        // Üst: simge, ad, tek cümlelik açıklama
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Background,
            Padding = new Padding(this.S(20), this.S(20), this.S(20), this.S(12)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var logo = new PictureBox
        {
            Image = Theme.AppIcon(256).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(this.S(44), this.S(44)),
            Margin = new Padding(0, 0, this.S(14), 0),
        };
        header.Controls.Add(logo, 0, 0);
        header.SetRowSpan(logo, 2);
        header.Controls.Add(new Label { Text = Branding.ProductName, Font = Theme.Title, ForeColor = Theme.Text, AutoSize = true, Margin = Padding.Empty }, 1, 0);
        header.Controls.Add(new Label
        {
            Text = WinText.U("Main.Subtitle"), ForeColor = Theme.Muted, AutoSize = true, Margin = new Padding(0, this.S(2), 0, 0),
        }, 1, 1);

        _list = new DiskListView(app, compact: false)
        {
            Dock = DockStyle.Fill, Padding = new Padding(this.S(20), this.S(4), this.S(20), this.S(4)), DialogOwner = this,
        };

        // Alt: işlemler ve sürüm
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 5, BackColor = Theme.Background,
            Padding = new Padding(this.S(16), this.S(8), this.S(20), this.S(14)),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rescan = new FriendlyButton(WinText.U("Action.Rescan"), FriendlyButton.Kind.Secondary) { BusyText = WinText.U("Action.Rescanning") };
        _rescan.Click += async (_, _) => await _app.RescanAsync();
        var log = new FriendlyButton(WinText.U("Action.OpenLog"), FriendlyButton.Kind.Quiet);
        log.Click += (_, _) => AppController.OpenLog();
        string version = Application.ProductVersion.Split('+')[0];
        var settings = new FriendlyButton(WinText.U("Action.Settings"), FriendlyButton.Kind.Quiet);
        settings.Click += (_, _) => ShowSettings();
        footer.Controls.Add(_rescan, 0, 0);
        footer.Controls.Add(log, 1, 0);
        footer.Controls.Add(settings, 2, 0);
        var about = new FriendlyButton(WinText.U("Main.Version", version), FriendlyButton.Kind.Quiet) { Anchor = AnchorStyles.Right, Font = Theme.Small };
        about.Click += (_, _) => ShowAbout(version);
        // Dil: geçerli dilin kendi dilindeki adı; tıklayınca seçim menüsü (sade, göze batmayan)
        string current = Languages.Supported.FirstOrDefault(l => l.Name == CultureInfo.CurrentUICulture.Name).NativeName ?? "English";
        var language = new FriendlyButton(current, FriendlyButton.Kind.Quiet) { Anchor = AnchorStyles.Right, Font = Theme.Small };
        language.Click += (_, _) => ShowLanguageMenu(language);
        footer.Controls.Add(language, 3, 0);
        footer.Controls.Add(about, 4, 0);

        // Yeni sürüm bağlantısı (yalnız bilinen daha yeni bir sürüm varken ve bildirim açıkken)
        _update = new LinkLabel
        {
            Dock = DockStyle.Bottom, AutoSize = true, Visible = false, UseMnemonic = false, BackColor = Theme.Background,
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentPressed, Padding = new Padding(this.S(24), this.S(4), this.S(20), 0),
        };
        _update.LinkClicked += (_, _) =>
        {
            if (_features?.AvailableUpdate is { } u) Process.Start(new ProcessStartInfo(u.ReleasePage.ToString()) { UseShellExecute = true });
        };

        Controls.Add(_list);
        Controls.Add(_update);
        Controls.Add(footer);
        Controls.Add(header);

        // Alt çubuk tek satır: uzun dillerde (fr, pt-BR…) sığmazsa dil düğmesi ile sürüm üst üste biniyordu → pencere en az
        // alt çubuğun gerektirdiği kadar geniş (yazı tipi forma eklendikten sonra ölçülür).
        int footerWidth = footer.GetPreferredSize(Size.Empty).Width;
        if (footerWidth > ClientSize.Width) ClientSize = new Size(footerWidth, ClientSize.Height);
        int minWidth = SizeFromClientSize(new Size(footerWidth, 0)).Width;
        if (minWidth > MinimumSize.Width) MinimumSize = new Size(minWidth, MinimumSize.Height);

        _app.Changed += OnChanged;
        if (_features is not null) _features.UpdateChanged += RefreshUpdateLink;
        OnChanged();
        RefreshUpdateLink();
    }

    private void RefreshUpdateLink()
    {
        var u = _features?.AvailableUpdate;
        _update.Visible = u is not null;
        if (u is not null) _update.Text = WinText.U("Update.Link", u.Version.ToString(3));
    }

    /// <summary>Ayarlar: seçimler Tamam'la uygulanır; dil değiştiyse program yeniden başlar.</summary>
    private void ShowSettings()
    {
        using var f = new SettingsForm(_features?.Sent ?? new Apfs.Windows.Reporting.SentLog(
            Path.Combine(Apfs.Windows.Reporting.ReportRecorder.DefaultUserDirectory, "sent.jsonl")));
        if (f.ShowDialog(this) != DialogResult.OK) return;
        if (UserSettings.ReportsConsent != f.Reports)
        {
            UserSettings.ReportsConsent = f.Reports;
            _features?.ConsentChanged();
        }
        UserSettings.UpdateNotify = f.UpdateNotify;
        RefreshUpdateLink();
        ChooseLanguage(f.Language);
    }

    private void OnChanged()
    {
        _rescan.Busy = _app.Rescanning;
        _list.Rebuild();
    }

    /// <summary>"Windows dili" + desteklenen diller (kendi dillerindeki adlarıyla); seçilen kaydedilir, program yeniden başlar.</summary>
    private void ShowLanguageMenu(Control anchor)
    {
        var saved = LanguageSetting.Load();
        _languageMenu.Items.Clear();
        _languageMenu.Items.Add(new ToolStripMenuItem(WinText.U("Language.Windows"), null, (_, _) => ChooseLanguage(null)) { Checked = saved is null });
        _languageMenu.Items.Add(new ToolStripSeparator());
        foreach (var (name, native) in Languages.Supported)
        {
            var culture = CultureInfo.GetCultureInfo(name);
            _languageMenu.Items.Add(new ToolStripMenuItem(native, null, (_, _) => ChooseLanguage(culture)) { Checked = saved?.Name == name });
        }
        _languageMenu.Show(anchor, new Point(0, anchor.Height));
    }

    private void ChooseLanguage(CultureInfo? language)
    {
        if (LanguageSetting.Load()?.Name == language?.Name) return;
        LanguageSetting.Save(language);
        RestartRequested?.Invoke();
    }

    /// <summary>
    /// Hakkında penceresi. WinFsp lisansı (FLOSS istisnası) WinFsp telif notunun ve depo bağlantısının arayüzde bulunmasını
    /// şart koşar; not burada durur.
    /// </summary>
    private void ShowAbout(string version)
    {
        var page = new TaskDialogPage
        {
            Caption = Branding.ProductName,
            Heading = $"{Branding.ProductName} {version}",
            Text = WinText.U("About.Text") + "\n" +
                   "<a href=\"https://github.com/ErsinZorba/Hashido\">github.com/ErsinZorba/Hashido</a>",
            Footnote = new TaskDialogFootnote(
                "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos\n" +
                "<a href=\"https://github.com/winfsp/winfsp\">github.com/winfsp/winfsp</a>"),
            EnableLinks = true,
            Buttons = { TaskDialogButton.OK },
        };
        page.LinkClicked += (_, e) => Process.Start(new ProcessStartInfo(e.LinkHref) { UseShellExecute = true });
        TaskDialog.ShowDialog(this, page);
    }

    /// <summary>Gizliyse gösterir, simge durumundaysa geri getirir ve öne alır.</summary>
    public void ShowAndActivate()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                HiddenToTray?.Invoke();
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.Changed -= OnChanged;
            if (_features is not null) _features.UpdateChanged -= RefreshUpdateLink;
            _languageMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
