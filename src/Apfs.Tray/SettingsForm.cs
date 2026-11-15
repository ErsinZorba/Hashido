using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apfs.Localization;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Localization;
using Apfs.Windows.Reporting;

namespace Apfs.Tray;

/// <summary>
/// Ayarlar: arayüz dili, otomatik raporlar (+ "Ne gönderiliyor?", "Gönderilenleri göster"), yeni sürüm bildirimi, Hakkında
/// (sürüm → Hakkında penceresi, günlüğü aç). Tamam'a
/// basılınca seçimler özelliklerden okunur ve çağıran uygular (dil değiştiyse program yeniden başlar).
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly ComboBox _language;
    private readonly FriendlyCheck _reports;
    private readonly FriendlyCheck _updates;
    private readonly List<CultureInfo?> _cultures = [];

    public SettingsForm(SentLog sent)
    {
        Text = WinText.U("Settings.Title", Branding.ProductName);
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Theme.ApplyFrame(this);
        int width = this.S(400);

        Label Line(string t, Font f, Color c, int top = 0) => new()
        {
            Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(width, 0), UseMnemonic = false,
            Margin = new Padding(0, this.S(top), 0, 0),
        };
        // Onay kutusu metni kaydırılır (uzun diller için şart); yükseklik kontrolün kendi ölçümünden (çizimle aynı hesap)
        FriendlyCheck Check(string t, bool value)
        {
            var check = new FriendlyCheck(t, value) { Margin = new Padding(0, this.S(6), 0, 0) };
            check.Size = check.GetPreferredSize(new Size(width, 0));
            return check;
        }

        // Sessiz düğme yazısını kendi sol kenarından ~9 px (odak payı 3 + yatay dolgu 6), etiket ~3 px içeriden yazar. Hakkında
        // satırının yazıları başlıklarla aynı hizada başlasın diye sol dolgu farkı (6) kadar az, öbür her öğe o kadar sağda
        // (WinForms negatif kenar boşluğunu sıfıra yuvarlıyor); sonuçta öbür öğeler eski yerinde. Ölçüm: localization-tool/v151.
        int quiet = this.S(6);
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Padding = new Padding(this.S(26) - quiet, this.S(20), this.S(26), this.S(18)),
        };

        // Dil
        layout.Controls.Add(Line(WinText.U("Settings.Language"), Theme.Heading(12f), Theme.Ink));
        _language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = this.S(240), Margin = new Padding(0, this.S(6), 0, 0) };
        Theme.Style(_language);
        var saved = LanguageSetting.Load();
        _cultures.Add(null);
        _language.Items.Add(WinText.U("Language.Windows"));
        foreach (var (name, native) in Languages.Supported)
        {
            _cultures.Add(CultureInfo.GetCultureInfo(name));
            _language.Items.Add(native);
        }
        _language.SelectedIndex = Math.Max(0, _cultures.FindIndex(c => c?.Name == saved?.Name));
        layout.Controls.Add(_language);
        layout.Controls.Add(Line(WinText.U("Settings.LanguageNote"), Theme.Small, Theme.InkSoft, 4));

        // Raporlar
        layout.Controls.Add(Line(WinText.U("Settings.ReportsHeading"), Theme.Heading(12f), Theme.Ink, 20));
        _reports = Check(WinText.U("Settings.Reports"), UserSettings.ReportsConsent == true);
        layout.Controls.Add(_reports);
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, MaximumSize = new Size(width, 0), Margin = Padding.Empty };
        var what = ConsentForm.PrivacyLink(this, width);
        what.Text = WinText.U("Settings.WhatIsSent");
        what.Margin = new Padding(0, this.S(10), this.S(12), 0);
        row.Controls.Add(what);
        var show = new FriendlyButton(WinText.U("Settings.ShowSent"), FriendlyButton.Kind.Quiet) { Margin = new Padding(0, this.S(4), 0, 0) };
        show.Click += (_, _) => { using var f = new SentReportsForm(sent); f.ShowDialog(this); };
        row.Controls.Add(show);
        layout.Controls.Add(row);

        // Güncellemeler
        layout.Controls.Add(Line(WinText.U("Settings.UpdatesHeading"), Theme.Heading(12f), Theme.Ink, 20));
        _updates = Check(WinText.U("Settings.UpdateNotify"), UserSettings.UpdateNotify);
        layout.Controls.Add(_updates);

        // Hakkında: sürüm (Hakkında penceresi; WinFsp notu orada) ve günlük.
        layout.Controls.Add(Line(WinText.U("Settings.AboutHeading"), Theme.Heading(12f), Theme.Ink, 20));
        string version = Application.ProductVersion.Split('+')[0];
        var about = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, MaximumSize = new Size(width + quiet, 0),
            Margin = new Padding(0, this.S(4), 0, 0),
        };
        var versionButton = new FriendlyButton(WinText.U("Main.Version", version), FriendlyButton.Kind.Quiet) { Margin = new Padding(0, 0, this.S(4), 0) };
        versionButton.Click += (_, _) => ShowAbout(version);
        var log = new FriendlyButton(WinText.U("Action.OpenLog"), FriendlyButton.Kind.Quiet) { Margin = Padding.Empty };
        log.Click += (_, _) => AppController.OpenLog();
        about.Controls.Add(versionButton);
        about.Controls.Add(log);
        layout.Controls.Add(about);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = width, MinimumSize = new Size(width, 0),
            Margin = new Padding(0, this.S(18), 0, 0),
        };
        var ok = new FriendlyButton(WinText.U("Action.OK")) { DialogResult = DialogResult.OK };
        var cancel = new FriendlyButton(WinText.U("Action.CancelDialog"), FriendlyButton.Kind.Quiet) { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        foreach (Control c in layout.Controls)
            if (c != about) c.Margin = new Padding(c.Margin.Left + quiet, c.Margin.Top, c.Margin.Right, c.Margin.Bottom);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>
    /// Hakkında penceresi. WinFsp lisansı (FLOSS istisnası) WinFsp telif notunun ve depo bağlantısının arayüzde bulunmasını
    /// şart koşar; not burada durur (1.5.1'e kadar ana pencerenin sürüm düğmesindeydi).
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
        AppController.Modal(() => TaskDialog.ShowDialog(this, page));
    }

    /// <summary>Seçilen dil (null = Windows dili).</summary>
    public CultureInfo? Language => _cultures[Math.Max(0, _language.SelectedIndex)];

    public bool Reports => _reports.Checked;

    public bool UpdateNotify => _updates.Checked;
}

