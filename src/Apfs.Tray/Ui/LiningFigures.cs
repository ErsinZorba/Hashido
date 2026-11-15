using System.Runtime.InteropServices;

namespace Apfs.Tray.Ui;

/// <summary>
/// OpenType <c>lnum</c> (düz rakamlar) eşlemesi, GDI ile fontun GSUB tablosundan (1.6.0). Başlık yazı tipi Sitka Heading varsayılan
/// olarak eski üslup rakam kullanıyor: "Disk 3 (320,1 GB)"de 3 taban çizgisinin altına iniyor, 0/1/2 küçük harf boyunda
/// (kullanıcı "rakamlar aşağı sarkıyor" dedi). GDI OpenType özelliği seçtirmez; eşleme okunup rakamlar glif numarasıyla
/// çizilir (<see cref="HeadingLabel"/>). Okunan: FeatureList'te <c>lnum</c> etiketli tüm özelliklerin lookup'ları; lookup türü 1
/// (tekli yerine koyma; biçim 1 delta, biçim 2 dizi) ve 7 (uzantı → içindeki tür 1); Coverage biçim 1 (dizi) ve 2 (aralık).
/// Eşleme yoksa ya da okunamazsa boş sözlük (çağıran normal çizime düşer). Font başına (ad + stil) bir kez okunur.
/// </summary>
internal static class LiningFigures
{
    private static readonly Dictionary<(string, FontStyle), IReadOnlyDictionary<ushort, ushort>> Cache = [];

    /// <summary>Glif → düz rakam glifi eşlemesi (boş olabilir). <paramref name="hdc"/>'de font seçili olmalı.</summary>
    public static IReadOnlyDictionary<ushort, ushort> Map(Font font, IntPtr hdc)
    {
        var key = (font.Name, font.Style);
        if (Cache.TryGetValue(key, out var map)) return map;
        map = Read(hdc);
        Cache[key] = map;
        return map;
    }

    private const uint GsubTag = 'G' | 'S' << 8 | 'U' << 16 | 'B' << 24; // GetFontData etiketi küçük sonlu

    private static IReadOnlyDictionary<ushort, ushort> Read(IntPtr hdc)
    {
        uint size = GetFontData(hdc, GsubTag, 0, null, 0);
        if (size is 0 or GdiError || size > 4 * 1024 * 1024) return new Dictionary<ushort, ushort>();
        var data = new byte[size];
        if (GetFontData(hdc, GsubTag, 0, data, size) != size) return new Dictionary<ushort, ushort>();
        try { return Parse(data); }
        catch (IndexOutOfRangeException) { return new Dictionary<ushort, ushort>(); } // bozuk tablo: normal çizim
        catch (ArgumentOutOfRangeException) { return new Dictionary<ushort, ushort>(); }
    }

    /// <summary>GSUB tablosundan <c>lnum</c> tekli yerine koyma eşlemesi (test edilebilir saf işlev).</summary>
    internal static Dictionary<ushort, ushort> Parse(byte[] t)
    {
        var map = new Dictionary<ushort, ushort>();
        int featureList = U16(t, 6), lookupList = U16(t, 8);
        var lookups = new SortedSet<int>();
        int featureCount = U16(t, featureList);
        for (int i = 0; i < featureCount; i++)
        {
            int rec = featureList + 2 + i * 6;
            if (t[rec] != 'l' || t[rec + 1] != 'n' || t[rec + 2] != 'u' || t[rec + 3] != 'm') continue;
            int feature = featureList + U16(t, rec + 4);
            int count = U16(t, feature + 2);
            for (int j = 0; j < count; j++) lookups.Add(U16(t, feature + 4 + j * 2));
        }
        int lookupCount = U16(t, lookupList);
        foreach (int index in lookups)
        {
            if (index >= lookupCount) continue;
            int lookup = lookupList + U16(t, lookupList + 2 + index * 2);
            int type = U16(t, lookup), subCount = U16(t, lookup + 4);
            for (int s = 0; s < subCount; s++)
            {
                int sub = lookup + U16(t, lookup + 6 + s * 2);
                if (type == 7)
                {
                    if (U16(t, sub) != 1 || U16(t, sub + 2) != 1) continue; // yalnız tekli yerine koymayı saran uzantı
                    sub += (int)U32(t, sub + 4);
                }
                else if (type != 1) continue;
                Single(t, sub, map);
            }
        }
        return map;
    }

    private static void Single(byte[] t, int sub, Dictionary<ushort, ushort> map)
    {
        int format = U16(t, sub);
        var covered = Coverage(t, sub + U16(t, sub + 2));
        if (format == 1)
        {
            short delta = (short)U16(t, sub + 4);
            foreach (var (glyph, _) in covered) map.TryAdd(glyph, (ushort)(glyph + delta));
        }
        else if (format == 2)
        {
            int count = U16(t, sub + 4);
            foreach (var (glyph, i) in covered)
                if (i < count) map.TryAdd(glyph, (ushort)U16(t, sub + 6 + i * 2));
        }
    }

    /// <summary>Coverage tablosu: (glif, kapsama sırası).</summary>
    private static IEnumerable<(ushort Glyph, int Index)> Coverage(byte[] t, int c)
    {
        int format = U16(t, c), count = U16(t, c + 2);
        if (format == 1)
        {
            for (int i = 0; i < count; i++) yield return ((ushort)U16(t, c + 4 + i * 2), i);
        }
        else if (format == 2)
        {
            for (int r = 0; r < count; r++)
            {
                int rec = c + 4 + r * 6;
                int start = U16(t, rec), end = U16(t, rec + 2), first = U16(t, rec + 4);
                for (int g = start; g <= end && g <= ushort.MaxValue; g++) yield return ((ushort)g, first + g - start);
            }
        }
    }

    private static int U16(byte[] t, int o) => t[o] << 8 | t[o + 1];
    private static uint U32(byte[] t, int o) => (uint)(t[o] << 24 | t[o + 1] << 16 | t[o + 2] << 8 | t[o + 3]);

    private const uint GdiError = 0xFFFFFFFF;

    [DllImport("gdi32.dll")]
    private static extern uint GetFontData(IntPtr hdc, uint table, uint offset, byte[]? buffer, uint length);
}
