# ARM sanal makinesi denemesi — Hashido 1.4.0 (Windows 11 ARM64, x64 öykünmesi)

Amaç: Apple işlemcili Mac'te çalışan Windows 11 ARM sanal makinesinde (Parallels Desktop ya da VMware Fusion) setup'ın
kurulduğunu, WinFsp'in x64 dosyalarıyla çalıştığını, okuma, yazma ve şifreli birim açmanın x64 öykünmesiyle sorunsuz
olduğunu göstermek. Kod ARM64 için değişmedi (WinFsp 2.1 ARM64'te `winfsp-x64.dll` ve `launchctl-x64.exe`'yi de kurar).
Gereken: `Hashido-Setup-1.4.0.exe` (Release'teki ya da Windows'ta derlenen), USBTEST belleği (şifreli USBTEST bölümü;
parolası sizde), sanal makineye USB aktarma (Parallels: Aygıtlar › USB; Fusion: Sanal Makine › USB ve Bluetooth).

Her adımın sonucunu (geçti / kaldı + ekran görüntüsü ya da hata metni) not edin.

## 1. Kurulum (sanal makinede, Windows 11 ARM)
1. `winver` → Windows 11 ve sürüm numarası; Ayarlar › Sistem › Hakkında → "Sistem türü: ARM tabanlı işlemci". Not edin.
2. Setup'ı çalıştırın (SmartScreen "Yine de çalıştır"). Sihirbaz WinFsp'i de kurar. Sonda "Hashido uygulamasını aç" işaretli bitirin.
3. Denetim (yönetici PowerShell):
   ```
   Get-Service APFSforWindows | Format-List Name, DisplayName, Status
   Test-Path "${env:ProgramFiles(x86)}\WinFsp\bin\winfsp-x64.dll"
   Test-Path "${env:ProgramFiles(x86)}\WinFsp\bin\launchctl-x64.exe"
   Get-ChildItem "C:\Program Files\Hashido" | Select-Object -First 5
   ```
   Beklenen: DisplayName `Hashido`, Status `Running`, iki `True`, klasör dolu.
4. Görev Yöneticisi › Ayrıntılar → sütunlara "Mimari"yi ekleyin: `apfs-service.exe` ve `apfs-tray.exe` **x64** görünmeli.
5. Başlat menüsünde **Hashido** var; açılınca ana pencere ve ilk açılışta raporlama onay penceresi çıkıyor (iki düğme eşit).
   Cevabınızı not edin.

## 2. Okuma
1. USBTEST'i Mac'ten ayırıp sanal makineye bağlayın.
2. Birkaç saniye içinde ana pencerede disk kartı çıkmalı. Şifresiz bölüm (non-password) sürücü harfiyle açılır; birkaç
   dosyayı (fotoğraf, belge) açın.
3. USBTEST (şifreli) için **Kilidi aç** → parola → sürücü harfi. Birkaç dosya açın.
4. Yönetici PowerShell'de öz denetim (diskin numarasını Disk Yönetimi'nden bakın; N):
   ```
   & "C:\Program Files\Hashido\apfs.exe" check \\.\PhysicalDriveN --password-prompt
   ```
   Beklenen: sonda hatasız özet.

## 3. Yazma (USBTEST'e)
1. Sanal makinede küçük bir deneme klasörü hazırlayın (ör. `C:\arm-deneme`; birkaç belge, bir fotoğraf, Türkçe adlı bir dosya
   `çalışma ğüşiöı.txt`, 100 MB'tan büyük bir dosya). Parmak izlerini alın:
   ```
   Get-ChildItem C:\arm-deneme -File -Recurse | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $($_.Path | Split-Path -Leaf)" } | Set-Content $HOME\Desktop\arm-deneme.sha256.txt
   ```
2. USBTEST kartında **Yazmaya izin ver** → onay → turuncu nokta "yazılabilir (deneysel)".
3. `C:\arm-deneme` klasörünü Gezgin'le USBTEST'e kopyalayın; bir dosyanın adını değiştirin, bir dosyayı silin.
4. **Salt okunura dön**, sonra **Güvenle çıkar** → "Kabloyu şimdi çekebilirsiniz".

## 4. Mac'te doğrulama
1. Belleği Mac'e takın (USBTEST parolasını macOS sorar).
2. Terminal:
   ```
   diskutil list
   diskutil verifyVolume /Volumes/USBTEST
   cd /Volumes/USBTEST/arm-deneme && shasum -a 256 *
   ```
   Beklenen: İlk Yardım "appears to be OK"; `shasum` çıktısı sanal makinedeki `arm-deneme.sha256.txt` ile aynı (adı
   değiştirilen dosya yeni adıyla, silinen yok). Finder'da Türkçe adlı dosya doğru adla görünür.
3. Mac kiti varsa (`APFS-Mac-Kiti` klasörü bellekte): `bash /Volumes/USBTEST/APFS-Mac-Kiti/apfs-mac-kit.sh usb USBTEST`
   (parmak izi listesi eskiyse "FARKLI" yalnız yeni klasör yüzünden çıkar; İlk Yardım sonucu esas alınır).

## 5. Getirilecekler
Sonuç notları, sanal makinedeki `C:\ProgramData\APFS for Windows\log.txt`, `arm-deneme.sha256.txt`, Mac Terminal çıktısı.
Bir adım kalırsa orada durun ve hata metnini/ekran görüntüsünü getirin; sanal makinede başka bir şey değiştirmeyin.
