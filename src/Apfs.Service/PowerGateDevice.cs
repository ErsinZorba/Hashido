using Apfs.IO;
using Apfs.Windows;

namespace Apfs.Service;

/// <summary>
/// Yazma oturumunun aygıtı ile yazılabilir aygıt arasındaki uyku kapısı (1.9.1). Uykudan önce hizmet canlı BÜTÜN yazma
/// oturumlarının kapısını DONDURUR (<see cref="Freeze"/>; yayımlanmış, yazmaya açılmakta ya da kaydederek kapanmakta olan): bundan sonra
/// aygıta tek bayt gitmez — bütçeyi aşıp uyku sırasında yarıda kalan kayıt uyanışta sürse de (bir sonraki yazımında atar, oturum arızalı
/// olur; diskteki önceki checkpoint sağlam), blok 0 da yazılmaz. Uykudayken disk başka bir bilgisayarda değişmiş olabilir; kapı bunun
/// güvencesidir. Okuma hep geçer. Alttaki aygıtın sahibi değildir (oturumun kaynak listesi bırakır). Diğer yazılabilir aygıtlar gibi yalnız
/// bir yazma izniyle kurulur.
/// </summary>
internal sealed class PowerGateDevice(IWritableBlockDevice inner, WriteGrant grant) : IWritableBlockDevice
{
    private readonly WriteGrant _grant = grant;
    private readonly Lock _sync = new();
    private bool _frozen;
    private int _inFlight; // _sync altında: kapıyı geçmiş, sürmekte olan yazma/boşaltma

    /// <summary>Yazmaları kalıcı olarak durdurur (oturum bırakılana dek; geri alınmaz). Kapıyı daha önce geçmiş yazma sürebilir (<see cref="WaitIdle"/>).</summary>
    public void Freeze()
    {
        lock (_sync) _frozen = true;
    }

    public bool Frozen
    {
        get { lock (_sync) return _frozen; }
    }

    /// <summary>Kapıyı geçmiş yazma/boşaltmanın bitmesini en çok <paramref name="wait"/> bekler; true: sürmekte olan kalmadı.</summary>
    public bool WaitIdle(TimeSpan wait)
    {
        long until = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            lock (_sync) if (_inFlight == 0) return true;
            if (Environment.TickCount64 >= until) return false;
            Thread.Sleep(5);
        }
    }

    private void Enter()
    {
        lock (_sync)
        {
            if (_frozen) throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Svc.WritesFrozenForSleep"));
            _inFlight++;
        }
    }

    private void Leave()
    {
        lock (_sync) _inFlight--;
    }

    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        Enter();
        try { inner.Write(offset, data); }
        finally { Leave(); }
    }

    public void Flush()
    {
        Enter();
        try { inner.Flush(); }
        finally { Leave(); }
    }

    public void Dispose() => GC.KeepAlive(_grant); // alttaki aygıtı oturumun kaynak listesi bırakır
}
