using System.Runtime.InteropServices;
using Apfs.Windows;

namespace Apfs.Service;

/// <summary>
/// Uyku ve uyanış bildirimi (1.9.1). WinFsp'nin hizmet çatısı SCM güç olaylarını vermediği için
/// <c>PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK)</c> ile (powrprof.dll; hizmet de kullanıcı süreci de alır).
/// Uyku (PBT_APMSUSPEND) geri çağrının İÇİNDE işlenir: sistem dönüşü bekler, süre <paramref name="suspend"/>'in bütçesindedir.
/// Uyanış (PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND; ikisi de gelebilir) iş parçacığı havuzuna bırakılır, korumalı. Hiçbir hata
/// hizmeti düşürmez; kayıt başarısızsa günlüğe bir satır (uyku güvencesi yok, önceki davranış).
/// </summary>
internal sealed class PowerEvents : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4, PbtApmResumeSuspend = 0x7, PbtApmResumeAutomatic = 0x12;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Callback(IntPtr context, uint type, IntPtr setting);

    private readonly Action _suspend, _resume;
    private readonly Callback _callback; // yerel kod çağırdıkça toplanmasın
    private IntPtr _parameters; // DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS (kayıt boyunca yaşar)
    private IntPtr _registration;

    public PowerEvents(Action suspend, Action resume)
    {
        _suspend = suspend;
        _resume = resume;
        _callback = OnPowerEvent;
        _parameters = Marshal.AllocHGlobal(IntPtr.Size * 2);
        Marshal.WriteIntPtr(_parameters, 0, Marshal.GetFunctionPointerForDelegate(_callback));
        Marshal.WriteIntPtr(_parameters, IntPtr.Size, IntPtr.Zero);
        uint error = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration);
        if (error != 0)
        {
            _registration = IntPtr.Zero;
            Marshal.FreeHGlobal(_parameters);
            _parameters = IntPtr.Zero;
            ServiceLog.Write(WinText.T("Svc.PowerRegisterFailed", error));
        }
    }

    private uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type == PbtApmSuspend) _suspend();
            else if (type is PbtApmResumeAutomatic or PbtApmResumeSuspend)
                ThreadPool.UnsafeQueueUserWorkItem(_ => // arka plan işi (bağlam taşımaz), korumalı
                {
                    try { _resume(); }
                    catch (Exception e) { ServiceLog.Write(WinText.T("Svc.PowerEventFailed", e.Message)); }
                }, null);
        }
        catch (Exception e) { ServiceLog.Write(WinText.T("Svc.PowerEventFailed", e.Message)); }
        return 0;
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            PowerUnregisterSuspendResumeNotification(_registration);
            _registration = IntPtr.Zero;
            Marshal.FreeHGlobal(_parameters); // kayıt kaldırıldı: geri çağrı artık gelmez
        }
        GC.KeepAlive(_callback);
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registration);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
}
