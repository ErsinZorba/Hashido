using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Apfs.Tray.Ui;

/// <summary>
/// Kendi çizen onay kutusu (橋戸 marka; iki temada aynı): yuvarlak köşeli kutu mürekkep çizgi, işaretliyken mürekkep dolgu
/// ve ortasında kâğıt renginde işaret; üzerine gelince ve işaretlenince renk yumuşakça değişir; odakta 2 px zinober halka.
/// Metin kaydırılır, kutu görünen yazının dikey ortasında (bkz. <see cref="Arrange"/>). <see cref="CheckBox"/>'tan türer: klavye (Boşluk), erişilebilirlik
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

    /// <summary>Metin bloğu, verilen kontrol genişliğinde (çizimle aynı bayraklar ve aynı genişlik).</summary>
    private Size MeasureText(int width) => TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, width - TextLeft), 0), Flags);

    /// <summary>
    /// Yerleşim (verilen genişlikte): kutunun dikey merkezi metnin GÖRÜNEN mürekkebinin ortasında — tek satırda o satırın,
    /// iki satırda iki satırın ortasında. Satır kutusunun ortası yetmiyor: yazı tipinin üstteki iç boşluğu ve alt uzantılar
    /// (ç, ş, g) yüzünden görünen yazı 2–3 px aşağıdadır, kutu yukarıda kalmış görünüyordu (kullanıcı 1.5.1 denemesi).
    /// </summary>
    private (Size Text, int TextTop, float BoxCenter, int Height) Arrange(int width)
    {
        var text = MeasureText(width);
        float center = text.Height / 2f + InkOffset(width, text); // metin üstü 0'dayken mürekkep merkezi
        float boxTop = center - Box / 2f - Ring, boxBottom = center + Box / 2f + Ring;
        int shift = (int)Math.Ceiling(Math.Max(0, -boxTop)); // kutu üstten taşarsa her şey aşağı
        int height = (int)Math.Ceiling(Math.Max(text.Height, boxBottom) + shift);
        return (text, shift, center + shift, height);
    }

    private float BoxCenter => Arrange(Width).BoxCenter;
    private int TextTop => Arrange(Width).TextTop;

    private (string Text, int Width, Font Font, float Offset)? _ink;

    /// <summary>
    /// Metnin mürekkep (görünen piksel) merkezinin, metin bloğunun geometrik merkezinden ne kadar aşağıda olduğu. Metin bir
    /// kez beyaz bitmap'e çizilip mürekkepli ilk ve son satır bulunur (Asya yazı tipleri dahil her yazı tipinde doğru);
    /// sonuç metin, genişlik ve yazı tipine göre saklanır.
    /// </summary>
    private float InkOffset(int width, Size text)
    {
        if (_ink is { } c && c.Text == Text && c.Width == width && ReferenceEquals(c.Font, Font)) return c.Offset;
        float offset = 0;
        if (text.Width > 0 && text.Height > 0)
        {
            using var bmp = new Bitmap(text.Width, text.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(Point.Empty, text), Color.Black, Color.White, Flags);
            }
            var data = bmp.LockBits(new Rectangle(Point.Empty, text), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var row = new int[text.Width];
                int top = -1, bottom = -1;
                for (int y = 0; y < text.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, text.Width);
                    if (!row.Any(p => (p & 0xFF) < 128)) continue; // mavi kanal: siyah mürekkep (ClearType kenarları hariç)
                    if (top < 0) top = y;
                    bottom = y;
                }
                if (top >= 0) offset = (top + bottom + 1) / 2f - text.Height / 2f;
            }
            finally { bmp.UnlockBits(data); }
        }
        _ink = (Text, width, Font, offset);
        return offset;
    }

    /// <summary>
    /// Verilen genişlikte (0 = sınırsız) gereken boyut; metin çizimle aynı bayraklarla, aynı genişlikte ölçülür (çizim ile
    /// yükseklik hesabı ayrışmasın — uzun dillerde son satır kesilmesin).
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : int.MaxValue / 2;
        var layout = Arrange(width);
        return new Size(proposedSize.Width > 0 ? proposedSize.Width : TextLeft + layout.Text.Width, layout.Height + this.S(2));
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
        var layout = Arrange(Width);
        var box = new RectangleF(Ring, MathF.Round(layout.BoxCenter - Box / 2f), Box, Box); // tam piksel: çerçeve keskin
        float radius = this.S(4);

        Color off = Theme.Lerp(bg, Theme.Track, 0.7f * _hover);
        Color on = Theme.Lerp(Theme.Accent, bg, 0.12f * _hover);
        Color fill = Theme.Lerp(off, on, _on);
        // Windows 11: işaretsiz çerçeve sabit InkSoft (Fluent ControlStrongStroke); markada üzerine gelince mürekkebe koyulaşır
        Color border = Theme.Lerp(Theme.IsWindows ? Theme.InkSoft : Theme.Lerp(Theme.InkSoft, Theme.Ink, _hover), on, _on);
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
            using var pen = new Pen(Theme.Lerp(fill, Theme.OnAccent, _on), 2f * k)
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
            using var pen = new Pen(Theme.Focus, w);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(TextLeft, layout.TextTop, Math.Max(1, Width - TextLeft), Height - layout.TextTop),
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
