using System.ComponentModel;
using System.Runtime.InteropServices;
using Apfs.IO;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// Fiziksel disk (\\.\PhysicalDriveN). Disk YALNIZCA GENERIC_READ erişimiyle açılır; bu handle ile
/// çekirdek seviyesinde diske yazmak mümkün değildir. Okumalar sektör hizalı yapılır.
/// </summary>
/// <remarks>
/// Okumaların süre sınırı var (1.8.2): okuma tanıtıcısı bindirmeli (overlapped) açılır, her okuma kendi olayıyla beklenir;
/// <see cref="ReadTimeout"/> içinde bitmezse iptal edilir (<c>CancelIoEx</c>) ve disk "okunamıyor" sayılır. Önceden takılan bir USB
/// okuması iş parçacığını süresiz tutuyordu (taramada <c>_scanLock</c> da tutulur: yeni diskler hiç bağlanmaz). Sürücü iptali de
/// yanıtlamazsa okumanın belleği ve olayı BIRAKILMAZ (çekirdek sonradan yazabilir; bellek bozulmasın), istisna yine döner; o aygıt
/// yolu <see cref="StalledFor"/> boyunca (ya da <see cref="ForgetStalled"/>'a dek: aygıt takılıp çıkarılınca, "Yeniden tara") hiç
/// açılmaz — her 15 sn'lik tarama bırakılan bir okuma daha biriktirmesin. <see cref="Dispose"/> süren okumaları iptal eder
/// (güvenli kaldırmada tanıtıcı yavaş bir okumanın sonuna dek açık kalıp çıkarmayı reddettirmesin). Bilgi sorguları (uzunluk,
/// sektör, bağlantı türü) açılışta ayrı, eşzamanlı bir tanıtıcıyla yapılır.
/// </remarks>
public sealed class RawDiskDevice : IBlockDevice
{
    /// <summary>İstenen erişim hakkı. Test ile yalnızca GENERIC_READ olduğu doğrulanır.</summary>
    internal const uint DesiredAccess = GenericRead;

    /// <summary>Bir okumanın en uzun süresi (uyuyan diskin kalkması birkaç saniye sürer; bu süre onun çok üstünde).</summary>
    public static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>İptal isteğinden sonra okumanın gerçekten bitmesi için beklenen süre.</summary>
    private const uint CancelGraceMs = 5000;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int ErrorIoPending = 997, ErrorOperationAborted = 995, ErrorHandleEof = 38;
    private const uint WaitObject0 = 0;

    private readonly SafeFileHandle _handle;
    private readonly int? _busType;

    /// <summary>
    /// İptali de tutmayan okumanın ya da süresinde bitmeyen açılışın bırakıldığı aygıt yolları → an
    /// (<see cref="Environment.TickCount64"/>) ve neden (true: açılış).
    /// </summary>
    private static readonly Dictionary<string, (long Since, bool Opening)> s_stalled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Açılışın ve bilgi sorgularının en uzun süresi (uyuyan diskin kalkması birkaç saniye sürer).</summary>
    public static readonly TimeSpan DefaultOpenTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Okuması bırakılan aygıtın yeniden açılmadığı süre.</summary>
    public static readonly TimeSpan StalledFor = TimeSpan.FromMinutes(10);

    /// <summary>Bırakılan okumalı aygıtlar yeniden denensin (aygıt takıldı/çıkarıldı, kullanıcı "Yeniden tara" dedi).</summary>
    public static void ForgetStalled()
    {
        lock (s_stalled) s_stalled.Clear();
    }

    public RawDiskDevice(int diskNumber) : this(DevicePathOf($"PhysicalDrive{diskNumber}")) { }

    /// <summary>Aygıt adının ("PhysicalDrive3") aygıt yolu (<c>\\.\PhysicalDrive3</c>). Öneksiz ad göreli DOSYA yoludur.</summary>
    public static string DevicePathOf(string deviceName) => $@"\\.\{deviceName}";

    /// <summary>
    /// Bu adlı aygıt hâlâ var mı ("Güvenle çıkar"dan sonra yalnız ayrılmış disk, 1.8.2). Yalnız "bulunamadı" yok sayılır:
    /// erişim reddi ya da yanıtsız aygıt VAR demektir (ayrılmış disk kendiliğinden yeniden bağlanmasın). Önceden aygıt öneksiz
    /// adla açılıyordu (göreli dosya yolu → her seferinde "bulunamadı"): çıkarılamayan disk (iç disk, VHD) 15–30 sn sonra yeniden
    /// bağlanıyordu.
    /// </summary>
    public static bool IsPresent(string deviceName)
    {
        try
        {
            using var device = new RawDiskDevice(DevicePathOf(deviceName));
            return true;
        }
        catch (ApfsException e) { return e.Code != ApfsErrorCode.NotFound; }
    }

