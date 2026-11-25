using System.Diagnostics;
using Apfs.Windows.Ipc;
using Apfs.Windows.Reporting;

namespace Apfs.Windows.Tray;

/// <summary>Hizmet kanalı: tek istek, tek yanıt. Hizmete ulaşılamıyorsa <see cref="TimeoutException"/>/<see cref="IOException"/>/iptal.</summary>
public interface ITrayChannel
{
    Task<T?> SendAsync<T>(Request request, TimeSpan timeout);
}

/// <summary>Gerçek kanal: <see cref="PipeProtocol.SendAsync{T}"/> (<see cref="PipeProtocol.PipeName"/>; dil her istekte gider).</summary>
public sealed class PipeChannel : ITrayChannel
{
    public static PipeChannel Instance { get; } = new();

    public Task<T?> SendAsync<T>(Request request, TimeSpan timeout) => PipeProtocol.SendAsync<T>(request, timeout);
}

/// <summary>
/// <see cref="AppController"/>'ın dış dünyası (winuiPlan P1): hizmet kanalı, "Bu bilgisayarda hatırla" kasası, Windows Hello,
/// günlük, sürücünün boş alanı. Program gerçeklerini kullanır (<see cref="Default"/>); testler sahtelerini verir.
/// </summary>
/// <param name="HelloVerify">Hello onayı: sahip pencerenin tutamacı ve ileti.</param>
/// <param name="ReadSpace">Sürücü harfinin toplam/boş alanı; okunamazsa null (arka plan iş parçacığında çağrılır).</param>
/// <param name="Notifications">Hangi balonlar gösterilir (Ayarlar › Bildirimler); null = kayıtlı seçim (<see cref="UserSettings.Notifications"/>).</param>
/// <param name="SessionActive">Tepsinin Windows oturumu etkin mi; null = gerçek sorgu (<see cref="SessionState.IsActive"/>).</param>
/// <param name="DriveShadowed">Harf bu oturumda başka bir aygıta (ağ, subst) bağlı mı; null = gerçek sorgu (<see cref="DriveMapping.IsShadowed"/>).</param>
public sealed record TrayServices(
    ITrayChannel Channel,
    CredentialStore Credentials,
    Func<Task<HelloAvailability>> HelloAvailability,
    Func<IntPtr, string, Task<HelloResult>> HelloVerify,
    Action<string> Log,
    Func<string, DriveSpace?> ReadSpace,
    Func<NotificationMode>? Notifications = null,
    Func<bool>? SessionActive = null,
    Func<string, bool>? DriveShadowed = null)
{
    /// <summary>Harf bu oturumda başka bir aygıta bağlı mı (her "Aç"ta yeniden sorulur); null = <see cref="DriveMapping.IsShadowed"/>.</summary>
    public bool IsDriveShadowed(string mountPoint) => DriveShadowed?.Invoke(mountPoint) ?? DriveMapping.IsShadowed(mountPoint);

    /// <summary>Tepsinin oturumu şu an etkin mi (her kilit açma kararında yeniden sorulur); null = <see cref="SessionState.IsActive"/>.</summary>
    public bool IsSessionActive => SessionActive?.Invoke() ?? SessionState.IsActive();

    /// <summary>Şu anki bildirim seçimi (her balonda yeniden okunur: Ayarlar'da değişince hemen geçerli).</summary>
    public NotificationMode NotificationMode => Notifications?.Invoke() ?? UserSettings.Notifications;

    public static TrayServices Default { get; } = new(PipeChannel.Instance, CredentialStore.Default, DefaultHelloAvailability,
        WindowsHello.VerifyAsync, ServiceLog.Write, ReadDriveSpace);

    /// <summary>Hello kullanılabilir mi; geliştirme kanalında <c>APFS_DEV_HELLO</c> kancası (<see cref="RememberPolicy.DevHelloOverride"/>).</summary>
    private static Task<HelloAvailability> DefaultHelloAvailability() =>
        RememberPolicy.DevHelloOverride(PipeProtocol.PipeName, Environment.GetEnvironmentVariable("APFS_DEV_HELLO")) is { } dev
            ? Task.FromResult(dev)
            : WindowsHello.AvailabilityAsync();

    private static DriveSpace? ReadDriveSpace(string mountPoint)
    {
        try
        {
            if (DriveMapping.IsShadowed(mountPoint)) return null; // harf bu oturumda başka sürücüyü gösteriyor: doluluk onunki olurdu
            var d = new DriveInfo(mountPoint);
            long total = d.TotalSize;
            return total > 0 ? new DriveSpace(total, Math.Clamp(d.AvailableFreeSpace, 0, total)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>
/// <see cref="TrayFeatures"/>'ın dış dünyası (winuiPlan P1): rapor istemcisi, güncelleme denetimi/indirme, setup'ı başlatma,
/// yönetici ve "başka setup çalışıyor" denetimleri, günlük. Program gerçeklerini kullanır (<see cref="Default"/>); testler
/// sahtelerini verir.
/// </summary>
/// <param name="NewReportClient">Rapor istemcisi (yerel kuyruk, gönderilenler günlüğü); testte null (rapor yok).</param>
/// <param name="QueryUpdate">Son Release'i sorar (kurulu sürüme göre; <see cref="UpdateChecker.QueryAsync"/>).</param>
/// <param name="UpdatesDirectory">İndirilen setup'ların klasörü.</param>
/// <param name="StartProcess">Setup'ı başlatır (yol, argümanlar); başlatılamazsa istisna.</param>
/// <param name="OpenPage">Release sayfasını varsayılan tarayıcıda açar.</param>
public sealed record UpdateServices(
    Func<ReportRecorder, SentLog, ReportClient?> NewReportClient,
    Func<Version> CurrentVersion,
    Func<Version, Task<UpdateCheckResult>> QueryUpdate,
    string UpdatesDirectory,
    Func<string, UpdateDownloader> NewDownloader,
    Func<bool> IsAdministrator,
    Func<bool> AnySetupRunning,
    Func<string, string, Process?> StartProcess,
    Action<string> Log,
    Action<Uri> OpenPage)
{
    public static UpdateServices Default { get; } = new(
        (local, sent) => new ReportClient(() => UserSettings.ReportsConsent, local, new ServiceReportSource(),
            new ReportUploader(ReportClient.Endpoint, sent)),
        UpdateChecker.CurrentVersion,
        async current =>
        {
            using var checker = new UpdateChecker(UpdateChecker.Api);
            return await checker.QueryAsync(current);
        },
        UpdateDownloader.DefaultDirectory,
        directory => new UpdateDownloader(directory),
        UpdatePolicy.UserIsAdministrator,
        UpdatePolicy.AnySetupRunning,
        (path, args) => Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = true }),
        ServiceLog.Write,
        page => AppController.TryStart(new ProcessStartInfo(page.ToString()) { UseShellExecute = true }, Process.Start, ServiceLog.Write));
}
