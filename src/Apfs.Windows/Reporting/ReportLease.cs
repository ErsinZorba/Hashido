namespace Apfs.Windows.Reporting;

/// <summary>
/// Hizmet kuyruğunu aynı anda tek tepsinin çekmesi (1.9.0): iki kullanıcı oturumundaki iki tepsi aynı olayları çekip ikisi de
/// gönderirse sunucuya çift kayıt giderdi. "reports-pull" ve "reports-ack" isteği tepsinin kira simgesini taşır; başka bir simge
/// kirayı tutarken (son isteğinden bu yana <see cref="Duration"/> geçmediyse) çekiş boş sayfa alır, tepsi sonraki turda yeniden dener.
/// Kirayı tutan her isteğiyle süreyi yeniler. Simgesiz istek (eski tepsi) kiraya bakmaz ve dokunmaz (bugünkü davranış).
/// </summary>
public sealed class ReportLease(Func<long>? milliseconds = null)
{
    /// <summary>Bir gönderim turunda iki istek arası en çok ~10 sn POST + 10 sn kanal; bolca pay.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(2);

    private readonly Func<long> _now = milliseconds ?? (() => Environment.TickCount64);
    private readonly Lock _gate = new();
    private string? _holder;
    private long _until;

    /// <summary>Simge kirayı alır ya da yeniler; başkası tutuyorsa false. Simge null ise her zaman true (kira değişmez).</summary>
    public bool TryHold(string? token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        lock (_gate)
        {
            long now = _now();
            if (_holder is not null && _holder != token && now < _until) return false;
            _holder = token;
            _until = now + (long)Duration.TotalMilliseconds;
            return true;
        }
    }
}