    public RawDiskDevice(string devicePath) : this(devicePath, DefaultOpenTimeout, null) { }

    /// <param name="openTimeout">Açılış + bilgi sorgularının süresi.</param>
    /// <param name="openForTests">Yalnız testler: açılış iş parçacığında <see cref="OpenAndQuery"/> yerine çalışır (takılan ya da geç
    /// açılan aygıtı taklit eder).</param>
    internal RawDiskDevice(string devicePath, TimeSpan openTimeout, Func<string, Opened>? openForTests)
    {
        Path = devicePath;
        lock (s_stalled)
            if (s_stalled.TryGetValue(devicePath, out var stalled) && Environment.TickCount64 - stalled.Since < (long)StalledFor.TotalMilliseconds)
                throw stalled.Opening
                    ? new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.OpenTimedOut", devicePath, (int)Math.Round(openTimeout.TotalSeconds)))
                    : new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.ReadTimedOut", devicePath, (int)DefaultReadTimeout.TotalSeconds));
        var opened = OpenWithin(devicePath, openTimeout, openForTests ?? OpenAndQuery);
        Length = opened.Length;
        SectorSize = opened.SectorSize;
        _busType = opened.BusType;
        _handle = opened.Handle;
    }

    /// <summary>Açılmış aygıt: bilgi sorgularının sonucu ve okuma için bindirmeli tanıtıcı.</summary>
    internal readonly record struct Opened(long Length, int SectorSize, int? BusType, SafeFileHandle Handle);

    /// <summary>Açılış ve bilgi sorguları (süresiz; <see cref="OpenWithin"/> ayrı iş parçacığında çağırır).</summary>
    internal static Opened OpenAndQuery(string devicePath)
    {
        long length;
        int sectorSize;
        int? busType;
        // Bilgi sorguları eşzamanlı tanıtıcıyla (bindirmeli tanıtıcıda OVERLAPPED'sız DeviceIoControl tanımsız davranır)
        using (var info = OpenChecked(devicePath, overlapped: false))
        {
            length = QueryLength(info);
            sectorSize = QuerySectorSize(info);
            busType = QueryBusType(info);
        }
        return new Opened(length, sectorSize, busType, OpenChecked(devicePath, overlapped: true));
    }

    /// <summary>Süreli açılışın durumu (açılış iş parçacığı ile bekleyen arasında, kendi kilidiyle).</summary>
    private sealed class Opening
    {
        public Opened? Result;
        public Exception? Error;
        public bool Finished, Abandoned;
        public readonly ManualResetEventSlim Done = new();
    }

