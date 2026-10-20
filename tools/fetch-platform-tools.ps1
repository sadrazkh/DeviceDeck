<#
.SYNOPSIS
    Fetches Google's Android SDK Platform-Tools (adb) into third_party/platform-tools.

.DESCRIPTION
    Downloads the official platform-tools-latest-windows.zip from dl.google.com, checks that
    adb.exe is there and carries a valid Authenticode signature, and installs it into the
    git-ignored third_party/platform-tools folder at the repo root. Idempotent: when adb is
    already there it only prints the version, unless -Force is given.

    The binaries are never committed. DeviceDeck runs its own adb server on port 5137
    (ANDROID_ADB_SERVER_PORT), so this copy never fights another adb on the PC.

.PARAMETER Force
    Download again even when third_party/platform-tools/adb.exe already exists.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/fetch-platform-tools.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
# Invoke-WebRequest in Windows PowerShell 5.1 is very slow while it draws progress.
$ProgressPreference = 'SilentlyContinue'

$url = 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip'
$serverPort = 5137

$repoRoot = Split-Path -Parent $PSScriptRoot
$thirdParty = Join-Path $repoRoot 'third_party'
$target = Join-Path $thirdParty 'platform-tools'
$adb = Join-Path $target 'adb.exe'

function Get-PlatformToolsVersion([string]$dir) {
    $props = Join-Path $dir 'source.properties'
    if (-not (Test-Path $props)) { return $null }
    foreach ($line in Get-Content $props) {
        if ($line -match '^\s*Pkg\.Revision\s*=\s*(.+)$') { return $Matches[1].Trim() }
    }
    return $null
}

function Write-AdbVersion([string]$adbPath) {
    # 'adb version' does not start a server.
    $lines = & $adbPath version 2>&1
    foreach ($line in $lines) { Write-Host "  $line" }
}

if ((Test-Path $adb) -and -not $Force) {
    $version = Get-PlatformToolsVersion $target
    Write-Host "platform-tools $version is already in $target (use -Force to download again)"
    Write-AdbVersion $adb
    exit 0
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$work = Join-Path ([IO.Path]::GetTempPath()) ("devicedeck-platform-tools-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work 'platform-tools.zip'
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $sizeMb = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "Downloaded $sizeMb MB"

    $extract = Join-Path $work 'extract'
    Expand-Archive -Path $zip -DestinationPath $extract
    $fresh = Join-Path $extract 'platform-tools'
    $freshAdb = Join-Path $fresh 'adb.exe'
    if (-not (Test-Path $freshAdb)) {
        throw "The archive has no platform-tools/adb.exe; Google may have changed its layout."
    }

    $signature = Get-AuthenticodeSignature $freshAdb
    if ($signature.Status -ne 'Valid') {
        throw "adb.exe in the download is not validly signed ($($signature.Status)); not installing it."
    }
    Write-Host "Signed by: $($signature.SignerCertificate.Subject)"

    # Our own server may hold adb.exe open; stop it before replacing the files.
    if (Test-Path $adb) {
        & $adb -P $serverPort kill-server 2>&1 | Out-Null
        Remove-Item -Recurse -Force $target
    }
    New-Item -ItemType Directory -Force -Path $thirdParty | Out-Null
    Move-Item -Path $fresh -Destination $target

    $version = Get-PlatformToolsVersion $target
    Write-Host "Installed platform-tools $version into $target"
    Write-AdbVersion $adb
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
