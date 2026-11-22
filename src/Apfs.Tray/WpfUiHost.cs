using System.Windows;
using Apfs.Tray.Ui;
using Apfs.Windows.Tray;

namespace Apfs.Tray;

/// <summary>
/// <see cref="IUiHost"/>'un WPF karşılığı (wpfPlan 2.2): kutular (<see cref="MessageWindow"/>), parola penceresi
/// (<see cref="PasswordWindow"/>), Hello'nun sahibi (<see cref="HelloOwnerWindow"/>) ve balonlar (tepsi simgesi). <c>owner</c>
/// bir WPF penceresi (<see cref="Window"/>) ya da null'dur (sahipsiz). Açık-pencere sayacını mantık tutar
/// (<see cref="AppController.ModalAsync{T}"/>); Hello sahibi sayılmaz.
/// </summary>
/// <param name="notify">Balon gösterir (tepsi simgesi; 5 sn); düzenekte null.</param>
internal sealed class WpfUiHost(Action<string, NoticeLevel>? notify = null) : IUiHost
{
    public Task<bool> ConfirmAsync(object? owner, string heading, string text, string proceed, ConfirmTone tone) =>
        MessageWindow.ConfirmAsync(owner as Window, heading, text, proceed, tone);

    public Task InformAsync(object? owner, string? heading, string text, InformIcon icon) =>
        MessageWindow.InformAsync(owner as Window, heading, text, icon);

    public Task<PasswordAnswer?> AskPasswordAsync(PasswordRequest request) => PasswordWindow.AskAsync(request);

    public async Task<T> RunWithHelloOwnerAsync<T>(string message, Func<IntPtr, Task<T>> verify)
    {
        var host = await HelloOwnerWindow.ShowAsync(message);
        try { return await verify(host.Handle); }
        finally { if (!host.IsClosed) host.Close(); } // Alt+F4 ile kapanmış olabilir
    }

    public void Notify(string text, NoticeLevel level) => notify?.Invoke(text, level);
}
