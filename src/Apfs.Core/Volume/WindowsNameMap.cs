using System.Globalization;
using Apfs.Util;

namespace Apfs.Volume;

/// <summary>
/// 1.8.2: bir klasörün Windows'ta görünen adları benzersiz ve geri çevrilebilir. SFM eşlemesi (<see cref="WindowsNames"/>)
/// macOS'ta geçerli olup Windows'ta yasak karakterleri U+F001–U+F029'a eşler. Adında bu aralıktan karakter TAŞIYAN Mac adı
/// ("tuhaf" ad; ör. bir SMB kopyasından kalma <c>ab</c>) eşlemeden geri dönmez: aynı klasörde <c>a:b</c> varsa ikisi aynı
/// Windows adına düşüyor, ikincisine ulaşılamıyordu (tek başına da aranınca bulunamıyordu).
/// <para>
/// Kural: Windows adı önce bugünkü gibi aranır (eşlemeden geri çevrilmiş ad, birimin ad kurallarıyla; tuhaf bir girdi bu yolla
/// asla bulunmaz); bulunamazsa klasörün haritasına bakılır. Tuhaf girdi kendi Windows adıyla görünür — o adın bugünkü araması bir
/// şey bulmuyorsa ve ad başka bir tuhaf girdiye verilmediyse (sıra: Mac adının sıralı karşılaştırması) —; yoksa adın
/// uzantısından önce <c>~2</c> eklenmiş adla (gerekirse artan: klasördeki bir adla ya da verilmiş bir adla çakışmayan ilk sayı).
/// Böylece her girdi görünür (kopyada sessiz eksik dosya yok) ve gösterilen ad doğru girdiye gider. Haritadan bulunan girdiye
/// yazılabilir sürücüde dokunulmaz (adla yapılan işlemler eşlemeden geri çevrilmiş adı hedefler).
/// </para>
/// <para>
/// 1.9.1: geçersiz UTF-8 baytlarının kaçış aralığı (U+F680–U+F6FF, <see cref="Bin.Utf8NameZ"/>) da tuhaf: aynı aralıktan
/// karakter taşıyan geçerli bir Mac adı geçersiz baytlı bir adla AYNI dizgeye düşebilir (ikiz). Bu aralık eşlemeden değişmediği için
/// adın kendisi bugünkü aramayla da bulunur; tuhaf girdiyi bulan arama yine haritaya bakar (kilitli), ikizlerden biri kendi adıyla,
/// öbürü <c>~2</c> ile görünür. Gösterilen ad girdinin kendisine bağlıdır (ada değil).
/// </para>
/// Harita klasör dizini başına ilk gerekince bir kez kurulur (yalnız tuhaf adlar için; çoğu klasörde boş), yazma oturumu dizini
/// değiştirince yeniden.
/// </summary>
public sealed class WindowsNameMap
{
    /// <summary>Windows'ta bir ad bileşeninin en çok uzunluğu (sürücülerin MaxComponentLength'i).</summary>
    private const int MaxComponentLength = 255;

    private static readonly WindowsNameMap Empty = new(new Dictionary<string, DirEntry>(), new Dictionary<DirEntry, string>(), 0, null);

    private readonly Dictionary<string, DirEntry> _byWindows; // gösterilen ad (birimin harf kuralıyla) → tuhaf girdi
    private readonly Dictionary<DirEntry, string> _display;   // tuhaf girdi → gösterilen ad (ikizler aynı ada sahip olabilir)

    private WindowsNameMap(Dictionary<string, DirEntry> byWindows, Dictionary<DirEntry, string> display, int suffixed, string? firstSuffixed)
    {
        _byWindows = byWindows;
        _display = display;
        SuffixedCount = suffixed;
        FirstSuffixed = firstSuffixed;
    }

    /// <summary>Klasörde tuhaf ad var mı (yoksa harita boştur ve sıradan ad eklenip silinince de boş kalır).</summary>
    internal bool HasOdd => !ReferenceEquals(this, Empty);

    /// <summary>Ek alarak gösterilen girdi sayısı (günlük için).</summary>
    public int SuffixedCount { get; }

    /// <summary>Ek alarak gösterilen ilk girdinin gösterilen adı (günlük için).</summary>
    public string? FirstSuffixed { get; }

    /// <summary>
    /// Adında SFM aralığından (U+F001–U+F029; böyle bir ad eşlemeden geri dönmez) ya da geçersiz UTF-8 kaçış aralığından
    /// (U+F680–U+F6FF; ikiz olabilir) karakter var mı.
    /// </summary>
    public static bool IsOdd(string apfsName)
    {
        foreach (char c in apfsName)
            if (c is >= (char)0xF001 and <= (char)0xF029 or >= Bin.NameEscapeFirst and <= Bin.NameEscapeLast) return true;
        return false;
    }

