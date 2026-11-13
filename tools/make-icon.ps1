<#
.SYNOPSIS
    Draw Ferry's icon: a rounded square in tide to deep harbour, with a download arrow landing on water (docs/mocks/ferry.js, mark).
.DESCRIPTION
    Writes src/Ferry.App/Assets/AppIcon.png (256 px, for the title bar and About) and AppIcon.ico (16 to 256 px, for
    the window, the taskbar and the installer). Windows PowerShell 5.1 or later on Windows (System.Drawing).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = New-Item -ItemType Directory -Force (Join-Path $root 'src\Ferry.App\Assets')

function Draw-Icon([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [single]($size * 0.28)
    $rect = New-Object System.Drawing.RectangleF 0, 0, ($size - 1), ($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 47, 191, 174)), ([System.Drawing.Color]::FromArgb(255, 11, 79, 99)), 45.0
    $g.FillPath($brush, $path)

    # the arrow landing on water, on a grid of 32 as docs/mocks/ferry.js draws it: a stem, a head, and a wave
    function At([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]($x * $size / 32)), ([single]($y * $size / 32)) }
    $w = [single]([Math]::Max(1.5, $size * 0.08))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLine($pen, (At 16 6), (At 16 17.5))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((At 11 12.5), (At 16 17.5), (At 21 12.5)))
    $g.DrawBeziers($pen, [System.Drawing.PointF[]]@(
        (At 6.5 23.5), (At 8.9 21.3), (At 10.9 21.3), (At 12.8 23.5),
        (At 14.7 25.7), (At 16.7 25.7), (At 19.2 23.5),
        (At 21.7 21.3), (At 23.5 21.3), (At 25.5 23.5)))
    $g.Dispose()
    return $bitmap
}

$big = Draw-Icon 256
$big.Save((Join-Path $assets 'AppIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# an .ico holding PNG-compressed images, one per size
$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bitmap = Draw-Icon $s
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    , $stream.ToArray()
}
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $writer.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $out.ToArray())
Write-Host "Wrote $assets\AppIcon.png and AppIcon.ico"
