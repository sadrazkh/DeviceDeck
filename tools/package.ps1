<#
.SYNOPSIS
    Builds DeviceDeck for release: the portable zip and, with -Installer, the Velopack installer and update packages.

.DESCRIPTION
    The same steps run locally and in .github/workflows/release.yml:
      1. npm ci + npm run build in web/
      2. dotnet publish src/DeviceDeck.Shell, self-contained, for the runtime asked for
      3. web/dist copied next to the exe as web/ (WebContent looks there first)
      4. Google's platform-tools fetched (tools/fetch-platform-tools.ps1) and copied as platform-tools/
      5. README.txt + THIRD-PARTY-NOTICES.md added
      6. With -SignTemplate: DeviceDeck's own .exe and .dll files signed
      7. All of it zipped into artifacts/DeviceDeck-<version>-<runtime>.zip (portable, no updates)
      8. With -Installer: vpk pack (Velopack) into artifacts/velopack/<runtime>/: the Setup.exe (renamed
         DeviceDeck-<version>-<runtime>-Setup.exe), the full (and, when the last release is there, delta)
         .nupkg and releases.<runtime>.json, which the installed app reads from GitHub Releases to update.
         The channel is the runtime, so x64 and ARM64 copies each get their own updates.

.PARAMETER Version
    Version for the file names and the app (e.g. 0.2.0). Default: <Version> from the Shell project.

.PARAMETER Runtime
    win-x64 (default) or win-arm64.

.PARAMETER Installer
    Also build the Velopack installer and update packages (needs vpk: dotnet tool install -g vpk --version 1.2.161).

