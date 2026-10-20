<#
.SYNOPSIS
    Builds the DeviceDeck release zip: web UI, self-contained app, Google's adb, notices.

.DESCRIPTION
    The same steps run locally and in .github/workflows/release.yml:
      1. npm ci + npm run build in web/
      2. dotnet publish src/DeviceDeck.Shell, self-contained, for the runtime asked for
      3. web/dist copied next to the exe as web/ (WebContent looks there first)
      4. Google's platform-tools fetched (tools/fetch-platform-tools.ps1) and copied as platform-tools/
      5. README.txt + THIRD-PARTY-NOTICES.md added, all zipped into artifacts/

.PARAMETER Version
    Version for the file name and the app (e.g. 0.1.0). Default: <Version> from the Shell project.

.PARAMETER Runtime
    win-x64 (default) or win-arm64.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/package.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = Split-Path -Parent $PSScriptRoot
$shell = Join-Path $repo 'src\DeviceDeck.Shell\DeviceDeck.Shell.csproj'
if (-not $Version) {
    $Version = ([xml](Get-Content $shell)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$artifacts = Join-Path $repo 'artifacts'
$stage = Join-Path $artifacts "DeviceDeck-$Version-$Runtime"
$zip = "$stage.zip"

function Step([string]$text) { Write-Host "==> $text" }
function Invoke-Native([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force $stage | Out-Null

Step 'Web UI: npm ci + build'
Push-Location (Join-Path $repo 'web')
try {
    Invoke-Native 'npm ci' { npm ci --no-audit --no-fund }
    Invoke-Native 'npm run build' { npm run build }
} finally { Pop-Location }

Step "App: dotnet publish $Runtime, self-contained, version $Version"
Invoke-Native 'dotnet publish' {
    dotnet publish $shell -c Release -r $Runtime --self-contained true "-p:Platform=$platform" "-p:Version=$Version" -o $stage -nologo
}
# DeviceDeck.pri carries the compiled XAML; without it the window fails in InitializeComponent.
foreach ($required in 'DeviceDeck.exe', 'DeviceDeck.pri') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "publish output is missing $required" }
}

Step 'Web UI next to the exe'
Copy-Item -Recurse (Join-Path $repo 'web\dist') (Join-Path $stage 'web')

Step "Google's platform-tools (adb)"
Invoke-Native 'fetch-platform-tools' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-platform-tools.ps1') }
Copy-Item -Recurse (Join-Path $repo 'third_party\platform-tools') (Join-Path $stage 'platform-tools')

Step 'Readme and notices'
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $stage
@"
DeviceDeck $Version ($Runtime)

Run DeviceDeck.exe. No installer: keep this folder together.

iPhone:  needs Apple Mobile Device Support (it comes with iTunes or the Apple Devices app).
         Unlock the phone and tap Trust when it asks.
Android: turn on USB debugging (Settings > Developer options) and tap Allow on the phone.
         adb from Google's platform-tools is in the platform-tools folder; DeviceDeck runs it
         on its own port (5137) so it never disturbs another adb on this PC.

Windows 11 and the Microsoft Edge WebView2 Runtime (built into Windows 11) are required.
Settings and logs: %LocalAppData%\DeviceDeck
"@ | Set-Content -Path (Join-Path $stage 'README.txt') -Encoding utf8

Step "Zip: $zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($mb MB)"
if ($env:GITHUB_OUTPUT) { "zip=$zip" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
