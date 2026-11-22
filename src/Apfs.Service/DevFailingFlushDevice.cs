using Apfs.IO;

namespace Apfs.Service;

/// <summary>
/// Yalnız geliştirme modu (<c>--image</c> + <c>APFS_DEV_COMMIT_FAIL=1</c>): her yazma bariyeri hata verir, böylece yazma oturumunun
/// ilk commit'i düşer (açık depo bildirimi #1'in hizmet testleri). Yazmalar alttaki aygıta olduğu gibi gider. Diğer yazılabilir
/// aygıtlar gibi yalnız bir yazma izniyle kurulur.
/// </summary>
internal sealed class DevFailingFlushDevice(IWritableBlockDevice inner, WriteGrant grant) : IWritableBlockDevice
{
    private readonly WriteGrant _grant = grant;

    public long Length => inner.Length;
    public int SectorSize => inner.SectorSize;
    public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);
    public void Write(long offset, ReadOnlySpan<byte> data) => inner.Write(offset, data);
    public void Flush() => throw new IOException("APFS_DEV_COMMIT_FAIL");

    public void Dispose()
    {
        GC.KeepAlive(_grant);
        inner.Dispose();
    }
}