/// <summary>Gönderilen son raporlar, gönderildiği biçimiyle (en yenisi başta). Salt okunur.</summary>
internal sealed class SentReportsForm : Form
{
    public SentReportsForm(SentLog sent)
    {
        Text = WinText.U("Sent.Title");
        Icon = Theme.AppIcon(32);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Theme.ApplyFrame(this);
        MinimizeBox = false;
        ClientSize = new Size(this.S(560), this.S(480));

        var lines = sent.Read();
        var intro = new Label
        {
            Text = lines.Count == 0 ? WinText.U("Sent.None") : WinText.U("Sent.Intro"), Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false,
            Padding = new Padding(this.S(16), this.S(14), this.S(16), this.S(8)), ForeColor = Theme.Ink,
            MaximumSize = new Size(this.S(560), 0),
        };
        var pretty = new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f), BackColor = Theme.Washi, ForeColor = Theme.Ink, BorderStyle = BorderStyle.None,
            Text = string.Join("\r\n\r\n", Enumerable.Reverse(lines).Select(l =>
            {
                try { return JsonNode.Parse(l)!.ToJsonString(pretty).Replace("\n", "\r\n"); } catch (JsonException) { return l; }
            })),
        };
        Theme.DarkScrollbars(text);
        var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(this.S(16), 0, this.S(16), 0), BackColor = Theme.Paper };
        holder.Controls.Add(text);
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true,
            Padding = new Padding(this.S(12), this.S(8), this.S(12), this.S(12)),
        };
        var close = new FriendlyButton(WinText.U("Action.Close"), FriendlyButton.Kind.Secondary) { DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        footer.Controls.Add(close);
        Controls.Add(holder);
        Controls.Add(footer);
        Controls.Add(intro);
        CancelButton = close;
        ActiveControl = close; // metin kutusu odaklanınca hepsi seçili görünüyordu
        Shown += (_, _) => text.Select(0, 0);
    }
}
