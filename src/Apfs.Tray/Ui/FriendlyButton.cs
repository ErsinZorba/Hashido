using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Apfs.Tray.Ui;

/// <summary>
/// Kendi çizen düğme (橋戸 marka). Türler: <see cref="Kind.Primary"/> mürekkep dolgu (yalnız iletişim kutularının ana eylemi),
/// <see cref="Kind.Secondary"/> mürekkep çizgi (üzerine gelince dolar), <see cref="Kind.Quiet"/> yalnız metin (isteğe bağlı
/// ikonla), <see cref="Kind.Icon"/> yalnız ikon (metin araç ipucu ve erişilebilir ad olur). Üzerine gelince renk yumuşakça
/// değişir; basınca her türde gövde ortasından yumuşakça küçülür (Quiet'te soluk bir zemin belirir), yazı yerinden
/// oynamaz; odakta 2 px halka (<see cref="Theme.Focus"/>). "Meşgul" durumunda dönen bir halka ve <see cref="BusyText"/>
/// gösterir, tıklanamaz. Windows 11 tasarımında (1.6.0, Fluent) basınca küçülme yok: birincil vurgu dolgu, ikincil hafif dolgu +
/// ince çerçeve + yükselti kenarı, sessiz/ikon saydam (üzerine gelince Track); durum dolgu ve metin rengiyle gösterilir.
/// </summary>
internal sealed class FriendlyButton : Control, IButtonControl
{
    public enum Kind { Primary, Secondary, Quiet, Icon }

    /// <summary>
    /// Sessiz, ikon ve ikincil düğmenin rengi (1.6.0): <see cref="Tone.Alert"/> geri alınmaz eylem (Güvenle çıkar; markada
    /// zinober), <see cref="Tone.Caution"/> dikkatli eylem (Yazmaya izin ver; markada kincha). Sessiz/ikonda metin/ikon rol
    /// renginde, üzerine gelince belirginleşir (mürekkebe doğru), basınca rol renginden açık bir zemin belirir. İkincilde
    /// (iletişim kutusunun devam düğmesi) markada çizgi ve metin rol renginde, üzerine gelince rol rengiyle dolar; Windows 11'de
    /// yalnız metin rol renginde.
    /// </summary>
    public enum Tone { None, Alert, Caution }

    private static readonly System.Windows.Forms.Timer Ticker = new() { Interval = 15 };
    private static readonly HashSet<FriendlyButton> Animating = [];

    private float _hover;          // 0 → 1 (üzerine gelme geçişi)
    private float _press;          // 0 → 1 (basma geçişi)
    private bool _mouseOver;
    private bool _pressed;         // fare/Boşluk basılı
    private bool _pressPeak;       // kısa tıkta da basma tepeye ulaşıp sonra geri dönsün
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

    /// <summary>Sessiz/ikon/ikincil düğmenin rol rengi (bkz. <see cref="Tone"/>); birincilde etkisiz.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Tone ButtonTone
    {
        get => _tone;
        set { _tone = value; Invalidate(); }
    }

    private Tone _tone;

