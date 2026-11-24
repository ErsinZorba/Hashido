using Apfs.IO;

namespace Apfs.Service;

/// <summary>
/// Yalnız geliştirme modu (<c>--image</c>). <c>APFS_DEV_COMMIT_FAIL=1</c>: her yazma bariyeri hata verir, böylece yazma oturumunun
/// ilk commit'i düşer (açık depo bildirimi #1'in hizmet testleri). <c>APFS_DEV_BLOCK0_FAIL=1</c> (<paramref name="block0Only"/>):
/// yalnız aygıtın 0. baytına yazım hata verir — bölümsüz görüntüde kapatırken yazılan blok 0 (kapatmadaki blok 0 hatasının hizmet testi); bariyerler
/// çalışır. Öbür yazmalar alttaki aygıta olduğu gibi gider. Diğer yazılabilir aygıtlar gibi yalnız bir yazma izniyle kurulur.
/// </summary>
internal sealed class DevFailingFlushDevice(IWritableBlockDevice inner, WriteGrant grant, bool block0Only = false) : IWritableBlockDevice
{
    private readonly WriteGrant _grant = grant;

    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (block0Only && offset == 0) throw new IOException("APFS_DEV_BLOCK0_FAIL");
        inner.Write(offset, data);
    }

    public void Flush()
    {
        if (!block0Only) throw new IOException("APFS_DEV_COMMIT_FAIL");
        inner.Flush();
    }

    public void Dispose()
    {
        GC.KeepAlive(_grant);
        inner.Dispose();
    }
}
