using Apfs.Util;

namespace Apfs.Volume;

/// <summary>
/// Bir klasörün bellek içi dizini. Arama sırası:
/// 1. Birebir ad (her zaman önce; aynı anahtara düşen birden çok ad varsa doğru olanı seçer).
/// 2. Büyük/küçük harf duyarsız birimde: katlanmış anahtar (NFD + tam Unicode katlaması).
///    Duyarlı birimde: NFD anahtarı; o da tutmazsa ve TEK bir aday varsa katlanmış anahtar
///    (Windows uygulamaları adı farklı harf büyüklüğüyle isteyebilir; birden çok aday varsa tahmin yapılmaz).
/// Arama B-ağacındaki ad karmasına dayanmaz: karma hesabındaki olası bir Unicode sürüm farkı dosyayı "bulunamaz" yapmaz.
/// Yazma oturumunun görüntüsünde dizin yerinde güncellenir (<see cref="SessionAdd"/>/<see cref="SessionRemove"/>).
/// <para>
/// <see cref="CompactThreshold"/> ya da daha çok girdili klasör SIKI dizinle tutulur: anahtar metinleri ve aday listeleri
/// saklanmaz, her anahtar türü için yalnız karma + zincir dizileri (girdi başına ~40 bayt); arama sonucu ve sırası aynıdır.
/// </para>
/// </summary>
public sealed class DirectoryIndex
{
    /// <summary>
    /// Bu sayıda ya da daha çok girdili klasör sıkı dizinle tutulur: birimin klasör önbelleğinin toplam sınırı (500.000 girdi) da
    /// budur (ağırlık girdi sayısı + 1: böyle bir klasör önbelleğe sığmaz); daha küçük klasörlerin dizini değişmez.
    /// </summary>
    internal const int CompactThreshold = 500_000;

    private readonly bool _caseInsensitive;
    private readonly List<DirEntry> _entries;
    private readonly Dictionary<string, DirEntry>? _exact;
    private readonly Dictionary<string, List<DirEntry>>? _normalized;
    private readonly Dictionary<string, List<DirEntry>>? _folded;
    private readonly KeyChain? _exactChain, _normalizedChain, _foldedChain;

    public DirectoryIndex(List<DirEntry> entries, bool caseInsensitive)
        : this(entries, caseInsensitive, compact: entries.Count >= CompactThreshold) { }

    internal DirectoryIndex(List<DirEntry> entries, bool caseInsensitive, bool compact)
    {
        _entries = entries;
        _caseInsensitive = caseInsensitive;
        if (compact)
        {
            _exactChain = new KeyChain(static n => n);
            _normalizedChain = new KeyChain(ApfsNames.NormalizationKey);
            _foldedChain = new KeyChain(ApfsNames.FoldKey);
            Rebuild();
            return;
        }
        _exact = new(entries.Count, StringComparer.Ordinal);
        _normalized = new(entries.Count, StringComparer.Ordinal);
        _folded = new(entries.Count, StringComparer.Ordinal);
        foreach (var e in entries) Index(e);
    }

    public IReadOnlyList<DirEntry> Entries => _entries;

    /// <summary>Harf büyüklüğüne duyarsız birim mi (Windows adlarının karşılaştırması da öyle; <see cref="WindowsNameMap"/>).</summary>
    public bool CaseInsensitive => _caseInsensitive;

    /// <summary>Windows adı haritası (ilk gerekince kurulur, dizin değişince silinir).</summary>
    internal WindowsNameMap? WindowsMap;

    /// <summary>Sıkı dizin mi (testler ve birimin büyük klasör yuvası için).</summary>
    internal bool IsCompact => _exactChain is not null;

    private void Index(DirEntry e)
    {
        _exact!.TryAdd(e.Name, e);
        AddCandidate(_normalized!, ApfsNames.NormalizationKey(e.Name), e);
        AddCandidate(_folded!, ApfsNames.FoldKey(e.Name), e);
    }

