using Apfs.Tray.Ui;
using Apfs.Windows;

namespace Apfs.Tray;

/// <summary>Şifreli birim için parola penceresi. Parola yalnızca hizmete (yerel adlandırılmış kanal) gönderilir, saklanmaz.</summary>
internal sealed class PasswordForm : Form
{
    private readonly TextBox _password;
    private readonly Label _error;

    public PasswordForm(string volumeName, string? hint, string? error)
    {
        Text = WinText.U("Password.Title", Branding.ProductName);
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Theme.ApplyFrame(this);
        int width = this.S(360);
        int glyph = this.S(32), indent = glyph + this.S(14);

        Label Line(string t, Font f, Color c, int top = 0, int max = 0) => new()
        {
            Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(max > 0 ? max : width, 0), UseMnemonic = false,
            Margin = new Padding(0, this.S(top), 0, 0),
        };

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Padding = new Padding(this.S(26), this.S(22), this.S(26), this.S(18)),
        };

        // Üst: kilitli kapı işareti, yanında başlık ve açıklama
        var top = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new StatusGlyph(Glyphs.State.Locked, glyph) { Margin = new Padding(0, this.S(2), this.S(14), 0), Anchor = AnchorStyles.Top }, 0, 0);
        var heads = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        heads.Controls.Add(Line(WinText.U("Password.Locked", volumeName), Theme.Heading(15.75f), Theme.Ink, max: width - indent));
        heads.Controls.Add(Line(WinText.U("Password.Prompt"), Theme.Body, Theme.InkSoft, 4, width - indent));
        top.Controls.Add(heads, 1, 0);
        layout.Controls.Add(top);
        if (!string.IsNullOrEmpty(hint))
            layout.Controls.Add(Line(WinText.U("Password.Hint", hint), Theme.Small, Theme.InkSoft, 16));

        // Parola: alt çizgili alan, sağda göster/gizle
        var field = new UnderlinedField { Width = width, MinimumSize = new Size(width, 0), MaximumSize = new Size(width, 0), Margin = new Padding(0, this.S(string.IsNullOrEmpty(hint) ? 16 : 4), 0, 0) };
        _password = new TextBox
        {
            UseSystemPasswordChar = true, BorderStyle = BorderStyle.None, Font = Theme.Font(11f), BackColor = Theme.Paper,
            ForeColor = Theme.Ink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, this.S(4), this.S(4), this.S(6)),
        };
        var eye = new FriendlyButton(WinText.U("Password.Show"), FriendlyButton.Kind.Icon, Glyphs.Icon.Eye)
        {
            Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 0, this.S(2)),
        };
        eye.Click += (_, _) =>
        {
            bool show = _password.UseSystemPasswordChar;
            _password.UseSystemPasswordChar = !show;
            eye.IconGlyph = show ? Glyphs.Icon.EyeOff : Glyphs.Icon.Eye;
            eye.Text = show ? WinText.U("Password.Hide") : WinText.U("Password.Show");
            _password.Focus();
            _password.SelectionStart = _password.TextLength;
        };
        field.Controls.Add(_password, 0, 0);
        field.Controls.Add(eye, 1, 0);
        layout.Controls.Add(field);

        _error = Line(error ?? "", Theme.Body, Theme.Shu, 8);
        _error.Visible = error is not null;
        layout.Controls.Add(_error);
        layout.Controls.Add(Line(WinText.U("Password.Reassurance"), Theme.Small, Theme.InkSoft, 10));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = width, Margin = new Padding(0, this.S(18), 0, 0),
        };
        buttons.MinimumSize = new Size(width, 0);
        var ok = new FriendlyButton(WinText.U("Action.Unlock")) { DialogResult = DialogResult.OK };
        var cancel = new FriendlyButton(WinText.U("Action.CancelDialog"), FriendlyButton.Kind.Quiet) { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { Activate(); _password.Focus(); };
    }

    public string Password => _password.Text;

    /// <summary>Kenarlıksız metin kutusu + altında mürekkep çizgi.</summary>
    private sealed class UnderlinedField : TableLayoutPanel
    {
        public UnderlinedField()
        {
            DoubleBuffered = true;
            ColumnCount = 2;
            AutoSize = true;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            float w = Math.Max(1.5f, this.S(3) / 2f);
            using var pen = new Pen(Theme.Ink, w);
            e.Graphics.DrawLine(pen, 0, Height - w / 2, Width, Height - w / 2);
        }
    }
}
