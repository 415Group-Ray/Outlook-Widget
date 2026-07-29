#Requires -Version 7.0
<#
.SYNOPSIS
    Generates the package's icon and widget picker assets.

.DESCRIPTION
    Draws a self-contained app icon - a rounded blue tile with an envelope glyph - at every size
    and scale Windows looks for, plus the widget picker screenshot at the exact size the Widgets
    Board documentation requires.

    THESE ARE THE v1 ASSETS, NOT PLACEHOLDERS

    Reviewed and accepted on 2026-07-28, with an explicit decision that 415 Group branding is not
    wanted. The generated look is the shipping look, and this script is the source of truth for it:
    the PNGs are build output that happens to be committed, so change the drawing code rather than
    editing an image. This file was named New-PlaceholderAssets.ps1 while the output was flat
    single-colour squares; keeping that name now would misdescribe what it produces.

    The widget screenshot is worth one further note. It illustrates the approved medium card -
    unread count plus the newest messages - which the provider does not render yet, because
    authentication and Graph arrive in Phase 1 slice 2. Having been accepted, it is now the design
    reference the medium card is expected to match, not merely a preview image. Sample senders and
    subjects are fictional and no mailbox data was involved in producing it.

    WHY THIS NOW USES GDI+, HAVING PREVIOUSLY ARGUED AGAINST AN IMAGING LIBRARY

    The earlier version hand-wrote PNG chunks and explained that System.Drawing.Common is a
    separate package on modern .NET and not worth a dependency "purely to draw a square". That
    reasoning was sound for a square and wrong for artwork. It also conflated two different
    things: a dependency of the PRODUCT, which would have to be justified, and a capability of a
    build-time script, which costs nothing. System.Drawing is available to PowerShell 7 on
    Windows, this repository is Windows-only already, and nothing here ships in the package -
    only the PNGs do. Directory.Packages.props is untouched.

    ASSETS PRODUCED

    App icon, as a self-contained rounded tile so it looks correct whether or not Windows draws a
    plate behind it:
      StoreLogo             50px, plus scale-100/125/150/200/400
      Square150x150Logo     150px, plus scale-125/150/200/400
      Square44x44Logo       44px, plus scale-125/150/200/400
      Square44x44Logo       targetsize-16/24/32/48/256, each also as _altform-unplated

    Widget assets:
      WidgetIcon            128px, shown in the widget's attribution area
      WidgetScreenshot      300x304, the size the picker documentation specifies, with
                            transparent rounded corners; dark and light variants

    The scale- and targetsize-qualified files only take effect if the package carries a
    resources.pri that indexes them. Build-Package.ps1 runs makepri for that reason; without it
    Windows falls back to the unqualified file and the variants are dead weight.

.PARAMETER OutputDirectory
    Where to write the assets. Defaults to the package project's Assets folder.

.PARAMETER Force
    Overwrite existing files. Without this, existing assets are left alone, so a run started by
    habit cannot quietly replace assets that have been reviewed.

.EXAMPLE
    pwsh -File scripts/New-Assets.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\OutlookWidget.Package\Assets'
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# ---------------------------------------------------------------------------
# Palette
# ---------------------------------------------------------------------------

# A blue gradient rather than the previous flat #005A9E. Flat fills read as unfinished at tile
# sizes, and the gradient costs nothing.
$tileTop    = [System.Drawing.Color]::FromArgb(255, 0x2C, 0x7C, 0xD8)
$tileBottom = [System.Drawing.Color]::FromArgb(255, 0x0A, 0x46, 0x8C)
$glyph      = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)
$accent     = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xB9, 0x00)

# Widget card colours, matched to the Widgets Board's own surfaces so the picker preview looks
# like a widget rather than like a poster of one.
$darkCard   = [System.Drawing.Color]::FromArgb(255, 0x2B, 0x2B, 0x2B)
$darkText   = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF)
$darkSubtle = [System.Drawing.Color]::FromArgb(255, 0x9A, 0x9A, 0x9A)
$lightCard  = [System.Drawing.Color]::FromArgb(255, 0xF7, 0xF7, 0xF7)
$lightText  = [System.Drawing.Color]::FromArgb(255, 0x1A, 0x1A, 0x1A)
$lightSub   = [System.Drawing.Color]::FromArgb(255, 0x60, 0x60, 0x60)

# ---------------------------------------------------------------------------
# Drawing helpers
# ---------------------------------------------------------------------------

