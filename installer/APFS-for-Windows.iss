; Hashido (eski adıyla APFS for Windows) kurulum sihirbazı (Inno Setup 6). Derlemek için: tools\build-installer.ps1
; Kurduğu her şey install.ps1 ile aynıdır: program dosyaları, LocalSystem hizmeti, oturum açılışında tepsi, WinFsp Launcher kaydı.
; WinFsp yoksa (veya 2.1'den eskiyse) içindeki resmi WinFsp kurulum paketini sessizce kurar.

#ifndef AppVersion
  #define AppVersion "1.5.0"
#endif
; Görünen ad: src/Apfs.Windows/Branding.cs ProductName ile aynı olmalı (BrandingTests). Metinler adı {#AppName} ile alır.
#define AppName "Hashido"
; Teknik kimlikler: ad değişse de DEĞİŞMEZ (yükseltmede eski değerler bulunsun; hizmetin günlük klasörü sabit).
#define ServiceName "APFSforWindows"
#define DataDirName "APFS for Windows"
#define RunValueName "APFS for Windows"
#define SrcDir "..\publish\APFS for Windows"
#define WinFspMsi "winfsp-2.1.25156.msi"

[Setup]
AppId={{8C1B6E2F-4A57-4F0E-9D3B-6A2E5C7F1A90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Ersin Zorba
AppPublisherURL=https://github.com/ErsinZorba/Hashido
AppSupportURL=https://github.com/ErsinZorba/Hashido/issues
VersionInfoVersion={#AppVersion}
; Yeni kurulum Program Files\Hashido; 1.3.0 ve öncesinden yükseltmede Inno önceki klasörü kullanır (UsePreviousAppDir,
; AppId aynı) → "APFS for Windows"ta kalır, taşınmaz. Hizmet ve "apfs register" yolu {app}'tan/kendi klasöründen alır.
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
; GPL kullanım için kabul şartı koymaz (madde 9): "kabul ediyorum" sayfası yerine kısa bilgi sayfası (dile göre, [Languages]).
; GPL'in tam metni program klasöründeki LICENSE dosyasıdır (build-release.ps1 kopyalar).
OutputDir=..\publish
OutputBaseFilename=Hashido-Setup-{#AppVersion}
SetupIconFile=..\src\Apfs.Tray\app.ico
UninstallDisplayIcon={app}\apfs-tray.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Tepsiyi ve hizmeti PrepareToInstall kendisi durdurur.
CloseApplications=no
; Kaldırma, kaldıran kullanıcının rapor onayını (HKCU) ve tepsinin rapor kuyruğunu ({localappdata}) siler. Yönetici kurulumunda
; bunlar UAC'yi onaylayan hesabındır (tek kullanıcılı makinede aynı hesap; başka profiller temizlenmez — bilinen sınır).
UsedUserAreasWarning=no

; İlk dil, Windows dili listede yoksa kullanılır: İngilizce. Inno Setup'ın resmî Çince çevirisi yok → Çince Windows'ta İngilizce sihirbaz.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "lisans-bilgisi.en.txt"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"; InfoBeforeFile: "lisans-bilgisi.tr.txt"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"; InfoBeforeFile: "lisans-bilgisi.de.txt"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"; InfoBeforeFile: "lisans-bilgisi.fr.txt"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"; InfoBeforeFile: "lisans-bilgisi.es.txt"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"; InfoBeforeFile: "lisans-bilgisi.pt-BR.txt"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"; InfoBeforeFile: "lisans-bilgisi.it.txt"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"; InfoBeforeFile: "lisans-bilgisi.ja.txt"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"; InfoBeforeFile: "lisans-bilgisi.ko.txt"

[CustomMessages]
; Türkçede ada ek getirilmez ("…'u aç" ad değişince yanlış ek olur): "… uygulamasını aç".
en.LaunchApp=Open {#AppName}
tr.LaunchApp={#AppName} uygulamasını aç
de.LaunchApp={#AppName} öffnen
fr.LaunchApp=Ouvrir {#AppName}
es.LaunchApp=Abrir {#AppName}
ptbr.LaunchApp=Abrir o {#AppName}
it.LaunchApp=Apri {#AppName}
ja.LaunchApp={#AppName} を開く
ko.LaunchApp={#AppName} 열기
en.InstallingWinFsp=Installing the WinFsp file system driver...
tr.InstallingWinFsp=WinFsp dosya sistemi sürücüsü kuruluyor...
de.InstallingWinFsp=Der Dateisystemtreiber WinFsp wird installiert...
fr.InstallingWinFsp=Installation du pilote de système de fichiers WinFsp...
es.InstallingWinFsp=Instalando el controlador de sistema de archivos WinFsp...
ptbr.InstallingWinFsp=Instalando o driver de sistema de arquivos WinFsp...
it.InstallingWinFsp=Installazione del driver del file system WinFsp...
ja.InstallingWinFsp=ファイル システム ドライバー WinFsp をインストールしています...
ko.InstallingWinFsp=파일 시스템 드라이버 WinFsp를 설치하는 중...
en.InstallingService=Installing the background service...
tr.InstallingService=Arka plan hizmeti kuruluyor...
de.InstallingService=Der Hintergrunddienst wird installiert...
fr.InstallingService=Installation du service d'arrière-plan...
es.InstallingService=Instalando el servicio en segundo plano...
ptbr.InstallingService=Instalando o serviço em segundo plano...
it.InstallingService=Installazione del servizio in background...
ja.InstallingService=バックグラウンド サービスをインストールしています...
ko.InstallingService=백그라운드 서비스를 설치하는 중...
en.WinFspFailed=WinFsp could not be installed (code %1). Mac disks cannot be mounted. Install WinFsp from https://winfsp.dev and run this setup again.
tr.WinFspFailed=WinFsp kurulamadı (kod %1). Mac diskleri bağlanamaz. WinFsp'i https://winfsp.dev adresinden kurup programı yeniden kurun.
de.WinFspFailed=WinFsp konnte nicht installiert werden (Code %1). Mac-Datenträger können nicht eingebunden werden. Installieren Sie WinFsp von https://winfsp.dev und führen Sie dieses Setup erneut aus.
fr.WinFspFailed=Impossible d'installer WinFsp (code %1). Les disques Mac ne peuvent pas être montés. Installez WinFsp depuis https://winfsp.dev, puis relancez cette installation.
es.WinFspFailed=No se pudo instalar WinFsp (código %1). No se pueden montar los discos de Mac. Instale WinFsp desde https://winfsp.dev y vuelva a ejecutar este instalador.
ptbr.WinFspFailed=Não foi possível instalar o WinFsp (código %1). Os discos do Mac não podem ser montados. Instale o WinFsp em https://winfsp.dev e execute esta instalação novamente.
it.WinFspFailed=Impossibile installare WinFsp (codice %1). I dischi Mac non possono essere montati. Installa WinFsp da https://winfsp.dev ed esegui di nuovo questa installazione.
ja.WinFspFailed=WinFsp をインストールできませんでした (コード %1)。Mac のディスクをマウントできません。https://winfsp.dev から WinFsp をインストールしてから、このセットアップをもう一度実行してください。
ko.WinFspFailed=WinFsp를 설치하지 못했습니다(코드 %1). Mac 디스크를 마운트할 수 없습니다. https://winfsp.dev 에서 WinFsp를 설치한 후 이 설치 프로그램을 다시 실행하세요.
en.ServiceFailed=The background service could not be started (code %1). Details: C:\ProgramData\{#DataDirName}\log.txt
tr.ServiceFailed=Arka plan hizmeti başlatılamadı (kod %1). Ayrıntı: C:\ProgramData\{#DataDirName}\log.txt
de.ServiceFailed=Der Hintergrunddienst konnte nicht gestartet werden (Code %1). Details: C:\ProgramData\{#DataDirName}\log.txt
fr.ServiceFailed=Impossible de démarrer le service d'arrière-plan (code %1). Détails : C:\ProgramData\{#DataDirName}\log.txt
es.ServiceFailed=No se pudo iniciar el servicio en segundo plano (código %1). Detalles: C:\ProgramData\{#DataDirName}\log.txt
ptbr.ServiceFailed=Não foi possível iniciar o serviço em segundo plano (código %1). Detalhes: C:\ProgramData\{#DataDirName}\log.txt
it.ServiceFailed=Impossibile avviare il servizio in background (codice %1). Dettagli: C:\ProgramData\{#DataDirName}\log.txt
ja.ServiceFailed=バックグラウンド サービスを開始できませんでした (コード %1)。詳細: C:\ProgramData\{#DataDirName}\log.txt
ko.ServiceFailed=백그라운드 서비스를 시작하지 못했습니다(코드 %1). 자세한 내용: C:\ProgramData\{#DataDirName}\log.txt
en.ServiceDescription=Mounts attached Mac (APFS) disks as drives in Windows (read-only by default; optional writing).
tr.ServiceDescription=Takılan Mac (APFS) disklerini Windows'ta sürücü olarak bağlar (önce salt okunur; isteğe bağlı yazma).
de.ServiceDescription=Bindet angeschlossene Mac-Datenträger (APFS) als Laufwerke in Windows ein (standardmäßig schreibgeschützt; Schreiben optional).
fr.ServiceDescription=Monte les disques Mac (APFS) connectés comme lecteurs dans Windows (en lecture seule par défaut ; écriture facultative).
es.ServiceDescription=Monta los discos de Mac (APFS) conectados como unidades en Windows (de solo lectura por defecto; escritura opcional).
ptbr.ServiceDescription=Monta os discos do Mac (APFS) conectados como unidades no Windows (somente leitura por padrão; gravação opcional).
it.ServiceDescription=Monta i dischi Mac (APFS) collegati come unità in Windows (in sola lettura per impostazione predefinita; scrittura facoltativa).
ja.ServiceDescription=接続された Mac (APFS) ディスクを Windows のドライブとしてマウントします (既定では読み取り専用、書き込みは任意)。
ko.ServiceDescription=연결된 Mac(APFS) 디스크를 Windows 드라이브로 마운트합니다(기본값은 읽기 전용, 쓰기는 선택 사항).

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\{#WinFspMsi}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWinFsp

[InstallDelete]
; 1.3.0 ve öncesinin Başlat menüsü kısayolu (ad değişti; yükseltmede eskisi kalmasın, yenisi [Icons]'ta).
Type: files; Name: "{autoprograms}\APFS for Windows.lnk"

[UninstallDelete]
; Hizmetin rapor kuyruğu ve rapor ayarı (config.json): kaldırılıp yeniden kurulunca hizmet eski "açık" ayarıyla başlamasın.
; Günlük (log.txt) incelemek için kalır.
Type: filesandordirs; Name: "{commonappdata}\{#DataDirName}\reports"
; Tepsinin gönderilmemiş raporları ve gönderilenler listesi (sent.jsonl). Aynı klasördeki tepsi günlüğü kalır.
Type: filesandordirs; Name: "{localappdata}\{#DataDirName}\reports"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\apfs-tray.exe"; Parameters: "--show"; Comment: "{cm:ServiceDescription}"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#RunValueName}"; \
  ValueData: """{app}\apfs-tray.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\apfs-tray.exe"; Parameters: "--show"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM apfs-tray.exe"; Flags: runhidden; RunOnceId: "KillTray"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{app}\apfs.exe"; Parameters: "unregister"; Flags: runhidden; RunOnceId: "Unregister"
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKLM\SOFTWARE\WOW6432Node\WinFsp\Services\apfs"" /f"; Flags: runhidden; RunOnceId: "DeleteLauncher"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM apfs.exe"; Flags: runhidden; RunOnceId: "KillCli"

[Code]
var
  NeedWinFsp: Boolean;
  RestartNeeded: Boolean;

{ WinFsp yoksa veya bin\winfsp-x64.dll sürümü 2.1'den eskiyse true. (Kayıt defterinde sürüm değeri yok, yalnızca InstallDir.) }
function DetectNeedsWinFsp: Boolean;
var
  Dir: String;
  MS, LS: Cardinal;
begin
  Result := True;
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', Dir) then Exit;
  if not GetVersionNumbers(AddBackslash(Dir) + 'bin\winfsp-x64.dll', MS, LS) then Exit;
  Result := MS < $00020001;
end;

function InitializeSetup: Boolean;
begin
  NeedWinFsp := DetectNeedsWinFsp;
  Result := True;
end;

function NeedsWinFsp: Boolean;
begin
  Result := NeedWinFsp;
end;

function Run(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then Result := -1;
end;

{ Eski sürüm (veya install.ps1 kurulumu) çalışıyorsa dosyalar kilitli olmasın: tepsiyi kapat, hizmeti durdur (net stop bekler). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM apfs-tray.exe');
  Run(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}');
  Run(ExpandConstant('{sys}\taskkill.exe'), '/F /IM apfs.exe');
  Result := '';
end;

procedure InstallService;
var
  Sc, Bin: String;
  Code: Integer;
begin
  Sc := ExpandConstant('{sys}\sc.exe');
  Bin := '"\"' + ExpandConstant('{app}') + '\apfs-service.exe\""';
  if Run(Sc, 'query {#ServiceName}') = 0 then
    { Yükseltmede görünen ad da güncellenir (hizmet adı teknik kimlik, değişmez) }
    Run(Sc, 'config {#ServiceName} binPath= ' + Bin + ' start= auto DisplayName= "{#AppName}"')
  else
    Run(Sc, 'create {#ServiceName} binPath= ' + Bin + ' start= auto DisplayName= "{#AppName}"');
  Run(Sc, 'description {#ServiceName} "' + CustomMessage('ServiceDescription') + '"');
  { Beklenmedik çökmede 5 sn sonra yeniden başlat (install.ps1 ile aynı) }
  Run(Sc, 'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000');
  Code := Run(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}');
  if (Code <> 0) and (Code <> 2) then
    SuppressibleMsgBox(FmtMessage(CustomMessage('ServiceFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
  Run(ExpandConstant('{app}\apfs.exe'), 'register');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then Exit;
  if NeedWinFsp then
  begin
    WizardForm.StatusLabel.Caption := CustomMessage('InstallingWinFsp');
    Code := Run(ExpandConstant('{sys}\msiexec.exe'), '/i "' + ExpandConstant('{tmp}\{#WinFspMsi}') + '" /qn /norestart');
    if Code = 3010 then RestartNeeded := True
    else if Code <> 0 then
      SuppressibleMsgBox(FmtMessage(CustomMessage('WinFspFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingService');
  InstallService;
end;

function NeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;

// Rapor onayı (UserSettings.ReportsConsent): kalırsa yeniden kurulumda eski "evet" sorulmadan hizmete iletilirdi. Dil ve
// güncelleme bildirimi ayarları kalır; anahtar ancak boşaldıysa silinir.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  RegDeleteValue(HKCU, 'Software\APFS for Windows', 'Reports');
  RegDeleteKeyIfEmpty(HKCU, 'Software\APFS for Windows');
end;
