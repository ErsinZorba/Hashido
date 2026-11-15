using Apfs.Windows;

namespace Apfs.Tray.Ui;

/// <summary>
/// Temalı iletişim kutusu (1.6.0): Windows'un görev ve ileti kutuları koyu temaya ve tasarıma uymuyordu; hepsinin
/// yerine bu. Solda ikon (uyarı üçgeni / bilgi dairesi rol renginde, Hakkında'da mühür), sağında başlık (<see cref="HeadingLabel"/>,
/// rakamlar düz), kaydırılan gövde, isteğe bağlı bağlantılar; altta isteğe bağlı dipnot (üstünde ince çizgi) ve sağda düğmeler.
/// <list type="bullet">
/// <item><see cref="Confirm"/>: devam düğmesi tonlu ikincil, "Vazgeç" birincil ve VARSAYILAN — Enter da Esc de Vazgeç
/// (Windows görev kutusundaki varsayılan Vazgeç ve Esc ile kapatma gibi).</item>
/// <item><see cref="Inform"/>: tek "Tamam" (Enter/Esc kapatır).</item>
/// <item><see cref="About"/>: sürüm, açıklama, depo bağlantısı ve WinFsp dipnotu (WinFsp lisansının şartı, kaldırılmaz).</item>
/// </list>
/// Gösterim <see cref="AppController.Modal{T}"/> ile (açıkken tepsi yeniden başlamaz). Sahipsiz kutu ekranın ortasında, en
/// üstte, görev çubuğunda ve gösterilince etkin (<see cref="PasswordForm"/> deseni: arkada kalmasın); sahipli kutu sahibin ortasında.
/// </summary>
internal sealed class MessageDialog : Form
{
    public const string RepoUrl = "https://github.com/ErsinZorba/Hashido";
    public const string WinFspUrl = "https://github.com/winfsp/winfsp";
    public const string WinFspNotice = "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos";

    private readonly FlowLayoutPanel _layout;
    private readonly FlowLayoutPanel _heads;
    private readonly int _width;
    private readonly int _indent;

