# Generates Assets\app.ico (spectrum bars on a dark rounded tile) at several sizes.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'Assets\app.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded background tile
    $r = [Math]::Max(2, $size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size), ([System.Drawing.Color]::FromArgb(255, 32, 24, 72)), ([System.Drawing.Color]::FromArgb(255, 11, 11, 18))
    $g.FillPath($bg, $path)

    # Spectrum bars
    $heights = 0.45, 0.75, 0.55, 0.9, 0.65
    $colors = @(
        [System.Drawing.Color]::FromArgb(255, 91, 75, 255),
        [System.Drawing.Color]::FromArgb(255, 140, 82, 255),
        [System.Drawing.Color]::FromArgb(255, 214, 72, 255),
        [System.Drawing.Color]::FromArgb(255, 255, 92, 170),
        [System.Drawing.Color]::FromArgb(255, 29, 185, 84)
    )
    $n = $heights.Count
    $pad = $size * 0.18
    $slot = ($size - 2 * $pad) / $n
    $bw = $slot * 0.68
    $base = $size - $pad
    for ($i = 0; $i -lt $n; $i++) {
        $h = ($size - 2 * $pad) * $heights[$i]
        $x = $pad + $i * $slot + ($slot - $bw) / 2
        $brush = New-Object System.Drawing.SolidBrush $colors[$i]
        $g.FillRectangle($brush, [single]$x, [single]($base - $h), [single]$bw, [single]$h)
        $brush.Dispose()
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(New-IconPng $s) }

# ICO container: header, one directory entry per image, then the PNG payloads.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Wrote $out"
