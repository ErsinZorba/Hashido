using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Apfs.Tray.Ui;

/// <summary>
/// Kendi çizen düğme (橋戸 marka). Türler: <see cref="Kind.Primary"/> mürekkep dolgu (yalnız iletişim kutularının ana eylemi),
/// <see cref="Kind.Secondary"/> mürekkep çizgi (üzerine gelince dolar), <see cref="Kind.Quiet"/> yalnız metin (isteğe bağlı
/// ikonla), <see cref="Kind.Icon"/> yalnız ikon (metin araç ipucu ve erişilebilir ad olur). Üzerine gelince renk yumuşakça
/// değişir, basınca hafifçe içe çöker; odakta 2 px zinober halka. "Meşgul" durumunda dönen bir halka ve
/// <see cref="BusyText"/> gösterir, tıklanamaz.
/// </summary>
internal sealed class FriendlyButton : Control, IButtonControl
{
    public enum Kind { Primary, Secondary, Quiet, Icon }

    private static readonly System.Windows.Forms.Timer Ticker = new() { Interval = 15 };
    private static readonly HashSet<FriendlyButton> Animating = [];

    private float _hover;          // 0 → 1 (üzerine gelme geçişi)
    private bool _mouseOver;
    private bool _pressed;
    private bool _busy;
    private float _spin;
    private readonly Kind _kind;
    private Glyphs.Icon _icon;
    private ToolTip? _tip;

    static FriendlyButton() => Ticker.Tick += (_, _) =>
    {
        foreach (var b in Animating.ToList()) b.Step();
        if (Animating.Count == 0) Ticker.Stop();
    };