    private MessageDialog(string? heading, string text, Control glyph)
    {
        Text = Branding.ProductName;
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Theme.ApplyFrame(this);
        _width = this.S(400);
        glyph.Size = new Size(this.S(32), this.S(32));
        _indent = glyph.Width + this.S(14);

        _layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Padding = new Padding(this.S(26), this.S(22), this.S(26), this.S(18)),
        };
        var top = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        glyph.Margin = new Padding(0, this.S(2), this.S(14), 0);
        glyph.Anchor = AnchorStyles.Top;
        top.Controls.Add(glyph, 0, 0);
        _heads = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        if (!string.IsNullOrEmpty(heading)) _heads.Controls.Add(Line(heading, Theme.Heading(15.75f), Theme.Ink, 0, _width - _indent));
        _heads.Controls.Add(Line(text, Theme.Body, Theme.Ink, string.IsNullOrEmpty(heading) ? 6 : 8, _width - _indent));
        top.Controls.Add(_heads, 1, 0);
        _layout.Controls.Add(top);
        Controls.Add(_layout);
    }

    private Label Line(string t, Font f, Color c, int top, int max)
    {
        var l = HeadingLabel.For(f); // başlıkta rakamlar düz
        l.Text = t; l.Font = f; l.ForeColor = c; l.AutoSize = true; l.MaximumSize = new Size(max, 0); l.UseMnemonic = false;
        l.Margin = new Padding(0, this.S(top), 0, 0);
        return l;
    }

    /// <summary>Gövdenin altına bağlantı.</summary>
    private void AddLink(string text, string url)
    {
        var link = ConsentForm.Link(this, text, url, _width - _indent);
        link.Margin = new Padding(0, this.S(8), 0, 0);
        _heads.Controls.Add(link);
    }

    /// <summary>Dipnot: ince çizginin altında küçük soluk metin (+ bağlantı).</summary>
    private void AddFootnote(string text, string? linkText = null, string? url = null)
    {
        _layout.Controls.Add(new Rule { Width = _width, Height = Math.Max(1, this.S(1)), Margin = new Padding(0, this.S(18), 0, 0) });
        _layout.Controls.Add(Line(text, Theme.Small, Theme.InkSoft, 10, _width));
        if (linkText is null || url is null) return;
        var link = ConsentForm.Link(this, linkText, url, _width);
        link.Font = Theme.Small;
        link.Margin = new Padding(0, this.S(2), 0, 0);
        _layout.Controls.Add(link);
    }

    /// <summary>Düğmeler sağa yaslı, verilen sırayla soldan sağa; her biri kendi sonucuyla kapatır.</summary>
    private void AddButtons(params (FriendlyButton Button, DialogResult Result)[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = _width, MinimumSize = new Size(_width, 0),
            Margin = new Padding(0, this.S(18), 0, 0),
        };
        foreach (var (button, result) in buttons.Reverse())
        {
            button.DialogResult = result;
            button.Margin = new Padding(this.S(8), 0, 0, 0);
            button.Click += (_, _) => { DialogResult = result; Close(); };
            row.Controls.Add(button);
        }
        _layout.Controls.Add(row);
    }

    // ---- Kurulum (düzenekler de bunları kullanır: kutuyu göstermeden kurar) ----

    /// <summary>Onay kutusu: devam (tonlu ikincil) ve Vazgeç (birincil, varsayılan; Enter ve Esc).</summary>
    internal static MessageDialog ForConfirm(string heading, string text, string proceed, FriendlyButton.Tone tone)
    {
        var d = new MessageDialog(heading, text, new IconView(Glyphs.Icon.Warning, tone == FriendlyButton.Tone.Caution ? Theme.Caution : Theme.Alert));
        var go = new FriendlyButton(proceed, FriendlyButton.Kind.Secondary) { ButtonTone = tone };
        var cancel = new FriendlyButton(WinText.U("Action.Cancel"));
        d.AddButtons((go, DialogResult.OK), (cancel, DialogResult.Cancel));
        d.AcceptButton = cancel;
        d.CancelButton = cancel;
        d.ActiveControl = cancel;
        return d;
    }

    /// <summary>Bilgi/uyarı kutusu: tek "Tamam". Uyarı ikonu <see cref="Theme.Alert"/>, bilgi ikonu mürekkep.</summary>
    internal static MessageDialog ForInform(string? heading, string text, Glyphs.Icon icon)
    {
        var d = new MessageDialog(heading, text, new IconView(icon, icon == Glyphs.Icon.Warning ? Theme.Alert : Theme.Ink));
        d.AddOk();
        return d;
    }

    /// <summary>Hakkında: mühür, "Hashido &lt;sürüm&gt;", açıklama, depo bağlantısı, WinFsp dipnotu ve bağlantısı.</summary>
    internal static MessageDialog ForAbout(string version)
    {
        var d = new MessageDialog($"{Branding.ProductName} {version}", WinText.U("About.Text"), new MarkView(true, 0)); // boyut kurucuda (32 px)
        d.AddLink("github.com/ErsinZorba/Hashido", RepoUrl);
        d.AddFootnote(WinFspNotice, "github.com/winfsp/winfsp", WinFspUrl);
        d.AddOk();
        return d;
    }

    private void AddOk()
    {
        var ok = new FriendlyButton(WinText.U("Action.OK"));
        AddButtons((ok, DialogResult.OK));
        AcceptButton = ok;
        CancelButton = ok;
        ActiveControl = ok;
    }

    // ---- Gösterim ----

    /// <returns>Kullanıcı devam düğmesine bastıysa true (Vazgeç, Esc, Enter ya da kapatma: false).</returns>
    public static bool Confirm(IWin32Window? owner, string heading, string text, string proceed, FriendlyButton.Tone tone)
    {
        using var d = ForConfirm(heading, text, proceed, tone);
        return d.Run(owner) == DialogResult.OK;
    }

    public static void Inform(IWin32Window? owner, string? heading, string text, Glyphs.Icon icon)
    {
        using var d = ForInform(heading, text, icon);
        d.Run(owner);
    }

    public static void About(IWin32Window? owner, string version)
    {
        using var d = ForAbout(version);
        d.Run(owner);
    }

    private DialogResult Run(IWin32Window? owner)
    {
        if (owner is null)
        {
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            Shown += (_, _) => Activate();
        }
        return AppController.Modal(() => ShowingForTest?.Invoke(this) is { } result ? result : owner is null ? ShowDialog() : ShowDialog(owner));
    }

    /// <summary>
    /// Yalnız düzenek kancası (v160/shots.cs): kutu gösterilmeden hemen önce, açık-pencere sayacı artmışken çağrılır; sonuç
    /// dönerse kutu gösterilmez, o sonuç döner. Gerçek ShowDialog ekran dışında tutulamıyor (Windows kutuyu ekrana çekip
    /// etkinleştiriyor: kullanıcının odağını alıyor). Programda hep null.
    /// </summary>
    internal static Func<MessageDialog, DialogResult?>? ShowingForTest { get; set; }

    /// <summary>Rol rengindeki çizgi ikonu (32 px).</summary>
    private sealed class IconView : Control
    {
        private readonly Glyphs.Icon _icon;
        private readonly Color _color;

        public IconView(Glyphs.Icon icon, Color color)
        {
            _icon = icon;
            _color = color;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
        }

        protected override void OnPaint(PaintEventArgs e) => Glyphs.DrawIcon(e.Graphics, new RectangleF(0, 0, Width, Height), _icon, _color);
    }

    /// <summary>Dipnotun üstündeki ince çizgi.</summary>
    private sealed class Rule : Control
    {
        public Rule() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Theme.Hair);
    }
}