    private void Rebuild()
    {
        _exactChain!.Build(_entries);
        _normalizedChain!.Build(_entries);
        _foldedChain!.Build(_entries);
    }

    public DirEntry? Find(string name)
    {
        if (IsCompact)
        {
            if (_exactChain!.First(_entries, name) is { } x) return x;
            if (_caseInsensitive) return _foldedChain!.First(_entries, ApfsNames.FoldKey(name));
            if (_normalizedChain!.Single(_entries, ApfsNames.NormalizationKey(name), out var one)) return one;
            return _foldedChain!.Single(_entries, ApfsNames.FoldKey(name), out var g) ? g : null;
        }
        if (_exact!.TryGetValue(name, out var exact)) return exact;
        if (_caseInsensitive)
            return _folded!.TryGetValue(ApfsNames.FoldKey(name), out var f) ? f[0] : null; // birim geçerliyse çakışma olmaz
        if (_normalized!.TryGetValue(ApfsNames.NormalizationKey(name), out var n) && n.Count == 1) return n[0];
        return _folded!.TryGetValue(ApfsNames.FoldKey(name), out var h) && h.Count == 1 ? h[0] : null; // birden çok adayda null
    }

    /// <summary>
    /// Birimin KATI kuralıyla çakışan girdi: harf duyarsız birimde katlanmış ad, normalleştirmeye duyarsız birimde NFD,
    /// tam duyarlı birimde bayt bayt. (Arama kolaylığı uygulanmaz.)
    /// </summary>
    public DirEntry? FindCollision(string name, bool normalizationInsensitive)
    {
        if (IsCompact)
        {
            if (_caseInsensitive) return _foldedChain!.First(_entries, ApfsNames.FoldKey(name));
            if (normalizationInsensitive) return _normalizedChain!.First(_entries, ApfsNames.NormalizationKey(name));
            return _exactChain!.First(_entries, name);
        }
        if (_caseInsensitive) return _folded!.TryGetValue(ApfsNames.FoldKey(name), out var f) ? f[0] : null;
        if (normalizationInsensitive) return _normalized!.TryGetValue(ApfsNames.NormalizationKey(name), out var n) ? n[0] : null;
        return _exact!.TryGetValue(name, out var e) ? e : null;
    }

    private static void AddCandidate(Dictionary<string, List<DirEntry>> map, string key, DirEntry e)
    {
        if (map.TryGetValue(key, out var list)) list.Add(e); else map[key] = [e];
    }

    private static void RemoveCandidate(Dictionary<string, List<DirEntry>> map, string key, string name)
    {
        if (!map.TryGetValue(key, out var list)) return;
        list.RemoveAll(x => x.Name == name);
        if (list.Count == 0) map.Remove(key);
    }

    /// <summary>Yazma oturumu: girdi eklendi.</summary>
    internal void SessionAdd(DirEntry e)
    {
        SessionRemove(e.Name);
        // Harita yalnız tuhaf ad varsa ya da tuhaf ad eklenince değişebilir: büyük klasöre sıradan dosya kopyalarken her eklemede
        // klasör yeniden taranmasın (bağımsız inceleme)
        if (WindowsMap is { HasOdd: true } || WindowsNameMap.IsOdd(e.Name)) WindowsMap = null;
        _entries.Add(e);
        if (!IsCompact) { Index(e); return; }
        // Sona eklenen girdi zincirlerin de sonuna gider (sıra = listedeki sıra, sözlüklü dizindeki aday sırasıyla aynı)
        if (!(_exactChain!.Append(_entries) && _normalizedChain!.Append(_entries) && _foldedChain!.Append(_entries))) Rebuild();
    }

