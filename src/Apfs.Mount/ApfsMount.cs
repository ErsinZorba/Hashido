using Apfs.Container;
using Apfs.Volume;
using Fsp;

namespace Apfs.Mount;

/// <summary>
/// Bir APFS birimini sürücü harfine bağlar ve ayırır. Bağlama başarılı olunca birim sürücünündür: ayrılınca atılır (şifreli birimin
/// şifreleyicisi bellekte kalmasın; 1.9.1); bağlama düşerse birim çağıranda kalır.
/// </summary>
public sealed class ApfsMount : IDisposable
{
    private readonly FileSystemHost _host;
    private readonly ApfsVolume _volume;

    private ApfsMount(FileSystemHost host, ApfsFileSystem fs, string mountPoint, ApfsVolume volume)
    {
        _host = host;
        FileSystem = fs;
        MountPoint = mountPoint;
        _volume = volume;
    }

    public ApfsFileSystem FileSystem { get; }
    public string MountPoint { get; }

    /// <param name="mountPoint">"X:" biçiminde sürücü harfi; null ise <see cref="DriveLetterAllocator"/> seçer (T'den F'ye geriye).</param>
    /// <param name="onLog">Yalnız günlüğe gidecek bilgi (<see cref="ApfsFileSystem.Log"/>; bildirim değil).</param>
    public static ApfsMount Mount(ApfsVolume volume, string? mountPoint, Action<string>? onError = null, Action<string>? onLog = null)
    {
        var c = volume.Container;
        // APFS birimleri kapsayıcının alanını paylaşır: her birimde toplam = kapsayıcı boyutu, boş = kapsayıcıdaki boş alan.
        long total = (long)c.Superblock.BlockCount * c.BlockSize;
        var fs = new ApfsFileSystem(volume, total, c.FreeBytes);
        if (onError is not null) fs.Error += onError;
        if (onLog is not null) fs.Log += onLog;

        FileSystemHost host;
        try
        {
            host = DriveLetterAllocator.MountHost(fs, mountPoint);
        }
        catch (DllNotFoundException e)
        {
            throw new ApfsException(ApfsErrorCode.Unsupported, CoreText.U("Mount.WinFspMissing"), e);
        }
        catch (TypeInitializationException e)
        {
            throw new ApfsException(ApfsErrorCode.Unsupported,
                CoreText.U("Mount.WinFspNotLoaded", e.InnerException?.Message ?? e.Message), e);
        }
        return new ApfsMount(host, fs, host.MountPoint(), volume);
    }

    public void Dispose()
    {
        _host.Unmount();
        _host.Dispose();
        _volume.Dispose(); // sürücü ayrıldı, okuma bitti (ayırma düşerse birim atılmaz: sürücü hâlâ okuyor olabilir)
    }
}
