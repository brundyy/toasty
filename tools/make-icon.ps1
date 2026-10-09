# Builds assets\toasty.ico (16-256 px, PNG-compressed entries) from assets\toasty.png.
# The source is trimmed to its visible pixels and centred on a square canvas first,
# so the toast fills as much of each icon size as possible.
param(
    [string]$Source = "$PSScriptRoot\..\assets\toasty.png",
    [string]$Output = "$PSScriptRoot\..\assets\toasty.ico"
)
Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Source))
try {
    # Find the bounding box of non-transparent pixels.
    $data = $src.LockBits([System.Drawing.Rectangle]::new(0, 0, $src.Width, $src.Height),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $src.Height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $src.UnlockBits($data)
    $minX = $src.Width; $minY = $src.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $src.Height; $y++) {
        $row = $y * $data.Stride
        for ($x = 0; $x -lt $src.Width; $x++) {
            if ($bytes[$row + $x * 4 + 3] -gt 16) {
                if ($x -lt $minX) { $minX = $x }; if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }; if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    $w = $maxX - $minX + 1; $h = $maxY - $minY + 1
    $side = [Math]::Max($w, $h)

    $sizes = 256, 128, 64, 48, 32, 24, 16
    $pngs = foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $scale = $size / $side
        $dw = $w * $scale; $dh = $h * $scale
        $dest = [System.Drawing.RectangleF]::new(($size - $dw) / 2, ($size - $dh) / 2, $dw, $dh)
        $g.DrawImage($src, $dest, [System.Drawing.RectangleF]::new($minX, $minY, $w, $h), [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , @{ Size = $size; Bytes = $ms.ToArray() }
    }
} finally {
    $src.Dispose()
}

# ICO container: header, directory entries, then the PNG blobs.
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$p.Bytes.Length); $bw.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path (Split-Path (Resolve-Path $Source)) (Split-Path $Output -Leaf)), $out.ToArray())
Write-Host "Wrote $Output ($($pngs.Count) sizes, trimmed to ${w}x${h})"
