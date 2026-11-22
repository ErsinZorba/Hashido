using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Apfs.Windows;

/// <summary>
/// Windows Hello onayı (parolaPlan 1.4), PAKETSİZ WinRT: <c>Microsoft.Windows.SDK.NET</c> hedef çerçeveyi değiştirir ve kuruluma
/// ~25 MB ekler; burada <c>RoGetActivationFactory</c> + vtable çağrısı (<c>Marshal.GetDelegateForFunctionPointer</c>, unsafe yok) +
/// <c>IAsyncInfo</c> yoklaması yeter. Yalnız tepsi kullanır. Hiçbir yol istisna fırlatmaz: her hata <see cref="HelloAvailability.Unknown"/>
/// ya da <see cref="HelloResult.Failed"/> olur ve tepsi parola penceresine düşer (<see cref="RememberPolicy"/>).
/// Çağrılar arka plan iş parçacığında (MTA) yapılır; arayüz iş parçacığını bekletmez.
/// </summary>
public static class WindowsHello
{
    /// <summary>Hello penceresinde kullanıcıyı bekleme sınırı; aşılırsa işlem iptal edilir, sonuç Failed.</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromMinutes(2);

    private const string VerifierClass = "Windows.Security.Credentials.UI.UserConsentVerifier";
    private static readonly Guid IUserConsentVerifierStatics = new("AF4F3F91-564C-4DDC-B8B5-973447627C65");
    private static readonly Guid IUserConsentVerifierInterop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    private static readonly Guid IAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    /// <summary>
    /// <c>IAsyncOperation&lt;UserConsentVerificationResult&gt;</c>'ın IID'si (parametreli arayüz: WinRT pinterface algoritmasıyla
    /// türetilir; <c>WindowsHelloTests</c> aynı algoritmayla hesaplayıp karşılaştırır).
    /// </summary>
    internal static readonly Guid AsyncOperationOfVerificationResult = new("fd596ffd-2318-558f-9dbe-d21df43764a5");

    /// <summary>Bu oturumda Hello kullanılabilir mi. Uzak oturum ya da herhangi bir hata → Unknown.</summary>
    public static Task<HelloAvailability> AvailabilityAsync() => Task.Run(() =>
    {
        if (IsRemoteSession()) return HelloAvailability.Unknown;
        int? value = null;
        try { value = WithFactory(IUserConsentVerifierStatics, f => Slot<FnOut>(f, 6)(f, out var op) == 0 ? Await(op, TimeSpan.FromSeconds(20)) : null); }
        catch (Exception e) when (e is not OutOfMemoryException) { Trace.WriteLine($"Hello availability: {e.GetType().Name}"); }
        return RememberPolicy.FromAvailability(value, remoteSession: false);
    });

    /// <summary>
    /// Hello onay penceresini <paramref name="owner"/> penceresine bağlı açar (<c>IUserConsentVerifierInterop
    /// .RequestVerificationForWindowAsync</c>; masaüstü uygulamasının yolu). İleti pencerede görünür (parola içermez).
    /// </summary>
    public static Task<HelloResult> VerifyAsync(IntPtr owner, string message) => Task.Run(() =>
    {
        int? value = null;
        try
        {
            value = WithFactory(IUserConsentVerifierInterop, f =>
            {
                IntPtr hs = IntPtr.Zero;
                try
                {
                    if (WindowsCreateString(message, message.Length, out hs) != 0) return null;
                    var iid = AsyncOperationOfVerificationResult;
                    return Slot<FnRequestForWindow>(f, 6)(f, owner, hs, ref iid, out var op) == 0 ? Await(op, VerifyTimeout) : null;
                }
                finally { if (hs != IntPtr.Zero) WindowsDeleteString(hs); }
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Trace.WriteLine($"Hello verify: {e.GetType().Name}"); }
        return RememberPolicy.FromVerification(value);
    });

    public static bool IsRemoteSession() => GetSystemMetrics(SmRemoteSession) != 0;

    private static int? WithFactory(Guid iid, Func<IntPtr, int?> use)
    {
        _ = RoInitialize(1); // MTA; iş parçacığı zaten başlatılmışsa S_FALSE / RPC_E_CHANGED_MODE — ikisi de sorun değil
        if (WindowsCreateString(VerifierClass, VerifierClass.Length, out var cls) != 0) return null;
        IntPtr f;
        try { if (RoGetActivationFactory(cls, ref iid, out f) != 0) return null; }
        finally { WindowsDeleteString(cls); }
        try { return use(f); }
        finally { Marshal.Release(f); }
    }

    /// <summary>
    /// <c>IAsyncOperation&lt;enum&gt;</c>'ı bitene dek yoklar, sonucu (Int32) döndürür; süre dolarsa iptal eder. Sahipliği alır
    /// (bırakır). Tamamlanmadıysa null.
    /// </summary>
    private static int? Await(IntPtr op, TimeSpan timeout)
    {
        var iid = IAsyncInfo;
        if (Marshal.QueryInterface(op, in iid, out var info) != 0) { Marshal.Release(op); return null; }
        try
        {
            var watch = Stopwatch.StartNew();
            int status = 0; // 0 Started, 1 Completed, 2 Canceled, 3 Error
            while (Slot<FnOutInt>(info, 7)(info, out status) == 0 && status == 0)
            {
                if (watch.Elapsed > timeout) { _ = Slot<FnVoid>(info, 9)(info); return null; } // Cancel
                Thread.Sleep(50);
            }
            if (status != 1) return null;
            return Slot<FnOutInt>(op, 8)(op, out int result) == 0 ? result : null; // IAsyncOperation<T>.GetResults
        }
        finally
        {
            _ = Slot<FnVoid>(info, 10)(info); // Close
            Marshal.Release(info);
            Marshal.Release(op);
        }
    }

    // IInspectable: 0 QueryInterface, 1 AddRef, 2 Release, 3 GetIids, 4 GetRuntimeClassName, 5 GetTrustLevel; arayüzün ilk
    // yöntemi 6. IAsyncInfo: 6 Id, 7 Status, 8 ErrorCode, 9 Cancel, 10 Close. IAsyncOperation<T>: 6 put_Completed, 7 get_Completed,
    // 8 GetResults.
    private static T Slot<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnOut(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnOutInt(IntPtr self, out int result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FnVoid(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FnRequestForWindow(IntPtr self, IntPtr window, IntPtr message, ref Guid riid, out IntPtr operation);

    private const int SmRemoteSession = 0x1000;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("combase.dll")] private static extern int RoInitialize(int type);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
