# make_icon.ps1 - Builds app.ico from packaging\icon\app-icon-source.jpg (PowerShell 5.1, .NET only).
# The source is a 2000x1116 picture. The script crops the icon to a square, rounds the corners,
# and writes 256, 128, 64, 48, 32, 24 and 16 px entries (PNG for 256, 32-bit BMP for the others) into one .ico file.
param([string]$Source = "$PSScriptRoot\app-icon-source.jpg", [string]$Out = "$PSScriptRoot\..\..\app.ico",
      [int]$X = 500, [int]$Y = 28, [int]$Size = 1040)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
$sizes = 256, 128, 64, 48, 32, 24, 16
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    # Rounded square clip, radius 18 % of the size.
    $r = [single]($s * 0.18); $d = 2 * $r; $w = [single]$s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.SetClip($path)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $s, $s), (New-Object System.Drawing.Rectangle $X, $Y, $Size, $Size), 'Pixel')
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # 256 px: PNG data (Windows Vista and later).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Smaller sizes: 32-bit DIB (BITMAPINFOHEADER, BGRA rows bottom-up, then an empty AND mask).
        # Old readers such as System.Drawing.Icon read only this format for small sizes.
        $w2 = New-Object System.IO.BinaryWriter $ms
        $w2.Write([uint32]40); $w2.Write([int32]$s); $w2.Write([int32](2 * $s)); $w2.Write([uint16]1); $w2.Write([uint16]32)
        $w2.Write([uint32]0); $w2.Write([uint32]0); $w2.Write([int32]0); $w2.Write([int32]0); $w2.Write([uint32]0); $w2.Write([uint32]0)
        for ($yy = $s - 1; $yy -ge 0; $yy--) {
            for ($xx = 0; $xx -lt $s; $xx++) {
                $c = $bmp.GetPixel($xx, $yy)
                $w2.Write([byte]$c.B); $w2.Write([byte]$c.G); $w2.Write([byte]$c.R); $w2.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([math]::Ceiling($s / 32.0) * 4)
        $w2.Write((New-Object byte[] ($maskRow * $s)))
        $w2.Flush()
    }
    $bmp.Dispose()
    $pngs += ,($ms.ToArray())
}
$src.Dispose()
# ICO file: ICONDIR, one ICONDIRENTRY per image, then the PNG data.
$fs = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Out), $fs.ToArray())
"Wrote $Out ($($fs.Length) bytes)"
