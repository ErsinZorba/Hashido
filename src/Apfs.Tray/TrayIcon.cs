using System.IO;
using System.Runtime.InteropServices;
using Apfs.Windows;
using Apfs.Windows.Tray;
using static Apfs.Tray.Native;

namespace Apfs.Tray;

/// <summary>
/// Bildirim alanı simgesi (winuiPlan P2.3, WPF'e aynen taşındı; WinForms <c>NotifyIcon</c>'un yerine <c>Shell_NotifyIcon</c>: ikinci arayüz yığını yok): gizli üst düzey bir
/// pencere iletileri alır (üst düzey olmalı: "TaskbarCreated" ve ayar yayını yalnız onlara gelir). Tıklama deseni WinForms'unki
/// gibi: ilk bırakışta <see cref="Click"/>, çift tıkta <see cref="DoubleClick"/> (çift tıkın bırakışı tık sayılmaz), sağ
/// bırakışta <see cref="RightClick"/>. Gezgin yeniden başlayınca simge yeniden eklenir (E7). Menü Windows'un kendi açılır menüsü.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = WM_APP + 1;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static WndProc? _proc; // GC toplamasın (pencere ömrü boyunca)
    private static TrayIcon? _instance;

    private readonly IntPtr _hwnd;
    private IntPtr _iconHandle;
    private string _tip;
    private bool _added;
    private bool _doubleClick;

    public TrayIcon(string tip)
    {
        _instance = this;
        _tip = tip;
        _proc ??= Proc;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), style = 0x8 /* CS_DBLCLKS: çift tık iletisi gelsin */,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandle(null),
            lpszClassName = "Hashido.TrayIcon",
        };
        RegisterClassEx(ref wc); // ikinci kez kayıt hatası önemsiz (aynı sınıf)
        // Üst düzey, hiç gösterilmeyen pencere (ileti penceresi değil: yayınları alsın)
        _hwnd = CreateWindowEx(0, "Hashido.TrayIcon", "", unchecked((int)0x80000000) /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        LoadIcon();
        Add();
    }

    /// <summary>Sol tık (bırakış).</summary>
    public event Action? Click;
    /// <summary>Sol çift tık.</summary>
    public event Action? DoubleClick;
    /// <summary>Sağ tık (bırakış): menü gösterilmeli.</summary>
    public event Action? RightClick;
    /// <summary>Balona tıklandı.</summary>
    public event Action? BalloonClicked;
    /// <summary>Windows ayar yayını (tema değişimi "ImmersiveColorSet" ile gelir).</summary>
    public event Action? SettingChanged;

    /// <summary>Gizli pencerenin tutamacı (menünün sahibi).</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>İpucu (çağıran 63 karakterle sınırlar: <see cref="TrayModel.Tooltip"/>).</summary>
    public string Tip
    {
        get => _tip;
        set
        {
            if (_tip == value) return;
            _tip = value;
            var d = Data(NIF_TIP);
            Shell_NotifyIcon(NIM_MODIFY, ref d);
        }
    }

    /// <summary>Balon (başlık her zaman "Hashido"; gönderen adı exe'nin dosya açıklaması). Süre Windows'ta öneridir.</summary>
    public void ShowBalloon(int timeoutMs, string text, NoticeLevel level)
    {
        var d = Data(NIF_INFO);
        d.szInfoTitle = Branding.ProductName;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.uTimeoutOrVersion = timeoutMs;
        d.dwInfoFlags = level switch { NoticeLevel.Error => NIIF_ERROR, NoticeLevel.Warning => NIIF_WARNING, _ => NIIF_INFO };
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// <summary>
    /// Windows'un açılır menüsünü imlecin yerinde gösterir ve seçilen öğenin kimliğini döndürür (seçim yoksa 0). Koyu temada
    /// menü koyu (<paramref name="dark"/>). Ön plana alma + WM_NULL: menü dışına tıklanınca kapansın (bilinen Win32 kuralı).
    /// </summary>
    public int ShowMenu(IReadOnlyList<(int Id, string? Text, bool Enabled, bool Default)> items, bool dark)
    {
        IntPtr menu = CreatePopupMenu();
        try
        {
            foreach (var (id, text, enabled, isDefault) in items)
            {
                if (text is null) { AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null); continue; }
                AppendMenu(menu, MF_STRING | (enabled ? 0 : MF_GRAYED), (UIntPtr)id, text);
                if (isDefault) SetMenuDefaultItem(menu, (uint)id, 0); // kalın
            }
            if (Environment.OSVersion.Version.Build >= 18362)
            {
                SetPreferredAppMode(dark ? 2 : 3);
                FlushMenuThemes();
            }
            GetCursorPos(out var p);
            SetForegroundWindow(_hwnd);
            int chosen = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD | TPM_NONOTIFY, p.X, p.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return chosen;
        }
        finally { DestroyMenu(menu); }
    }

    private void LoadIcon()
    {
        int size = GetSystemMetrics(SM_CXSMICON); // küçük simge boyutu (DPI'a göre)
        _iconHandle = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "app.ico"), IMAGE_ICON, size, size, LR_LOADFROMFILE);
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, uFlags = flags, uCallbackMessage = CallbackMessage,
        hIcon = _iconHandle, szTip = _tip, szInfo = "", szInfoTitle = "",
    };

    private void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref d);
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try { return Dispatch(hwnd, msg, wParam, lParam); }
        catch (Exception e)
        {
            // Yerel geri çağrıdan istisna çıkmasın: rapor yazılır, istisna arayüz kuyruğunda yeniden atılır → süreç çöker (A9)
            Crash.Record(e);
            var info = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(info.Throw);
            return IntPtr.Zero;
        }
    }

    private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _instance;
        if (self is not null && hwnd == self._hwnd)
        {
            if (msg == CallbackMessage)
            {
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONUP:
                        // Çift tıkın ikinci bırakışı tık sayılmaz (WinForms NotifyIcon gibi)
                        if (!self._doubleClick) self.Click?.Invoke();
                        self._doubleClick = false;
                        break;
                    case WM_LBUTTONDBLCLK:
                        self._doubleClick = true;
                        self.DoubleClick?.Invoke();
                        break;
                    case WM_RBUTTONUP:
                        self.RightClick?.Invoke();
                        break;
                    case NIN_BALLOONUSERCLICK:
                        self.BalloonClicked?.Invoke();
                        break;
                }
                return IntPtr.Zero;
            }
            if (msg == TaskbarCreated && TaskbarCreated != 0)
            {
                self.Add(); // Gezgin yeniden başladı: simge yeniden eklenir
                return IntPtr.Zero;
            }
            if (msg == WM_SETTINGCHANGE) self.SettingChanged?.Invoke();
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref d); // hayalet simge kalmasın (A13)
            _added = false;
        }
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = IntPtr.Zero;
        DestroyWindow(_hwnd);
        if (_instance == this) _instance = null;
    }
}
