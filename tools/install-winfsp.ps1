<#
  Geliştirme makinesine WinFsp 2.1.25156'yı kurar (yönetici PowerShell'inde çalıştırın).
  winfsp.net 2.1.25156 paketi kurulu sürücüyle eşleşmeli; 2.2 paketi 2.1'i reddeder.
  Paket: installer\redist\winfsp-2.1.25156.msi (yoksa resmî GitHub sürümünden indirir).
  Kurmadan önce SHA-256'yı (CI ile aynı) ve Authenticode imzasını (NAVIMATICS LLC) denetler.
  Rapor: fixtures\winfsp-install.txt, msiexec günlüğü: fixtures\winfsp-msiexec.log
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$url = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
$sha = '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A'
$msi = Join-Path $root 'installer\redist\winfsp-2.1.25156.msi'
$fix = Join-Path $root 'fixtures'
New-Item -ItemType Directory -Force $fix | Out-Null
$report = Join-Path $fix 'winfsp-install.txt'
$log = Join-Path $fix 'winfsp-msiexec.log'

# Yönetici denetimi rapordan önce: yönetici olmayan bir deneme önceki raporu ezmesin.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Host 'SONUÇ: HATA - Bu betik yönetici PowerShell''inde çalıştırılmalı.'
    exit 1
}

$ok = $false
Start-Transcript -Path $report -Force | Out-Null
try {
    if (-not (Test-Path $msi)) {
        New-Item -ItemType Directory -Force (Split-Path $msi) | Out-Null
        Write-Host "İndiriliyor: $url"
        Invoke-WebRequest -Uri $url -OutFile $msi -UseBasicParsing
    }

    $hash = (Get-FileHash $msi -Algorithm SHA256).Hash
    if ($hash -ne $sha) { throw "SHA-256 tutmuyor: $hash" }
    $sig = Get-AuthenticodeSignature $msi
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'CN=NAVIMATICS LLC') {
        throw "İmza geçersiz: $($sig.Status) $($sig.SignerCertificate.Subject)"
    }
    Write-Host "Paket doğrulandı (SHA-256 ve NAVIMATICS LLC imzası)."

    $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) { throw "msiexec çıkış kodu $($p.ExitCode) (günlük: $log)" }
    if ($p.ExitCode -eq 3010) { Write-Host 'Kurulum tamam; yeniden başlatma öneriliyor.' }

    $dir = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\WinFsp').InstallDir
    $dll = Join-Path $dir 'bin\winfsp-x64.dll'
    Write-Host "WinFsp: $dir"
    Write-Host "winfsp-x64.dll: $((Get-Item $dll).VersionInfo.FileVersion)"
    Get-Service WinFsp.Launcher | Format-Table -AutoSize Name, Status | Out-String | Write-Host
    Write-Host 'SONUÇ: BAŞARILI'
    $ok = $true
}
catch {
    Write-Host "SONUÇ: HATA - $($_.Exception.Message)"
}
finally {
    Stop-Transcript | Out-Null
    # Yükseltilmiş pencere sonucu okumadan kapanmasın; etkileşimsiz kipte Read-Host hata verir, yok sayılır.
    try { Read-Host 'Kapatmak için Enter' | Out-Null } catch { }
}
if (-not $ok) { exit 1 }
