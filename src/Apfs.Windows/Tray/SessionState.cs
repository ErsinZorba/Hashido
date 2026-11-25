using System.Runtime.InteropServices;

namespace Apfs.Windows.Tray;

/// <summary>Bu sürecin Windows oturumu etkin mi (kullanıcı şu an bu oturumda: yerel konsol ya da bağlı uzak masaüstü).</summary>
public static class SessionState
{
    private const int WtsConnectState = 8;  // WTS_INFO_CLASS.WTSConnectState
    private const int WtsActive = 0;        // WTS_CONNECTSTATE_CLASS.WTSActive
    private static readonly IntPtr CurrentServer = IntPtr.Zero;
    private const int CurrentSession = -1;  // WTS_CURRENT_SESSION

    /// <summary>
    /// Oturum <c>WTSActive</c> mi. Hızlı kullanıcı değiştirmede arka plana düşen oturum <c>WTSDisconnected</c> olur. Sorgu başarısızsa
    /// false (güvenli yön: kayıtlı parola kendiliğinden kullanılmaz, parola penceresi açılır).
    /// </summary>
    public static bool IsActive()
    {
        if (!WTSQuerySessionInformationW(CurrentServer, CurrentSession, WtsConnectState, out IntPtr buffer, out int bytes)) return false;
        try { return bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == WtsActive; }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
