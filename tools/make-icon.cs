#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishAot=false
#:property Nullable=enable

// Hashido uygulama simgesi (橋戸 mühür): zinober yuvarlak kare içinde kâğıt renginde işaret (köprü güvertesi + kemerli kapı).
//   dotnet run tools/make-icon.cs -- <çıktı.ico> [--png <klasör>]
// 16–64 px 32 bit DIB, 256 px PNG (bugünkü app.ico ile aynı yapı: 8 girdi). Küçük boyutlarda işaret biraz büyütülür ve
// çizgiler piksel ortasına oturtulur (16 px'te 1 px çizgi bulanıklaşmasın). İşaretin ölçüleri tepsinin Ui/Glyphs.cs çizimiyle aynı.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
string output = args.Length > 0 ? args[0] : throw new ArgumentException("çıktı .ico yolu gerekli");
string? pngDir = args.Length > 2 && args[1] == "--png" ? args[2] : null;
if (pngDir is not null) Directory.CreateDirectory(pngDir);

var images = new List<(int Size, byte[] Data)>();
foreach (int size in sizes)
{
    using var bmp = Draw(size);
    if (pngDir is not null) bmp.Save(Path.Combine(pngDir, $"icon-{size}.png"), ImageFormat.Png);
    images.Add((size, size >= 256 ? Png(bmp) : Dib(bmp)));
}

using (var f = File.Create(output))
using (var w = new BinaryWriter(f))
{
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(data.Length); w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
}
Console.WriteLine($"{output}: {images.Count} girdi ({string.Join(", ", sizes)})");

static Bitmap Draw(int size)
{
    var shu = Color.FromArgb(0xC2, 0x41, 0x2A);
    var paper = Color.FromArgb(0xFB, 0xFA, 0xF7);
    var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    g.Clear(Color.Transparent);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.Half; // SVG gibi: piksel ortası x+0,5

    // Mühür: 48'lik ızgarada rx 7
    float u = size / 48f;
    using (var path = RoundedRect(new RectangleF(0, 0, size, size), 7 * u))
    using (var b = new SolidBrush(shu))
        g.FillPath(b, path);

    // İşaret 48'lik ızgarada; mühürde %60 (küçük boyutlarda daha büyük ki çizgiler kaybolmasın)
    float ratio = size switch { <= 16 => 0.78f, <= 20 => 0.72f, <= 24 => 0.68f, <= 32 => 0.64f, _ => 0.6f };
    float s = size / 48f * ratio;
    float cx = size / 2f, cy = size / 2f;
    const float markCx = 24f, markCy = 24.3f; // işaretin görsel ortası (güverte tepesi ~6,9 … ayak ucu 42)
    float X(float x) => cx + (x - markCx) * s;
    float Y(float y) => cy + (y - markCy) * s;
    bool hint = size <= 32;
    float stroke = 3.2f * s;
    if (hint) stroke = Math.Max(1f, MathF.Round(stroke));
    // Tek piksel kalınlıktaki dikey/yatay çizgi piksel ortasına, çift kalınlıktaki piksel sınırına oturur
    float Snap(float v) => !hint ? v : (int)MathF.Round(stroke) % 2 == 1 ? MathF.Floor(v) + 0.5f : MathF.Round(v);

    using var pen = new Pen(paper, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat, LineJoin = LineJoin.Miter };

    // Güverte: M5 11.5 Q24 5.5 43 11.5 (ikinci dereceden → kübik)
    float y0 = Snap(Y(11.5f));
    var p0 = new PointF(hint ? MathF.Round(X(5)) : X(5), y0);
    var p2 = new PointF(hint ? MathF.Round(X(43)) : X(43), y0);
    var q = new PointF(X(24), y0 + (Y(5.5f) - Y(11.5f)));
    // 24 px ve altında güverte yumuşatmasız: 1 px eğri yumuşatılınca ortası bulanık bir lekeye dönüyor, basamaklı çizgi net
    if (size <= 24) g.SmoothingMode = SmoothingMode.None;
    g.DrawBezier(pen, p0, Lerp(p0, q, 2 / 3f), Lerp(p2, q, 2 / 3f), p2);
    g.SmoothingMode = SmoothingMode.AntiAlias;

    // Kemer: M12.5 42 V26 a11.5 11.5 0 0 1 23 0 v16
    float xl = Snap(X(12.5f)), xr = Snap(X(35.5f));
    float r = (xr - xl) / 2;
    float spring = Snap(Y(26f));
    float foot = hint ? MathF.Round(Y(42f)) : Y(42f);
    using var arch = new GraphicsPath();
    arch.AddLine(xl, foot, xl, spring);
    arch.AddArc(xl, spring - r, 2 * r, 2 * r, 180, 180);
    arch.AddLine(xr, spring, xr, foot);
    g.DrawPath(pen, arch);
    return bmp;
}

static PointF Lerp(PointF a, PointF b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

static GraphicsPath RoundedRect(RectangleF r, float radius)
{
    var p = new GraphicsPath();
    float d = radius * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}

static byte[] Png(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

/// <summary>ICO içindeki DIB: BITMAPINFOHEADER (yükseklik 2 kat), alttan üste BGRA, sıfır AND maskesi (saydamlık alfa kanalında).</summary>
static byte[] Dib(Bitmap bmp)
{
    int n = bmp.Width;
    int maskStride = (n + 31) / 32 * 4;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(n); w.Write(n * 2); w.Write((ushort)1); w.Write((ushort)32);
    w.Write(0); w.Write(n * n * 4); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    var data = bmp.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    try
    {
        var row = new byte[n * 4];
        for (int y = n - 1; y >= 0; y--)
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
            w.Write(row);
        }
    }
    finally { bmp.UnlockBits(data); }
    w.Write(new byte[maskStride * n]);
    return ms.ToArray();
}
