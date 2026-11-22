<#
.SYNOPSIS
  Kurulabilir sürümü derler: publish\APFS for Windows\ (apfs.exe, apfs-service.exe, apfs-tray.exe ve kütüphaneler).
  .NET çalışma zamanı programla birlikte gelir (self-contained): kullanıcının ayrıca .NET kurması gerekmez. Üç program aynı
  klasöre derlenir, çalışma zamanı dosyaları tek kopya kalır.
  -Runtime win-arm64: ARM Windows için (ör. Apple işlemcili Mac'te VMware Fusion) → publish\APFS for Windows (arm64)\.
  Kurulum sihirbazı (build-installer.ps1) şimdilik yalnız varsayılan win-x64 klasörünü paketler.
#>
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root $(if ($Runtime -eq 'win-x64') { 'publish\APFS for Windows' } else { 'publish\APFS for Windows (arm64)' })
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
# Sıra önemli: tepsi (WPF, wpfPlan 3.1) EN SON derlenir; ortak bir dosya (ör. çalışma zamanı DLL'i) iki projede farklıysa
# tepsininki kalır. Her projeden sonra boyut ve üzerine FARKLI içerikle yazılan dosyalar raporlanır (bilinmeyen çakışma görünsün).
function Get-Hashes { $h = @{}; if (Test-Path $out) { Get-ChildItem $out -Recurse -File | ForEach-Object { $h[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }; $h }
foreach ($p in 'src\Apfs.Cli', 'src\Apfs.Service', 'src\Apfs.Tray') {
    $before = Get-Hashes
    & dotnet publish (Join-Path $root $p) -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o $out --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "derleme başarısız: $p" }
    $after = Get-Hashes
    $changed = @($before.Keys | Where-Object { $after[$_] -ne $before[$_] } | ForEach-Object { $_.Substring($out.Length + 1) })
    $added = ($after.Keys | Where-Object { -not $before.ContainsKey($_) } | ForEach-Object { (Get-Item $_).Length } | Measure-Object -Sum).Sum
    "$(Split-Path -Leaf $p): +$([math]::Round($added / 1MB, 1)) MB yeni dosya; içeriği değişen ortak dosya: $($changed.Count)"
    $changed | Sort-Object | ForEach-Object { "    değişti: $_" }
}
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'README.tr.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY_NOTICES.md') $out
# Sihirbazsız kurulum betikleri yalnız gizli depoda (geliştirme); açık depoda yoklar, setup'a da girmezler
if ($Runtime -eq 'win-x64') {
    foreach ($s in 'install.ps1', 'uninstall.ps1') {
        if (Test-Path (Join-Path $root $s)) { Copy-Item (Join-Path $root $s) (Split-Path -Parent $out) }
    }
}
$size = (Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum
"Hazır: $out ($([math]::Round($size / 1MB, 1)) MB)"
