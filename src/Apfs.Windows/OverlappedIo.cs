using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Apfs.Windows;

/// <summary>
/// Ham disk tanıtıcısında tek okuma/yazma: bindirmeli (overlapped), kendi olayıyla, süre sınırlı. Okuyucu (<see cref="RawDiskDevice"/>,
/// 1.8.2) ve yazıcı (<see cref="RawDiskWriter"/>, 1.9.1) aynı yolu kullanır. Süre dolunca istek iptal edilir
/// (<c>CancelIoEx</c>); iptal de <see cref="CancelGraceMs"/> içinde tutmazsa isteğin belleği ve olayı BIRAKILMAZ (çekirdek sonradan
/// yazabilir/okuyabilir; bellek bozulmasın), aygıt yolu "yanıtsız" listesine girer (<see cref="RawDiskDevice.MarkStalled"/>) ve istisna
/// yine döner. Tanıtıcı istek boyunca tutulur (<c>DangerousAddRef</c>): kapatma son isteğin sonuna kalır.
/// </summary>
internal static class OverlappedIo
{
    /// <summary>İptal isteğinden sonra işlemin gerçekten bitmesi için beklenen süre.</summary>
    internal const uint CancelGraceMs = 5000;

    private const int ErrorAccessDenied = 5, ErrorHandleEof = 38, ErrorOperationAborted = 995, ErrorIoPending = 997;
    private const uint WaitObject0 = 0;

    /// <summary>
    /// <paramref name="buffer"/>'dan/na tek aktarım. Okumada dosya sonu 0 döner. <paramref name="abandoned"/>: istek iptal edildiği hâlde
    /// bitmedi — çağıran <paramref name="buffer"/>'ı bırakmamalı. Aktarılan bayt sayısını döner.
    /// </summary>
    /// <param name="timedOut">Süre dolunca atılacak istisna (okuma/yazma kendi iletisiyle).</param>
    internal static unsafe int Transfer(SafeFileHandle safe, string path, byte* buffer, int length, long offset, bool write, TimeSpan timeout,
        ref bool abandoned, Func<ApfsException> timedOut)
    {
        bool added = false;
        try { safe.DangerousAddRef(ref added); } // istek sürerken tanıtıcı kapanmasın (Dispose onu sonraya bırakır)
        catch (ObjectDisposedException e) { throw new ApfsException(ApfsErrorCode.IoError, WinText.T("Raw.DiskClosed"), e); }
        IntPtr handle = safe.DangerousGetHandle();
        IntPtr ev = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        bool keep = false;
        try
        {
            if (ev == IntPtr.Zero) throw new ApfsException(ApfsErrorCode.IoError, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            ov->OffsetLow = unchecked((int)offset);
            ov->OffsetHigh = (int)(offset >> 32);
            ov->EventHandle = ev;
            bool started = write ? WriteFile(handle, buffer, length, IntPtr.Zero, ov) : ReadFile(handle, buffer, length, IntPtr.Zero, ov);
            if (!started)
            {
                int err = Marshal.GetLastPInvokeError();
                if (!write && err == ErrorHandleEof) return 0;
                if (err != ErrorIoPending) throw Failure(path, err, write);
            }
            uint timeoutMs = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            bool expired = WaitForSingleObject(ev, timeoutMs) != WaitObject0;
            if (expired)
            {
                CancelIoEx(handle, ov);
                if (WaitForSingleObject(ev, CancelGraceMs) != WaitObject0)
                {
                    keep = abandoned = true; // çekirdek hâlâ bu belleğe ve olaya erişebilir: bırakılmaz
                    RawDiskDevice.MarkStalled(path, write);
                    throw timedOut();
                }
            }
            if (!GetOverlappedResult(handle, ov, out uint done, false))
            {
                int err = Marshal.GetLastPInvokeError();
                if (!write && err == ErrorHandleEof) return 0;
                if (err == ErrorOperationAborted && expired) throw timedOut();
                throw Failure(path, err, write);
            }
            return (int)done; // süre dolduğu anda biten istek de geçerli
        }
        finally
        {
            if (!keep)
            {
                NativeMemory.Free(ov);
                if (ev != IntPtr.Zero) CloseHandle(ev);
            }
            if (added) safe.DangerousRelease();
        }
    }

    /// <summary>Yazmada erişim reddi: Windows ham yazmayı engelledi (bölüme birim bağlıysa); öbür hatalar G/Ç hatası.</summary>
    private static ApfsException Failure(string path, int err, bool write) =>
        write && err == ErrorAccessDenied
            ? new ApfsException(ApfsErrorCode.AccessDenied, WinText.T("Raw.WriteDenied"))
            : new ApfsException(ApfsErrorCode.IoError, $"{path}: {new Win32Exception(err).Message}");

    /// <summary>Tanıtıcıdaki bütün süren istekleri iptal eder (Dispose: güvenli kaldırmada tanıtıcı yavaş bir isteğin sonuna dek açık kalmasın).</summary>
    internal static unsafe void CancelAll(SafeFileHandle safe)
    {
        bool added = false;
        try
        {
            safe.DangerousAddRef(ref added);
            CancelIoEx(safe.DangerousGetHandle(), null);
        }
        catch (ObjectDisposedException) { } // zaten kapatılmış
        finally
        {
            if (added) safe.DangerousRelease();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool ReadFile(IntPtr hFile, byte* lpBuffer, int nNumberOfBytesToRead, IntPtr lpNumberOfBytesRead, NativeOverlapped* lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern unsafe bool WriteFile(IntPtr hFile, byte* lpBuffer, int nNumberOfBytesToWrite, IntPtr lpNumberOfBytesWritten, NativeOverlapped* lpOverlapped);

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
