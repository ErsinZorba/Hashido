# Privacy statement / Gizlilik açıklaması

Last updated: 2026-11-08. The English text is binding; the Turkish translation is for information only.
Son güncelleme: 2026-11-08. Bağlayıcı olan İngilizce metindir; Türkçe çeviri bilgi amaçlıdır.

The same text is in the README ([English](../README.md#privacy), [Türkçe](../README.tr.md#gizlilik)).

## English

**Automatic reports are off until you say yes.** The first time the program starts it asks once whether it may send anonymous
reports. Until you answer "Yes, send", nothing is collected or sent. You can change your choice at any time in **Settings**;
turning reports off also deletes the reports on your computer that have not been sent yet.

**What is sent and why.** Reports describe errors, crashes and successful operations (connecting a disk, unlocking, enabling
writing, a writing session, safely ejecting). They help find bugs and show which disks and situations cause problems, especially
with the experimental writing feature. A report contains only: program and Windows version, processor type, interface language,
the operation, its result and error code, a rough duration range, the day; technical disk details: connection type (USB, NVMe…),
sector and block size, partition scheme, number of APFS partitions and volumes, a rough size range (powers of two), APFS format
feature flags, volume roles, encryption type, case sensitivity, whether snapshots exist, the name and version of the software
that last wrote the volume (e.g. `apfs_kext (2313.1)`), a rough file count range; for writing sessions, rough ranges of the
number of saves, bytes and operations and how the session ended; for crashes, the error type and the names of the program's
own functions where it happened. The exact list of allowed fields is [`worker/schema.json`](schema.json); the program and
the server both reject any report with a field not in that list.

**Never sent:** file, folder, volume or disk names, file contents, serial numbers, UUIDs or other identifiers, exact sizes,
passwords, keys or password hints, computer name, user name, file paths, error messages, or any permanent device identifier.

**Where it goes.** The program sends reports over HTTPS to `hashido-api.zorba.dev`, a Cloudflare Worker run by the developer,
which stores them in a Cloudflare D1 database (Western Europe). Your IP address is not stored and the server keeps no request
logs; Cloudflare processes the IP address only briefly to deliver the request (see Cloudflare's privacy policy). Reports are
not sold, not used for advertising and not shared with anyone else.

**How long.** Reports are deleted automatically 18 months after they are received.

**On your computer.** Reports waiting to be sent are kept in `%ProgramData%\APFS for Windows\reports\` (at most 200) and, for
the tray program's own crash reports, in `%LOCALAPPDATA%\APFS for Windows\reports\`. The last 20 reports sent are shown, exactly
as sent, under **Settings › Show what was sent**.

**Your rights.** Because reports contain no identifier, nobody (including the developer) can tell which reports came from you,
so individual reports cannot be found or deleted on request. Turning reports off stops all future reports.

**Update check.** Unless you turn it off in Settings, the program asks GitHub (`api.github.com`) once a day whether a newer
version exists. Nothing about you or your disks is sent; GitHub sees the request as described in GitHub's privacy statement.

**Contact.** Questions and requests: [GitHub Issues](https://github.com/ErsinZorba/Hashido/issues). Changes to this
statement are visible in the repository history.

## Türkçe

**Otomatik raporlar siz "evet" diyene kadar kapalıdır.** Program ilk açılışta bir kez anonim rapor gönderip gönderemeyeceğini
sorar. "Evet, gönder" demediğiniz sürece hiçbir şey toplanmaz ve gönderilmez. Seçiminizi istediğiniz zaman **Ayarlar**'dan
değiştirebilirsiniz; raporları kapatmak bilgisayarınızda henüz gönderilmemiş raporları da siler.

**Ne gönderilir, neden.** Raporlar hataları, çökmeleri ve başarılı işlemleri (disk bağlama, kilit açma, yazmaya açma, yazma
oturumu, güvenle çıkarma) anlatır. Hataları bulmaya ve özellikle deneysel yazma özelliğinde hangi disklerin ve durumların sorun
çıkardığını görmeye yarar. Bir rapor yalnız şunları içerir: program ve Windows sürümü, işlemci türü, arayüz dili, işlem, sonucu
ve hata kodu, kaba süre aralığı, gün; diskin teknik özellikleri: bağlantı türü (USB, NVMe…), sektör ve blok boyutu, bölüm
şeması, APFS bölümü ve birim sayısı, kaba boyut aralığı (2'nin kuvvetleri), APFS biçim özellik bayrakları, birim rolleri,
şifreleme türü, harf duyarlılığı, anlık görüntü (snapshot) olup olmadığı, birime en son yazan yazılımın adı ve sürümü (ör.
`apfs_kext (2313.1)`), kaba dosya sayısı aralığı; yazma oturumlarında kaydetme, bayt ve işlem sayılarının kaba aralıkları ve
oturumun nasıl bittiği; çökmelerde hata türü ve programın kendi işlevlerinden hangisinde olduğu. İzin verilen alanların tam
listesi [`worker/schema.json`](schema.json); program da sunucu da bu listede olmayan alan taşıyan raporu reddeder.

**Asla gönderilmez:** dosya, klasör, birim ya da disk adları, dosya içerikleri, seri numaraları, UUID ya da başka kimlikler, tam
boyutlar, parolalar, anahtarlar ya da parola ipuçları, bilgisayar adı, kullanıcı adı, dosya yolları, hata iletileri ya da kalıcı
bir cihaz kimliği.

**Nereye gider.** Program raporları HTTPS ile geliştiricinin işlettiği bir Cloudflare Worker'a (`hashido-api.zorba.dev`)
gönderir; raporlar bir Cloudflare D1 veritabanında (Batı Avrupa) saklanır. IP adresiniz saklanmaz ve sunucu istek günlüğü
tutmaz; Cloudflare, isteği iletmek için IP adresini yalnız kısa süre işler (Cloudflare'in gizlilik politikası). Raporlar
satılmaz, reklam için kullanılmaz ve kimseyle paylaşılmaz.

**Ne kadar süre.** Raporlar alındıktan 18 ay sonra kendiliğinden silinir.

**Bilgisayarınızda.** Gönderilmeyi bekleyen raporlar `%ProgramData%\APFS for Windows\reports\` klasöründe (en çok 200), tepsi
programının kendi çökme raporları ise `%LOCALAPPDATA%\APFS for Windows\reports\` klasöründe durur. Gönderilen son 20 rapor, gönderildiği biçimiyle **Ayarlar › Gönderilenleri göster**'de görünür.

**Haklarınız.** Raporlar hiçbir kimlik taşımadığı için hiç kimse (geliştirici dahil) hangi raporun sizden geldiğini bilemez; bu
yüzden tek tek raporlar istek üzerine bulunamaz ya da silinemez. Raporları kapatmak bundan sonraki bütün raporları durdurur.

**Güncelleme denetimi.** Ayarlar'dan kapatmadığınız sürece program günde bir kez GitHub'a (`api.github.com`) yeni sürüm olup
olmadığını sorar. Size ya da disklerinize dair hiçbir şey gönderilmez; GitHub isteği kendi gizlilik açıklamasındaki gibi görür.

**İletişim.** Sorular ve istekler: [GitHub Issues](https://github.com/ErsinZorba/Hashido/issues). Bu açıklamadaki
değişiklikler depo geçmişinde görünür.
