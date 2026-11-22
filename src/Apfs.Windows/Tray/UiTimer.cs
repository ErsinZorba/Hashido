namespace Apfs.Windows.Tray;

/// <summary>
/// Arayüz bağlamında tıklayan zamanlayıcı (winuiPlan P1; WinForms zamanlayıcısının (<c>Forms.Timer</c>) eşi, kütüphaneden
/// bağımsız): süre <see cref="TimeProvider"/>'dan, tık <see cref="SynchronizationContext.Post"/> ile arayüz iş parçacığında.
/// WinForms zamanlayıcısı gibi: bir tık kuyrukta beklerken yenisi eklenmez (arayüz meşgulken tıklar birikmez), durdurulunca ya
/// da elden çıkarılınca kuyruktaki tık çalışmaz, zaten çalışırken <see cref="Start"/> sayacı sıfırlamaz (sıfırlamak için önce
/// <see cref="Stop"/>). Fark: tık sıradan bir gönderimdir (WM_TIMER gibi kuyruğun en sonunda değil); görünür etkisi yok.
/// </summary>
public sealed class UiTimer : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Lock _gate = new();
    private ITimer? _timer;
    private bool _enabled;   // yalnız arayüz iş parçacığında değişir
    private bool _disposed;
    private int _generation; // her başlatma/durdurmada artar (ilk kuşak 1): eski sayaçtan kalan tık yenisinin yerine çalışmasın
    private int _pendingFor; // kuyrukta tıkı bekleyen kuşak; 0 = yok (iş parçacıkları arası)

    public UiTimer(SynchronizationContext ui, TimeSpan interval, TimeProvider? time = null)
    {
        _ui = ui;
        _interval = interval;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Süre doldu (arayüz iş parçacığında).</summary>
    public event Action? Tick;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed || value == _enabled) return;
            _enabled = value;
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _generation++;
                if (value)
                {
                    int generation = _generation;
                    _timer = _time.CreateTimer(_ => Fire(generation), null, _interval, _interval);
                }
            }
        }
    }

    public void Start() => Enabled = true;

    public void Stop() => Enabled = false;

    /// <summary>
    /// Sayaç doldu (iş parçacığı havuzunda). Kuyrukta zaten bir tık varsa eklenmez. Bayrak onu koyan kuşağındır ve kendi geri
    /// çağrısı onu her durumda temizler: durdurulduktan hemen sonra gelen geç bir tık yeni sayacı takılı bırakamaz (bağımsız
    /// denetçinin bulduğu yarış; <c>UiTimerTests</c>), en çok yenisinin bir tıkı düşer.
    /// </summary>
    private void Fire(int generation)
    {
        if (Interlocked.CompareExchange(ref _pendingFor, generation, 0) != 0) return;
        try
        {
            _ui.Post(_ =>
            {
                Interlocked.CompareExchange(ref _pendingFor, 0, generation);
                bool current;
                lock (_gate) current = generation == _generation;
                if (current && _enabled && !_disposed) Tick?.Invoke();
            }, null);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Arayüz bağlamı kapandı (çıkışta havadaki tık; WinForms tutamacı yok): tık anlamsız, düşer
            Interlocked.CompareExchange(ref _pendingFor, 0, generation);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _generation++;
        }
    }
}
