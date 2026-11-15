using System.Runtime.InteropServices;

namespace Apfs.Tray.Ui;

/// <summary>
/// Başlık etiketi (1.6.0): rakamlar düz (lining) biçimleriyle çizilir — Sitka Heading'in varsayılan eski üslup rakamları
/// "Disk 3 (320,1 GB)"de aşağı sarkıyordu (<see cref="LiningFigures"/>). Metin rakam içeriyorsa, sola yaslıysa, satır sonu
/// yoksa, her karakteri fontta varsa (<c>GGI_MARK_NONEXISTING_GLYPHS</c>) ve eşleme en az bir rakamı değiştiriyorsa satırlar
/// glif numarasıyla (<c>ExtTextOutW(ETO_GLYPH_INDEX)</c>) çizilir; aksi hâlde normal <see cref="Label"/> çizimi (fail-open:
/// Segoe gibi rakamları zaten düz fontlar, Asya dilleri, ortalı başlıklar). Köken normal etiketinkiyle aynı: TextRenderer'ın sol
/// iç boşluğu (glif taşma payı) + üst kenar; satır boyu fontun yüksekliği; kaydırma boşluktan açgözlü, yalnız
/// <see cref="Control.MaximumSize"/> genişliğinde. Doğrulama: localization-tool/v160/numerals.cs ("Disk" kısmı normal çizimle
/// piksel aynı) ve v160/shots.cs (yerleşim temel görüntülerle aynı).
/// </summary>
internal sealed class HeadingLabel : Label
{
    private LineLayout? _layout;

    /// <summary>Başlık yazı tipiyse (<see cref="Theme.Heading"/>) başlık etiketi, değilse normal etiket.</summary>
    internal static Label For(Font font) => Theme.IsHeading(font) ? new HeadingLabel() : new Label();

    private sealed record LineLayout(string Text, Font Font, int Limit, List<ushort[]> Lines, List<int> Widths, int Margin, int RightMargin, int LineHeight);

    protected override void OnTextChanged(EventArgs e) { _layout = null; base.OnTextChanged(e); }
    protected override void OnFontChanged(EventArgs e) { _layout = null; base.OnFontChanged(e); }

    /// <summary>Glif yolu kullanılıyor mu (düzenek ve testler için).</summary>
    internal bool UsesLiningGlyphs => Plan(WrapLimit) is not null;

    /// <summary>
    /// Kaydırma yalnız <see cref="Control.MaximumSize"/> genişliğinde (başlıklar böyle kaydırılır). Tablonun ölçüm sırasında
    /// önerdiği geçici dar genişlik sınır sayılınca disk başlığı iki satır ölçülüp satır uzuyordu (ilk deneme).
    /// </summary>
    private int WrapLimit => MaximumSize.Width > 0 ? MaximumSize.Width - Padding.Horizontal : int.MaxValue;

    private LineLayout? Plan(int limit)
    {
        if (_layout is { } c && c.Text == Text && ReferenceEquals(c.Font, Font) && c.Limit == limit) return c.Lines.Count == 0 ? null : c;
        _layout = Build(limit) ?? new LineLayout(Text, Font, limit, [], [], 0, 0, 0);
        return _layout.Lines.Count == 0 ? null : _layout;
    }

