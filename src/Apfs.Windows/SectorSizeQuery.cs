using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// Ham diskin mantıksal sektör boyu (okuyucu <see cref="RawDiskDevice"/> ve yazıcı <see cref="RawDiskWriter"/> aynı yoldan; 1.9.1).
/// Önce <c>IOCTL_DISK_GET_DRIVE_GEOMETRY_EX</c>; düşerse ya da değer tanınmazsa <c>IOCTL_STORAGE_QUERY_PROPERTY</c>
/// (StorageAccessAlignmentProperty, BytesPerLogicalSector); o da olmazsa 512. Önceden geometri düşünce doğrudan 512 deniyordu:
/// 4Kn diskte hizasız G/Ç reddedilir. Eşzamanlı tanıtıcıda çağrılır (bindirmeli tanıtıcıda OVERLAPPED'sız DeviceIoControl tanımsız).
/// </summary>
internal static class SectorSizeQuery
{
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageAccessAlignmentProperty = 6;

    public static int Query(SafeFileHandle handle)
    {
        Span<byte> buf = stackalloc byte[256];
        int? geometry = DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(buf), 256, out uint n, IntPtr.Zero)
            ? ParseGeometry(buf[..(int)Math.Min(n, 256)]) : null;
        if (geometry is not null) return geometry.Value;
        Span<byte> query = stackalloc byte[12]; // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (PropertyStandardQuery = 0), ek
        query.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(query, StorageAccessAlignmentProperty);
        int? alignment;
        unsafe
        {
            fixed (byte* q = query)
                alignment = DeviceIoControl(handle, IoctlStorageQueryProperty, (IntPtr)q, 12, ref MemoryMarshal.GetReference(buf), 256, out n, IntPtr.Zero)
                    ? ParseAlignment(buf[..(int)Math.Min(n, 256)]) : null;
        }
        return Choose(geometry, alignment);
    }

    internal static int Choose(int? geometry, int? alignment) => geometry ?? alignment ?? 512;

    /// <summary>DISK_GEOMETRY_EX.Geometry.BytesPerSector: Cylinders(8) + MediaType(4) + TracksPerCylinder(4) + SectorsPerTrack(4).</summary>
    internal static int? ParseGeometry(ReadOnlySpan<byte> returned) =>
        returned.Length >= 24 && BinaryPrimitives.ReadInt32LittleEndian(returned[20..]) is int bps && Known(bps) ? bps : null;

    /// <summary>STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR: Version, Size, BytesPerCacheLine, BytesOffsetForCacheAlignment, BytesPerLogicalSector.</summary>
    internal static int? ParseAlignment(ReadOnlySpan<byte> returned) =>
        returned.Length >= 20 && BinaryPrimitives.ReadInt32LittleEndian(returned[16..]) is int bps && Known(bps) ? bps : null;

    private static bool Known(int bps) => bps is 512 or 1024 or 2048 or 4096;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        ref byte lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
