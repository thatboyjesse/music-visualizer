# Builds a standalone MusicVisualizer.exe and the installer.
#
#   .\build.ps1                 # version from MusicVisualizer.csproj
#   .\build.ps1 -Version 1.2.0  # override version
#   .\build.ps1 -SkipInstaller  # exe only
#
# Output:
#   publish\MusicVisualizer.exe                 single-file, self-contained (no .NET install needed)
#   dist\MusicVisualizer-Setup-<version>.exe    the custom installer (Setup\), with the exe embedded
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
$exe = Join-Path $publishDir 'MusicVisualizer.exe'
Write-Host "Executable: $exe" -ForegroundColor Green

if ($SkipInstaller) { return }

# The installer embeds the exe we just published (see Setup\Setup.csproj).
$setupOut = Join-Path $root 'Setup\bin\out'
if (Test-Path $setupOut) { Remove-Item -Recurse -Force $setupOut }
dotnet build (Join-Path $root 'Setup\Setup.csproj') -c Release `
    -p:PayloadPath=$exe `
    -p:Version=$Version `
    -o $setupOut
if ($LASTEXITCODE -ne 0) { throw "Setup build failed" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$installer = Join-Path $dist "MusicVisualizer-Setup-$Version.exe"
Copy-Item (Join-Path $setupOut 'MusicVisualizerSetup.exe') $installer -Force
Write-Host "Installer: $installer" -ForegroundColor Green
