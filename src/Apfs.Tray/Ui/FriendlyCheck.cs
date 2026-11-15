using System.Drawing.Drawing2D;

namespace Apfs.Tray.Ui;

/// <summary>
/// Kendi çizen onay kutusu (橋戸 marka; iki temada aynı): yuvarlak köşeli kutu mürekkep çizgi, işaretliyken mürekkep dolgu
/// ve ortasında kâğıt renginde işaret; üzerine gelince ve işaretlenince renk yumuşakça değişir; odakta 2 px zinober halka.
/// Metin kaydırılır, kutu ilk satıra ortalanır. <see cref="CheckBox"/>'tan türer: klavye (Boşluk), erişilebilirlik
/// (CheckButton + durum), <see cref="CheckBox.Checked"/> aynen; yalnız çizim bizim.
/// </summary>
internal sealed class FriendlyCheck : CheckBox
{
    private static readonly System.Windows.Forms.Timer Ticker = new() { Interval = 15 };
    private static readonly HashSet<FriendlyCheck> Animating = [];
    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;

    private float _hover; // 0 → 1
    private float _on;    // 0 → 1 (işaret)
    private bool _mouseOver;

    static FriendlyCheck() => Ticker.Tick += (_, _) =>
    {
        foreach (var c in Animating.ToList()) c.Step();
        if (Animating.Count == 0) Ticker.Stop();
    };

    public FriendlyCheck(string text, bool value)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        Text = text;
        Checked = value;
        _on = value ? 1 : 0;
        AutoSize = false;
        UseMnemonic = false;
        Font = Theme.Body;
        ForeColor = Theme.Ink;
        Cursor = Cursors.Hand;
    }

    private int Box => this.S(18);
    private int Ring => this.S(3);
    private int TextLeft => Ring * 2 + Box + this.S(8);

    /// <summary>Tek satırın yüksekliği (kutu bu satıra ortalanır).</summary>
    private int LineHeight => TextRenderer.MeasureText("Ag", Font, Size.Empty, Flags).Height;

    /// <summary>Kutunun dikey merkezi: kutu (halkasıyla) ya da ilk satır, hangisi yüksekse.</summary>
    private float BoxCenter => Math.Max(Ring + Box / 2f, LineHeight / 2f);

    private int TextTop => (int)Math.Round(BoxCenter - LineHeight / 2f);

    /// <summary>
    /// Verilen genişlikte (0 = sınırsız) gereken boyut; metin çizimle aynı bayraklarla, aynı genişlikte ölçülür (çizim ile
    /// yükseklik hesabı ayrışmasın — uzun dillerde son satır kesilmesin).
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : int.MaxValue;
        var t = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, width - TextLeft), 0), Flags);
        int height = Math.Max((int)Math.Ceiling(BoxCenter + Box / 2f + Ring), TextTop + t.Height + this.S(2));
        return new Size(proposedSize.Width > 0 ? proposedSize.Width : TextLeft + t.Width, height);
    }

    // ---- Animasyon ----

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _mouseOver = true; StartAnimation(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _mouseOver = false; StartAnimation(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); StartAnimation(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); StartAnimation(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private void StartAnimation()
    {
        if (IsDisposed) return;
        Animating.Add(this);
        Ticker.Start();
    }

    private void Step()
    {
        if (IsDisposed) { Animating.Remove(this); return; }
        _hover = Approach(_hover, _mouseOver && Enabled ? 1 : 0);
        _on = Approach(_on, Checked ? 1 : 0);
        if (_hover == (_mouseOver && Enabled ? 1 : 0) && _on == (Checked ? 1 : 0)) Animating.Remove(this);
        Invalidate();
    }

    private static float Approach(float value, float target)
    {
        value += Math.Sign(target - value) * 0.125f; // ~120 ms
        return Math.Abs(target - value) < 0.125f ? target : value;
    }

    protected override void Dispose(bool disposing)
    {
        Animating.Remove(this);
        base.Dispose(disposing);
    }

    // ---- Çizim ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var bg = BackgroundColor();
        g.Clear(bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float scale = DeviceDpi / 96f;
        float line = Math.Max(1f, 1.25f * scale);
        var box = new RectangleF(Ring, BoxCenter - Box / 2f, Box, Box);
        float radius = this.S(4);

        Color off = Theme.Lerp(bg, Theme.Track, 0.7f * _hover);
        Color on = Theme.Lerp(Theme.Ink, bg, 0.12f * _hover);
        Color fill = Theme.Lerp(off, on, _on);
        Color border = Theme.Lerp(Theme.Lerp(Theme.InkSoft, Theme.Ink, _hover), on, _on);
        Color text = Theme.Ink;
        if (!Enabled)
        {
            fill = Theme.Lerp(fill, bg, 0.5f);
            border = Theme.Lerp(border, bg, 0.5f);
            text = Theme.Lerp(Theme.InkSoft, bg, 0.35f);
        }

        var inner = RectangleF.Inflate(box, -line / 2, -line / 2);
        using (var path = Theme.RoundedRect(inner, radius - line / 2))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            using var pen = new Pen(border, line);
            g.DrawPath(pen, path);
        }

        if (_on > 0)
        {
            // İşaret (16'lık ızgara; x 4–12 ortada, y 4,875–10,625: alttaki dönüm noktası üstteki yuvarlak uçtan dolgun görünür, 0,25 yukarı alınınca göze ortalı)
            float k = Box / 16f;
            PointF P(float x, float y) => new(box.X + x * k, box.Y + y * k);
            using var pen = new Pen(Theme.Lerp(fill, Theme.OnInk, _on), 2f * k)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
            };
            g.DrawLines(pen, [P(4, 7.875f), P(6.75f, 10.625f), P(12, 4.875f)]);
        }

        if (Focused && ShowFocusCues)
        {
            float w = this.S(2);
            var ring = RectangleF.Inflate(box, Ring - w / 2, Ring - w / 2);
            using var path = Theme.RoundedRect(ring, radius + Ring - w / 2);
            using var pen = new Pen(Theme.Shu, w);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(TextLeft, TextTop, Math.Max(1, Width - TextLeft), Height - TextTop),
            text, bg, Flags);
    }

    /// <summary>Kutunun arkasındaki zemin: saydam üst denetimlerde ilk opak ataya kadar çıkılır.</summary>
    private Color BackgroundColor()
    {
        for (Control? c = Parent; c is not null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return Theme.Paper;
    }
}
