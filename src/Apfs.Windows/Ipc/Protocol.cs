using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Apfs.Windows.Reporting;

namespace Apfs.Windows.Ipc;

/// <summary>Bir birimin durumu (hizmetten tepsiye).</summary>
/// <param name="Index">Birimin kapsayıcı içindeki sırası.</param>
/// <param name="Writable">Birim yazılabilir bağlı (deneysel yazma açık).</param>
/// <param name="Container">Birimin APFS bölümünün disk içindeki sırası (yazma ve kilit açma bölüm başınadır).</param>
/// <param name="WriteBlockReason">Bu bölüm için son "yazmaya izin ver" denemesi reddedildiyse nedeni.</param>
/// <param name="Uuid">Şifreli birimin APFS UUID'si (kilitliyken de bilinir; tepsi kayıtlı parolayı buna bağlar). Şifresiz birimde ve
/// eski hizmette null.</param>
public sealed record VolumeStatus(int Index, string Name, string State, string? MountPoint, string? Hint, string? Message, bool Writable = false,
    int Container = 0, string? WriteBlockReason = null, string? Uuid = null)
{
    public const string Mounted = "bağlı";
    public const string NeedsPassword = "parola-gerekli";
    public const string Failed = "hata";
    public const string Hidden = "gizli";
}

/// <param name="Writable">Diskin en az bir bölümü yazmaya açık.</param>
/// <param name="WriteBlockReason">Son "yazmaya izin ver" denemesi reddedildiyse nedeni (sade Türkçe; çok bölümlüde ilk bölümünki,
/// bölüm başına neden <see cref="VolumeStatus.WriteBlockReason"/>'da).</param>
public sealed record DiskStatus(string Id, string Device, string Description, List<VolumeStatus> Volumes, bool Writable = false, string? WriteBlockReason = null);

/// <summary>Kullanıcıya gösterilecek bildirim. Parola veya dosya içeriği asla içermez.</summary>
/// <param name="Requested">Kullanıcının istediği işin sonucu (Güvenle çıkar, yazmaya aç/kapat; 1.8.0): tepsi "yalnız önemli
/// bildirimler"de de gösterir. Eski hizmette yok → false.</param>
public sealed record Notice(long Id, string Level, string Text, DateTime Time, bool Requested = false);

/// <param name="Instance">Hizmet sürecinin bu çalışmaya özgü kimliği (1.8.2): hizmet yeniden başlayınca bildirim kimlikleri
/// 1'den başlar; değiştiğini gören tepsi bildirim imlecini sıfırlar (yoksa yeni hizmetin ilk "bağlandı" balonları kayboluyordu).
/// Eski hizmette null.</param>
public sealed record StatusResponse(List<DiskStatus> Disks, List<Notice> Notices, long LastNoticeId, string? Instance = null);

/// <param name="Busy">Yalnızca "eject": diskte açık dosya/kopyalama var, hiçbir şey yapılmadı; kullanıcı onaylarsa Force ile tekrar.</param>
/// <param name="Code">Yalnızca "unlock" hatası: <c>ApfsErrorCode</c> adı (ör. "WrongPassword"; tepsi kayıtlı parolayı buna göre siler).
/// Hizmete ulaşılamadıysa ya da birim beklemiyorsa null.</param>
public sealed record UnlockResponse(bool Ok, string Message, bool Busy = false, string? Code = null);

/// <summary>
/// "reports-pull" yanıtı: hizmet kuyruğundaki olaylar (bir sayfa, ≤ ~48 KiB). <paramref name="More"/>: sayfa sınırı yüzünden
/// kalanlar var, tepsi son kimlikten devam eder. Eski hizmet bu isteği tanımaz ("bilinmeyen istek" yanıtı): Reports null gelir,
/// tepsi boş yığın sayar.
/// </summary>
public sealed record ReportsResponse(List<QueuedReport>? Reports, bool More = false, bool Enabled = false);

