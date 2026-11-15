# Downloads the mining engine for BCN Miner into .\engine:
#   - XMRig (latest official release, Windows x64) — CPU, AMD/Intel OpenCL
#   - xmrig-cuda plugin (only if an NVIDIA GPU is present, or with -Cuda) — the newest CUDA build in the release
# Every archive is checked against the SHA-256 that GitHub publishes for it (asset digest or SHA256SUMS).
#
#   powershell -ExecutionPolicy Bypass -File .\get-engine.ps1            # auto
#   powershell -ExecutionPolicy Bypass -File .\get-engine.ps1 -Cuda      # force the CUDA plugin
#   powershell -ExecutionPolicy Bypass -File .\get-engine.ps1 -NoCuda    # skip it
param([switch]$Cuda, [switch]$NoCuda)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$dest = Join-Path $PSScriptRoot 'engine'
$tmp = Join-Path $env:TEMP ('bcn-miner-engine-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $dest, $tmp | Out-Null
$headers = @{ 'User-Agent' = 'bcn-miner-setup'; 'Accept' = 'application/vnd.github+json' }

function Get-Release([string]$repo) {
    Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
}

function Get-Verified($release, $asset) {
    $file = Join-Path $tmp $asset.name
    Write-Host "  downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -Headers @{ 'User-Agent' = 'bcn-miner-setup' } -UseBasicParsing
    $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = $null
    if ($asset.PSObject.Properties.Name -contains 'digest' -and $asset.digest -match '^sha256:([0-9a-f]{64})$') { $expected = $Matches[1] }
    if (-not $expected) {
        $sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' } | Select-Object -First 1
        if ($sums) {
            $text = (Invoke-WebRequest -Uri $sums.browser_download_url -Headers @{ 'User-Agent' = 'bcn-miner-setup' } -UseBasicParsing).Content
            if ($text -is [byte[]]) { $text = [Text.Encoding]::UTF8.GetString($text) }
            foreach ($line in ($text -split "`n")) {
                if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+?)\s*$' -and $Matches[2] -eq $asset.name) { $expected = $Matches[1].ToLowerInvariant() }
            }
        }
    }
    if (-not $expected) { throw "GitHub publishes no SHA-256 for $($asset.name); not installing an unverified file." }
    if ($actual -ne $expected) { throw "SHA-256 mismatch for $($asset.name): expected $expected, got $actual" }
    Write-Host "  SHA-256 OK  $actual"
    return $file
}

function Install-Zip([string]$zip, [string[]]$patterns) {
    $x = Join-Path $tmp ([IO.Path]::GetFileNameWithoutExtension($zip))
    Expand-Archive -Path $zip -DestinationPath $x -Force
    $n = 0
    foreach ($p in $patterns) {
        Get-ChildItem $x -Recurse -File -Filter $p | ForEach-Object { Copy-Item $_.FullName (Join-Path $dest $_.Name) -Force; $n++ }
    }
    if ($n -eq 0) { throw "nothing matching $($patterns -join ', ') in $zip" }
}

try {
    Write-Host 'XMRig:'
    $r = Get-Release 'xmrig/xmrig'
    # asset names: 6.26+ "windows-x64.zip" / "windows-gcc-x64.zip" / "windows-arm64.zip"; older "msvc-win64.zip" / "gcc-win64.zip"
    $arm = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64'
    $patterns = if ($arm) { @('-windows-arm64\.zip$') } else { @('-windows-x64\.zip$', '-msvc-win64\.zip$', '-windows-gcc-x64\.zip$', '-gcc-win64\.zip$') }
    $asset = $null
    foreach ($pat in $patterns) {
        if (-not $asset) { $asset = $r.assets | Where-Object { $_.name -match $pat } | Select-Object -First 1 }
    }
    if (-not $asset) { throw "no Windows $(if ($arm) { 'ARM64' } else { 'x64' }) build in xmrig $($r.tag_name)" }
    Install-Zip (Get-Verified $r $asset) @('xmrig.exe', 'WinRing0x64.sys', 'LICENSE*')
    Write-Host "  installed xmrig $($r.tag_name)"

    $nv = @(Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -match 'VEN_10DE' })
    $hasNvidia = $nv.Count -gt 0
    if (($hasNvidia -or $Cuda) -and -not $NoCuda) {
        Write-Host 'xmrig-cuda (NVIDIA):'
        # lowest NVIDIA driver on this PC: Windows 27.21.14.5167 -> NVIDIA 451.67
        $driver = 0.0
        foreach ($g in $nv) {
            $p = "$($g.DriverVersion)".Split('.')
            if ($p.Count -ge 4) {
                $d = [int]($p[2].Substring($p[2].Length - 1) + $p[3].PadLeft(4, '0')) / 100.0
                if ($driver -eq 0 -or $d -lt $driver) { $driver = $d }
            }
        }
        function Get-DriverForCuda([version]$v) {
            if ($v.Major -ge 13) { 580.0 } elseif ($v.Major -eq 12) { 527.41 } elseif ($v.Major -eq 11) { 452.39 }
            elseif ($v.Major -eq 10 -and $v.Minor -ge 2) { 441.22 } else { 411.31 }
        }
        $c = Get-Release 'xmrig/xmrig-cuda'
        # the newest CUDA build the driver can run: Blackwell (RTX 50) needs 12.8+, old drivers need 10.2 / 11.x
        $best = $null; $bestVer = [version]'0.0'
        foreach ($a in $c.assets) {
            if ($a.name -match 'cuda(\d+)_(\d+)-win64\.zip$') {
                $v = [version]("$($Matches[1]).$($Matches[2])")
                $fits = ($driver -eq 0) -or ($driver -ge (Get-DriverForCuda $v))
                if ($fits -and $v -gt $bestVer) { $best = $a; $bestVer = $v }
            }
        }
        if (-not $best) { throw "no xmrig-cuda $($c.tag_name) build runs on NVIDIA driver $driver (441.22 or newer is needed)" }
        if ($driver -gt 0) { Write-Host "  NVIDIA driver $driver" }
        Get-ChildItem $dest -Filter *.dll -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'xmrig-cuda.dll' -or $_.Name -like 'nvrtc*' } | Remove-Item -Force
        Install-Zip (Get-Verified $c $best) @('xmrig-cuda.dll', '*.dll')
        Write-Host "  installed xmrig-cuda $($c.tag_name), CUDA $bestVer (needs an NVIDIA driver that supports CUDA $bestVer)"
    } else {
        Write-Host 'xmrig-cuda: skipped (no NVIDIA GPU found; use -Cuda to install anyway)'
    }
    Write-Host ''
    Write-Host "Done: $dest"
    Write-Host 'If Windows Defender removes xmrig.exe, add this folder to its exclusions yourself (Windows Security > Virus & threat protection > Exclusions).'
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