function New-RoundedPath {
    param(
        [Parameter(Mandatory)][double]$X,
        [Parameter(Mandatory)][double]$Y,
        [Parameter(Mandatory)][double]$Width,
        [Parameter(Mandatory)][double]$Height,
        [Parameter(Mandatory)][double]$Radius
    )

    # Clamp so a large radius on a small box cannot produce a self-intersecting path.
    $r = [Math]::Min($Radius, [Math]::Min($Width, $Height) / 2)
    $d = $r * 2

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()

    if ($r -le 0) {
        $path.AddRectangle([System.Drawing.RectangleF]::new($X, $Y, $Width, $Height))
        return $path
    }

    $path.AddArc([single]$X, [single]$Y, [single]$d, [single]$d, 180, 90)
    $path.AddArc([single]($X + $Width - $d), [single]$Y, [single]$d, [single]$d, 270, 90)
    $path.AddArc([single]($X + $Width - $d), [single]($Y + $Height - $d), [single]$d, [single]$d, 0, 90)
    $path.AddArc([single]$X, [single]($Y + $Height - $d), [single]$d, [single]$d, 90, 90)
    $path.CloseFigure()

    return $path
}

function New-Canvas {
    param([Parameter(Mandatory)][int]$Width, [Parameter(Mandatory)][int]$Height)

    $bmp = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    return @{ Bitmap = $bmp; Graphics = $g }
}