/// <summary>
/// Tepsiden hizmete istek. "status" (since: son görülen bildirim), "unlock" (disk, birim, parola), "rescan",
/// "eject" (disk; force: açık dosya olsa da çıkar), "set-writable" (disk, container; enable: yazmayı aç/kapat; force: açık dosya
/// olsa da). Container: APFS bölüm sırası ("unlock" ve "set-writable"; eski istemcide 0 = ilk bölüm).
/// Lang: tepsinin arayüz dili (ör. "de"); hizmet bu isteğin yanıtını (ve o sırada ürettiği metinleri) bu dilde üretir; istek etkin
/// konsol oturumundan geliyorsa hizmetin varsayılan dili de olur (1.8.2). Yoksa varsayılan dil.
/// Raporlama (1.4.0): "reports-pull" (ReportAfter: bu kimlikten sonrakiler), "reports-ack" (ReportAfter: bu kimlik dahil
/// öncekiler silinir), "reports-config" (Enable: kullanıcının onayı; kapatınca kuyruk silinir).
/// Culture (1.8.2): tepsinin bölge ayarı (sayı biçimi, ör. "de-DE"); disk başlığındaki boyut ("1,81 TB" / "1.81 TB") Lang gibi
/// isteğe ve konsol oturumundan geliyorsa varsayılana uygulanır. Eski tepside yok → hizmetin kendi kültürü (sistem yereli).
/// </summary>
public sealed record Request(string Op, long Since = 0, string? Disk = null, int Volume = 0, string? Password = null, bool Force = false, bool Enable = false,
    int Container = 0, string? Lang = null, long ReportAfter = 0, string? Culture = null);

/// <summary>
/// Adlandırılmış kanal: her bağlantıda tek satır JSON istek, tek satır JSON yanıt. Erişim listesi yalnızca SYSTEM, Yöneticiler ve
/// etkileşimli (oturum açmış) kullanıcılara izin verir; ağdan bağlantı reddedilir. İstekler 64 KiB, yanıtlar (istemci tarafı) 1 MiB ile
/// sınırlıdır; hizmet "status" yanıtını yine de eski istemcinin 64 KiB'ına sığacak biçimde kırpar (1.8.1).
/// </summary>
public static class PipeProtocol
{
    /// <summary>Kanal adı. Yalnızca geliştirme modunda (`--console --pipe ad`) değiştirilir; kurulu hizmet ile çakışmasın diye.</summary>
    public static string PipeName { get; set; } = DefaultPipeName;

    /// <summary>Kurulu hizmetin kanal adı (teknik kimlik, eski adla).</summary>
    public const string DefaultPipeName = "APFSforWindows";
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>İstemcinin okuduğu YANIT satırının sınırı (1.8.1). İstekler <see cref="MaxMessageBytes"/>'ta kalır.</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    /// <summary>"status" yanıtındaki bildirimlerin toplam JSON boyu (1.8.1): yanıt eski istemcinin 64 KiB'ına da sığsın.</summary>
    public const int MaxNoticeBytes = 32 * 1024;

    /// <summary>
    /// Tek bildirim metninin en çok uzunluğu (karakter; 1.8.2). JSON kaçışıyla en kötü 6 bayt/karakter: tek bildirim
    /// <see cref="MaxNoticeBytes"/> bütçesine her zaman sığar (önceden daha uzunu yanıttan sessizce düşerdi; bugünkü metinlerle olanaksız).
    /// </summary>
    public const int MaxNoticeChars = 2048;

    /// <summary>Bildirim metnini <see cref="MaxNoticeChars"/>'a kısaltır (sonuna "…"; vekil çifti bölünmez).</summary>
    public static string ClampNoticeText(string text)
    {
        if (text.Length <= MaxNoticeChars) return text;
        int n = MaxNoticeChars - 1;
        if (char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n] + "…";
    }

