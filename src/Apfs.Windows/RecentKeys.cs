namespace Apfs.Windows;

/// <summary>
/// Anahtar başına "bu süre içinde bir kez" seyreltmesi (hizmetin bildirim ve günlük seyreltmesi; 1.8.2). Çağıran kendi
/// kilidini tutar (iş parçacığı güvenli değil).
/// </summary>
/// <remarks>
/// (1) Süre tekdüze saatle (<see cref="Environment.TickCount64"/>) ölçülür: önceden duvar saatiydi, saat geri alınınca aynı ileti
/// saat yeniden o ana gelene dek susuyordu. (2) Anahtar sayısı sınırlı: anahtar dosya adı içeren iletidir, düşmanca bir
/// klasör sınırsız sayıda farklı ileti üretebiliyordu ve sözlük hiç temizlenmiyordu. Dolunca süresi geçmişler atılır; yine doluysa en
/// eskiler (sona en yakın) atılır — atılan anahtarın iletisi süresi dolmadan bir kez daha geçebilir, bu ancak süre içinde
/// <paramref name="capacity"/>'den çok FARKLI ileti gelince olur. Aynı iletinin ritmi (ör. değişiklik kaybı "dakikada bir") değişmez.
/// </remarks>
/// <param name="capacity">En çok anahtar.</param>
/// <param name="clock">Yalnız testler: milisaniye saati (varsayılan <see cref="Environment.TickCount64"/>).</param>
public sealed class RecentKeys(int capacity = RecentKeys.DefaultCapacity, Func<long>? clock = null)
{
    public const int DefaultCapacity = 4096;

    private readonly Dictionary<string, long> _until = new(StringComparer.Ordinal);
    private readonly Func<long> _now = clock ?? (() => Environment.TickCount64);

    public int Count => _until.Count;

    /// <summary>
    /// <paramref name="key"/> son <paramref name="window"/> içinde işaretlenmediyse şimdi işaretler ve true döner; işaretlendiyse
    /// false (işaret değişmez: süre ilk işaretten sayılır).
    /// </summary>
    public bool TryMark(string key, TimeSpan window)
    {
        long now = _now();
        if (_until.TryGetValue(key, out long until) && now < until) return false;
        if (_until.Count >= capacity && !_until.ContainsKey(key)) Prune(now);
        _until[key] = now + (long)window.TotalMilliseconds;
        return true;
    }

    private void Prune(long now)
    {
        foreach (var k in _until.Where(p => p.Value <= now).Select(p => p.Key).ToList()) _until.Remove(k);
        if (_until.Count < capacity) return;
        // Hepsi süre içinde: en eskilerin dörtte biri atılır (her yeni anahtarda yeniden sıralanmasın)
        int drop = _until.Count - capacity * 3 / 4;
        foreach (var k in _until.OrderBy(p => p.Value).Take(drop).Select(p => p.Key).ToList()) _until.Remove(k);
    }
}
