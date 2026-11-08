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
/// Ayarlar: arayüz dili, otomatik raporlar (+ "Ne gönderiliyor?", "Gönderilenleri göster"), yeni sürüm bildirimi. Tamam'a
/// basılınca seçimler özelliklerden okunur ve çağıran uygular (dil değiştiyse program yeniden başlar).
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly ComboBox _language;
    private readonly CheckBox _reports;
    private readonly CheckBox _updates;
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
        BackColor = Theme.Background;
        Font = Theme.Body;
        int width = this.S(400);

        Label Line(string t, Font f, Color c, int top = 0) => new()
        {
            Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(width, 0), UseMnemonic = false,
            Margin = new Padding(0, this.S(top), 0, 0),
        };
        // Onay kutusu metni kaydırılır (AutoSize'lı CheckBox tek satırda kalıp kesiliyordu; uzun diller için şart)
        CheckBox Check(string t, bool value)
        {
            var text = TextRenderer.MeasureText(t, Theme.Body, new Size(width - this.S(24), 0), TextFormatFlags.WordBreak);
            return new CheckBox
            {
                Text = t, Checked = value, AutoSize = false, Width = width, Height = Math.Max(this.S(24), text.Height + this.S(6)),
                CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft, UseMnemonic = false, Font = Theme.Body,
                Margin = new Padding(0, this.S(6), 0, 0), ForeColor = Theme.Text,
            };
        }

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Padding = new Padding(this.S(20), this.S(16), this.S(20), this.S(14)),
        };

        // Dil
        layout.Controls.Add(Line(WinText.U("Settings.Language"), Theme.Bold, Theme.Text));
        _language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = this.S(240), Margin = new Padding(0, this.S(6), 0, 0) };
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
        layout.Controls.Add(Line(WinText.U("Settings.LanguageNote"), Theme.Small, Theme.Muted, 4));

        // Raporlar
        layout.Controls.Add(Line(WinText.U("Settings.ReportsHeading"), Theme.Bold, Theme.Text, 16));
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
        layout.Controls.Add(Line(WinText.U("Settings.UpdatesHeading"), Theme.Bold, Theme.Text, 16));
        _updates = Check(WinText.U("Settings.UpdateNotify"), UserSettings.UpdateNotify);
        layout.Controls.Add(_updates);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = width, MinimumSize = new Size(width, 0),
            Margin = new Padding(0, this.S(18), 0, 0),
        };
        var ok = new FriendlyButton(WinText.U("Action.OK")) { DialogResult = DialogResult.OK };
        var cancel = new FriendlyButton(WinText.U("Action.CancelDialog"), FriendlyButton.Kind.Secondary) { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
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
        BackColor = Theme.Background;
        Font = Theme.Body;
        MinimizeBox = false;
        ClientSize = new Size(this.S(560), this.S(480));

        var lines = sent.Read();
        var intro = new Label
        {
            Text = lines.Count == 0 ? WinText.U("Sent.None") : WinText.U("Sent.Intro"), Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false,
            Padding = new Padding(this.S(16), this.S(14), this.S(16), this.S(8)), ForeColor = Theme.Text,
            MaximumSize = new Size(this.S(560), 0),
        };
        var pretty = new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9f), BackColor = Color.White, BorderStyle = BorderStyle.None,
            Text = string.Join("\r\n\r\n", Enumerable.Reverse(lines).Select(l =>
            {
                try { return JsonNode.Parse(l)!.ToJsonString(pretty).Replace("\n", "\r\n"); } catch (JsonException) { return l; }
            })),
        };
        var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(this.S(16), 0, this.S(16), 0), BackColor = Theme.Background };
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