    /// <summary>"status" yanıtındaki parola ipucunun en çok uzunluğu (karakter; 1.8.1).</summary>
    public const int MaxHintChars = 256;

    /// <summary>
    /// Bildirimlerden JSON boyu (<see cref="Json"/> ile, ayırıcı virgüller dahil) bütçeye sığan EN YENİLERİ, sıralarını koruyarak
    /// döndürür. Hizmetin "status" yanıtı için: kuyruktaki 200 bildirim uzun ve ASCII dışı metinle (Web JSON her birini
    /// 6 baytlık kaçışla yazar) yanıtı 64 KiB'ın üstüne çıkarıyordu. Atılan eski bildirimleri tepsi ya zaten gördü ya da ilk
    /// açılışta göstermez.
    /// </summary>
    public static List<Notice> NewestWithinBudget(IReadOnlyList<Notice> notices, int budget = MaxNoticeBytes)
    {
        int used = 2; // [ ]
        int first = notices.Count;
        while (first > 0)
        {
            int size = JsonSerializer.SerializeToUtf8Bytes(notices[first - 1], Json).Length + (first < notices.Count ? 1 : 0);
            if (used + size > budget) break;
            used += size;
            first--;
        }
        return [.. notices.Skip(first)];
    }

    /// <summary>
    /// İpucunu <see cref="MaxHintChars"/>'a kısaltır (düşmanca diskte keybag ipucu ~11 bin karakter olabiliyor). Vekil çifti
    /// bölünmez.
    /// </summary>
    public static string? ClampHint(string? hint)
    {
        if (hint is null || hint.Length <= MaxHintChars) return hint;
        int n = MaxHintChars;
        if (char.IsHighSurrogate(hint[n - 1])) n--;
        return hint[..n];
    }

    /// <summary>Test kancası: bu ad da varsayılan kanal gibi oturum denetiminden geçer (ortam değişkeni bilerek YOK).</summary>
    internal static string? SessionCheckedPipe { get; set; }

    /// <summary>
    /// Bu kanal adında istemci sunucunun hizmetlerin oturumunda (0) olmasını şart koşar mı. Yalnız kurulu hizmetin adı:
    /// geliştirme kanalının (<c>--pipe</c>) sunucusu kullanıcı oturumunda çalışır.
    /// </summary>
    public static bool RequiresServiceSession(string pipeName) =>
        string.Equals(pipeName, DefaultPipeName, StringComparison.OrdinalIgnoreCase) // kanal adları büyük/küçük harf ayırmaz
        || (SessionCheckedPipe is { } test && string.Equals(pipeName, test, StringComparison.OrdinalIgnoreCase));

    /// <summary>"reports-pull" sayfasındaki olayların toplam boyu (yanıt kanal sınırının altında kalsın).</summary>
    public const int MaxReportPageBytes = 48 * 1024;

    /// <summary>İstemcinin arayüz dili; <see cref="SendAsync{T}"/> dili belirtilmemiş her isteğe ekler (tepsi açılışta ayarlar).</summary>
    public static string? ClientLanguage { get; set; }

