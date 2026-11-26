using Apfs.Write;
using Fsp;

namespace Apfs.Mount;

/// <summary>
/// Bir birimi YAZILABİLİR sürücü harfine bağlar ve ayırır. Ayırma sırası:
/// WinFsp ayrılır (yazılabilir sürücüde Windows önbelleği kapalı olduğu için bütün veri zaten gelmiştir; ayırmadan sonra hiçbir
/// çağrı gelmez) → oturum kapatılır (son commit + blok 0). Aygıtın sahibi çağırandır.
/// </summary>
public sealed class ApfsWritableMount : IDisposable
{
    private readonly Action _unmount;
    private readonly WriteSession _session;
    private readonly bool _closeSession;
    private bool _disposed;

    private ApfsWritableMount(Action unmount, ApfsWritableFileSystem fs, WriteSession session, string mountPoint, bool closeSession)
    {
        _unmount = unmount;
        _session = session;
        _closeSession = closeSession;
        FileSystem = fs;
        MountPoint = mountPoint;
    }

    /// <summary>Testler: WinFsp'siz; ayırma <paramref name="unmount"/> (ör. atan) ile.</summary>
    internal static ApfsWritableMount ForTest(ApfsWritableFileSystem fs, WriteSession session, Action unmount, bool closeSession = true) =>
        new(unmount, fs, session, "T:", closeSession);

    public ApfsWritableFileSystem FileSystem { get; }
    public string MountPoint { get; }

    /// <param name="closeSession">true ise ayırırken oturum da kapatılır (tek birimli kullanım); false ise oturumu çağıran kapatır.</param>
    /// <param name="onChangeLost">Değişiklik kaybı (<see cref="ApfsWritableFileSystem.ChangeLost"/>); verilmezse
    /// <paramref name="onError"/>'a gider.</param>
    /// <param name="onFaulted">Oturumun commit'i yarıda kaldı (<see cref="ApfsWritableFileSystem.Faulted"/>, oturum başına bir kez);
    /// verilirse bu hata <paramref name="onChangeLost"/>'a gitmez. Bir WinFsp geri çağrısının içinden gelir: dinleyici sürücü
    /// AYIRMAMALI (kilitlenir), işi başka bir iş parçacığına bırakmalı.</param>
    /// <param name="onLog">Yalnız günlüğe gidecek bilgi (<see cref="ApfsWritableFileSystem.Log"/>; bildirim değil).</param>
    public static ApfsWritableMount Mount(WriteSession session, int volumeIndex, string? mountPoint, Action<string>? onError = null, bool closeSession = true,
        Action<string>? onChangeLost = null, Action<string>? onFaulted = null, Action<string>? onLog = null)
    {
        var fs = new ApfsWritableFileSystem(session, volumeIndex);
        if (onError is not null) fs.Error += onError;
        if ((onChangeLost ?? onError) is { } lost) fs.ChangeLost += lost;
        if (onFaulted is not null) fs.Faulted += onFaulted;
        if (onLog is not null) fs.Log += onLog;
        FileSystemHost host;
        try
        {
            host = DriveLetterAllocator.MountHost(fs, mountPoint); // null: T'den F'ye geriye (düz "X:", Geri Dönüşüm Kutusu yok)
        }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException)
        {
            fs.Dispose();
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.T("Fs.WinFspMissing"), e);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
        return new ApfsWritableMount(() => { host.Unmount(); host.Dispose(); }, fs, session, host.MountPoint(), closeSession);
    }

    /// <summary>
    /// Ayırır ve oturumu kapatır (son değişiklikler kalıcı olur, disk "düzgün kapatıldı" işaretlenir). Ayırma atsa da sürücünün
    /// zamanlayıcısı durur ve oturum kapanır (1.9.1; önceden ikisi de atlanıyor, oturum açık kalıyordu); ayırmanın istisnası
    /// yine çağırana gider. Sürücü sonradan çağrı alırsa durdurulmuş sürücü olarak reddeder (bkz. <see cref="ApfsWritableFileSystem"/>).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _unmount(); }
        finally
        {
            try { FileSystem.Dispose(); }
            finally { if (_closeSession) _session.Close(); }
        }
    }
}
