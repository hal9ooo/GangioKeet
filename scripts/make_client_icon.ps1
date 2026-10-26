# Genera l'icona multi-risoluzione del client GangioKeet.
# Concept: pappagallino (parakeet) stilizzato su fondo verde/teal con onde sonore.
# Uso: powershell -ExecutionPolicy Bypass -File scripts/make_client_icon.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot "..\client-windows\src\GangioKeet.Client\Assets"
New-Item -ItemType Directory -Force $outDir | Out-Null
$icoPath = Join-Path $outDir "gangio.ico"

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Sfondo: quadrato arrotondato con gradiente teal -> verde.
    $bg = New-RoundedRectPath 8 8 240 240 54
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(8, 8)),
        (New-Object System.Drawing.PointF(248, 248)),
        [System.Drawing.Color]::FromArgb(255, 11, 59, 54),
        [System.Drawing.Color]::FromArgb(255, 16, 185, 129))
    $g.FillPath($grad, $bg)

    # Highlight morbido in alto a sinistra.
    $hl = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(28, 255, 255, 255))
    $g.FillEllipse($hl, 24, 18, 150, 110)

    $cream = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 248, 250, 247))
    $creamSoft = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 226, 240, 229))
    $amber = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 245, 158, 11))
    $amberDark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 217, 119, 6))
    $ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 12, 30, 26))

    # Cresta (tre ciuffi) solo da 32 px in su.
    if ($size -ge 32) {
        $g.FillEllipse($cream, 72, 44, 26, 34)
        $g.FillEllipse($cream, 97, 32, 26, 38)
        $g.FillEllipse($cream, 122, 44, 26, 34)
    }

    # Testa del pappagallino.
    $g.FillEllipse($cream, 40, 62, 128, 140)

    # Guancia e ombra morbida.
    $cheek = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(46, 16, 185, 129))
    $g.FillEllipse($cheek, 68, 128, 74, 58)
    $g.FillEllipse($creamSoft, 52, 66, 92, 74)

    # Becco ricurvo.
    $beak = New-Object System.Drawing.Drawing2D.GraphicsPath
    $beak.StartFigure()
    $beak.AddBezier(142, 112, 184, 108, 208, 126, 208, 140)
    $beak.AddBezier(208, 140, 208, 154, 184, 172, 142, 168)
    $beak.AddBezier(142, 168, 160, 150, 160, 130, 142, 112)
    $beak.CloseFigure()
    $g.FillPath($amber, $beak)
    $beakLow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $beakLow.StartFigure()
    $beakLow.AddBezier(146, 142, 172, 146, 194, 152, 202, 158)
    $beakLow.AddBezier(202, 158, 184, 170, 154, 170, 142, 168)
    $beakLow.AddBezier(142, 168, 152, 156, 148, 150, 146, 142)
    $beakLow.CloseFigure()
    $g.FillPath($amberDark, $beakLow)

    # Occhio con riflesso.
    $g.FillEllipse($ink, 110, 102, 22, 22)
    if ($size -ge 32) {
        $g.FillEllipse([System.Drawing.Brushes]::White, 114, 105, 7, 7)
    }

    # Onde sonore dal becco.
    if ($size -ge 24) {
        $arcPen1 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(230, 255, 255, 255), 7)
        $arcPen2 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 255, 255), 6)
        $arcPen3 = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 255, 255, 255), 6)
        $arcPen1.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $arcPen1.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawArc($arcPen1, 204 - 16, 140 - 16, 32, 32, -52, 104)
        if ($size -ge 48) {
            $arcPen2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $arcPen2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $arcPen3.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $arcPen3.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $g.DrawArc($arcPen2, 204 - 30, 140 - 30, 60, 60, -52, 104)
            $g.DrawArc($arcPen3, 204 - 44, 140 - 44, 88, 88, -52, 104)
        }
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
    return ,$bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$payloads = @{}
foreach ($s in $sizes) { $payloads[$s] = Draw-Icon $s }

$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms)
$w.Write([UInt16]0)                 # reserved
$w.Write([UInt16]1)                 # type: icon
$w.Write([UInt16]$sizes.Count)      # count

$offset = 6 + (16 * $sizes.Count)
foreach ($s in $sizes) {
    $png = $payloads[$s]
    $w.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $w.Write([Byte]$(if ($s -ge 256) { 0 } else { $s }))
    $w.Write([Byte]0)               # palette
    $w.Write([Byte]0)               # reserved
    $w.Write([UInt16]1)             # planes
    $w.Write([UInt16]32)            # bpp
    $w.Write([UInt32]$png.Length)
    $w.Write([UInt32]$offset)
    $offset += $png.Length
}
foreach ($s in $sizes) { $w.Write($payloads[$s]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$w.Dispose()
$ms.Dispose()

Write-Output "Icona scritta: $icoPath ($([System.IO.File]::ReadAllBytes($icoPath).Length) byte, $($sizes.Count) risoluzioni)"