    public FriendlyButton(string text, Kind kind = Kind.Primary, Glyphs.Icon icon = Glyphs.Icon.None)
    {
        _kind = kind;
        _icon = icon;
        Text = text;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false); // hızlı iki tık iki ayrı tık sayılsın
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        TabStop = true;
        AutoSize = true;
        Margin = new Padding(this.S(2));
        AccessibleRole = AccessibleRole.PushButton;
        if (kind == Kind.Icon) SetTip(text);
    }

    /// <summary>Meşgulken gösterilecek metin (ör. "Çıkarılıyor…").</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? BusyText { get; set; }

    /// <summary>Metnin solundaki (Quiet) ya da tek başına (Icon) çizgi ikonu.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Glyphs.Icon IconGlyph
    {
        get => _icon;
        set { _icon = value; PerformLayout(); Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Cursor = value ? Cursors.Default : Cursors.Hand;
            if (value) StartAnimation();
            if (_kind == Kind.Icon) SetTip(value && BusyText is not null ? BusyText : Text);
            PerformLayout(); // metin değişir: boyut yeniden hesaplanır
            Parent?.PerformLayout();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DialogResult DialogResult { get; set; }
    public void NotifyDefault(bool value) { }

    public void PerformClick()
    {
        if (CanClick) OnClick(EventArgs.Empty);
    }

    private bool CanClick => Enabled && !_busy && Visible;

    private string ShownText => _busy && BusyText is not null ? BusyText : Text;

    /// <summary>Odak halkası için gövdenin çevresinde bırakılan boşluk.</summary>
    private int Ring => this.S(3);

    private bool HasLeadingIcon => _kind != Kind.Icon && _icon != Glyphs.Icon.None;

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (_kind == Kind.Icon) return new Size(this.S(32), this.S(32));
        var t = TextRenderer.MeasureText(ShownText, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        int lead = _busy ? this.S(20) : HasLeadingIcon ? this.S(22) : 0;
        int padX = _kind == Kind.Quiet ? this.S(6) : this.S(14);
        return new Size(t.Width + padX * 2 + lead + Ring * 2, Math.Max(this.S(30), t.Height + this.S(12)) + Ring * 2);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (_kind == Kind.Icon) SetTip(ShownText);
        Parent?.PerformLayout();
        Invalidate();
    }

    private void SetTip(string text)
    {
        AccessibleName = text;
        if (_kind != Kind.Icon) return;
        _tip ??= new ToolTip();
        _tip.SetToolTip(this, text);
    }

    // ---- Fare ve klavye ----

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _mouseOver = true; StartAnimation(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _mouseOver = false; _pressed = false; StartAnimation(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanClick) return;
        _pressed = true;
        Focus();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        if (CanClick) base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter && CanClick)
        {
            _pressed = true;
            Invalidate();
            if (e.KeyCode == Keys.Enter) { _pressed = false; PerformClick(); }
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && _pressed)
        {
            _pressed = false;
            Invalidate();
            PerformClick();
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _pressed = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    // ---- Animasyon ----

    private void StartAnimation()
    {
        if (IsDisposed) return;
        Animating.Add(this);
        Ticker.Start();
    }

    private void Step()
    {
        if (IsDisposed) { Animating.Remove(this); return; }
        float target = _mouseOver && CanClick ? 1 : 0;
        _hover += Math.Sign(target - _hover) * 0.12f; // ~120 ms
        if (Math.Abs(target - _hover) < 0.12f) _hover = target;
        if (_busy) _spin = (_spin + 9) % 360;
        if (_hover == target && !_busy) Animating.Remove(this);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        Animating.Remove(this);
        if (disposing) _tip?.Dispose();
        base.Dispose(disposing);
    }

    // ---- Çizim ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var bg = BackgroundColor();
        g.Clear(bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        bool off = !Enabled || _busy;
        float h = _pressed ? 1 : _hover;
        // Zemin (bg ile karışık, opak) ve metin rengi
        Color fill, fore, border = Color.Empty;
        switch (_kind)
        {
            case Kind.Primary:
                fill = Theme.Lerp(Theme.Ink, bg, 0.12f * h + (_pressed ? 0.08f : 0));
                fore = Theme.OnInk;
                break;
            case Kind.Secondary:
                fill = Theme.Lerp(bg, Theme.Ink, h);
                fore = Theme.Lerp(Theme.Ink, Theme.OnInk, h);
                border = Theme.Ink;
                break;
            case Kind.Icon:
                fill = Theme.Lerp(bg, Theme.Track, h);
                fore = Theme.Ink;
                break;
            default: // Quiet
                fill = bg;
                fore = Theme.Lerp(Theme.InkSoft, Theme.Ink, h);
                break;
        }
        if (off)
        {
            fill = _kind == Kind.Primary ? Theme.Lerp(Theme.Ink, bg, 0.55f) : bg;
            fore = _kind == Kind.Primary ? Theme.OnInk : Theme.Lerp(Theme.InkSoft, bg, 0.35f);
            border = border.IsEmpty ? border : Theme.Lerp(Theme.Ink, bg, 0.6f);
        }

        float inset = Ring + (_pressed ? this.S(1) : 0) + 0.5f; // basınca içe çökme
        var body = new RectangleF(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);
        float radius = this.S(4);
        if (fill != bg)
        {
            using var path = Theme.RoundedRect(body, radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        if (!border.IsEmpty)
        {
            using var path = Theme.RoundedRect(body, radius);
            using var pen = new Pen(border, Math.Max(1, this.S(1)));
            g.DrawPath(pen, path);
        }

        if (Focused && ShowFocusCues)
        {
            float w = this.S(2);
            using var path = Theme.RoundedRect(new RectangleF(w / 2, w / 2, Width - 1 - w, Height - 1 - w), radius + Ring / 2f);
            using var pen = new Pen(Theme.Shu, w);
            g.DrawPath(pen, path);
        }

        var textRect = Rectangle.Round(body);
        if (_pressed) textRect.Offset(0, 1);
        if (_busy)
        {
            int d = this.S(12);
            string shown = _kind == Kind.Icon ? "" : ShownText;
            var size = TextRenderer.MeasureText(shown, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int total = shown.Length == 0 ? d : d + this.S(8) + size.Width;
            int x = (Width - total) / 2;
            using var pen = new Pen(fore, Math.Max(1.5f, this.S(2) * 0.75f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, x, (Height - d) / 2f, d, d, _spin, 270);
            if (shown.Length == 0) return;
            textRect = new Rectangle(x + d + this.S(8), 0, size.Width + this.S(4), Height);
            TextRenderer.DrawText(g, shown, Font, textRect, fore, fill, TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            return;
        }
        if (_kind == Kind.Icon)
        {
            int s = this.S(16);
            Glyphs.DrawIcon(g, new RectangleF((Width - s) / 2f, (Height - s) / 2f + (_pressed ? 1 : 0), s, s), _icon, fore);
            return;
        }
        if (HasLeadingIcon)
        {
            int s = this.S(16);
            var t = TextRenderer.MeasureText(ShownText, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int total = s + this.S(6) + t.Width;
            float x = textRect.X + (textRect.Width - total) / 2f;
            Glyphs.DrawIcon(g, new RectangleF(x, textRect.Y + (textRect.Height - s) / 2f, s, s), _icon, fore);
            textRect = new Rectangle((int)(x + s + this.S(6)), textRect.Y, t.Width + this.S(4), textRect.Height);
            TextRenderer.DrawText(g, ShownText, Font, textRect, fore, fill,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            return;
        }
        TextRenderer.DrawText(g, ShownText, Font, textRect, fore, fill,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    /// <summary>Düğmenin arkasındaki zemin: saydam üst denetimlerde ilk opak ataya kadar çıkılır.</summary>
    private Color BackgroundColor()
    {
        for (Control? c = Parent; c is not null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return Theme.Paper;
    }
}
