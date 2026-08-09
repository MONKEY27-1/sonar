# Generates src/Soundboard/Assets/icon.ico — a "sonar ping" mark (accent-purple rounded-square
# backdrop, white concentric rings + center dot) matching the app's own theme colors
# (AccentBrush #6366F1 / AccentHoverBrush #818CF8 from Themes/Generic.xaml).
Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $Radius * 2
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-SonarBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = $Size * 0.04
    $rectSize = $Size - ($pad * 2)
    $radius = $Size * 0.22

    $bgPath = New-RoundedRectPath -X $pad -Y $pad -Width $rectSize -Height $rectSize -Radius $radius
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF($pad, $pad)),
        (New-Object System.Drawing.PointF(($pad + $rectSize), ($pad + $rectSize))),
        [System.Drawing.Color]::FromArgb(255, 0x63, 0x66, 0xF1),
        [System.Drawing.Color]::FromArgb(255, 0x81, 0x8C, 0xF8))
    $g.FillPath($bgBrush, $bgPath)

    $cx = $Size / 2
    $cy = $Size / 2
    $strokeWidth = [Math]::Max(2, $Size * 0.045)

    $ringRadii = @(0.16, 0.27, 0.38) | ForEach-Object { $_ * $Size }
    $ringOpacities = @(255, 200, 130)

    for ($i = 0; $i -lt $ringRadii.Length; $i++) {
        $r = $ringRadii[$i]
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($ringOpacities[$i], 255, 255, 255), $strokeWidth)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawEllipse($pen, ($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
        $pen.Dispose()
    }

    $dotRadius = $Size * 0.07
    $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($dotBrush, ($cx - $dotRadius), ($cy - $dotRadius), ($dotRadius * 2), ($dotRadius * 2))

    $g.Dispose()
    return $bmp
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

$sizes = @(16, 32, 48, 256)
$pngBytesBySize = @{}
foreach ($size in $sizes) {
    $bmp = New-SonarBitmap -Size $size
    $pngBytesBySize[$size] = ConvertTo-PngBytes -Bitmap $bmp
    $bmp.Dispose()
}

# Assemble a standard multi-resolution .ico container: ICONDIR header, one ICONDIRENTRY per
# image, then each image's raw PNG bytes back-to-back (the modern, Explorer-supported way to
# store the larger sizes — confirmed the existing icon.ico already used this same format).
$outPath = "src/Soundboard/Assets/icon.ico"
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([UInt16]0)      # reserved
$writer.Write([UInt16]1)      # type = icon
$writer.Write([UInt16]$sizes.Length)

$headerSize = 6 + (16 * $sizes.Length)
$offset = $headerSize
foreach ($size in $sizes) {
    $bytes = $pngBytesBySize[$size]
    $dim = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([Byte]$dim)          # width
    $writer.Write([Byte]$dim)          # height
    $writer.Write([Byte]0)             # color count
    $writer.Write([Byte]0)             # reserved
    $writer.Write([UInt16]1)           # color planes
    $writer.Write([UInt16]32)          # bits per pixel
    $writer.Write([UInt32]$bytes.Length)
    $writer.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($size in $sizes) {
    # Explicit [byte[]] cast matters here — without it, PowerShell's overload resolution
    # can't tell this apart from BinaryWriter.Write(bool) and silently writes 1 byte
    # instead of the whole array (confirmed by tracing stream length after each write).
    $writer.Write([byte[]]$pngBytesBySize[$size])
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($outPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Output "Wrote $outPath ($($sizes -join ', ') px)"
