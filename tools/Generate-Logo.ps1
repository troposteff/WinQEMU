# Génère les assets PNG + l'ICO de WinQEMU à partir de la géométrie du logo.
# La géométrie est exprimée dans le repère 256x256 du SVG maître Assets\Logo.svg : un écran
# dont le cadre forme la boucle d'un Q, et une queue qui sort du coin bas-droite.
#
# Le dessin est monochrome d'un bout à l'autre : une seule couleur, donc il se décline
# indifféremment en couleur de marque, en blanc, ou sur la couleur d'accentuation de Windows.

param(
    # Par défaut : le dossier Assets du projet WinQEMU, relatif à ce script.
    [string]$AssetsDir = (Join-Path $PSScriptRoot '..\WinQEMU\Assets'),
    # Couleur de marque des assets du package. Le rendu dans l'application suit l'accent système.
    [string]$Color = '#1F6FEB'
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$Brand = [System.Drawing.ColorTranslator]::FromHtml($Color)
$White = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)

# --- géométrie source (repère 256) ---------------------------------------
# Rayon assez serré pour que le cadre se lise encore comme un écran, assez rond pour faire un Q.
$RectX = 46.0; $RectY = 44.0; $RectW = 164.0; $RectH = 140.0; $Radius = 34.0
# La queue part du milieu du trait et s'en éloigne radialement : en la faisant partir de l'intérieur
# de la boucle, son amorce ressortait en bosse dans le vide de l'écran.
#
# Elle est inclinée à 55° et non à 45° pour que la boîte englobante reste presque carrée (202x195).
# Une boîte plus large que haute se fait cadrer par sa largeur dans les icônes carrées de Windows,
# et le logo paraît alors plus petit que ses voisins dans la barre des tâches.
$TailX0 = 196.0; $TailY0 = 178.0; $TailX1 = 216.0; $TailY1 = 207.0

# Le trait EST le logo : il garde donc une épaisseur quasi constante, juste renforcée aux
# tailles où un trait fin se délave sous le pixel.
function Get-StrokeWidth([double]$contentPx) {
    if ($contentPx -ge 28) { return 32.0 }
    if ($contentPx -ge 20) { return 38.0 }
    return 44.0
}

function New-LogoPath {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [float](2 * $Radius)
    $p.AddArc([float]$RectX, [float]$RectY, $d, $d, 180, 90)
    $p.AddArc([float]($RectX + $RectW - 2 * $Radius), [float]$RectY, $d, $d, 270, 90)
    $p.AddArc([float]($RectX + $RectW - 2 * $Radius), [float]($RectY + $RectH - 2 * $Radius), $d, $d, 0, 90)
    $p.AddArc([float]$RectX, [float]($RectY + $RectH - 2 * $Radius), $d, $d, 90, 90)
    $p.CloseFigure()
    $p.StartFigure()
    $p.AddLine([float]$TailX0, [float]$TailY0, [float]$TailX1, [float]$TailY1)
    return $p
}

