<#
.SYNOPSIS
    Fetches scrcpy-server (Apache-2.0) into third_party/scrcpy for Android screen mirroring.

.DESCRIPTION
    Downloads the scrcpy-server file of the pinned scrcpy release from GitHub, checks its SHA-256 against the
    value published with that release, and installs it as third_party/scrcpy/scrcpy-server with scrcpy's
    LICENSE next to it. Idempotent: a file that already has the right hash is left alone, unless -Force.

    Only the server is used: DeviceDeck pushes it to the phone (/data/local/tmp) and speaks its protocol itself.
    The client and server versions must match exactly, so this version is pinned together with
    src/DeviceDeck.Android/Mirror/ScrcpyProtocol.cs (ServerVersion). Never committed: third_party/ is git-ignored.

.PARAMETER Force
    Download again even when the file is already there.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/fetch-scrcpy-server.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$version = '5.0.1'
$sha256 = '764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536'
$serverUrl = "https://github.com/Genymobile/scrcpy/releases/download/v$version/scrcpy-server-v$version"
$licenseUrl = "https://raw.githubusercontent.com/Genymobile/scrcpy/v$version/LICENSE"

$repoRoot = Split-Path -Parent $PSScriptRoot
$target = Join-Path (Join-Path $repoRoot 'third_party') 'scrcpy'
$server = Join-Path $target 'scrcpy-server'

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -Path $path).Hash.ToLowerInvariant() }

if ((Test-Path $server) -and -not $Force -and (Get-Sha256 $server) -eq $sha256) {
    Write-Host "scrcpy-server $version is already in $target (use -Force to download again)"
    exit 0
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$work = Join-Path ([IO.Path]::GetTempPath()) ("devicedeck-scrcpy-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $download = Join-Path $work 'scrcpy-server'
    Write-Host "Downloading $serverUrl"
    Invoke-WebRequest -Uri $serverUrl -OutFile $download -UseBasicParsing
    $actual = Get-Sha256 $download
    if ($actual -ne $sha256) {
        throw "scrcpy-server $version has SHA-256 $actual, expected $sha256; not installing it."
    }
    Write-Host "SHA-256 matches the scrcpy $version release"

    $license = Join-Path $work 'LICENSE'
    Invoke-WebRequest -Uri $licenseUrl -OutFile $license -UseBasicParsing
    if (-not (Select-String -Path $license -Pattern 'Apache License' -Quiet)) {
        throw "The downloaded LICENSE is not scrcpy's Apache License."
    }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -Force $download $server
    Copy-Item -Force $license (Join-Path $target 'LICENSE')
    Set-Content -Path (Join-Path $target 'VERSION') -Value $version -Encoding ascii
    $kb = [Math]::Round((Get-Item $server).Length / 1KB)
    Write-Host "Installed scrcpy-server $version ($kb KB) into $target"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