function Add-EnvelopeGlyph {
    <#
        Draws the envelope into a square region. Proportional to $Size so one definition serves
        16px and 600px, and deliberately bold and simple: a glyph with fine detail turns to mush
        at the 16px taskbar size, which is the one users see most often.
    #>
    param(
        [Parameter(Mandatory)]$Graphics,
        [Parameter(Mandatory)][double]$Left,
        [Parameter(Mandatory)][double]$Top,
        [Parameter(Mandatory)][double]$Size,
        [switch]$IncludeAccent
    )

    $bodyX = $Left + $Size * 0.13
    $bodyY = $Top + $Size * 0.26
    $bodyW = $Size * 0.74
    $bodyH = $Size * 0.48
    $radius = $Size * 0.06

    $body = New-RoundedPath -X $bodyX -Y $bodyY -Width $bodyW -Height $bodyH -Radius $radius
    $brush = [System.Drawing.SolidBrush]::new($glyph)
    $Graphics.FillPath($brush, $body)
    $brush.Dispose()

    # The flap, stroked in the tile colour over the white body. Drawing it as a stroke rather than
    # a filled triangle keeps the envelope readable when the glyph is only a few pixels tall.
    $penWidth = [Math]::Max($Size * 0.055, 1.0)
    $pen = [System.Drawing.Pen]::new($tileBottom, [single]$penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $inset = $bodyW * 0.10

    # Explicitly typed as PointF[]. DrawLines is overloaded for Point[] and PointF[], and an
    # untyped PowerShell array binds to the integer overload and then fails to convert.
    [System.Drawing.PointF[]]$flap = @(
        [System.Drawing.PointF]::new([single]($bodyX + $inset), [single]($bodyY + $bodyH * 0.16))
        [System.Drawing.PointF]::new([single]($bodyX + $bodyW / 2), [single]($bodyY + $bodyH * 0.62))
        [System.Drawing.PointF]::new([single]($bodyX + $bodyW - $inset), [single]($bodyY + $bodyH * 0.16))
    )

    $Graphics.DrawLines($pen, $flap)
    $pen.Dispose()

    # An amber unread dot, omitted below 32px because at that size it merges with the envelope and
    # costs legibility rather than adding meaning.
    if ($IncludeAccent -and $Size -ge 32) {
        $dotSize = $Size * 0.26
        $dotX = $Left + $Size * 0.66
        $dotY = $Top + $Size * 0.14

        # A tile-coloured ring separates the dot from the white body underneath.
        $ring = [System.Drawing.SolidBrush]::new($tileBottom)
        $Graphics.FillEllipse($ring, [single]($dotX - $Size * 0.035), [single]($dotY - $Size * 0.035),
            [single]($dotSize + $Size * 0.07), [single]($dotSize + $Size * 0.07))
        $ring.Dispose()

        $dot = [System.Drawing.SolidBrush]::new($accent)
        $Graphics.FillEllipse($dot, [single]$dotX, [single]$dotY, [single]$dotSize, [single]$dotSize)
        $dot.Dispose()
    }

    $body.Dispose()
}

function New-IconTile {
    <#
        The app icon: a rounded gradient tile with the envelope centred on it.

        Self-contained on purpose. Windows may or may not draw a plate behind an icon depending on
        the variant and the surface, and an icon that supplies its own background looks correct
        either way. The previous flat blue square on a blue taskbar plate read as a missing icon.
    #>
    param([Parameter(Mandatory)][int]$Size)

    $canvas = New-Canvas -Width $Size -Height $Size
    $g = $canvas.Graphics

    # Windows 11 app icons are roughly a 22% corner radius. Small sizes get slightly less, or the
    # tile stops reading as a square at all.
    $radiusFactor = if ($Size -le 24) { 0.16 } else { 0.22 }

    # Inset by half a pixel so the anti-aliased edge lands inside the bitmap rather than being
    # clipped, which otherwise leaves a hard corner on one side.
    $inset = 0.5
    $tile = New-RoundedPath -X $inset -Y $inset -Width ($Size - $inset * 2) -Height ($Size - $inset * 2) `
        -Radius ($Size * $radiusFactor)

    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0),
        [System.Drawing.PointF]::new(0, [single]$Size),
        $tileTop, $tileBottom)
    $g.FillPath($brush, $tile)
    $brush.Dispose()
    $tile.Dispose()

    # The glyph occupies a generous share of the tile. Icons that leave too much padding look
    # smaller than their neighbours in the Start list.
    $glyphSize = $Size * 0.68
    $glyphOffset = ($Size - $glyphSize) / 2
    Add-EnvelopeGlyph -Graphics $g -Left $glyphOffset -Top $glyphOffset -Size $glyphSize -IncludeAccent

    $g.Dispose()
    return $canvas.Bitmap
}

function New-WidgetScreenshot {
    <#
        The widget picker preview.

        300x304 with transparent rounded corners, per the picker documentation, showing the medium
        size card. The content is an ILLUSTRATION of the approved medium layout - unread count plus
        the newest messages - not a capture of the current build, which still renders a
        coordination-state placeholder because authentication does not exist yet. Sample senders and
        subjects are obviously fictional; no mailbox data is involved in producing this file.
    #>
    param([Parameter(Mandatory)][ValidateSet('Dark', 'Light')][string]$Theme)

    $width = 300
    $height = 304

    $canvas = New-Canvas -Width $width -Height $height
    $g = $canvas.Graphics

    $isDark = $Theme -eq 'Dark'
    $cardColour = if ($isDark) { $darkCard } else { $lightCard }
    $textColour = if ($isDark) { $darkText } else { $lightText }
    $subtleColour = if ($isDark) { $darkSubtle } else { $lightSub }

    $card = New-RoundedPath -X 0 -Y 0 -Width $width -Height $height -Radius 16
    $cardBrush = [System.Drawing.SolidBrush]::new($cardColour)
    $g.FillPath($cardBrush, $card)
    $cardBrush.Dispose()

    # Clip to the card so nothing drawn later spills past the rounded corners and reintroduces the
    # square edges the documentation asks to avoid.
    $g.SetClip($card)

    $textBrush = [System.Drawing.SolidBrush]::new($textColour)
    $subtleBrush = [System.Drawing.SolidBrush]::new($subtleColour)
    $accentBrush = [System.Drawing.SolidBrush]::new($accent)

    $fontFamily = 'Segoe UI'
    $titleFont = [System.Drawing.Font]::new($fontFamily, 9.5, [System.Drawing.FontStyle]::Regular)
    $countFont = [System.Drawing.Font]::new($fontFamily, 30, [System.Drawing.FontStyle]::Bold)
    $countLabelFont = [System.Drawing.Font]::new($fontFamily, 9.5, [System.Drawing.FontStyle]::Regular)
    $senderFont = [System.Drawing.Font]::new($fontFamily, 9, [System.Drawing.FontStyle]::Bold)
    $subjectFont = [System.Drawing.Font]::new($fontFamily, 8.5, [System.Drawing.FontStyle]::Regular)
    $timeFont = [System.Drawing.Font]::new($fontFamily, 8, [System.Drawing.FontStyle]::Regular)

    $pad = 16

    # Attribution row: the app icon and name, as the Board itself draws above a widget.
    $icon = New-IconTile -Size 18
    $g.DrawImage($icon, [single]$pad, [single]$pad, 18, 18)
    $icon.Dispose()
    $g.DrawString('Outlook Inbox', $titleFont, $subtleBrush, [single]($pad + 24), [single]($pad + 1))

    # Unread count.
    $countY = $pad + 32
    $g.DrawString('12', $countFont, $textBrush, [single]($pad - 4), [single]$countY)
    $countSize = $g.MeasureString('12', $countFont)
    $g.DrawString('unread of 148', $countLabelFont, $subtleBrush,
        [single]($pad - 4 + $countSize.Width - 6), [single]($countY + 24))

    # Message rows.
    $rows = @(
        @{ Sender = 'Dana Whitfield'; Subject = 'Q3 forecast review moved to Thursday'; Time = '9:41 AM'; Unread = $true }
        @{ Sender = 'Build Service';  Subject = 'Nightly pipeline completed with 0 failures'; Time = '7:02 AM'; Unread = $true }
        @{ Sender = 'Marcus Osei';    Subject = 'Signed lease attached for review'; Time = 'Yesterday'; Unread = $false }
    )

    $rowY = $countY + 74

    foreach ($row in $rows) {
        # Unread marker: a small amber bar, consistent with the icon's accent.
        if ($row.Unread) {
            $marker = New-RoundedPath -X $pad -Y ($rowY + 2) -Width 3 -Height 26 -Radius 1.5
            $g.FillPath($accentBrush, $marker)
            $marker.Dispose()
        }

        $textX = $pad + 10

        # The time is drawn first and the sender clipped to what remains, so a long sender name
        # cannot overrun the timestamp.
        $timeSize = $g.MeasureString($row.Time, $timeFont)
        $g.DrawString($row.Time, $timeFont, $subtleBrush,
            [single]($width - $pad - $timeSize.Width + 2), [single]($rowY + 1))

        $senderWidth = $width - $pad - $textX - $timeSize.Width - 6
        $senderRect = [System.Drawing.RectangleF]::new([single]$textX, [single]$rowY, [single]$senderWidth, 15)
        $format = [System.Drawing.StringFormat]::new()
        $format.Trimming = [System.Drawing.StringTrimming]::EllipsisCharacter
        $format.FormatFlags = [System.Drawing.StringFormatFlags]::NoWrap

        $g.DrawString($row.Sender, $senderFont, $textBrush, $senderRect, $format)

        $subjectRect = [System.Drawing.RectangleF]::new(
            [single]$textX, [single]($rowY + 14), [single]($width - $pad - $textX), 15)
        $g.DrawString($row.Subject, $subjectFont, $subtleBrush, $subjectRect, $format)
        $format.Dispose()

        $rowY += 36
    }

    foreach ($d in @($titleFont, $countFont, $countLabelFont, $senderFont, $subjectFont, $timeFont,
                     $textBrush, $subtleBrush, $accentBrush, $card)) {
        $d.Dispose()
    }

    $g.Dispose()
    return $canvas.Bitmap
}

# ---------------------------------------------------------------------------
# Emit
# ---------------------------------------------------------------------------

$written = 0
$kept = 0

function Save-Asset {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][System.Drawing.Bitmap]$Bitmap
    )

    $path = Join-Path $OutputDirectory $Name

    if ((Test-Path -LiteralPath $path) -and -not $Force) {
        $Bitmap.Dispose()
        $script:kept++
        return
    }

    $Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $Bitmap.Dispose()
    $script:written++
}

# Scale variants. The percentages are the ones Windows resolves for packaged apps.
$scales = @(100, 125, 150, 200, 400)

foreach ($item in @(
    @{ Name = 'StoreLogo';         Base = 50 }
    @{ Name = 'Square150x150Logo'; Base = 150 }
    @{ Name = 'Square44x44Logo';   Base = 44 }
)) {
    # The unqualified file, which is what the manifest names and what Windows falls back to when
    # no resources.pri is present.
    Save-Asset -Name "$($item.Name).png" -Bitmap (New-IconTile -Size $item.Base)

    foreach ($scale in $scales) {
        $size = [int][Math]::Round($item.Base * $scale / 100.0)
        Save-Asset -Name "$($item.Name).scale-$scale.png" -Bitmap (New-IconTile -Size $size)
    }
}

# Target sizes drive the taskbar, the Start list, and Alt-Tab. The unplated variants are used where
# Windows does not draw its own background plate; both are the same self-contained tile here, which
# is what makes the icon look right in either position.
foreach ($target in @(16, 24, 32, 48, 256)) {
    Save-Asset -Name "Square44x44Logo.targetsize-$target.png" -Bitmap (New-IconTile -Size $target)
    Save-Asset -Name "Square44x44Logo.targetsize-${target}_altform-unplated.png" `
        -Bitmap (New-IconTile -Size $target)
}

# The widget's attribution icon.
Save-Asset -Name 'WidgetIcon.png' -Bitmap (New-IconTile -Size 128)

# The picker screenshots. 300x304 with transparent rounded corners, per the documentation.
Save-Asset -Name 'WidgetScreenshot.png' -Bitmap (New-WidgetScreenshot -Theme 'Dark')
Save-Asset -Name 'WidgetScreenshotDark.png' -Bitmap (New-WidgetScreenshot -Theme 'Dark')
Save-Asset -Name 'WidgetScreenshotLight.png' -Bitmap (New-WidgetScreenshot -Theme 'Light')

Write-Output "Wrote $written asset(s), kept $kept existing."
Write-Output "Assets in $OutputDirectory"
Write-Output ''
Write-Output 'These are the accepted v1 assets. 415 Group branding was considered and declined, so'
Write-Output 'this script is the source of truth for the look: change the drawing code, not the PNGs.'
