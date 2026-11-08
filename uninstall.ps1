<#
.SYNOPSIS
  Hashido'yu kaldırır (YÖNETİCİ olarak çalıştırın). Bağlı diskler ayrılır; disklerinize hiçbir şey yazılmaz.
  Günlük dosyası (C:\ProgramData\APFS for Windows) incelemeniz için bırakılır. WinFsp kaldırılmaz.
#>
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$serviceName = 'APFSforWindows'
# Kurulum klasörü: hizmetin klasörü (Program Files altındaysa); hizmet yoksa programı içeren "Hashido" ya da eski
# "APFS for Windows" (1.3.0 ve öncesi; yükseltenler orada kalır). Hizmet silinmeden önce okunur.
function Get-InstallDir {
    $img = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -ErrorAction SilentlyContinue).ImagePath
    if ($img) {
        $exe = $img.Trim().Trim('"')
        if ($exe -match '\\apfs-service\.exe$' -and $exe.StartsWith($env:ProgramFiles + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return Split-Path -Parent $exe
        }
    }
    foreach ($name in 'Hashido', 'APFS for Windows') {
        $d = Join-Path $env:ProgramFiles $name
        if (Test-Path (Join-Path $d 'apfs.exe')) { return $d }
    }
    return Join-Path $env:ProgramFiles 'Hashido'
}
$installDir = Get-InstallDir

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Host 'PowerShell''i "Yönetici olarak çalıştır" ile açıp tekrar deneyin.' -ForegroundColor Red; exit 1 }

Get-Process apfs-tray -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'APFS for Windows' -ErrorAction SilentlyContinue  # değer adı teknik kimlik

$svc = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') { Stop-Service $serviceName -Force; $svc.WaitForStatus('Stopped', '00:00:30') }
    & sc.exe delete $serviceName | Out-Null
    Write-Host 'Hizmet kaldırıldı.'
}

if (Test-Path (Join-Path $installDir 'apfs.exe')) { & (Join-Path $installDir 'apfs.exe') unregister | Out-Null }
& reg.exe delete 'HKLM\SOFTWARE\WOW6432Node\WinFsp\Services\apfs' /f 2>$null | Out-Null
Get-Process apfs -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir; Write-Host "Silindi: $installDir" }
# Rapor kuyruğu ve rapor ayarı (sihirbazın [UninstallDelete]'i ile aynı); günlük kalır.
$reports = Join-Path $env:ProgramData 'APFS for Windows\reports'
if (Test-Path $reports) { Remove-Item -Recurse -Force $reports; Write-Host "Silindi: $reports" }

Write-Host 'Hashido kaldırıldı.' -ForegroundColor Green