    private Color? ToneColor => _tone switch { Tone.Alert => Theme.Alert, Tone.Caution => Theme.Caution, _ => null };

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
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _mouseOver = false; Release(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanClick) return;
        Press();
        Focus();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Release();
    }

    private void Press()
    {
        _pressed = _pressPeak = true;
        StartAnimation();
    }

    private void Release()
    {
        _pressed = false;
        StartAnimation();
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
            Press();
            if (e.KeyCode == Keys.Enter) { Release(); PerformClick(); } // basma tepeye ulaşıp geri döner
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space && _pressed)
        {
            Release();
            PerformClick();
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Release(); }
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
        _hover = Approach(_hover, target, 0.12f); // ~120 ms
        // Basma: ~60 ms'de tepeye, bırakınca ~120 ms'de geri; kısa tıkta önce tepeye ulaşır (yoksa hareket görünmezdi)
        if (_pressed || _pressPeak)
        {
            _press = Approach(_press, 1, 0.25f);
            if (_press == 1) _pressPeak = false;
        }
        else _press = Approach(_press, 0, 0.125f);
        if (_busy) _spin = (_spin + 9) % 360;
        // Durgun: hover hedefte, basma ya bırakılmış ve sıfırda ya da basılı ve tepede, meşgul değil
        bool pressSettled = _pressed ? _press == 1 : _press == 0 && !_pressPeak;
        if (_hover == target && pressSettled && !_busy) Animating.Remove(this);
        Invalidate();
    }

    private static float Approach(float value, float target, float step)
    {
        value += Math.Sign(target - value) * step;
        return Math.Abs(target - value) < step ? target : value;
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
        bool win = Theme.IsWindows;
        float h = Math.Max(_hover, _press);
        // Zemin (bg ile karışık, opak) ve metin rengi
        Color fill, fore, border = Color.Empty, edge = Color.Empty;
        switch (_kind)
        {
            // ---- Windows 11 (1.6.0): basınca küçülme yok; durum dolgu ve metin rengiyle (Fluent) ----
            case Kind.Primary when win:
                // Fluent hover/basılıda vurgu %10/%20 açılır ve metin solar; açık temada yazı kontrastı 3'e iniyordu → en çok
                // %12 açılma, metin solmaz (her karede ≥ 4,5; v160/buttons.cs)
                fill = Theme.Lerp(Theme.Accent, bg, 0.08f * h + 0.04f * _press);
                fore = Theme.OnAccent;
                break;
            case Kind.Secondary when win:
                fill = Theme.Lerp(Theme.Lerp(Theme.ControlFill(0), Theme.ControlFill(1), _hover), Theme.ControlFill(2), _press);
                fore = ToneColor ?? Theme.Lerp(Theme.Ink, Theme.InkSoft, _press); // tonlu: rol renginde, basınca solmaz
                (border, edge) = Theme.ControlStroke;
                break;
            case Kind.Icon or Kind.Quiet when win:
                fill = Theme.Lerp(bg, Theme.Track, _hover * (1 - 0.4f * _press));
                if (ToneColor is { } winTone)
                {
                    fill = Theme.Lerp(fill, winTone, 0.10f * _press);
                    fore = Theme.Lerp(winTone, Theme.Ink, 0.3f * h); // basınca solmaz (Caution açıkta 4,5'in altına iniyordu)
                }
                else fore = Theme.Lerp(_kind == Kind.Icon ? Theme.Ink : Theme.Lerp(Theme.InkSoft, Theme.Ink, _hover), Theme.InkSoft, _press);
                break;
            // ---- 橋戸 marka ----
            case Kind.Secondary when ToneColor is { } secondaryTone: // tonlu (iletişim kutusunun devam eylemi): rol rengi çizgi
                fill = Theme.Lerp(bg, secondaryTone, h);
                fore = Theme.Lerp(secondaryTone, Theme.OnInk, h);
                border = secondaryTone;
                break;
            case Kind.Primary:
                fill = Theme.Lerp(Theme.Accent, bg, 0.12f * h + 0.08f * _press);
                fore = Theme.OnAccent;
                break;
            case Kind.Secondary:
                fill = Theme.Lerp(bg, Theme.Ink, h);
                fore = Theme.Lerp(Theme.Ink, Theme.OnInk, h);
                border = Theme.Ink;
                break;
            case Kind.Icon when ToneColor is { } tone:
                fill = Theme.Lerp(bg, tone, 0.10f * h + 0.06f * _press);
                fore = Theme.Lerp(tone, Theme.Ink, 0.3f * h);
                break;
            case Kind.Icon:
                fill = Theme.Lerp(bg, Theme.Track, h);
                fore = Theme.Ink;
                break;
            default: // Quiet: zemin yalnız basınca (öbür türler gibi ortasından küçüldüğü görünsün)
                if (ToneColor is { } quietTone)
                {
                    fill = Theme.Lerp(bg, quietTone, 0.12f * _press);
                    fore = Theme.Lerp(quietTone, Theme.Ink, 0.3f * h);
                    break;
                }
                fill = Theme.Lerp(bg, Theme.Track, _press);
                fore = Theme.Lerp(Theme.InkSoft, Theme.Ink, h);
                break;
        }
        if (off)
        {
            fill = _kind == Kind.Primary ? Theme.Lerp(Theme.Accent, bg, 0.55f) : bg;
            fore = _kind == Kind.Primary ? Theme.OnAccent : Theme.Lerp(Theme.InkSoft, bg, 0.35f);
            border = border.IsEmpty ? border : Theme.Lerp(Theme.Ink, bg, 0.6f);
        }

        // Marka: basınca gövde dört yandan eşit içe çekilir (ortasından küçülür); yazı ve ikon sabit dikdörtgende kalır.
        // Windows 11: gövde yerinde kalır.
        float rest = Ring + 0.5f;
        float inset = rest + (win ? 0 : _press * 1.5f * DeviceDpi / 96f);
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
            if (!edge.IsEmpty && !off)
            {
                // Yükselti: açıkta alt kenar, koyuda üst kenar bir ton farklı (Fluent ControlElevationBorder)
                var state = g.Save();
                float band = radius;
                g.SetClip(Theme.IsDark
                    ? new RectangleF(body.X - 1, body.Y - 1, body.Width + 2, band)
                    : new RectangleF(body.X - 1, body.Bottom - band + 1, body.Width + 2, band));
                using var edgePen = new Pen(edge, Math.Max(1, this.S(1)));
                g.DrawPath(edgePen, path);
                g.Restore(state);
            }
        }

        if (Focused && ShowFocusCues)
        {
            float w = this.S(2);
            using var path = Theme.RoundedRect(new RectangleF(w / 2, w / 2, Width - 1 - w, Height - 1 - w), radius + Ring / 2f);
            using var pen = new Pen(Theme.Focus, w);
            g.DrawPath(pen, path);
        }

        var textRect = Rectangle.Round(new RectangleF(rest, rest, Width - 1 - rest * 2, Height - 1 - rest * 2));
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
            float s = this.S(16) * (1 - (win ? 0 : 0.08f) * _press); // markada ikon da ortasından küçülür
            Glyphs.DrawIcon(g, new RectangleF((Width - s) / 2f, (Height - s) / 2f, s, s), _icon, fore);
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
