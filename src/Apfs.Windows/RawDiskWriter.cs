using System.ComponentModel;
using System.Runtime.InteropServices;
using Apfs.IO;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// Fiziksel diske YAZABİLEN aygıt (\\.\PhysicalDriveN). Yalnızca yazma kapısının verdiği izinle kurulur; okuma yolundaki
/// <see cref="RawDiskDevice"/> değişmeden GENERIC_READ kalır. Yazmalar sektör hizalı olmalıdır (yazıcı her zaman 4 KiB'lık
/// bloklar yazar). <see cref="Flush"/> = FlushFileBuffers (diskin kendi önbelleği dahil kalıcı ortama boşaltma).
/// Not: Windows bölüm için bir birim (ör. RAW) bağlamışsa bölüm içine ham yazmayı engelleyebilir; bu durumda yazma
/// "erişim engellendi" ile başarısız olur ve hiçbir şey yazılmaz (VHD'de ölçüldü: Windows APFS bölümüne birim atamıyor,
/// ham yazma engellenmiyor).
/// </summary>
/// <remarks>
/// G/Ç süre sınırlı (1.9.1; okuyucudaki 1.8.2 deseni, <see cref="OverlappedIo"/>): yazma tanıtıcısı bindirmeli, her okuma/yazma
/// <see cref="IoTimeout"/> içinde bitmezse iptal edilir ve IoError (<c>Raw.WriteTimedOut</c> / <c>Raw.ReadTimedOut</c>) döner; iptal
/// de tutmazsa belleği bırakılmaz ve yol "yanıtsız" listesine girer. <see cref="Flush"/> ayrı iş parçacığında aynı süreyle (süre
/// dolunca <c>CancelSynchronousIo</c>; iş parçacığı tanıtıcıyı kendisi tutar ve sonradan bırakır). Zaman aşımı yazıcıya sıradan
/// aygıt arızasıdır: oturum kaydedilmeden bırakılır, diskteki önceki checkpoint sağlam (kopyala-yaz). Önceden takılan bir USB köprüsü
/// yazmayı oturum kilidi altında süresiz tutup taramayı, çıkarmayı ve Gezgin'i kilitliyordu. Uzunluk/geometri sorguları açılışta
/// AYRI, eşzamanlı bir tanıtıcıyla (bindirmeli tanıtıcıda OVERLAPPED'sız <c>DeviceIoControl</c> belgeye aykırı).
/// </remarks>
public sealed class RawDiskWriter : IWritableBlockDevice
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint ThreadTerminate = 0x0001; // CancelSynchronousIo için

    /// <summary>Bir okuma/yazmanın ya da diske boşaltmanın en uzun süresi (uyuyan diskin kalkması birkaç saniye sürer).</summary>
    public static readonly TimeSpan DefaultIoTimeout = TimeSpan.FromSeconds(30);

    private readonly SafeFileHandle _handle;

    public RawDiskWriter(int diskNumber, WriteGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Path = DevicePath(diskNumber);
        // Bir isteği iptali de tutmadığı için bırakılmış aygıta yeniden yazılmaz (1.9.1, inceleme): çekirdekte bekleyen eski yazma, köprü
        // toparlanınca yeni oturumun ayırdığı blokların üstüne inebilirdi (sessiz bozulma)
        if (RawDiskDevice.IsWriteStalled(Path))
            throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.WriteTimedOut", Path, (int)Math.Round(DefaultIoTimeout.TotalSeconds)));
        using (var info = OpenForWrite(Path, overlapped: false))
            (Length, SectorSize) = QueryInfo(info);
        _handle = OpenForWrite(Path, overlapped: true);
    }

    private static string DevicePath(int diskNumber) => $@"\\.\PhysicalDrive{diskNumber}";

    /// <summary>
    /// Açılmış tanıtıcıdan aygıt (testler: bilgi sorgusu başarısız olan tanıtıcı). Sorgu başarısız olursa tanıtıcı HEMEN kapanır
    /// (1.8.2): önceden çöp toplayıcıya dek açık kalıyor, o sırada Windows'un diski çıkarmasını reddettiriyordu.
    /// </summary>
    internal RawDiskWriter(SafeFileHandle handle, string path, WriteGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Path = path;
        try { (Length, SectorSize) = QueryInfo(handle); }
        catch
        {
            handle.Dispose();
            throw;
        }
        _handle = handle;
    }

    /// <summary>Yalnız testler: verilen bindirmeli tanıtıcıya yazan aygıt (süre sınırını hiç yanıtlamayan bir kanalla sınamak için).</summary>
    internal RawDiskWriter(SafeFileHandle overlappedHandle, string path, long length, int sectorSize, TimeSpan ioTimeout, WriteGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        _handle = overlappedHandle;
        Path = path;
        Length = length;
        SectorSize = sectorSize;
        IoTimeout = ioTimeout;
    }

    /// <summary>Uzunluk ve sektör boyu (eşzamanlı tanıtıcıda). Uzunluk alınamazsa IoError.</summary>
    private static (long Length, int SectorSize) QueryInfo(SafeFileHandle handle)
    {
        Span<byte> len = stackalloc byte[8];
        if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(len), 8, out _, IntPtr.Zero))
            throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.LengthQueryFailed"));
        return (BitConverter.ToInt64(len), SectorSizeQuery.Query(handle));
    }

    /// <summary>Yazma haklı tanıtıcı (izin kurucuda denetlendi; izinsiz hiçbir şey açılmaz).</summary>
    private static SafeFileHandle OpenForWrite(string path, bool overlapped)
    {
        var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            overlapped ? FileFlagOverlapped : 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int err = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw err == 5
            ? new ApfsException(ApfsErrorCode.AccessDenied, WinText.T("Raw.WriteAdminRequired", path))
            : new ApfsException(ApfsErrorCode.IoError, $"{path}: {new Win32Exception(err).Message}");
    }

    public string Path { get; }
    /// <summary>"Güvenli kaldır" bildirimine kaydolmak için (hizmet).</summary>
    public SafeFileHandle Handle => _handle;
    public long Length { get; }
    public int SectorSize { get; }

    /// <summary>Bir okuma/yazmanın ya da diske boşaltmanın en uzun süresi (bkz. sınıf açıklaması).</summary>
    public TimeSpan IoTimeout { get; } = DefaultIoTimeout;

    /// <summary>Okuma: hizasız istekler (dosya verisinin ortasından) sektör hizalı ara bellekle karşılanır.</summary>
    public unsafe void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > Length - buffer.Length) throw new ApfsException(ApfsErrorCode.Corrupt, WinText.T("Raw.ReadOutOfRange", offset, buffer.Length));
        if (buffer.IsEmpty) return;
        long start = offset / SectorSize * SectorSize;
        long end = (offset + buffer.Length + SectorSize - 1) / SectorSize * SectorSize;
        int alignedLen = checked((int)(end - start));
        byte* mem = (byte*)NativeMemory.AlignedAlloc((nuint)alignedLen, (nuint)Math.Max(SectorSize, 4096));
        bool abandoned = false;
        try
        {
            int done = 0;
            while (done < alignedLen)
            {
                int n = OverlappedIo.Transfer(_handle, Path, mem + done, alignedLen - done, start + done, write: false, IoTimeout, ref abandoned, ReadTimedOut);
                if (n <= 0) throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.ShortRead"));
                done += n;
            }
            new Span<byte>(mem + (offset - start), buffer.Length).CopyTo(buffer);
        }
        finally
        {
            if (!abandoned) NativeMemory.AlignedFree(mem); // bırakılan isteğin belleği çekirdeğe kalır
        }
    }

    public unsafe void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset > Length - data.Length) throw new ApfsException(ApfsErrorCode.InvalidArgument, WinText.T("Raw.WriteOutOfRange", offset, data.Length));
        Aligned(offset, data.Length);
        if (data.IsEmpty) return;
        // Hizalı yerel bellek: istek bırakılırsa (iptal de tutmadı) çekirdek ona sonradan erişebilir; yönetilen dizi taşınabilirdi
        byte* mem = (byte*)NativeMemory.AlignedAlloc((nuint)data.Length, (nuint)Math.Max(SectorSize, 4096));
        bool abandoned = false;
        try
        {
            data.CopyTo(new Span<byte>(mem, data.Length));
            int done = 0;
            while (done < data.Length)
            {
                int n = OverlappedIo.Transfer(_handle, Path, mem + done, data.Length - done, offset + done, write: true, IoTimeout, ref abandoned, WriteTimedOut);
                if (n <= 0) throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.ShortWrite"));
                done += n;
            }
        }
        finally
        {
            if (!abandoned) NativeMemory.AlignedFree(mem);
        }
    }

    /// <summary>Süreli diske boşaltmanın iş parçacığıyla paylaşılan durumu (kendi kilidiyle).</summary>
    private sealed class FlushState
    {
        public IntPtr Thread; // boşaltan iş parçacığının tanıtıcısı (CancelSynchronousIo için); iş parçacığı bitince kapatır
        public bool Finished;
        public int Error;
        public readonly ManualResetEventSlim Done = new();
    }

    /// <summary>
    /// FlushFileBuffers eşzamanlıdır (bindirmeli tanıtıcıda da): ayrı iş parçacığında, <see cref="IoTimeout"/> ile. Süre dolunca
    /// <c>CancelSynchronousIo</c> denenir ve IoError döner; iş parçacığı bittiğinde tanıtıcı başvurusunu kendisi bırakır (o ana dek
    /// tanıtıcı kapanmaz: Dispose onu sonraya bırakır).
    /// </summary>
    public void Flush()
    {
        bool added = false;
        try { _handle.DangerousAddRef(ref added); }
        catch (ObjectDisposedException e) { throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.DiskClosed"), e); }
        var state = new FlushState();
        IntPtr handle = _handle.DangerousGetHandle();
        var worker = new Thread(() =>
        {
            try
            {
                IntPtr self = OpenThread(ThreadTerminate, false, GetCurrentThreadId());
                lock (state) state.Thread = self;
                int error = FlushFileBuffers(handle) ? 0 : Marshal.GetLastPInvokeError();
                lock (state) state.Error = error;
            }
            finally
            {
                lock (state)
                {
                    state.Finished = true;
                    if (state.Thread != IntPtr.Zero) CloseHandle(state.Thread);
                    state.Thread = IntPtr.Zero;
                }
                _handle.DangerousRelease();
                state.Done.Set();
            }
        }) { IsBackground = true, Name = "RawDiskWriter.Flush" };
        try { worker.Start(); }
        catch
        {
            if (added) _handle.DangerousRelease();
            throw;
        }
        if (!state.Done.Wait(IoTimeout))
        {
            lock (state)
                if (!state.Finished && state.Thread != IntPtr.Zero) CancelSynchronousIo(state.Thread);
            // İptal de tutmazsa (iş parçacığı tanıtıcıyı tutmaya devam eder) aygıt "yanıtsız" listesine (inceleme)
            if (!state.Done.Wait(TimeSpan.FromMilliseconds(OverlappedIo.CancelGraceMs))) RawDiskDevice.MarkStalled(Path, write: true);
            throw WriteTimedOut();
        }
        if (state.Error != 0) throw new ApfsException(ApfsErrorCode.IoError, $"{Path}: {new Win32Exception(state.Error).Message}");
    }

    private ApfsException WriteTimedOut() =>
        new(ApfsErrorCode.IoError, WinText.T("Raw.WriteTimedOut", Path, (int)Math.Round(IoTimeout.TotalSeconds)));

    private ApfsException ReadTimedOut() =>
        new(ApfsErrorCode.IoError, WinText.T("Raw.ReadTimedOut", Path, (int)Math.Round(IoTimeout.TotalSeconds)));

    /// <summary>Ham disk G/Ç'si sektör hizalı olmalı; yazıcı 4 KiB'lık blokla çalıştığı için hizasızlık programlama hatasıdır.</summary>
    private void Aligned(long offset, int length)
    {
        if (offset % SectorSize != 0 || length % SectorSize != 0)
            throw new ArgumentException(WinText.T("Raw.Unaligned", offset, length));
    }

    /// <summary>Süren okuma/yazmalar iptal edilir; tanıtıcı son istek (ya da süren boşaltma) bitince kapanır.</summary>
    public void Dispose()
    {
        OverlappedIo.CancelAll(_handle);
        _handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        ref byte lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelSynchronousIo(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