    /// <summary>Klasörün haritası (ilk çağrıda kurulur; aynı anda iki kez kurulması zararsızdır).</summary>
    public static WindowsNameMap Of(DirectoryIndex dir) => dir.WindowsMap ??= Build(dir);

    /// <summary>
    /// Windows adının girdisi; <paramref name="canonical"/> = bugünkü yolla bulundu (Mac adı eşlemeden geri çevrilmiş ad: adla yapılan
    /// işlemler bu girdiyi hedefler). false: haritadan (tuhaf ad; yazılabilir sürücüde dokunulmaz).
    /// </summary>
    public static DirEntry? Find(DirectoryIndex dir, string windowsName, out bool canonical)
    {
        if (dir.Find(WindowsNames.FromWindows(windowsName)) is { } e && !IsOdd(e.Name))
        {
            canonical = true;
            return e;
        }
        canonical = false;
        return Of(dir)._byWindows.TryGetValue(windowsName, out var odd) ? odd : null;
    }

    /// <summary>Girdinin Windows'ta gösterilen adı.</summary>
    public static string Display(DirectoryIndex dir, DirEntry e) =>
        IsOdd(e.Name) && Of(dir)._display.TryGetValue(e, out var shown) ? shown : WindowsNames.ToWindows(e.Name);

    private static WindowsNameMap Build(DirectoryIndex dir)
    {
        List<DirEntry>? odd = null;
        foreach (var e in dir.Entries)
            if (IsOdd(e.Name)) (odd ??= []).Add(e);
        if (odd is null) return Empty;
        odd.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        IEqualityComparer<string> cmp = dir.CaseInsensitive ? FoldComparer.Instance : StringComparer.Ordinal;
        var byWindows = new Dictionary<string, DirEntry>(cmp);
        var display = new Dictionary<DirEntry, string>();
        var shadowed = new List<(DirEntry Entry, string Windows)>();
        foreach (var e in odd)
        {
            string w = WindowsNames.ToWindows(e.Name);
            // Adın bugünkü araması başka bir girdi bulmuyorsa (kaçış aralıklı ad kendini bulur; ikizinden yalnız dizindeki ilki)
            var same = dir.Find(WindowsNames.FromWindows(w));
            if ((same is null || ReferenceEquals(same, e)) && byWindows.TryAdd(w, e)) display[e] = w;
            else shadowed.Add((e, w));
        }
        var next = new Dictionary<string, int>(cmp);
        foreach (var (e, w) in shadowed)
        {
            int k = next.TryGetValue(w, out int n) ? n : 2;
            string candidate;
            do candidate = WithSuffix(w, k++);
            while (byWindows.ContainsKey(candidate) || dir.Find(WindowsNames.FromWindows(candidate)) is not null);
            next[w] = k;
            byWindows[candidate] = e;
            display[e] = candidate;
        }
        return new WindowsNameMap(byWindows, display, shadowed.Count, shadowed.Count > 0 ? display[shadowed[0].Entry] : null);
    }

    /// <summary>
    /// Harf duyarsız birimde Windows adlarının karşılaştırması birimin kuralıyla: katlanmış anahtar (<see cref="ApfsNames.FoldKey"/>;
    /// NFD + tam Unicode katlaması). OrdinalIgnoreCase değil (mimari kuralı): uzunluk değiştiren katlamaları ("İ" → "i̇", "ß" → "ss")
    /// ve Kelvin işaretini birimin aynı saydığı biçimle eşleştirmiyordu (1.9.1).
    /// </summary>
    private sealed class FoldComparer : IEqualityComparer<string>
    {
        public static readonly FoldComparer Instance = new();

        public bool Equals(string? a, string? b) =>
            a is null ? b is null : b is not null && string.Equals(ApfsNames.FoldKey(a), ApfsNames.FoldKey(b), StringComparison.Ordinal);

        public int GetHashCode(string s) => string.GetHashCode(ApfsNames.FoldKey(s), StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="windowsName"/>'e uzantısından önce <c>~k</c> ekler (uzantı: baştaki nokta sayılmadan son noktadan sonrası);
    /// sonuç 255 karakteri aşarsa adın gövdesi kısaltılır (vekil çifti bölünmez).
    /// </summary>
    internal static string WithSuffix(string windowsName, int k)
    {
        string suffix = "~" + k.ToString(CultureInfo.InvariantCulture);
        int dot = windowsName.LastIndexOf('.');
        bool hasExt = dot > 0 && dot < windowsName.Length - 1;
        string stem = hasExt ? windowsName[..dot] : windowsName, ext = hasExt ? windowsName[dot..] : "";
        if (ext.Length + suffix.Length >= MaxComponentLength) { stem = windowsName; ext = ""; }
        int room = MaxComponentLength - ext.Length - suffix.Length;
        if (stem.Length > room)
        {
            stem = stem[..room];
            if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        }
        return stem + suffix + ext;
    }
}