    /// <summary>
    /// Açılış ve bilgi sorguları (CreateFile, uzunluk/geometri/bağlantı türü) ayrı iş parçacığında, süre sınırıyla (1.8.2):
    /// takılan bir USB köprüsü önceden taramayı (<c>_scanLock</c>) süresiz tutabiliyordu (önceden yalnız okumaların süresi sınırlıydı).
    /// Süre dolarsa aygıt "yanıtsız" listesine girer (okuması bırakılan aygıt gibi: <see cref="StalledFor"/> ya da
    /// <see cref="ForgetStalled"/>'a dek açılmaz); iş parçacığı sonradan biterse açtığı tanıtıcıyı kendisi kapatır. Sonradan BAŞARIYLA
    /// biterse (yavaş kalkan ama sağlıklı disk) kendi koyduğu "yanıtsız" kaydını da siler: sonraki tarama diski açar.
    /// </summary>
    private static Opened OpenWithin(string devicePath, TimeSpan timeout, Func<string, Opened> open)
    {
        var o = new Opening();
        var worker = new Thread(() =>
        {
            SafeFileHandle? late = null; // bekleyen vazgeçtikten sonra açılan tanıtıcı
            try
            {
                var r = open(devicePath);
                lock (o)
                {
                    o.Finished = true;
                    if (o.Abandoned) late = r.Handle; // bekleyen vazgeçti (kaydını vazgeçerken bu kilit altında koydu)
                    else o.Result = r;
                }
                if (late is not null)
                    lock (s_stalled)
                        if (s_stalled.TryGetValue(devicePath, out var s) && s.Opening) s_stalled.Remove(devicePath);
            }
            catch (Exception e)
            {
                lock (o) { o.Finished = true; o.Error = e; }
            }
            finally
            {
                late?.Dispose();
                o.Done.Set();
            }
        }) { IsBackground = true, Name = "RawDiskDevice.Open" };
        worker.Start();
        if (!o.Done.Wait(timeout))
        {
            lock (o)
            {
                if (!o.Finished)
                {
                    o.Abandoned = true;
                    lock (s_stalled) s_stalled[devicePath] = (Environment.TickCount64, true);
                    throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.OpenTimedOut", devicePath, (int)Math.Round(timeout.TotalSeconds)));
                }
            }
            o.Done.Wait(); // süre dolarken bitti: sonucu kullan
        }
        if (o.Error is { } error)
        {
            if (error is ApfsException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw new ApfsException(ApfsErrorCode.IoError, $"{devicePath}: {error.Message}", error);
        }
        return o.Result!.Value;
    }

    /// <summary>Yalnız testler: verilen bindirmeli tanıtıcıdan okuyan aygıt (süre sınırını kanal gibi hiç yanıtlamayan bir kaynakla sınamak için).</summary>
    internal RawDiskDevice(SafeFileHandle overlappedHandle, string path, long length, int sectorSize, TimeSpan readTimeout)
    {
        _handle = overlappedHandle;
        Path = path;
        Length = length;
        SectorSize = sectorSize;
        ReadTimeout = readTimeout;
    }

    private static SafeFileHandle OpenChecked(string devicePath, bool overlapped)
    {
        var handle = Open(devicePath, overlapped);
        if (!handle.IsInvalid) return handle;
        int err = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw err switch
        {
            5 => new ApfsException(ApfsErrorCode.AccessDenied, WinText.T("Raw.ReadAdminRequired", devicePath)),
            2 or 3 => new ApfsException(ApfsErrorCode.NotFound, devicePath),
            _ => new ApfsException(ApfsErrorCode.IoError, $"{devicePath}: {new Win32Exception(err).Message}"),
        };
    }

    /// <summary>Handle açan tek yer. Erişim hakkı her zaman <see cref="DesiredAccess"/>'tir.</summary>
    private static SafeFileHandle Open(string path, bool overlapped) =>
        CreateFileW(path, DesiredAccess, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, overlapped ? FileFlagOverlapped : 0, IntPtr.Zero);

    public string Path { get; }

    /// <summary>Yalnızca okuma haklı tanıtıcı; "güvenli kaldır" bildirimine kaydolmak için (hizmet) kullanılır.</summary>
    public SafeFileHandle Handle => _handle;
    public long Length { get; }
    public int SectorSize { get; }

    /// <summary>Bir okumanın en uzun süresi (bkz. sınıf açıklaması).</summary>
    public TimeSpan ReadTimeout { get; } = DefaultReadTimeout;

    public void Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > Length - buffer.Length)
            throw new ApfsException(ApfsErrorCode.Corrupt, WinText.T("Raw.ReadOutOfRange", offset, buffer.Length));
        if (buffer.IsEmpty) return;

        long alignedStart = offset / SectorSize * SectorSize;
        long alignedEnd = (offset + buffer.Length + SectorSize - 1) / SectorSize * SectorSize;
        int alignedLen = checked((int)(alignedEnd - alignedStart));

