namespace Apfs.Windows.Tray;

/// <summary>Balonun düzeyi (ikonu). Hizmetin "hata"/"uyarı" düzeylerinden ve tepsinin kendi bilgi balonlarından.</summary>
public enum NoticeLevel { Info, Warning, Error }

/// <summary>Onay kutusundaki devam düğmesinin rol tonu: dikkatli eylem (Yazmaya izin ver, Kur) ya da geri alınmaz eylem (Yine de çıkar).</summary>
public enum ConfirmTone { Caution, Alert }

/// <summary>Bilgi kutusunun ikonu: bilgi (mürekkep) ya da uyarı (Alert).</summary>
public enum InformIcon { Info, Warning }

/// <summary>Parola penceresinin girdileri (bugünkü <c>PasswordForm</c> kurucusunun parametreleri).</summary>
/// <param name="Error">Önceki denemenin hatası (pencerede kırmızı satır); yoksa null.</param>
/// <param name="Remember">"Bu bilgisayarda hatırla" kutusunun ilk hâli; null = kutu yok (birimin UUID'si bilinmiyor: eski hizmet).</param>
/// <param name="Hello">Windows Hello kullanılabilir: kutunun altındaki satır Hello'yu söyler, değilse "oturum açan herkes açar".</param>
public sealed record PasswordRequest(string VolumeName, string? Hint, string? Error, bool? Remember, bool Hello);

/// <summary>Kullanıcının "Kilidi aç" cevabı. <paramref name="Remember"/>: kutunun son hâli (kutu yoksa null).</summary>
public sealed record PasswordAnswer(string Password, bool? Remember);

/// <summary>
/// Tepsi mantığının (<see cref="AppController"/>, <see cref="TrayFeatures"/>) arayüze dönük tek kapısı (winuiPlan P1): kutular,
/// parola penceresi, Hello'nun sahibi olan pencere ve balonlar. Mantık hangi arayüz kütüphanesiyle çizildiğini bilmez; bugün
/// WinForms (<c>Apfs.Tray.WinFormsUiHost</c>), testlerde sahte arayüz.
/// <para>
/// <c>owner</c>: kutuyu açan pencere (arayüzün kendi pencere nesnesi; mantık yalnız taşır). null = SAHİPSİZ kutu: ekranın
/// ortasında, en üstte, görev çubuğunda, gösterilince etkin (panelden, tepsi menüsünden açılanlar). Sahipli kutu sahibin
/// ortasında, görev çubuğunda değil. Onaylarda VARSAYILAN düğme Vazgeç'tir: Enter, Esc ve kapatma Vazgeç = false.
/// </para>
/// Açık kutu sayacını (<see cref="AppController.Idle"/>) mantık tutar; kutu dönene kadar tepsi yeniden başlamaz.
/// </summary>
public interface IUiHost
{
    /// <summary>Onay kutusu: başlık, metin, tonlu devam düğmesi ve varsayılan Vazgeç. Devam'a basıldıysa true.</summary>
    Task<bool> ConfirmAsync(object? owner, string heading, string text, string proceed, ConfirmTone tone);

    /// <summary>Bilgi/uyarı kutusu: isteğe bağlı başlık, metin, tek "Tamam".</summary>
    Task InformAsync(object? owner, string? heading, string text, InformIcon icon);

    /// <summary>
    /// Şifreli birimin parola penceresi (her zaman sahipsiz: ekranın ortasında, en üstte). İptal (Esc, kapatma) → null.
    /// "Kilidi aç" → pencere HEMEN kapanır, cevap döner (kilit açma beklenirken pencere yok).
    /// </summary>
    Task<PasswordAnswer?> AskPasswordAsync(PasswordRequest request);

    /// <summary>
    /// Windows Hello onayı için GÖRÜNÜR, küçük, önde ve ortada bir Hashido penceresi açar (kapatma düğmesi yok; kalıcı DEĞİL: Hello
    /// beklerken öbür pencereler kullanılabilir), <paramref name="verify"/>'ı o pencerenin tutamacıyla çağırır, bitince kapatır.
    /// Gizli bir pencereye bağlanan Hello isteği ekrana gelmiyor (parolaPlan 2.7 f; <c>kurallar/arayuz.md</c> 🔴).
    /// </summary>
    Task<T> RunWithHelloOwnerAsync<T>(string message, Func<IntPtr, Task<T>> verify);

    /// <summary>Balon (gönderen "Hashido"; tıklayınca ana pencere). Hizmet bildirimleri 5 sn.</summary>
    void Notify(string text, NoticeLevel level);
}
