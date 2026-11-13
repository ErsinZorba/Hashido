using System.Drawing.Drawing2D;

namespace Apfs.Tray.Ui;

/// <summary>
/// 橋戸 marka çizimleri (GDI+, verilen dikdörtgene ölçeklenir; DPI'ı çağıran verir). İşaret: kavisli köprü güvertesi + altında
/// kemerli kapı. Durum işaretleri bu kapıdan türer; ikonlar 16'lık ızgarada ince çizgi. Ölçüler marka taslağındaki SVG'lerle aynı.
/// </summary>
internal static class Glyphs
{
    /// <summary>Birim durumu: açık (salt okunur), kilitli (parola bekler), yazılabilir, sorunlu.</summary>
    public enum State { Open, Locked, Writable, Problem }

    /// <summary>Küçük çizgi ikonları.</summary>
    public enum Icon { None, Refresh, Settings, Eject, Eye, EyeOff, Out }

    /// <summary>Marka işareti (48'lik ızgara, çizgi 3,2). <paramref name="dashed"/>: hizmet kapalıyken kesik çizgi.</summary>
    public static void Mark(Graphics g, RectangleF r, Color color, bool dashed = false)
    {
        float k = Math.Min(r.Width, r.Height) / 48f;
        using var pen = new Pen(color, 3.2f * k) { StartCap = LineCap.Flat, EndCap = LineCap.Flat, LineJoin = LineJoin.Miter };
        if (dashed) pen.DashPattern = [6f / 3.2f, 4.8f / 3.2f];
        Stroke(g, pen, Deck(5, 11.5f, 24, 5.5f, 43, 11.5f), r.X, r.Y, k);
        Stroke(g, pen, Arch(12.5f, 35.5f, 26, 42), r.X, r.Y, k);
    }

    /// <summary>Durum işareti (24'lük ızgara, çizgi 1,6). Açık ve kilitli mürekkep, yazılabilir ve sorunlu zinober.</summary>
    public static void Status(Graphics g, RectangleF r, State state)
    {
        float k = Math.Min(r.Width, r.Height) / 24f;
        Color color = state is State.Writable or State.Problem ? Theme.Shu : Theme.Ink;
        using var pen = new Pen(color, 1.6f * k) { StartCap = LineCap.Flat, EndCap = LineCap.Flat, LineJoin = LineJoin.Miter };
        if (state == State.Problem) pen.DashPattern = [3f / 1.6f, 2.4f / 1.6f];
        Stroke(g, pen, Deck(2, 4.6f, 12, 2.2f, 22, 4.6f), r.X, r.Y, k);
        using var arch = Arch(6, 18, 12.5f, 21);
        if (state == State.Locked)
        {
            arch.CloseFigure();
            using var brush = new SolidBrush(color);
            Fill(g, brush, arch, r.X, r.Y, k);
        }
        Stroke(g, pen, arch, r.X, r.Y, k);
        if (state == State.Writable)
        {
            using var brush = new SolidBrush(color);
            using var sill = new GraphicsPath();
            sill.AddRectangle(new RectangleF(9.5f, 16, 5, 5)); // eşik
            Fill(g, brush, sill, r.X, r.Y, k);
        }
    }

