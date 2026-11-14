# Builds dist\BCNMiner.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# Nothing to install: run from PowerShell in this folder:  .\build.ps1   (add -Release for the portable zip)
param([switch]$Release, [string]$OutDir)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x compiler not found: $csc" }

$refs = @('System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Xml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll',
          'WPF\PresentationFramework.dll', 'System.Management.dll', 'System.Web.Extensions.dll',
          'System.Windows.Forms.dll', 'System.Drawing.dll') | ForEach-Object { '/r:' + (Join-Path $fw $_) }

$obj = Join-Path $root 'obj'
$dist = if ($OutDir) { $OutDir } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $obj, $dist, (Join-Path $root 'assets') | Out-Null

# 1. icon from the vector logo
$ico = Join-Path $root 'assets\app.ico'
& $csc /nologo /target:exe /platform:anycpu /out:"$obj\MakeIcon.exe" $refs (Join-Path $root 'tools\MakeIcon.cs')
if ($LASTEXITCODE -ne 0) { throw 'MakeIcon build failed' }
& "$obj\MakeIcon.exe" $ico (Join-Path $root 'assets\icon-256.png')
if ($LASTEXITCODE -ne 0) { throw 'icon render failed' }

# 2. the application
$src = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$langs = Get-ChildItem (Join-Path $root 'src\lang') -Filter *.txt | ForEach-Object { "/resource:$($_.FullName),BcnMiner.lang.$($_.Name)" }
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 `
    /out:"$dist\BCNMiner.exe" /win32icon:"$ico" /win32manifest:"$root\src\app.manifest" `
    /resource:"$root\src\Styles.xaml,BcnMiner.Styles.xaml" $langs $refs $src
if ($LASTEXITCODE -ne 0) { throw 'BCNMiner build failed' }

# 3. side files
foreach ($f in @('get-engine.ps1', 'LICENSE', 'COPYING') + (Get-ChildItem $root -Filter 'README*.md' | ForEach-Object Name)) { Copy-Item (Join-Path $root $f) $dist -Force }
New-Item -ItemType Directory -Force (Join-Path $dist 'engine') | Out-Null
Write-Host "OK: $dist\BCNMiner.exe"

# 4. portable release zip: program + scripts + docs, an empty engine\ (filled by get-engine.ps1)
#    and data\ (its presence switches on portable mode). No XMRig binaries are shipped.
if ($Release) {
    $version = (Get-Item "$dist\BCNMiner.exe").VersionInfo.ProductVersion
    $stage = Join-Path $obj 'release\BCNMiner'
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force $stage, "$stage\engine", "$stage\data" | Out-Null
    foreach ($f in @('BCNMiner.exe', 'get-engine.ps1', 'LICENSE', 'COPYING') + (Get-ChildItem $dist -Filter 'README*.md' | ForEach-Object Name)) { Copy-Item (Join-Path $dist $f) $stage }
    Set-Content "$stage\engine\README.txt" -Encoding UTF8 -Value @(
        'Run get-engine.ps1 from the program folder to download XMRig (and xmrig-cuda for NVIDIA) into this folder.',
        'Запустіть get-engine.ps1 з папки програми, щоб завантажити сюди XMRig (і xmrig-cuda для NVIDIA).')
    Set-Content "$stage\data\README.txt" -Encoding UTF8 -Value @(
        'Portable mode: while this folder exists, BCN Miner keeps its settings and logs here. Delete it to use %APPDATA%\BCN Miner.',
        'Портативний режим: поки ця папка існує, BCN Miner зберігає тут налаштування й журнали. Видаліть її, щоб використовувати %APPDATA%\BCN Miner.')
    $rel = Join-Path $root 'release'
    New-Item -ItemType Directory -Force $rel | Out-Null
    $zip = Join-Path $rel "BCNMiner-$version-win-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $stage -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content "$zip.sha256" -Encoding Ascii -Value "$hash  $(Split-Path $zip -Leaf)"
    Write-Host "OK: $zip"
    Write-Host "SHA-256: $hash"
}
