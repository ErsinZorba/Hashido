using System.Runtime.InteropServices;
using Apfs.Tray.Ui;
using Apfs.Windows;

namespace Apfs.Tray;

/// <summary>
/// Tepsi simgesine sol tıklayınca saatin üstünde açılan küçük panel: başlıkta mühür, "Yeniden tara" ve "Ana pencereyi aç";
/// diskler, "Aç" / "Kilidi aç" / "Güvenle çıkar". Başka bir yere tıklanınca kapanır.
/// </summary>
internal sealed class TrayFlyout : Form
{
    private readonly AppController _app;
    private readonly DiskListView _list;
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private int _targetTop;
    private long _hiddenAt;

    public TrayFlyout(AppController app, Action openMain)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Paper;
        ForeColor = Theme.Ink;
        Font = Theme.Body;
        Theme.ApplyFrame(this); // Windows 11'in yuvarlak köşe kenarlığı da koyu olsun
        Width = this.S(360);
        Padding = new Padding(1); // ince kenarlık (Windows 10'da köşe yuvarlatma yok)

        // Başlık: mühür, "Hashido", sağda yeniden tara ve ana pencere (simge düğmeleri; metinleri araç ipucu)
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, BackColor = Theme.Paper,
            Padding = new Padding(this.S(16), this.S(10), this.S(8), this.S(4)),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new MarkView(seal: true, this.S(20)) { Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, this.S(10), 0) }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = Branding.ProductName, Font = Theme.Heading(12f), ForeColor = Theme.Ink, AutoSize = true, UseMnemonic = false,
            Anchor = AnchorStyles.Left, Margin = Padding.Empty,
        }, 1, 0);
        var rescan = new FriendlyButton(WinText.U("Action.Rescan"), FriendlyButton.Kind.Icon, Glyphs.Icon.Refresh)
        {
            BusyText = WinText.U("Action.Rescanning"), Anchor = AnchorStyles.Right, Margin = Padding.Empty,
        };
        rescan.Click += async (_, _) => await _app.RescanAsync();
        var main = new FriendlyButton(WinText.U("Action.OpenMain"), FriendlyButton.Kind.Icon, Glyphs.Icon.Out)
        {
            Anchor = AnchorStyles.Right, Margin = Padding.Empty,
        };
        main.Click += (_, _) => { Hide(); openMain(); };
        header.Controls.Add(rescan, 2, 0);
        header.Controls.Add(main, 3, 0);

        _list = new DiskListView(app, compact: true)
        {
            Dock = DockStyle.Fill, Padding = new Padding(this.S(16), this.S(4), this.S(16), this.S(6)),
        };

        Controls.Add(_list);
        Controls.Add(header);

        _app.Changed += () =>
        {
            rescan.Busy = _app.Rescanning;
            _list.Rebuild();
            if (Visible) FitHeight();
        };
        // Doluluk satırı gelince/gidince satır boyu değişir (liste kendini yerinde günceller; bu abonelik ondan sonra çalışır)
        _app.SpaceChanged += () => { if (Visible) FitHeight(); };
        _fade.Tick += (_, _) => FadeStep();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND: Windows 11'de yuvarlak köşe (Windows 10'da etkisiz)
        _ = DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        using var pen = new Pen(Theme.Hair);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>Simgeye tıklanınca: açıksa kapat, kapalıysa imlecin yanında (görev çubuğunun hemen üstünde) aç.</summary>
    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        // Panel açıkken simgeye tıklamak önce paneli kapatır (odak kaybı); aynı tık yeniden açmasın.
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        _list.Rebuild();
        FitHeight();
        var cursor = Cursor.Position;
        var wa = Screen.FromPoint(cursor).WorkingArea;
        int gap = this.S(12);
        Left = Math.Clamp(cursor.X - Width / 2, wa.Left + gap, wa.Right - Width - gap);
        _targetTop = Math.Clamp(cursor.Y - Height - gap, wa.Top + gap, wa.Bottom - Height - gap);
        Top = _targetTop + this.S(10);
        Opacity = 0;
        Show();
        Activate();
        _fade.Start();
    }

    private void FitHeight()
    {
        int chrome = Controls.Cast<Control>().Where(c => c != _list).Sum(c => c.Height) + Padding.Vertical;
        int wanted = chrome + _list.Padding.Vertical + _list.ContentHeight;
        int max = (int)(Screen.FromControl(this).WorkingArea.Height * 0.8);
        int old = Height;
        Height = Math.Clamp(wanted, this.S(200), max);
        if (Visible) Top -= Height - old; // alt kenar sabit kalsın
        _targetTop -= Height - old;
    }

    private void FadeStep()
    {
        Opacity = Math.Min(1, Opacity + 0.15);
        int step = Math.Max(1, (Top - _targetTop + 1) / 2);
        Top = Math.Max(_targetTop, Top - step);
        if (Opacity >= 1 && Top == _targetTop) _fade.Stop();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _app.WatchSpace(this, Visible); // boş alan yalnız panel (ya da ana pencere) açıkken sorulur
        if (!Visible)
        {
            _hiddenAt = Environment.TickCount64;
            _fade.Stop();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fade.Dispose();
        base.Dispose(disposing);
    }
}
