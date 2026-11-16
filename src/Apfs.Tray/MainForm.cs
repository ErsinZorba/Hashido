using System.Diagnostics;
using System.Globalization;
using Apfs.Localization;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Localization;

namespace Apfs.Tray;

/// <summary>
/// Ana pencere: üstte "Diskler" + yeniden tara / ayarlar, bağlı Mac diskleri defter düzeninde, altta dil ve 橋戸 imzası
/// (günlük ve sürüm Ayarlar'da). Kapatınca tepsiye gizlenir;
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
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Theme.ApplyFrame(this);
        _languageMenu.Renderer = Theme.MenuRenderer;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(this.S(500), this.S(600));
        MinimumSize = new Size(this.S(380), this.S(360));

        // Üst: "Diskler" başlığı, sağda yeniden tara ve ayarlar (simge düğmeleri; metinleri araç ipucu)
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Theme.Paper,
            Padding = new Padding(this.S(24), this.S(18), this.S(18), this.S(14)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new HeadingLabel
        {
            Text = WinText.U("Main.Disks"), Font = Theme.Heading(18f), ForeColor = Theme.Ink, AutoSize = true, UseMnemonic = false,
            Anchor = AnchorStyles.Left, Margin = Padding.Empty,
        }, 0, 0);
        _rescan = new FriendlyButton(WinText.U("Action.Rescan"), FriendlyButton.Kind.Icon, Glyphs.Icon.Refresh)
        {
            BusyText = WinText.U("Action.Rescanning"), Anchor = AnchorStyles.Right, Margin = Padding.Empty,
        };
        _rescan.Click += async (_, _) => await _app.RescanAsync();
        var settings = new FriendlyButton(WinText.U("Action.Settings"), FriendlyButton.Kind.Icon, Glyphs.Icon.Settings)
        {
            Anchor = AnchorStyles.Right, Margin = Padding.Empty,
        };
        settings.Click += (_, _) => ShowSettings();
        header.Controls.Add(_rescan, 1, 0);
        header.Controls.Add(settings, 2, 0);

        _list = new DiskListView(app, compact: false)
        {
            Dock = DockStyle.Fill, Padding = new Padding(this.S(24), this.S(4), this.S(24), this.S(4)), DialogOwner = this,
        };

        // Alt: dil, sağda küçük 橋戸 imzası; üstünde ince çizgi. Günlük ve sürüm (Hakkında) 1.5.1'den beri Ayarlar'da.
        var footer = new FooterPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, BackColor = Theme.Paper,
            Padding = new Padding(this.S(18), this.S(8), this.S(24), this.S(10)),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // Dil: geçerli dilin kendi dilindeki adı; tıklayınca seçim menüsü (sade, göze batmayan)
        string current = Languages.Supported.FirstOrDefault(l => l.Name == CultureInfo.CurrentUICulture.Name).NativeName ?? "English";
        var language = new FriendlyButton(current, FriendlyButton.Kind.Quiet) { Font = Theme.Small };
        language.Click += (_, _) => ShowLanguageMenu(language);
        var signature = new Label
        {
            Text = "橋戸", Font = Theme.Signature, ForeColor = Theme.InkSoft, AutoSize = true, UseMnemonic = false,
            Anchor = AnchorStyles.Right, Margin = new Padding(this.S(8), 0, 0, 0), AccessibleRole = AccessibleRole.StaticText,
        };
        footer.Controls.Add(language, 0, 0);
        footer.Controls.Add(signature, 2, 0);

        // Yeni sürüm: sakin bilgi satırı (yalnız bilinen daha yeni bir sürüm varken ve bildirim açıkken)
        _update = new LinkLabel
        {
            Dock = DockStyle.Bottom, AutoSize = true, Visible = false, UseMnemonic = false, BackColor = Theme.Paper,
            ForeColor = Theme.InkSoft, LinkColor = Theme.Link, ActiveLinkColor = Theme.Link, VisitedLinkColor = Theme.Link,
            LinkBehavior = LinkBehavior.HoverUnderline, Font = Theme.Small,
            Padding = new Padding(this.S(24), this.S(6), this.S(24), this.S(4)),
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

    /// <summary>Ayarlar: seçimler Tamam'la uygulanır; dil ya da görünüm değiştiyse kaydedilir ve program BİR kez yeniden başlar.</summary>
    private void ShowSettings()
    {
        using var f = new SettingsForm(_features?.Sent ?? new Apfs.Windows.Reporting.SentLog(
            Path.Combine(Apfs.Windows.Reporting.ReportRecorder.DefaultUserDirectory, "sent.jsonl")));
        if (AppController.Modal(() => f.ShowDialog(this)) != DialogResult.OK) return;
        ApplySettings(f);
    }

    /// <summary>Tamam'dan sonra seçimleri kaydeder (düzenek de pencereyi açmadan bunu çağırır: v160/settings.cs).</summary>
    private void ApplySettings(SettingsForm f)
    {
        if (UserSettings.ReportsConsent != f.Reports)
        {
            UserSettings.ReportsConsent = f.Reports;
            _features?.ConsentChanged();
        }
        UserSettings.UpdateNotify = f.UpdateNotify;
        RefreshUpdateLink();
        bool restart = false;
        if (f.Design != UserSettings.Design || f.ThemeMode != UserSettings.ThemeMode)
        {
            UserSettings.Design = f.Design;
            UserSettings.ThemeMode = f.ThemeMode;
            restart = true;
        }
        if (LanguageSetting.Load()?.Name != f.Language?.Name)
        {
            LanguageSetting.Save(f.Language);
            restart = true;
        }
        if (restart) RestartRequested?.Invoke(); // dil ve görünüm birlikte değişse de tek yeniden başlama
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

    /// <summary>Gizliyse gösterir, simge durumundaysa geri getirir ve öne alır.</summary>
    public void ShowAndActivate()
    {
        var restoreTo = WindowState == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        if (!Visible)
        {
            // Windows 11, çalışan bir programın Show() ile gösterdiği pencereye açılış animasyonu oynatmıyor (öndeyken de;
            // v160/open-anim.cs ölçtü: 0 ms), simge durumundan geri getirmeye ise kendi animasyonunu oynatıyor (~110-180 ms).
            // Kendi animasyonumuz yok: pencere simge durumunda gösterilip hemen geri getirilir, Windows'un geçişi oynar (1.6.0).
            WindowState = FormWindowState.Minimized;
            Show();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = restoreTo;
        Activate();
        BringToFront();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _app.WatchSpace(this, Visible); // boş alan yalnız pencere (ya da panel) açıkken sorulur
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            // İpucu kullanıcı başına bir kez (kalıcı işaret); kayıt yazılamazsa en azından bu oturumda bir kez
            if (!_hintShown && !UserSettings.TrayHintShown)
            {
                UserSettings.TrayHintShown = true;
                HiddenToTray?.Invoke();
            }
            _hintShown = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.Changed -= OnChanged;
            _app.WatchSpace(this, false);
            if (_features is not null) _features.UpdateChanged -= RefreshUpdateLink;
            _languageMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Alt çubuk: üstünde dolgunun içinde ince çizgi.</summary>
    private sealed class FooterPanel : TableLayoutPanel
    {
        public FooterPanel() => DoubleBuffered = true;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Hair, 1);
            e.Graphics.DrawLine(pen, Padding.Left + this.S(6), 0, Width - Padding.Right, 0);
        }
    }
}