        // Sektör hizalı ara bellek (ham disk okumaları hizalı bellek ve uzunluk ister)
        unsafe
        {
            byte* mem = (byte*)NativeMemory.AlignedAlloc((nuint)alignedLen, (nuint)Math.Max(SectorSize, 4096));
            bool abandoned = false;
            try
            {
                int done = 0;
                while (done < alignedLen)
                {
                    int n = ReadOnce(mem + done, alignedLen - done, alignedStart + done, ref abandoned);
                    if (n <= 0) throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.ShortRead"));
                    done += n;
                }
                new Span<byte>(mem + (offset - alignedStart), buffer.Length).CopyTo(buffer);
            }
            finally
            {
                if (!abandoned) NativeMemory.AlignedFree(mem); // bırakılan okumanın belleği çekirdeğe kalır
            }
        }
    }

    /// <summary>
    /// Tek bindirmeli okuma, süre sınırıyla. <paramref name="abandoned"/>: okuma iptal edildiği hâlde bitmedi; çağıran belleği
    /// bırakmamalı. Okunan bayt sayısını döner.
    /// </summary>
    private unsafe int ReadOnce(byte* buffer, int length, long offset, ref bool abandoned)
    {
        bool added = false;
        try { _handle.DangerousAddRef(ref added); } // okuma sürerken tanıtıcı kapanmasın (Dispose onu sonraya bırakır)
        catch (ObjectDisposedException e) { throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.DiskClosed"), e); }
        IntPtr handle = _handle.DangerousGetHandle();
        IntPtr ev = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        bool keep = false;
        try
        {
            if (ev == IntPtr.Zero) throw new ApfsException(ApfsErrorCode.IoError, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            ov->OffsetLow = unchecked((int)offset);
            ov->OffsetHigh = (int)(offset >> 32);
            ov->EventHandle = ev;
            if (!ReadFile(handle, buffer, length, IntPtr.Zero, ov))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ErrorHandleEof) return 0;
                if (err != ErrorIoPending) throw new ApfsException(ApfsErrorCode.IoError, $"{Path}: {new Win32Exception(err).Message}");
            }
            uint timeoutMs = (uint)Math.Clamp(ReadTimeout.TotalMilliseconds, 1, int.MaxValue);
            bool timedOut = WaitForSingleObject(ev, timeoutMs) != WaitObject0;
            if (timedOut)
            {
                CancelIoEx(handle, ov);
                if (WaitForSingleObject(ev, CancelGraceMs) != WaitObject0)
                {
                    keep = abandoned = true; // çekirdek hâlâ bu belleğe ve olaya yazabilir: bırakılmaz
                    lock (s_stalled) s_stalled[Path] = (Environment.TickCount64, false);
                    throw TimedOut();
                }
            }
            if (!GetOverlappedResult(handle, ov, out uint read, false))
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ErrorHandleEof) return 0;
                if (err == ErrorOperationAborted && timedOut) throw TimedOut();
                throw new ApfsException(ApfsErrorCode.IoError, $"{Path}: {new Win32Exception(err).Message}");
            }
            return (int)read; // süre dolduğu anda biten okuma da geçerli
        }
        finally
        {
            if (!keep)
            {
                NativeMemory.Free(ov);
                if (ev != IntPtr.Zero) CloseHandle(ev);
            }
            if (added) _handle.DangerousRelease();
        }
    }

    private ApfsException TimedOut() =>
        new(ApfsErrorCode.IoError, WinText.T("Raw.ReadTimedOut", Path, (int)Math.Round(ReadTimeout.TotalSeconds)));

    private static long QueryLength(SafeFileHandle handle)
    {
        Span<byte> outBuf = stackalloc byte[8];
        if (!DeviceIoControl(handle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(outBuf), 8, out _, IntPtr.Zero))
            throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.LengthQueryFailedDetail", new Win32Exception(Marshal.GetLastPInvokeError()).Message));
        return BitConverter.ToInt64(outBuf);
    }

    private static int QuerySectorSize(SafeFileHandle handle)
    {
        Span<byte> outBuf = stackalloc byte[256];
        if (!DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, ref MemoryMarshal.GetReference(outBuf), 256, out _, IntPtr.Zero))
            return 512;
        // DISK_GEOMETRY_EX.Geometry.BytesPerSector: Cylinders(8) + MediaType(4) + TracksPerCylinder(4) + SectorsPerTrack(4)
        int bps = BitConverter.ToInt32(outBuf[20..]);
        return bps is 512 or 1024 or 2048 or 4096 ? bps : 512;
    }

    /// <summary>
    /// Bağlantı türü (STORAGE_BUS_TYPE: 7 USB, 0xB SATA, 0x11 NVMe, 0xF VHD…; açılışta okunur). Öğrenilemezse null. Raporlama için;
    /// aygıtın adı/seri numarası okunmaz.
    /// </summary>
    public int? QueryBusType() => _busType;

    private static int? QueryBusType(SafeFileHandle handle)
    {
        Span<byte> query = stackalloc byte[12]; // PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0)
        query.Clear();
        Span<byte> outBuf = stackalloc byte[512];
        unsafe
        {
            fixed (byte* q = query)
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, (IntPtr)q, 12, ref MemoryMarshal.GetReference(outBuf), 512, out uint n, IntPtr.Zero) || n < 32)
                    return null;
        }
        return BitConverter.ToInt32(outBuf[28..]); // STORAGE_DEVICE_DESCRIPTOR.BusType
    }

    public unsafe void Dispose()
    {
        // Süren okumalar iptal edilir (bkz. sınıf açıklaması); tanıtıcı son okuma bitince kapanır
        bool added = false;
        try
        {
            _handle.DangerousAddRef(ref added);
            CancelIoEx(_handle.DangerousGetHandle(), null);
        }
        catch (ObjectDisposedException) { } // zaten kapatılmış
        finally
        {
            if (added) _handle.DangerousRelease();
        }
        _handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        ref byte lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(IntPtr hFile, byte* lpBuffer, int nNumberOfBytesToRead, IntPtr lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool GetOverlappedResult(IntPtr hFile, NativeOverlapped* lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool CancelIoEx(IntPtr hFile, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