    private LineLayout? Build(int limit)
    {
        string text = Text;
        if (text.Length == 0 || !text.Any(char.IsAsciiDigit) || text.IndexOfAny(['\r', '\n', '\t']) >= 0) return null;
        if (TextAlign != ContentAlignment.TopLeft || RightToLeft == RightToLeft.Yes) return null;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), font = Font.ToHfont(), old = IntPtr.Zero;
        try
        {
            old = SelectObject(dc, font);
            var glyphs = new ushort[text.Length];
            if (GetGlyphIndicesW(dc, text, text.Length, glyphs, GgiMarkNonexistingGlyphs) == GdiError) return null;
            if (glyphs.Any(g => g == 0xFFFF)) return null; // fontta olmayan karakter: Windows'un yedek fontu gerekir
            var map = LiningFigures.Map(Font, dc);
            int changed = 0;
            for (int i = 0; i < text.Length; i++)
                if (char.IsAsciiDigit(text[i]) && map.TryGetValue(glyphs[i], out var lining) && lining != glyphs[i])
                {
                    glyphs[i] = lining;
                    changed++;
                }
            if (changed == 0) return null;
            if (!GetTextMetricsW(dc, out var tm)) return null;
            // TextRenderer'ın iç boşluğu (glif taşma payı): solda ⌈yükseklik/6⌉, sağda ⌈yükseklik/6 × 1,5⌉ (eğik yazı payı her
            // zaman eklenir). Dolgulu/dolgusuz ölçüm farkının yarısı 1–2 px fazla çıkıyordu (numerals.cs: "Disk " 1 px sağa kaydı).
            int margin = (int)Math.Ceiling(tm.Height / 6f);
            int rightMargin = TextRenderer.MeasureText(text, Font).Width - TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding).Width - margin;
            int room = limit == int.MaxValue ? int.MaxValue : Math.Max(1, limit - margin - Math.Max(0, rightMargin));
            int Width(int start, int end)
            {
                if (end <= start) return 0;
                var run = glyphs[start..end];
                return GetTextExtentPointI(dc, run, run.Length, out var size) ? size.Width : 0;
            }
            // Açgözlü kaydırma: boşluktan kır; satır sonundaki boşluk çizilmez
            var lines = new List<ushort[]>();
            var widths = new List<int>();
            int lineStart = 0;
            while (lineStart < text.Length)
            {
                int end = text.Length;
                if (Width(lineStart, end) > room)
                {
                    int lastFit = -1;
                    for (int i = lineStart + 1; i < text.Length; i++)
                        if (text[i] == ' ' && Width(lineStart, i) <= room) lastFit = i;
                    if (lastFit > 0) end = lastFit;
                }
                int trimmed = end;
                while (trimmed > lineStart && text[trimmed - 1] == ' ') trimmed--;
                lines.Add(glyphs[lineStart..trimmed]);
                widths.Add(Width(lineStart, trimmed));
                lineStart = end;
                while (lineStart < text.Length && text[lineStart] == ' ') lineStart++;
            }
            return new LineLayout(text, Font, limit, lines, widths, margin, Math.Max(0, rightMargin), tm.Height);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(dc, old);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Plan(WrapLimit) is not { } p) return base.GetPreferredSize(proposedSize);
        // Kaydırma yalnız MaximumSize'la: normal etiketin boyu da o sınırla ölçülür (tablonun önerdiği dar genişlik değil)
        var size = base.GetPreferredSize(new Size(MaximumSize.Width, 0));
        int width = p.Widths.Max() + p.Margin + p.RightMargin + Padding.Horizontal;
        if (MaximumSize.Width > 0) width = Math.Min(width, MaximumSize.Width);
        // Tek satırda boy normal etiketinkiyle aynı (satır yüksekliği kaymasın); çok satırda satır sayısı × font yüksekliği
        int height = p.Lines.Count == 1 ? size.Height : p.Lines.Count * p.LineHeight + Padding.Vertical;
        return new Size(width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Plan(WrapLimit) is not { } p)
        {
            base.OnPaint(e);
            return;
        }
        // Graphics'in ötelemesi (saydam üst denetim çizimi, DrawToBitmap) HDC'ye taşınır
        (int X, int Y) offset;
        using (var transform = e.Graphics.Transform) // her okumada yeni Matrix (GDI+ nesnesi) döner
            offset = transform.Elements is [_, _, _, _, var dx, var dy] ? ((int)dx, (int)dy) : (0, 0);
        IntPtr hdc = e.Graphics.GetHdc(), font = Font.ToHfont(), old = IntPtr.Zero;
        try
        {
            old = SelectObject(hdc, font);
            SetTextColor(hdc, ColorTranslator.ToWin32(Enabled ? ForeColor : SystemColors.GrayText));
            SetBkMode(hdc, Transparent);
            int x = offset.X + Padding.Left + p.Margin, y = offset.Y + Padding.Top;
            foreach (var line in p.Lines)
            {
                ExtTextOutW(hdc, x, y, EtoGlyphIndex, IntPtr.Zero, line, line.Length, IntPtr.Zero);
                y += p.LineHeight;
            }
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(hdc, old);
            DeleteObject(font);
            e.Graphics.ReleaseHdc(hdc);
        }
    }

    private const uint GgiMarkNonexistingGlyphs = 1;
    private const uint GdiError = 0xFFFFFFFF;
    private const uint EtoGlyphIndex = 0x0010;
    private const int Transparent = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TextMetric
    {
        public int Height, Ascent, Descent, InternalLeading, ExternalLeading, AveCharWidth, MaxCharWidth, Weight, Overhang,
            DigitizedAspectX, DigitizedAspectY;
        public char FirstChar, LastChar, DefaultChar, BreakChar;
        public byte Italic, Underlined, StruckOut, PitchAndFamily, Charset;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int count, [Out] ushort[] glyphs, uint flags);
    [DllImport("gdi32.dll")] private static extern bool GetTextExtentPointI(IntPtr hdc, ushort[] glyphs, int count, out Size size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextMetricsW(IntPtr hdc, out TextMetric tm);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect, ushort[] glyphs, int count, IntPtr dx);
}