.PARAMETER SignTemplate
    A signing command with {{file}} where the file goes, e.g.
    "C:\...\signtool.exe" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /s My /sha1 <thumbprint> {{file}}
    (the certificate imported into your store: vpk runs this through cmd, where a password with & % ^ or " breaks).
    Used for DeviceDeck's binaries in the zip and passed to vpk for everything in the installer.
    Empty (the default): nothing is signed.

.PARAMETER Vpk
    The vpk command. Default: vpk on PATH.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/package.ps1 -Version 0.2.0 -Runtime win-x64 -Installer
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Installer,
    [string]$SignTemplate = '',
    [string]$Vpk = 'vpk'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Velopack's install folder is %LocalAppData%\<PackId>; DeviceDeck keeps its data in %LocalAppData%\DeviceDeck,
# which an uninstall must not delete, so the package id is not "DeviceDeck".
$packId = 'DeviceDeck.App'

$repo = Split-Path -Parent $PSScriptRoot
$shell = Join-Path $repo 'src\DeviceDeck.Shell\DeviceDeck.Shell.csproj'
if (-not $Version) {
    $Version = ([xml](Get-Content $shell)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$artifacts = Join-Path $repo 'artifacts'
$stage = Join-Path $artifacts "DeviceDeck-$Version-$Runtime"
$zip = "$stage.zip"
$velopack = Join-Path $artifacts "velopack\$Runtime"

function Step([string]$text) { Write-Host "==> $text" }
function Invoke-Native([string]$what, [scriptblock]$block) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}
function Write-Output-Value([string]$name, [string]$value) {
    if ($env:GITHUB_OUTPUT) { "$name=$value" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
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
foreach ($required in 'DeviceDeck.exe', 'DeviceDeck.pri', 'Assets\DeviceDeck.ico') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "publish output is missing $required" }
}

Step 'Web UI next to the exe'
Copy-Item -Recurse (Join-Path $repo 'web\dist') (Join-Path $stage 'web')

Step "Google's platform-tools (adb)"
Invoke-Native 'fetch-platform-tools' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-platform-tools.ps1') }
Copy-Item -Recurse (Join-Path $repo 'third_party\platform-tools') (Join-Path $stage 'platform-tools')

# Toolbox: Android screen mirroring pushes scrcpy-server (Apache-2.0) to the phone; it looks in scrcpy\ next to the exe.
Step 'scrcpy-server for Android mirroring'
Invoke-Native 'fetch-scrcpy-server' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-scrcpy-server.ps1') }
Copy-Item -Recurse (Join-Path $repo 'third_party\scrcpy') (Join-Path $stage 'scrcpy')

Step 'Readme and notices'
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $stage
@"
DeviceDeck $Version ($Runtime)

Run DeviceDeck.exe. From the portable zip, keep this folder together; that copy doesn't update itself.
The installer (DeviceDeck-$Version-$Runtime-Setup.exe on the GitHub release) installs DeviceDeck for
this Windows user and keeps it up to date from GitHub Releases (Settings > Updates).

iPhone:  needs Apple Mobile Device Support (it comes with iTunes or the Apple Devices app).
         Unlock the phone and tap Trust when it asks.
Android: turn on USB debugging (Settings > Developer options) and tap Allow on the phone.
         adb from Google's platform-tools is in the platform-tools folder; DeviceDeck runs it
         on its own port (5137) so it never disturbs another adb on this PC.

Windows 11 and the Microsoft Edge WebView2 Runtime (built into Windows 11) are required.
Settings and logs: %LocalAppData%\DeviceDeck
"@ | Set-Content -Path (Join-Path $stage 'README.txt') -Encoding utf8

if ($SignTemplate) {
    Step "Sign DeviceDeck's own binaries"
    $own = Get-ChildItem $stage -File | Where-Object { $_.Name -like 'DeviceDeck*.exe' -or $_.Name -like 'DeviceDeck*.dll' }
    foreach ($file in $own) {
        $command = $SignTemplate.Replace('{{file}}', "`"$($file.FullName)`"")
        Invoke-Native "signing $($file.Name)" { cmd /c $command }
    }
    $exe = Get-AuthenticodeSignature (Join-Path $stage 'DeviceDeck.exe')
    if ($exe.Status -ne 'Valid') { throw "DeviceDeck.exe is not validly signed after signing: $($exe.Status) $($exe.StatusMessage)" }
}

Step "Zip: $zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($mb MB)"
Write-Output-Value 'zip' $zip

if ($Installer) {
    Step "Installer: vpk pack $packId $Version ($Runtime)"
    New-Item -ItemType Directory -Force $velopack | Out-Null
    # Earlier releases already downloaded into $velopack (vpk download github) become delta packages.
    $vpkArgs = @(
        'pack', '--yes',
        '--packId', $packId,
        '--packVersion', $Version,
        '--packDir', $stage,
        '--packTitle', 'DeviceDeck',
        '--packAuthors', 'sadrazkh',
        '--mainExe', 'DeviceDeck.exe',
        '--icon', (Join-Path $repo 'src\DeviceDeck.Shell\Assets\DeviceDeck.ico'),
        '--runtime', $Runtime,
        '--channel', $Runtime,
        '--outputDir', $velopack
    )
    $notes = Join-Path $repo "docs\releases\v$Version.md"
    if (Test-Path $notes) { $vpkArgs += @('--releaseNotes', $notes) }
    if ($SignTemplate) { $vpkArgs += @('--signTemplate', $SignTemplate) }
    Invoke-Native 'vpk pack' { & $Vpk @vpkArgs }

    # The portable zip above is the one we ship; Velopack's own portable build would only confuse.
    Get-ChildItem $velopack -Filter '*-Portable.zip' | Remove-Item -Force
    # vpk names it <packId>-<channel>-Setup.exe; a Setup.exe left from an earlier run here is replaced.
    $setup = Join-Path $velopack "$packId-$Runtime-Setup.exe"
    if (-not (Test-Path $setup)) { throw "vpk pack produced no $packId-$Runtime-Setup.exe" }
    $named = Join-Path $velopack "DeviceDeck-$Version-$Runtime-Setup.exe"
    Move-Item -Force $setup $named
    Write-Host "Installer: $named ($([math]::Round((Get-Item $named).Length / 1MB, 1)) MB)"
    Get-ChildItem $velopack | ForEach-Object { Write-Host "  $($_.Name)" }
    Write-Output-Value 'installer' $named
    Write-Output-Value 'velopack' $velopack
}