    /// <summary>Çizgi ikonu (16'lık ızgara, çizgi 1,3, yuvarlak uçlar).</summary>
    public static void DrawIcon(Graphics g, RectangleF r, Icon icon, Color color)
    {
        if (icon == Icon.None) return;
        float k = Math.Min(r.Width, r.Height) / 16f;
        using var pen = new Pen(color, 1.3f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var p = new GraphicsPath();
        switch (icon)
        {
            case Icon.Refresh:
                p.AddArc(2.5f, 2.5f, 11, 11, 0, 315);
                p.StartFigure();
                p.AddLines([new PointF(13.5f, 2), new PointF(13.5f, 5), new PointF(10.5f, 5)]);
                break;
            case Icon.Settings:
                p.AddEllipse(5.8f, 5.8f, 4.4f, 4.4f);
                (float, float, float, float)[] spokes =
                [
                    (8, 1.5f, 8, 3.5f), (8, 12.5f, 8, 14.5f), (1.5f, 8, 3.5f, 8), (12.5f, 8, 14.5f, 8),
                    (3.4f, 3.4f, 4.8f, 4.8f), (11.2f, 11.2f, 12.6f, 12.6f), (3.4f, 12.6f, 4.8f, 11.2f), (11.2f, 4.8f, 12.6f, 3.4f),
                ];
                foreach (var (x1, y1, x2, y2) in spokes) { p.StartFigure(); p.AddLine(x1, y1, x2, y2); }
                break;
            case Icon.Eject:
                p.AddPolygon([new PointF(8, 3.5f), new PointF(3.5f, 9), new PointF(12.5f, 9)]);
                p.StartFigure();
                p.AddLine(3.5f, 12.5f, 12.5f, 12.5f);
                break;
            case Icon.Eye:
            case Icon.EyeOff:
                p.AddBezier(1.5f, 8, 1.5f, 8, 4, 3.5f, 8, 3.5f);
                p.AddBezier(8, 3.5f, 12, 3.5f, 14.5f, 8, 14.5f, 8);
                p.AddBezier(14.5f, 8, 14.5f, 8, 12, 12.5f, 8, 12.5f);
                p.AddBezier(8, 12.5f, 4, 12.5f, 1.5f, 8, 1.5f, 8);
                p.CloseFigure();
                p.AddEllipse(6, 6, 4, 4);
                if (icon == Icon.EyeOff) { p.StartFigure(); p.AddLine(2.5f, 13.5f, 13.5f, 2.5f); }
                break;
            case Icon.Out:
                p.AddLines([new PointF(9, 2.5f), new PointF(13.5f, 2.5f), new PointF(13.5f, 7)]);
                p.StartFigure();
                p.AddLine(13.5f, 2.5f, 8, 8);
                p.StartFigure();
                p.AddLines([new PointF(11.5f, 9.5f), new PointF(11.5f, 13.5f), new PointF(2.5f, 13.5f), new PointF(2.5f, 4.5f), new PointF(6.5f, 4.5f)]);
                break;
        }
        Stroke(g, pen, p, r.X, r.Y, k);
    }

    /// <summary>Uygulama simgesi (mühür) verilen kareye: gömülü app.ico'nun en yakın boyutu (küçük boyutları piksele oturtulmuş).</summary>
    public static void Seal(Graphics g, Rectangle r)
    {
        int size = Math.Min(r.Width, r.Height);
        int pick = new[] { 16, 20, 24, 32, 40, 48, 64, 256 }.FirstOrDefault(s => s >= size, 256);
        using var bmp = Theme.AppIcon(pick).ToBitmap();
        var old = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(bmp, r);
        g.InterpolationMode = old;
    }

    // ---- Yollar (ızgara birimiyle; çizerken ölçeklenir) ----

    /// <summary>Köprü güvertesi: ikinci dereceden eğri (SVG Q) → kübik.</summary>
    private static GraphicsPath Deck(float x0, float y0, float qx, float qy, float x2, float y2)
    {
        var p = new GraphicsPath();
        p.AddBezier(x0, y0, x0 + (qx - x0) * 2 / 3, y0 + (qy - y0) * 2 / 3, x2 + (qx - x2) * 2 / 3, y2 + (qy - y2) * 2 / 3, x2, y2);
        return p;
    }

    /// <summary>Kemerli kapı: sol ayak yukarı, yarım daire, sağ ayak aşağı.</summary>
    private static GraphicsPath Arch(float left, float right, float spring, float foot)
    {
        float r = (right - left) / 2;
        var p = new GraphicsPath();
        p.AddLine(left, foot, left, spring);
        p.AddArc(left, spring - r, 2 * r, 2 * r, 180, 180);
        p.AddLine(right, spring, right, foot);
        return p;
    }

    private static void Stroke(Graphics g, Pen pen, GraphicsPath path, float x, float y, float k)
    {
        using var copy = (GraphicsPath)path.Clone();
        using var m = new Matrix(k, 0, 0, k, x, y);
        copy.Transform(m);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawPath(pen, copy);
        g.SmoothingMode = old;
    }

    private static void Fill(Graphics g, Brush brush, GraphicsPath path, float x, float y, float k)
    {
        using var copy = (GraphicsPath)path.Clone();
        using var m = new Matrix(k, 0, 0, k, x, y);
        copy.Transform(m);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, copy);
        g.SmoothingMode = old;
    }
}

/// <summary>Birim satırının solundaki durum işareti (eski renkli noktanın yerine).</summary>
internal sealed class StatusGlyph : Control
{
    private readonly Glyphs.State _state;

    public StatusGlyph(Glyphs.State state, int size)
    {
        _state = state;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(size, size);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs e) => Glyphs.Status(e.Graphics, new RectangleF(0, 0, Width, Height), _state);
}

/// <summary>Kendi çizen işaret/mühür görüntüsü (PictureBox'ın bulanık büyütmesi yerine vektör).</summary>
internal sealed class MarkView : Control
{
    private readonly bool _seal;
    private readonly bool _dashed;

    /// <param name="seal">true: mühür (zinober kare); false: yalnız işaret (<see cref="Control.ForeColor"/> renginde).</param>
    public MarkView(bool seal, int size, bool dashed = false)
    {
        _seal = seal;
        _dashed = dashed;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(size, size);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_seal) Glyphs.Seal(e.Graphics, ClientRectangle);
        else Glyphs.Mark(e.Graphics, new RectangleF(0, 0, Width, Height), ForeColor, _dashed);
    }
}
