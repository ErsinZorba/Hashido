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
    /// <summary>
    /// Hizmet parolanın yanlış olduğunu söyledi (Mac'te değişmiş ya da aynı UUID'li başka bir disk, ör. eski bir kopya): kayıt
    /// SİLİNMEZ (1.9.0; silmek gerçek diskin kaydını da götürüyordu), pencere "geçersiz" iletisiyle bir kez, kutu işaretli; disk
    /// takılı kaldıkça kayıtlı parola o birime bir daha gönderilmez.
    /// </summary>
    WrongAndAsk,
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
    /// <param name="sessionActive">Tepsinin Windows oturumu etkin (<see cref="Tray.SessionState.IsActive"/>). Arka plandaki oturum
    /// (hızlı kullanıcı değiştirmede bağlantısı kesik) kayıtlı parolayı Hello'suz KENDİLİĞİNDEN kullanmaz: açılan disk makinedeki
    /// herkese açık olurdu, o sırada bilgisayarın başındaki başka bir kullanıcı da okurdu (1.9.0). Hello akışı değişmez.</param>
    public static RememberAction Decide(bool hasSaved, HelloAvailability hello, bool partitionWritable, bool sessionActive) =>
        partitionWritable ? RememberAction.None
        : !hasSaved ? RememberAction.Ask
        : hello switch
        {
            HelloAvailability.Available => RememberAction.VerifyWithHello,
            HelloAvailability.NotAvailable => sessionActive ? RememberAction.Unlock : RememberAction.Ask,
            _ => RememberAction.Ask,
        };

    /// <summary>Hello penceresinden sonra: yalnız onay kayıtlı parolayı kullandırır; vazgeç, hata, süre → parola penceresi.</summary>
    public static RememberAction AfterHello(HelloResult result) =>
        result == HelloResult.Verified ? RememberAction.Unlock : RememberAction.Ask;

    /// <summary>
    /// Kayıtlı parolayla <c>unlock</c> yanıtı → ne yapılacağı. Hizmet kesin "parola yanlış" derse
    /// (<c>UnlockResponse.Code == "WrongPassword"</c>) pencere "geçersiz" iletisiyle; ulaşılamayan hizmet ya da başka bir ret
    /// yalnız hatayla. Kayıt hiçbirinde silinmez; yalnız "hatırla" ile tutan parola değiştirir, kutusu boş tutan parola siler.
    /// </summary>
    /// <param name="reached">Hizmet yanıt verdi (zaman aşımı / kanal hatası değil).</param>
    public static SavedAttemptOutcome AfterSavedAttempt(bool reached, bool ok, string? code) =>
        !reached ? SavedAttemptOutcome.KeepAndAsk
        : ok ? SavedAttemptOutcome.Unlocked
        : code == nameof(ApfsErrorCode.WrongPassword) ? SavedAttemptOutcome.WrongAndAsk
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