    /// <summary>Yazma oturumu: girdi silindi (diskteki gerçek adıyla).</summary>
    internal void SessionRemove(string name)
    {
        if (_entries.RemoveAll(x => x.Name == name) == 0) return;
        if (WindowsMap is { HasOdd: true }) WindowsMap = null; // tuhaf adı olmayan klasör silmeyle de öyle kalır
        if (IsCompact) { Rebuild(); return; } // konumlar kaydı: yeniden kur (büyük klasörde silme seyrek; O(n))
        _exact!.Remove(name);
        foreach (var other in _entries) if (other.Name == name) _exact.TryAdd(name, other);
        RemoveCandidate(_normalized!, ApfsNames.NormalizationKey(name), name);
        RemoveCandidate(_folded!, ApfsNames.FoldKey(name), name);
    }

    /// <summary>
    /// Bir anahtar türünün sıkı dizini: kova başları ve zincirler girdinin listedeki konumuyla (+1; 0 = yok), girdi başına anahtarın
    /// karması. Zincir konum sırasıyla artar: ilk eşleşen, sözlüklü dizindeki ilk adaydır. Anahtar metni saklanmaz; karması
    /// tutan adayın anahtarı aramada yeniden hesaplanır.
    /// </summary>
    private sealed class KeyChain(Func<string, string> keyOf)
    {
        private int[] _buckets = [];
        private int[] _next = [];
        private int[] _hash = [];
        private int _count;

        private static int Hash(string key) => string.GetHashCode(key, StringComparison.Ordinal);

        public void Build(List<DirEntry> entries)
        {
            int n = entries.Count;
            int size = 16;
            while (size < n + n / 2) size <<= 1; // doluluk ≤ 2/3
            _buckets = new int[size];
            _next = new int[Math.Max(16, n + n / 8)];
            _hash = new int[_next.Length];
            _count = n;
            // Sondan başa, başa ekleyerek: her zincir artan konum sırasıyla kalır
            for (int i = n - 1; i >= 0; i--)
            {
                int h = Hash(keyOf(entries[i].Name));
                int b = h & (size - 1);
                _hash[i] = h;
                _next[i] = _buckets[b];
                _buckets[b] = i + 1;
            }
        }

        /// <summary>Listenin son girdisini zincirinin sonuna ekler; yer yoksa false (çağıran yeniden kurar).</summary>
        public bool Append(List<DirEntry> entries)
        {
            int i = entries.Count - 1;
            if (i != _count || i >= _next.Length || (i + 1) * 3 > _buckets.Length * 2) return false;
            int h = Hash(keyOf(entries[i].Name));
            int b = h & (_buckets.Length - 1);
            _hash[i] = h;
            _next[i] = 0;
            if (_buckets[b] == 0) _buckets[b] = i + 1;
            else
            {
                int j = _buckets[b] - 1;
                while (_next[j] != 0) j = _next[j] - 1;
                _next[j] = i + 1;
            }
            _count++;
            return true;
        }

        /// <summary>Anahtarı tutan ilk girdi.</summary>
        public DirEntry? First(List<DirEntry> entries, string key)
        {
            int h = Hash(key);
            for (int j = _buckets[h & (_buckets.Length - 1)]; j != 0; j = _next[j - 1])
                if (_hash[j - 1] == h && string.Equals(keyOf(entries[j - 1].Name), key, StringComparison.Ordinal)) return entries[j - 1];
            return null;
        }

        /// <summary>Anahtarı tutan TEK girdi varsa o (ikinci eşleşmede durur).</summary>
        public bool Single(List<DirEntry> entries, string key, out DirEntry? found)
        {
            found = null;
            int h = Hash(key);
            for (int j = _buckets[h & (_buckets.Length - 1)]; j != 0; j = _next[j - 1])
            {
                if (_hash[j - 1] != h || !string.Equals(keyOf(entries[j - 1].Name), key, StringComparison.Ordinal)) continue;
                if (found is not null) { found = null; return false; }
                found = entries[j - 1];
            }
            return found is not null;
        }
    }
}
