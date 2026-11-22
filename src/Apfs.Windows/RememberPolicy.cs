namespace Apfs.Windows;

/// <summary>Windows Hello bu oturumda kullanılabilir mi (<see cref="WindowsHello.AvailabilityAsync"/>).</summary>
public enum HelloAvailability
{
    /// <summary>Yerel oturum ve Windows "Available" dedi: kayıtlı parola Hello onayıyla kullanılır.</summary>
    Available,
    /// <summary>Yerel oturum ve Windows kesin "yok" dedi (aygıt yok, kullanıcı kurmamış, ilkeyle kapalı): kendiliğinden açılır.</summary>
    NotAvailable,
    /// <summary>Bilinmiyor: uzak oturum, aygıt meşgul, API hatası, tanınmayan yanıt. Güvenli yön: parola penceresi.</summary>
    Unknown,
}

/// <summary>Hello onay penceresinin sonucu.</summary>
public enum HelloResult { Verified, Canceled, Failed }

/// <summary>Kilitli birim için tepsinin yapacağı (<see cref="RememberPolicy.Decide"/>).</summary>
public enum RememberAction
{
    /// <summary>Hiçbir şey (bölüm yazmaya açık: kilitli birim dokunulmadan kalır, parola da sorulmaz; 1.7.0 kuralı).</summary>
    None,
    /// <summary>Parola penceresi.</summary>
    Ask,
    /// <summary>Önce Windows Hello onayı; onaylanınca kayıtlı parolayla açılır, aksi hâlde parola penceresi.</summary>
    VerifyWithHello,
    /// <summary>Kayıtlı parolayla doğrudan açılır.</summary>
    Unlock,
}

/// <summary>Kayıtlı parolayla kilit açma denemesinden sonra (<see cref="RememberPolicy.AfterSavedAttempt"/>).</summary>
public enum SavedAttemptOutcome
{
    /// <summary>Açıldı.</summary>
    Unlocked,
    /// <summary>Hizmet parolanın yanlış olduğunu söyledi (Mac'te değişmiş): kayıt silinir, pencere "geçersiz" iletisiyle bir kez.</summary>
    ForgetAndAsk,
    /// <summary>Başka her hata (hizmete ulaşılamıyor, birim beklemiyor, bölüm yazılabilir…): kayıt KALIR, parola penceresi.</summary>
    KeepAndAsk,
}

/// <summary>Elle girilen parola tuttuktan sonra kayda ne olacağı (<see cref="RememberPolicy.AfterManualUnlock"/>).</summary>
public enum RememberStore { None, Save, Delete }

/// <summary>
/// "Bu bilgisayarda hatırla" kararları (parolaPlan 1.5; saf işlevler, tablo testleri <c>RememberPolicyTests</c>). İlke: Hello'dan
/// kesin "var" ya da kesin "yok" gelmedikçe kayıtlı parola kendiliğinden kullanılmaz — uzak masaüstünde Hello kurulu kullanıcıda
/// bile "yok" göründüğü için "yoksa kendiliğinden aç" kuralı orada Hello'yu atlatırdı (plan Risk 3).
/// </summary>
public static class RememberPolicy
{
    public static RememberAction Decide(bool hasSaved, HelloAvailability hello, bool partitionWritable) =>
        partitionWritable ? RememberAction.None
        : !hasSaved ? RememberAction.Ask
        : hello switch
        {
            HelloAvailability.Available => RememberAction.VerifyWithHello,
            HelloAvailability.NotAvailable => RememberAction.Unlock,
            _ => RememberAction.Ask,
        };

    /// <summary>Hello penceresinden sonra: yalnız onay kayıtlı parolayı kullandırır; vazgeç, hata, süre → parola penceresi.</summary>
    public static RememberAction AfterHello(HelloResult result) =>
        result == HelloResult.Verified ? RememberAction.Unlock : RememberAction.Ask;

    /// <summary>
    /// Kayıtlı parolayla <c>unlock</c> yanıtı → ne yapılacağı. Kayıt YALNIZ hizmet kesin "parola yanlış" derse silinir
    /// (<c>UnlockResponse.Code == "WrongPassword"</c>); ulaşılamayan hizmet ya da başka bir ret kaydı silmez.
    /// </summary>
    /// <param name="reached">Hizmet yanıt verdi (zaman aşımı / kanal hatası değil).</param>
    public static SavedAttemptOutcome AfterSavedAttempt(bool reached, bool ok, string? code) =>
        !reached ? SavedAttemptOutcome.KeepAndAsk
        : ok ? SavedAttemptOutcome.Unlocked
        : code == nameof(ApfsErrorCode.WrongPassword) ? SavedAttemptOutcome.ForgetAndAsk
        : SavedAttemptOutcome.KeepAndAsk;

