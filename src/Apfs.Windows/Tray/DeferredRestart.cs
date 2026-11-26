namespace Apfs.Windows.Tray;

/// <summary>
/// Dil ya da tasarım değişince tepsinin yeniden başlaması (1.9.1): süren bir iş varken (güncelleme "Kur" akışı, bu tepsinin
/// başlattığı setup, açık kutu, çıkarma, yazma geçişi, kilit açma, tarama; <see cref="TrayModel.RestartMustWait"/>) ERTELENİR,
/// iş bitince bir kez yapılır. Önceden koşulsuz hemen yeniden başlıyordu: Kur'un indirmesi ya da çıkardığı diskleri geri bağlaması
/// yarıda kalıyordu. Koşul, çağıranın olaylarında (<see cref="Recheck"/>) ve her saniye yeniden sorulur (açık kutu gibi bazı işlerin
/// bitişi olay vermez). Yalnız arayüz iş parçacığında kullanılır.
/// </summary>
public sealed class DeferredRestart : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly Func<bool> _mustWait;
    private readonly Action _restart;
    private readonly UiTimer _timer;

    /// <param name="mustWait">Şu an yeniden başlamak süren bir işi keser mi.</param>
    /// <param name="restart">Yeniden başlatır (en çok bir kez çağrılır, istek başına).</param>
    public DeferredRestart(SynchronizationContext ui, Func<bool> mustWait, Action restart, TimeProvider? time = null)
    {
        _ui = ui;
        _mustWait = mustWait;
        _restart = restart;
        _timer = new UiTimer(ui, TimeSpan.FromSeconds(1), time);
        _timer.Tick += Recheck;
    }

    /// <summary>İstendi, iş bitmesi bekleniyor.</summary>
    public bool Pending { get; private set; }

    /// <summary>Yeniden başlama istendi (seçim zaten kaydedildi): iş yoksa hemen, varsa bitince.</summary>
    public void Request()
    {
        if (_disposed) return;
        if (_mustWait())
        {
            Pending = true;
            _timer.Start();
            // Bir sonraki arayüz turunda da bakılır: isteyen Ayarlar sayfası kapanırken kendi "açık kutu" sayısını ancak o zaman bırakır
            _ui.Post(_ => Recheck(), null);
            return;
        }
        Fire();
    }

    /// <summary>Bekleyen istek varsa ve iş bittiyse şimdi yeniden başlatır.</summary>
    public void Recheck()
    {
        if (!_disposed && Pending && !_mustWait()) Fire();
    }

    private void Fire()
    {
        Pending = false;
        _timer.Stop();
        _restart();
    }

    private bool _disposed;

    /// <summary>Tepsi kapanıyor: bekleyen istek düşer; sonra gelen olay ya da kuyruktaki denetim yeniden başlatmaz.</summary>
    public void Dispose()
    {
        _disposed = true;
        Pending = false;
        _timer.Dispose();
    }
}
