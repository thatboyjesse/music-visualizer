# Builds the app (the Rust engine plus the C# settings app) and the installer.
#
#   .\build.ps1                 # version from MusicVisualizer.csproj
#   .\build.ps1 -Version 1.2.0  # override version
#   .\build.ps1 -SkipInstaller  # app folder only
#   .\build.ps1 -Portable       # also a single-file publish\MusicVisualizer.exe (C# only) to run without installing
#
# Output:
#   publish\app\                                what Setup installs: MusicVisualizer.exe (the Rust engine) and
#                                               MusicVisualizerSettings.exe (C#, self-contained, with its runtime)
#   dist\MusicVisualizer-Setup-<version>.exe    the installer, with that folder embedded as a zip
#   publish\MusicVisualizer.exe                 (-Portable only) the C#-only app as one exe (no Rust engine:
#                                               it runs the wallpaper itself, using more CPU and memory)
#
# Needs the .NET 8 SDK and Rust (rustup, stable toolchain; GNU or MSVC).
#
# Why a folder: a single-file exe has to load the .NET runtime and WPF from inside itself,
# which costs memory (about 100 MB more with compression on). A normal folder lets Windows
# map those files from disk and share them, so the app stays small while it runs all day.
param(
    [string]$Version,
    [switch]$SkipInstaller,
    [switch]$Portable
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
$appDir = Join-Path $publishDir 'app'

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $appDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# The engine: the always-running part (wallpaper, audio, drawing, tray).
$cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source
if (-not $cargo) { $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe' }
if (-not (Test-Path $cargo)) { throw "Rust isn't installed (https://rustup.rs): it's needed to build the engine in native\" }
$env:MV_VERSION = $Version   # embedded in the exe's version info
$ErrorActionPreference = 'Continue'   # cargo reports progress on stderr
& $cargo build --release --manifest-path (Join-Path $root 'native\Cargo.toml')
$ErrorActionPreference = 'Stop'
if ($LASTEXITCODE -ne 0) { throw "cargo build failed" }
Copy-Item (Join-Path $root 'native\target\release\MusicVisualizer.exe') (Join-Path $appDir 'MusicVisualizer.exe') -Force
Write-Host "App folder: $appDir" -ForegroundColor Green

if ($Portable) {
    # Uncompressed: compression saves disk space but costs ~100 MB of memory at run time.
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:Version=$Version `
        -o (Join-Path $publishDir 'portable')
    if ($LASTEXITCODE -ne 0) { throw "portable publish failed" }
    Move-Item (Join-Path $publishDir 'portable\MusicVisualizerSettings.exe') (Join-Path $publishDir 'MusicVisualizer.exe')
    Remove-Item -Recurse -Force (Join-Path $publishDir 'portable')
    Write-Host "Portable exe: $publishDir\MusicVisualizer.exe" -ForegroundColor Green
}

if ($SkipInstaller) { return }

# Zip the folder; Setup embeds the zip (see Setup\Setup.csproj) and extracts it on install.
$payload = Join-Path $root 'obj\payload.zip'
New-Item -ItemType Directory -Force (Split-Path $payload) | Out-Null
if (Test-Path $payload) { Remove-Item -Force $payload }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($appDir, $payload, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$setupOut = Join-Path $root 'Setup\bin\out'
if (Test-Path $setupOut) { Remove-Item -Recurse -Force $setupOut }
dotnet build (Join-Path $root 'Setup\Setup.csproj') -c Release `
    -p:PayloadPath=$payload `
    -p:Version=$Version `
    -o $setupOut
if ($LASTEXITCODE -ne 0) { throw "Setup build failed" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$installer = Join-Path $dist "MusicVisualizer-Setup-$Version.exe"
Copy-Item (Join-Path $setupOut 'MusicVisualizerSetup.exe') $installer -Force
Write-Host "Installer: $installer" -ForegroundColor Green
