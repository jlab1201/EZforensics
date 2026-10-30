<#
.SYNOPSIS
    Builds the EZ Forensics Windows installer (Velopack) into <repo>\dist.

.DESCRIPTION
    1. Runs the Core unit tests (skip with -SkipTests).
    2. Publishes src\EzForensics.App self-contained for win-x64 (the .NET runtime is bundled: nothing to preinstall).
    3. Packs it with Velopack's vpk tool: per-user Setup.exe (no admin), portable zip, update packages.
    4. Copies the installer to dist\EzForensics-Setup.exe and the portable zip to dist\EzForensics-Portable.zip.

    Requirements: .NET 8 SDK; vpk (installed automatically with "dotnet tool install -g vpk" when missing).
    Nothing is uploaded or published anywhere.

    This file is ASCII only so Windows PowerShell 5.1 reads it correctly.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
    powershell -ExecutionPolicy Bypass -File build\make-installer.ps1 -Version 1.0.0 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo "dist"
$work = Join-Path $repo "artifacts\installer"
$publish = Join-Path $work "publish"
$releases = Join-Path $work "releases"
$app = Join-Path $repo "src\EzForensics.App\EzForensics.App.csproj"
$icon = Join-Path $repo "src\EzForensics.App\Assets\EzForensics.ico"
$packId = "EzForensics"
$title = "EZ Forensics"

function Step([string]$text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

Step "EZ Forensics $Version installer build"
Write-Host "Repository: $repo"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET SDK (dotnet) is not on PATH." }

if (-not $SkipTests) {
    Step "Unit tests"
    Invoke-Checked "dotnet" @("test", (Join-Path $repo "tests\EzForensics.Core.Tests"), "-c", $Configuration, "--nologo")
}

Step "Publish (self-contained win-x64) to $publish"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
Invoke-Checked "dotnet" @("publish", $app, "-c", $Configuration, "-r", "win-x64", "--self-contained", "true",
    "-p:Version=$Version", "-p:FileVersion=$Version.0", "-p:InformationalVersion=$Version",
    "-p:PublishReadyToRun=false", "-o", $publish, "--nologo")
if (-not (Test-Path (Join-Path $publish "EzForensics.exe"))) { throw "Publish did not produce EzForensics.exe." }

Step "Velopack (vpk)"
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    Write-Host "vpk not found: installing the official Velopack CLI (dotnet tool install -g vpk)"
    Invoke-Checked "dotnet" @("tool", "install", "-g", "vpk")
    $env:PATH = "$env:PATH;$env:USERPROFILE\.dotnet\tools"
}
if (Test-Path $releases) { Remove-Item -Recurse -Force $releases }
New-Item -ItemType Directory -Force $releases | Out-Null
# Splash image for Setup.exe. Without --splashImage, Velopack's Setup.exe shows a small "Setting up <title> v<version>, please wait..."
# progress dialog with an OK button instead, which users read as a message box to answer.
$splash = Join-Path $work "splash.png"
& (Join-Path $PSScriptRoot "make-splash.ps1") -Out $splash -Version $Version
if (-not (Test-Path $splash)) { throw "make-splash.ps1 did not produce $splash" }
Invoke-Checked "vpk" @("pack",
    "--packId", $packId,
    "--packVersion", $Version,
    "--packDir", $publish,
    "--runtime", "win-x64",
    "--mainExe", "EzForensics.exe",
    "--packTitle", $title,
    "--packAuthors", "EZ Forensics",
    "--icon", $icon,
    "--splashImage", $splash,
    "--splashProgressColor", "#4C9AFF",
    "--shortcuts", "Desktop,StartMenuRoot",
    "--outputDir", $releases)

Step "Copy to $dist"
New-Item -ItemType Directory -Force $dist | Out-Null
$setup = Get-ChildItem $releases -Filter "*Setup.exe" | Select-Object -First 1
$portable = Get-ChildItem $releases -Filter "*Portable.zip" | Select-Object -First 1
if (-not $setup) { throw "vpk did not produce a Setup.exe in $releases" }
Copy-Item $setup.FullName (Join-Path $dist "EzForensics-Setup.exe") -Force
if ($portable) { Copy-Item $portable.FullName (Join-Path $dist "EzForensics-Portable.zip") -Force }
# The update feed files (RELEASES / releases.win.json / *.nupkg) go into dist\releases, ready to attach to a GitHub release later.
$feed = Join-Path $dist "releases"
if (Test-Path $feed) { Remove-Item -Recurse -Force $feed }
New-Item -ItemType Directory -Force $feed | Out-Null
Get-ChildItem $releases -File | Copy-Item -Destination $feed -Force

Step "Done"
Get-ChildItem $dist -File | ForEach-Object { "{0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
Write-Host ""
Write-Host "Installer: $(Join-Path $dist 'EzForensics-Setup.exe')"
