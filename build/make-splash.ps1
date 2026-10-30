<#
.SYNOPSIS
    Draws the installer splash image (PNG, 400x250) from the app icon: icon, "EZ Forensics", version, "Installing...".

.DESCRIPTION
    Velopack's Setup.exe shows this image while it installs. Without a splash image (vpk pack --splashImage) Setup.exe falls back
    to a small progress dialog ("Setting up ... please wait") with an OK button, which looks like a message the user must answer.
    make-installer.ps1 calls this script and passes the result to vpk. Uses System.Drawing (Windows PowerShell 5.1 / Windows only).

    This file is ASCII only so Windows PowerShell 5.1 reads it correctly.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\make-splash.ps1 -Out artifacts\installer\splash.png -Version 1.0.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Version = "",
    [string]$Icon = (Join-Path (Split-Path -Parent $PSScriptRoot) "src\EzForensics.App\Assets\EzForensics.png"),
    [int]$Width = 400,
    [int]$Height = 250
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Icon)) { throw "Icon not found: $Icon" }
$dir = Split-Path -Parent ([System.IO.Path]::GetFullPath($Out))
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

$bmp = New-Object System.Drawing.Bitmap $Width, $Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
try {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    # Background: the app's dark window colour (Setup.exe draws its progress bar along the bottom edge).
    $bg = [System.Drawing.Color]::FromArgb(255, 32, 33, 36)
    $g.Clear($bg)

    # Icon, left.
    $img = [System.Drawing.Image]::FromFile((Resolve-Path $Icon))
    try {
        $size = 112
        $g.DrawImage($img, 32, [int](($Height - $size) / 2) - 4, $size, $size)
    }
    finally { $img.Dispose() }

    # Name, version, status, right of the icon.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 240, 240, 240))
    $grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 170, 174, 180))
    $titleFont = New-Object System.Drawing.Font "Segoe UI Semibold", 22, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Point)
    $smallFont = New-Object System.Drawing.Font "Segoe UI", 10, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Point)
    $x = 164
    $g.DrawString("EZ Forensics", $titleFont, $white, $x, 78)
    $line2 = if ($Version) { "Version $Version" } else { "" }
    if ($line2) { $g.DrawString($line2, $smallFont, $grey, $x + 2, 122) }
    $g.DrawString("Installing...", $smallFont, $grey, $x + 2, 144)
    $titleFont.Dispose(); $smallFont.Dispose(); $white.Dispose(); $grey.Dispose()

    $bmp.Save([System.IO.Path]::GetFullPath($Out), [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $g.Dispose()
    $bmp.Dispose()
}
Write-Host "Splash image: $Out ($Width x $Height)"
