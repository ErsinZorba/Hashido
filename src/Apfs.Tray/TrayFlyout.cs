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
    private int _targetTop, _targetLeft;
    private long _hiddenAt;
    private Screen? _screen; // panelin açılacağı (tıklanan simgenin) ekranı
    private bool _armed;     // ekran dışında kurulu, yerine gelmeyi bekliyor

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
        header.Controls.Add(new HeadingLabel
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
        if (Theme.IsWindows)
        {
            // Windows 11 tasarımı: kenarlığı Windows çizer (DWMWA_BORDER_COLOR = Hair; COLORREF 0x00BBGGRR), içte ikinci çizgi yok
            int border = ColorTranslator.ToWin32(Theme.Hair);
            _ = DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        if (Theme.IsWindows) return; // kenarlık DWM'de (OnHandleCreated)
        using var pen = new Pen(Theme.Hair);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>
    /// Simgeye tek tıklanınca, tıklama anında (Windows öne gelme hakkını yalnız bu anda verir): açıksa kapatır ve false döner;
    /// kapalıysa listeyi kurar, hedef konumu imleçten hesaplar ve paneli ekran DIŞINDA gösterip etkinleştirir, true döner —
    /// çağıran kısa bekleme (<see cref="TrayApp.RevealDelay"/>) dolunca <see cref="Reveal"/> der. Bekleme sırasında odak
    /// giderse panel kendiliğinden gizlenir.
    /// </summary>
    public bool Arm()
    {
        if (Visible) { Hide(); return false; }
        // Panel açıkken simgeye tıklamak önce paneli kapatır (odak kaybı); aynı tık yeniden açmasın.
        if (Environment.TickCount64 - _hiddenAt < 300) return false;
        var cursor = Cursor.Position;
        _screen = Screen.FromPoint(cursor); // ekran dışındayken Screen.FromControl başka monitörü verebilir
        _list.Rebuild();
        FitHeight();
        var wa = _screen.WorkingArea;
        int gap = this.S(12);
        _targetLeft = Math.Clamp(cursor.X - Width / 2, wa.Left + gap, wa.Right - Width - gap);
        _targetTop = Math.Clamp(cursor.Y - Height - gap, wa.Top + gap, wa.Bottom - Height - gap);
        // Bekleme yeri tüm ekranların sağında: boy değişip Top kaysa da hiçbir ekrana girmez
        var virt = SystemInformation.VirtualScreen;
        Left = virt.Right + this.S(200);
        Top = virt.Top;
        Opacity = 0;
        Show();
        Activate();
        _armed = true;
        return true;
    }

    /// <summary>Bekleme doldu: panel hâlâ kurulu ve açıksa (odak gitmediyse) yerine gelip yukarı kayarak belirir.</summary>
    public void Reveal()
    {
        if (!_armed || !Visible) return;
        _armed = false;
        Left = _targetLeft;
        Top = _targetTop + this.S(10);
        _fade.Start();
    }

    /// <summary>Çift tık: kurulu panel hiç görünmeden gizlenir.</summary>
    public void Cancel()
    {
        _armed = false;
        if (Visible) Hide();
    }

    private void FitHeight()
    {
        int chrome = Controls.Cast<Control>().Where(c => c != _list).Sum(c => c.Height) + Padding.Vertical;
        int wanted = chrome + _list.Padding.Vertical + _list.ContentHeight;
        int max = (int)((_screen ?? Screen.FromControl(this)).WorkingArea.Height * 0.8);
        int old = Height;
        Height = Math.Clamp(wanted, this.S(200), max);
        if (Visible) Top -= Height - old; // alt kenar sabit kalsın
        _targetTop -= Height - old;
    }

    private void FadeStep()
    {
        Opacity = Math.Min(1, Opacity + 0.25); // ~4 adımda (1.6.0: 0,15 ile ~7 adım kullanıcıya ağır geldi)
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
            _armed = false;
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
