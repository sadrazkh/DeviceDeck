<#
.SYNOPSIS
Draws the three clearly labelled test images for the LocalSend check on a real iPhone (round 3).

.DESCRIPTION
Writes "DeviceDeck test 1-3.png", "DeviceDeck test 2-3.png" and "DeviceDeck test 3-3.png" (1200 x 1600, one colour
each) into -Folder. Each says "DeviceDeck test N/3", when it was made, and that it is safe to delete, so the owner can
find and delete them in Photos afterwards. Never writes to C: (it is nearly full). Windows PowerShell 5.1 or later.

.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-localsend-test-images.ps1
#>
param(
    [string]$Folder = 'E:\DeviceDeck-test\localsend'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if ($Folder -match '^[Cc]:') {
    throw 'Not on C: (it is nearly full); pick a folder on E:.'
}
New-Item -ItemType Directory -Force -Path $Folder | Out-Null

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm'
$colours = @('#3B5BDB', '#2B8A3E', '#C92A2A')
for ($i = 1; $i -le 3; $i++) {
    $bitmap = New-Object System.Drawing.Bitmap 1200, 1600
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.Clear([System.Drawing.ColorTranslator]::FromHtml($colours[$i - 1]))
        $centre = New-Object System.Drawing.StringFormat
        $centre.Alignment = [System.Drawing.StringAlignment]::Center
        $centre.LineAlignment = [System.Drawing.StringAlignment]::Center
        $big = New-Object System.Drawing.Font('Segoe UI', 96, [System.Drawing.FontStyle]::Bold)
        $small = New-Object System.Drawing.Font('Segoe UI', 40)
        $g.DrawString("DeviceDeck test $i/3", $big, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, 450, 1200, 300)), $centre)
        $g.DrawString("LocalSend check, $stamp`nSafe to delete", $small, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, 800, 1200, 300)), $centre)
        $path = Join-Path $Folder "DeviceDeck test $i-3.png"
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Output $path
    }
    finally {
        $g.Dispose()
        $bitmap.Dispose()
    }
}
