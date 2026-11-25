# Builds a standalone MusicVisualizer.exe and the installer.
#
#   .\build.ps1                 # version from the .csproj
#   .\build.ps1 -Version 1.2.0  # override version
#   .\build.ps1 -SkipInstaller  # exe only
#
# Output:
#   publish\MusicVisualizer.exe                 single-file, self-contained (no .NET install needed)
#   dist\MusicVisualizer-Setup-<version>.exe    installer (needs Inno Setup 6)
param(
    [string]$Version,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'MusicVisualizer.csproj'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Building Music Visualizer $Version" -ForegroundColor Cyan

$publishDir = Join-Path $root 'publish'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Executable: $publishDir\MusicVisualizer.exe" -ForegroundColor Green

if ($SkipInstaller) { return }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 not found, so the installer was skipped. Install it with:  winget install JRSoftware.InnoSetup"
    return
}

& $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\MusicVisualizer.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Installer: $root\dist\MusicVisualizer-Setup-$Version.exe" -ForegroundColor Green
