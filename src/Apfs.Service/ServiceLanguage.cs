using System.Globalization;
using Apfs.Localization;
using Apfs.Windows;

namespace Apfs.Service;

/// <summary>
/// Hizmetin dili ve sayı biçimi. Hizmet LocalSystem olarak çalıştığı için kullanıcının dilini bilmez: tepsi her istekte kendi
/// arayüz dilini (<see cref="Apfs.Windows.Ipc.Request.Lang"/>) ve bölge ayarını (<see cref="Apfs.Windows.Ipc.Request.Culture"/>)
/// gönderir, geliştirme modunda `--lang` verilebilir. Yalnız desteklenen diller ve tanımlı kültürler kabul edilir.
/// <list type="bullet">
/// <item><b>İstek başına (1.8.2):</b> her isteğin işlenmesi (yanıtı ve o sırada üretilen metinler) isteyenin dilinde ve
/// sayı biçimindedir (<see cref="BeginRequest"/>; değerler istek boyunca eşzamansız akışla taşınır, başka iş parçacığına sızmaz).</item>
/// <item><b>Varsayılan:</b> hizmetin kendiliğinden ürettiği metinler (takma bildirimi, dosya hatası, WinFsp iş parçacıkları) varsayılan
/// dil ve biçimdedir. Varsayılanı yalnız ETKİN KONSOL oturumundan gelen istek değiştirir (konsol oturumu yoksa her istek): iki dilli
/// iki tepsi (hızlı kullanıcı değiştirme) her istekte varsayılanı çevirip günlüğe satır yazdırıyordu. Günlüğe yalnız
/// varsayılan dil değişince satır gider.</item>
/// <item>Biçim kültürü <see cref="Format"/> ile AÇIKÇA geçirilir (<see cref="Apfs.Windows.Localization.SizeText.Format"/>);
/// hizmetin genel <see cref="CultureInfo.CurrentCulture"/>'ı değişmez (ayrıştıran kodları etkilemesin). Eski tepside biçim yok →
/// hizmetin kendi kültürü (sistem yereli).</item>
/// </list>
/// </summary>
internal static class ServiceLanguage
{
    private static string? _current;
    private static CultureInfo? _defaultFormat;
    private static readonly AsyncLocal<CultureInfo?> s_requestFormat = new();

    /// <summary>
    /// Etkin konsol oturumu yerine kullanılacak oturum (yalnız geliştirme kipi: kurulu hizmet <c>WTSGetActiveConsoleSessionId</c>'yi
    /// sorar). Geliştirme hizmeti kullanıcının oturumunda çalışır; varsayılan olarak kendi oturumu "konsol" sayılır.
    /// </summary>
    public static uint? ConsoleSessionOverride { get; set; }

    private static readonly Lock s_applyGate = new();

    /// <summary>Dil değişikliği satırının gittiği yer (test kancası: testler günlüğe yazmaz).</summary>
    internal static Action<string> Log { get; set; } = ServiceLog.Write;

    /// <summary>Varsayılan arayüz dilini değiştirir (açılışta `--lang`, sonra konsol oturumunun istekleri); değişince günlüğe bir satır.</summary>
    public static void Apply(string? name) => Apply(Languages.Resolve(name));

    /// <summary>
    /// Karşılaştır-yaz tek kilit altında (1.9.1): iki istek aynı anda farklı dil verince <c>_current</c> birinin, varsayılan UI
    /// kültürü öbürünün dilinde kalabiliyordu; sonra o dili isteyen istek "zaten bu dil" sanıp düzeltmiyordu.
    /// </summary>
    private static void Apply(CultureInfo? culture)
    {
        if (culture is null) return;
        lock (s_applyGate)
        {
            if (culture.Name == _current) return;
            _current = culture.Name;
            // İş parçacıkları kendi dilini hiç ayarlamadığı için bu, hizmetin bütün iş parçacıklarına (WinFsp dahil) geçer.
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Log(WinText.T("Service.LanguageSet", culture.Name));
        }
    }

    /// <summary>Metinlerdeki sayıların biçimi: istek sürerken isteyeninki, yoksa varsayılan, o da yoksa hizmetin kendi kültürü.</summary>
    public static CultureInfo Format => s_requestFormat.Value ?? Volatile.Read(ref _defaultFormat) ?? CultureInfo.CurrentCulture;

    /// <summary>
    /// İsteğin işlenmesine başlarken (kanal işleyicisinin eşzamansız akışında) çağrılır: istek boyunca arayüz dili ve sayı biçimi
    /// isteyeninki; <paramref name="setsDefault"/> (etkin konsol oturumundan) ise varsayılanlar da. Geçersiz/eksik değer yok sayılır
    /// (o alan için varsayılan geçerli).
    /// </summary>
    public static void BeginRequest(string? lang, string? culture, bool setsDefault)
    {
        var ui = Languages.Resolve(lang);
        var format = Languages.ResolveFormat(culture);
        if (setsDefault)
        {
            Apply(ui);
            if (format is not null) Volatile.Write(ref _defaultFormat, format);
        }
        if (ui is not null) CultureInfo.CurrentUICulture = ui; // yalnız bu akış; işleyici dönünce eski hâline döner
        s_requestFormat.Value = format;
    }
}