function Save-Logo {
    param(
        [string]$Path, [int]$W, [int]$H,
        [double]$Fit,                          # part de la boîte occupée par le dessin
        [System.Drawing.Color]$Ink = $Brand
    )

    # L'épaisseur dépend de la taille rendue, qui dépend de la boîte englobante, qui dépend de
    # l'épaisseur : deux passes suffisent à converger.
    $stroke = 32.0
    for ($i = 0; $i -lt 2; $i++) {
        $bw = ($TailX1 + $stroke / 2) - ($RectX - $stroke / 2)
        $bh = ($TailY1 + $stroke / 2) - ($RectY - $stroke / 2)
        $scale = [Math]::Min(($W * $Fit) / $bw, ($H * $Fit) / $bh)
        $stroke = Get-StrokeWidth ($bh * $scale)
    }
    $x0 = $RectX - $stroke / 2; $y0 = $RectY - $stroke / 2
    $bw = ($TailX1 + $stroke / 2) - $x0
    $bh = ($TailY1 + $stroke / 2) - $y0
    $scale = [Math]::Min(($W * $Fit) / $bw, ($H * $Fit) / $bh)

    $ss = if ([Math]::Max($W, $H) -le 512) { 4 } elseif ([Math]::Max($W, $H) -le 1600) { 2 } else { 1 }
    $sp = $scale * $ss
    $offX = (($W * $ss) - $bw * $sp) / 2 - $x0 * $sp
    $offY = (($H * $ss) - $bh * $sp) / 2 - $y0 * $sp

    $bmp = New-Object System.Drawing.Bitmap ($W * $ss), ($H * $ss), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.TranslateTransform([float]$offX, [float]$offY)
    $g.ScaleTransform([float]$sp, [float]$sp)

    # $path serait la même variable que le paramètre $Path (PowerShell ignore la casse), qui est
    # typé [string] : le tracé y serait converti en texte.
    $geom = New-LogoPath
    $pen = New-Object System.Drawing.Pen $Ink, ([float]$stroke)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawPath($pen, $geom)
    $pen.Dispose(); $geom.Dispose(); $g.Dispose()

    if ($ss -gt 1) {
        $final = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g2 = [System.Drawing.Graphics]::FromImage($final)
        $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g2.Clear([System.Drawing.Color]::Transparent)
        $g2.DrawImage($bmp, (New-Object System.Drawing.Rectangle 0, 0, $W, $H))
        $g2.Dispose(); $bmp.Dispose()
        $bmp = $final
    }
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

# --- jeu d'assets --------------------------------------------------------
# Fit : les tuiles réservent une marge autour du logo, les icônes de la barre des tâches
# (targetsize) le laissent remplir presque tout le carré.
$Scales = @(100, 125, 150, 200, 400)
$Bases = @(
    @{ Name = 'Square44x44Logo';   W = 44;  H = 44;  Fit = 0.72 },
    @{ Name = 'Square150x150Logo'; W = 150; H = 150; Fit = 0.66 },
    @{ Name = 'StoreLogo';         W = 50;  H = 50;  Fit = 0.86 },
    @{ Name = 'Wide310x150Logo';   W = 310; H = 150; Fit = 0.50 },
    @{ Name = 'SplashScreen';      W = 620; H = 300; Fit = 0.55 },
    @{ Name = 'LockScreenLogo';    W = 24;  H = 24;  Fit = 0.92 }
)

$count = 0
foreach ($b in $Bases) {
    # L'écran de verrouillage n'accepte que du blanc sur transparent.
    $ink = if ($b.Name -eq 'LockScreenLogo') { $White } else { $Brand }
    foreach ($s in $Scales) {
        $w = [int][Math]::Round($b.W * $s / 100.0)
        $h = [int][Math]::Round($b.H * $s / 100.0)
        Save-Logo -Path (Join-Path $AssetsDir "$($b.Name).scale-$s.png") -W $w -H $h -Fit $b.Fit -Ink $ink
        $count++
    }
    Copy-Item (Join-Path $AssetsDir "$($b.Name).scale-100.png") (Join-Path $AssetsDir "$($b.Name).png") -Force
    $count++
}

# Le dessin étant monochrome dans la couleur de marque, il tient sur fond clair comme sur fond
# sombre : une seule déclinaison suffit, sans variante altform-lightunplated.
$TargetSizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)
foreach ($t in $TargetSizes) {
    Save-Logo -Path (Join-Path $AssetsDir "Square44x44Logo.targetsize-$t.png") -W $t -H $t -Fit 0.94
    Save-Logo -Path (Join-Path $AssetsDir "Square44x44Logo.targetsize-${t}_altform-unplated.png") -W $t -H $t -Fit 1.0
    $count += 2
}

# --- icône Win32 (ApplicationIcon) ---------------------------------------
$IcoSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("winqemu-ico-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$blobs = @()
foreach ($s in $IcoSizes) {
    $p = Join-Path $tmp "$s.png"
    Save-Logo -Path $p -W $s -H $s -Fit 0.96
    $blobs += , ([System.IO.File]::ReadAllBytes($p))
}
$icoPath = Join-Path $AssetsDir 'WinQEMU.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$IcoSizes.Count)
$offset = 6 + 16 * $IcoSizes.Count
for ($i = 0; $i -lt $IcoSizes.Count; $i++) {
    $d = if ($IcoSizes[$i] -ge 256) { 0 } else { $IcoSizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$blobs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Remove-Item $tmp -Recurse -Force
$count++

Write-Output "$count fichiers écrits dans $AssetsDir"