    /// <summary>
    /// Elle girilen parola TUTTUKTAN sonra (yalnız o zaman çağrılır): kutu işaretli → kaydet, boş → varsa sil, kutu yok
    /// (birim UUID'si bilinmiyor) → dokunma.
    /// </summary>
    public static RememberStore AfterManualUnlock(bool? remember) => remember switch
    {
        true => RememberStore.Save,
        false => RememberStore.Delete,
        null => RememberStore.None,
    };

    /// <summary>Birim Mac'te yeniden adlandırılmış: kayıtlı parolayla açınca kaydın adı (Ayarlar listesi) güncellenir.</summary>
    public static bool NeedsRename(string? storedName, string currentName) => storedName is not null && storedName != currentName;

    /// <summary>
    /// Parola penceresini açmadan önce: birim hâlâ aynı yerde kilitli bekliyor mu (Hello sürerken disk çekilmiş, başka yoldan
    /// açılmış ya da bölüm yazmaya açılmış olabilir). Durum alınamadıysa (null) bilinmiyor → true (eski davranış: pencere açılır).
    /// UUID ikisinde de varsa eşit olmalı (aynı yuvaya başka disk takılmış olabilir).
    /// </summary>
    public static bool StillLocked(Ipc.StatusResponse? now, string diskId, int container, int index, string? uuid)
    {
        if (now is null) return true;
        var disk = now.Disks.FirstOrDefault(d => d.Id == diskId);
        var v = disk?.Volumes.FirstOrDefault(x => x.Container == container && x.Index == index);
        if (disk is null || v is null || v.State != Ipc.VolumeStatus.NeedsPassword) return false;
        if (uuid is not null && v.Uuid is not null && !string.Equals(uuid, v.Uuid, StringComparison.OrdinalIgnoreCase)) return false;
        return !disk.Volumes.Any(x => x.Container == container && x.Writable);
    }

    /// <summary>
    /// Geliştirme kancası <c>APFS_DEV_HELLO</c> (none / unknown / available): Hello'lu makinede Hello'suz "kendiliğinden aç"
    /// yolunu uçtan uca denemek için. YALNIZ geliştirme kanalında (<c>--pipe</c> başka bir ad) geçerli; kurulu tepside
    /// (<see cref="Ipc.PipeProtocol.DefaultPipeName"/>) her zaman null → gerçek Hello sorulur. Tanınmayan değer de null.
    /// </summary>
    public static HelloAvailability? DevHelloOverride(string pipeName, string? env) =>
        pipeName == Ipc.PipeProtocol.DefaultPipeName ? null
        : env switch
        {
            "none" => HelloAvailability.NotAvailable,
            "unknown" => HelloAvailability.Unknown,
            "available" => HelloAvailability.Available,
            _ => null,
        };

    /// <summary>
    /// <c>UserConsentVerifierAvailability</c> değeri (null = sorgu başarısız) → <see cref="HelloAvailability"/>. Uzak oturumda
    /// Windows'un yanıtına bakılmaz (orada Hello hep "yok" görünür).
    /// </summary>
    public static HelloAvailability FromAvailability(int? value, bool remoteSession) =>
        remoteSession ? HelloAvailability.Unknown
        : value switch
        {
            0 => HelloAvailability.Available,          // Available
            1 or 2 or 3 => HelloAvailability.NotAvailable, // DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy
            _ => HelloAvailability.Unknown,            // DeviceBusy (4), tanınmayan, hata
        };

    /// <summary><c>UserConsentVerificationResult</c> değeri (null = çağrı başarısız) → <see cref="HelloResult"/>.</summary>
    public static HelloResult FromVerification(int? value) => value switch
    {
        0 => HelloResult.Verified,  // Verified
        6 => HelloResult.Canceled,  // Canceled
        _ => HelloResult.Failed,    // DeviceNotPresent, NotConfiguredForUser, DisabledByPolicy, DeviceBusy, RetriesExhausted, hata
    };
}
