using System.Diagnostics;
using System.Runtime.InteropServices;
using Apfs.Tray.Ui;
using Apfs.Windows;
using Apfs.Windows.Reporting;

namespace Apfs.Tray;

/// <summary>
/// İlk açılışta bir kez sorulan raporlama onayı. Ne gönderildiği ve neyin asla gönderilmediği listelenir. "Evet" ile "Hayır"
/// AYNI boyda ve aynı görünüşte; "Evet" yalnız varsayılan odaktır (kullanıcı kararı, 2026-11-07). Pencere kapatılırsa
/// (Esc, X) cevap verilmemiş sayılır: hiçbir şey toplanmaz, bir sonraki açılışta yine sorulur.
/// </summary>
internal sealed class ConsentForm : Form
{
    private readonly FriendlyButton _yes;

    /// <summary>true: evet, false: hayır, null: cevaplanmadı (kapatıldı).</summary>
    public bool? Answer { get; private set; }

    public ConsentForm()
    {
        Text = Branding.ProductName;
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Background;
        Font = Theme.Body;
        KeyPreview = true;
        int width = this.S(440);

        Label Line(string t, Font f, Color c, int top = 0) => new()
        {
            Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(width, 0), UseMnemonic = false,
            Margin = new Padding(0, this.S(top), 0, 0),
        };

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Padding = new Padding(this.S(22), this.S(18), this.S(22), this.S(16)),
        };
        layout.Controls.Add(Line(WinText.U("Consent.Heading", Branding.ProductName), Theme.Font(12f, FontStyle.Bold), Theme.Text));
        layout.Controls.Add(Line(WinText.U("Consent.Text", Branding.ProductName), Theme.Body, Theme.Text, 8));
        layout.Controls.Add(Line(WinText.U("Consent.SentHeading"), Theme.Bold, Theme.Text, 12));
        layout.Controls.Add(Line(WinText.U("Consent.SentList"), Theme.Body, Theme.Muted, 4));
        layout.Controls.Add(Line(WinText.U("Consent.NeverHeading"), Theme.Bold, Theme.Text, 10));
        layout.Controls.Add(Line(WinText.U("Consent.NeverList"), Theme.Body, Theme.Muted, 4));
        layout.Controls.Add(PrivacyLink(this, width));

        // İki düğme eşit genişlikte iki sütunda
        var buttons = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 1, AutoSize = true, Width = width, Margin = new Padding(0, this.S(16), 0, 0),
            MinimumSize = new Size(width, 0),
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _yes = new FriendlyButton(WinText.U("Consent.Yes"), FriendlyButton.Kind.Secondary) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, this.S(6), 0) };
        var no = new FriendlyButton(WinText.U("Consent.No"), FriendlyButton.Kind.Secondary) { Dock = DockStyle.Fill, Margin = new Padding(this.S(6), 0, 0, 0) };
        _yes.Click += (_, _) => { Answer = true; DialogResult = DialogResult.OK; Close(); };
        no.Click += (_, _) => { Answer = false; DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(_yes, 0, 0);
        buttons.Controls.Add(no, 1, 0);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = _yes;
        ActiveControl = _yes;
    }

    /// <summary>Gizlilik açıklaması bağlantısı (ayarlar penceresi de kullanır).</summary>
    internal static LinkLabel PrivacyLink(Control owner, int width) => Link(owner, WinText.U("Consent.Privacy"), ReportClient.PrivacyUrl, width);

    internal static LinkLabel Link(Control owner, string text, string url, int width)
    {
        var link = new LinkLabel
        {
            Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(0, owner.S(10), 0, 0),
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentPressed, UseMnemonic = false,
        };
        link.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return link;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _yes.Focus();
        // Odak çerçevesi fareyle açılan pencerede de görünsün: varsayılanın "Evet" olduğu anlaşılsın
        SendMessage(Handle, WmChangeUiState, (IntPtr)((UisfHideFocus << 16) | UisClear), IntPtr.Zero);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Answer = null; DialogResult = DialogResult.Cancel; Close(); return; }
        base.OnKeyDown(e);
    }

    private const int WmChangeUiState = 0x0127, UisClear = 2, UisfHideFocus = 0x1;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
}