    /// <summary>İstemcinin bölge ayarı (sayı biçimi kültürü); <see cref="SendAsync{T}"/> belirtilmemiş her isteğe ekler (1.8.2).</summary>
    public static string? ClientCulture { get; set; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="firstInstance">Kanalın ilk örneği (<see cref="PipeOptions.FirstPipeInstance"/>): ad başka bir sürecin
    /// elindeyse <see cref="UnauthorizedAccessException"/>; hizmet gaspı böyle fark eder. Sonraki örnekler false (ilk örnek
    /// bizde açıkken açılır).</param>
    public static NamedPipeServerStream CreateServer(bool firstInstance = false)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Kanalı açan sürecin kendisi (hizmette SYSTEM): yeni kanal örnekleri açabilmesi için gerekli
        using (var self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : 0),
            MaxMessageBytes, MaxMessageBytes, security);
    }

    /// <param name="maxBytes">Satır sınırı: sunucu istekleri <see cref="MaxMessageBytes"/>'la, istemci yanıtları
    /// <see cref="MaxResponseBytes"/>'la okur. Aşılırsa <see cref="InvalidDataException"/>.</param>
    /// <remarks>
    /// Arabellekli okur (1.8.2; önceden bayt bayt <c>await ReadAsync</c>: 72 KB'lık yanıt 72 000 iş parçacığı geçişiydi, ağır
    /// işlemci yükünde saniyeler sürüyordu). Satır sonundan sonra okunan baytlar atılır: bağlantı başına tek istek, tek yanıt.
    /// Satırın içeriği <paramref name="maxBytes"/>'tan kısa olmalı (eskisi gibi).
    /// </remarks>
    public static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct, int maxBytes = MaxMessageBytes)
    {
        var buf = new MemoryStream();
        var chunk = new byte[Math.Min(16 * 1024, maxBytes)];
        while (buf.Length < maxBytes)
        {
            int want = (int)Math.Min(chunk.Length, maxBytes - buf.Length);
            int n = await s.ReadAsync(chunk.AsMemory(0, want), ct);
            if (n == 0) return buf.Length == 0 ? null : Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
            int newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
            if (newline >= 0)
            {
                buf.Write(chunk, 0, newline);
                return Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
            }
            buf.Write(chunk, 0, n);
        }
        throw new InvalidDataException(WinText.T("Ipc.MessageTooLong"));
    }

    public static async Task WriteLineAsync(Stream s, object value, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n");
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    /// <summary>
    /// <see cref="SendAsync{T}"/>'in "hizmete ulaşılamadı" sayılan bütün hataları (1.8.2): bağlanamama/süre (Timeout,
    /// OperationCanceled), kanal kopması ve güvenilmeyen sunucu (IOException), erişim reddi (UnauthorizedAccess), aşırı uzun yanıt
    /// (InvalidData), bozuk yanıt (Json). Tepsinin ve rapor istemcisinin her isteği bununla yakalar: önceden bazı istekler yalnız
    /// ilk üçünü yakalıyordu, öbürleri tepsiyi düşürebiliyordu.
    /// </summary>
    public static bool IsChannelFailure(Exception e) =>
        e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException or JsonException;

    /// <summary>İstemci tarafı: tek istek gönderir, yanıtı döndürür. Hizmet çalışmıyorsa TimeoutException/IOException.</summary>
    public static async Task<T?> SendAsync<T>(Request request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await client.ConnectAsync(cts.Token);
        // Kurulu hizmetin kanalında sunucu oturum 0'da olmalı: hizmet dururken adı kapan kullanıcı süreci oturum 0'da
        // çalışamaz. Denetlenemezse de güvenilmez sayılır; isteğin tek baytı gitmez.
        if (RequiresServiceSession(PipeName) && !(GetNamedPipeServerSessionId(client.SafePipeHandle, out uint session) && session == 0))
            throw new UntrustedServerException(WinText.T("Ipc.UntrustedServer", PipeName));
        if (request.Lang is null && ClientLanguage is not null) request = request with { Lang = ClientLanguage };
        if (request.Culture is null && ClientCulture is not null) request = request with { Culture = ClientCulture };
        await WriteLineAsync(client, request, cts.Token);
        var line = await ReadLineAsync(client, cts.Token, MaxResponseBytes);
        return line is null ? default : JsonSerializer.Deserialize<T>(line, Json);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);
}

/// <summary>
/// Kanalın karşısındaki sunucu kurulu hizmet değil (1.8.1): varsayılan kanalda sunucu hizmetlerin oturumunda (0) olmalı.
/// <see cref="IOException"/>: tepsi bunu "hizmete ulaşılamadı" sayar ve hiçbir şey göndermez.
/// </summary>
public sealed class UntrustedServerException(string message) : IOException(message);
