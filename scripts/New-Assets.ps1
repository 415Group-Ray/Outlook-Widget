#Requires -Version 7.0
<#
.SYNOPSIS
    Generates the package's icon and widget picker assets.

.DESCRIPTION
    Draws the app icon - a gradient envelope on transparency, with no background plate - at every
    size and scale Windows looks for, plus the widget picker screenshot at the exact size the
    Widgets Board documentation requires.

    WHAT IS SETTLED AND WHAT IS NOT

    Reviewed on 2026-07-28. The outcome was split, and the two halves must not be conflated:

      - The WIDGET PICKER SCREENSHOTS are accepted. They illustrate the approved medium card -
        unread count plus the newest messages - which the provider does not render yet, because
        authentication and Graph arrive in Phase 1 slice 2. Having been accepted, the screenshot is
        the design reference the medium card is expected to match, not merely a preview image.
        Sample senders and subjects are fictional and no mailbox data was involved.

      - The APP ICON is NOT accepted. Two designs were rejected: a white envelope on a filled blue
        tile, which looked dated beside the glyph-on-transparency icons around it in the widget
        picker, and a flatter transparent envelope. A third attempt, an open envelope with a card
        rising out of it, was also rejected and is not in this file. The icon is being designed
        separately and will be supplied later. What ships now is interim.

    415 Group branding remains declined; that part of the decision holds. The open question is the
    icon's design, not whether it carries a company mark.

    This script is still the source of truth for whatever it generates: the PNGs are build output
    that happens to be committed, so change the drawing code rather than editing an image. If the
    replacement icon arrives as finished artwork rather than as a design to draw, the icon functions
    here give way to a resize-and-emit step and the size and variant plumbing stays.

    The file was named New-PlaceholderAssets.ps1 while the output was flat single-colour squares.
    The name changed because that output was replaced, not because everything it emits is final.

    WHY THIS NOW USES GDI+, HAVING PREVIOUSLY ARGUED AGAINST AN IMAGING LIBRARY

    The earlier version hand-wrote PNG chunks and explained that System.Drawing.Common is a
    separate package on modern .NET and not worth a dependency "purely to draw a square". That
    reasoning was sound for a square and wrong for artwork. It also conflated two different
    things: a dependency of the PRODUCT, which would have to be justified, and a capability of a
    build-time script, which costs nothing. System.Drawing is available to PowerShell 7 on
    Windows, this repository is Windows-only already, and nothing here ships in the package -
    only the PNGs do. Directory.Packages.props is untouched.

    ASSETS PRODUCED

    App icon, as a glyph on transparency so it matches its neighbours on every surface and does not
    fight the plate Windows draws on the taskbar:
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

# The envelope is the icon. There is no background tile.
#
# The first version drew a white envelope on a filled blue rounded square, which looked dated beside
# its neighbours: in the widget picker's provider list, and in Windows 11's own Start and taskbar
# icons, the convention is a coloured glyph on transparency with no plate. A filled tile also fought
# the taskbar's own plate and read as a missing icon.
#
# Colours are chosen to survive on BOTH light and dark surfaces, which rules out white as a primary
# fill: the icon sits on a dark taskbar, a light Start flyout, and either picker theme. Mid-blue
# against amber does that; white against blue did not.
$bodyTop     = [System.Drawing.Color]::FromArgb(255, 0x4A, 0x9B, 0xF0)
$bodyBottom  = [System.Drawing.Color]::FromArgb(255, 0x18, 0x55, 0xAE)
$flapTop     = [System.Drawing.Color]::FromArgb(255, 0x8F, 0xC6, 0xFA)
$flapBottom  = [System.Drawing.Color]::FromArgb(255, 0x52, 0xA1, 0xF2)
$seam        = [System.Drawing.Color]::FromArgb(70,  0x0B, 0x33, 0x6B)
$badgeTop    = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xCB, 0x45)
$badgeBottom = [System.Drawing.Color]::FromArgb(255, 0xF5, 0x9E, 0x0B)

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

function New-GradientBrush {
    param(
        [Parameter(Mandatory)][double]$Top,
        [Parameter(Mandatory)][double]$Bottom,
        [Parameter(Mandatory)][System.Drawing.Color]$From,
        [Parameter(Mandatory)][System.Drawing.Color]$To
    )

    # A one-pixel-tall gradient produces a divide-by-zero inside GDI+, which happens at 16px where
    # the flap is genuinely about that tall.
    $height = [Math]::Max($Bottom - $Top, 1.0)

    return [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, [single]$Top),
        [System.Drawing.PointF]::new(0, [single]($Top + $height)),
        $From, $To)
}

function New-AppIcon {
    <#
        The app icon: a gradient envelope on transparency, with no background plate.

        WHY NO TILE. The picker's provider list, the Windows 11 Start list, and the taskbar all
        surround this icon with glyph-on-transparency neighbours. A filled rounded square reads as
        dated next to them, and on the taskbar it fights the plate Windows draws itself. The first
        version of this script drew exactly that and it looked wrong in place.

        Proportional to $Size throughout, so one definition serves 16px and 600px. Detail is dropped
        rather than scaled below 32px: the taskbar size is the one seen most often, and a gradient
        seam and an unread badge at 16px are noise that costs legibility.
    #>
    param([Parameter(Mandatory)][int]$Size)

    $canvas = New-Canvas -Width $Size -Height $Size
    $g = $canvas.Graphics

    $detailed = $Size -ge 32

    # The envelope fills most of the canvas. Windows already pads icons on every surface that shows
    # them, so building in more padding just makes the icon look smaller than its neighbours.
    $left = $Size * 0.06
    $right = $Size * 0.94
    $width = $right - $left

    # A badge needs room above the envelope, so the envelope sits lower when one is drawn.
    $bodyTopY = if ($detailed) { $Size * 0.28 } else { $Size * 0.24 }
    $bodyBottomY = $Size * 0.80
    $bodyHeight = $bodyBottomY - $bodyTopY

    # Generous rounding, which is what separates a modern envelope from a rectangle with a line in
    # it. Clamped at small sizes or the corners eat the whole shape.
    $radius = if ($detailed) { $Size * 0.10 } else { $Size * 0.07 }

    $body = New-RoundedPath -X $left -Y $bodyTopY -Width $width -Height $bodyHeight -Radius $radius
    $bodyBrush = New-GradientBrush -Top $bodyTopY -Bottom $bodyBottomY -From $bodyTop -To $bodyBottom
    $g.FillPath($bodyBrush, $body)
    $bodyBrush.Dispose()

    # The flap: a filled triangle with its apex pointing down, clipped to the body so it inherits
    # the body's rounded top corners. Filled rather than stroked - a stroke reads as a line drawn on
    # a box, while a fill reads as an envelope, and the lighter gradient is what gives it depth.
    $g.SetClip($body)

    $flapApexY = $bodyTopY + $bodyHeight * 0.62

    [System.Drawing.PointF[]]$flapPoints = @(
        [System.Drawing.PointF]::new([single]$left, [single]$bodyTopY)
        [System.Drawing.PointF]::new([single]($left + $width / 2), [single]$flapApexY)
        [System.Drawing.PointF]::new([single]$right, [single]$bodyTopY)
    )

    $flapBrush = New-GradientBrush -Top $bodyTopY -Bottom $flapApexY -From $flapTop -To $flapBottom
    $g.FillPolygon($flapBrush, $flapPoints)
    $flapBrush.Dispose()

    # A translucent seam along the flap's underside. This is the whole depth cue: without it the
    # flap and body read as two flat triangles meeting, with it the flap sits on top of the body.
    if ($detailed) {
        $seamPen = [System.Drawing.Pen]::new($seam, [single][Math]::Max($Size * 0.022, 1.0))
        $seamPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawLines($seamPen, $flapPoints)
        $seamPen.Dispose()
    }

    $g.ResetClip()
    $body.Dispose()

    # The unread badge, in the corner the envelope leaves free. Amber against blue needs no
    # separating ring, and adding one would show as a halo on the transparent side.
    if ($detailed) {
        $badgeSize = $Size * 0.34
        $badgeX = $Size - $badgeSize - $Size * 0.03
        $badgeY = $Size * 0.03

        $badgeBrush = New-GradientBrush -Top $badgeY -Bottom ($badgeY + $badgeSize) `
            -From $badgeTop -To $badgeBottom
        $g.FillEllipse($badgeBrush, [single]$badgeX, [single]$badgeY, [single]$badgeSize, [single]$badgeSize)
        $badgeBrush.Dispose()
    }

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
    $accentBrush = [System.Drawing.SolidBrush]::new($badgeBottom)

    $fontFamily = 'Segoe UI'
    $titleFont = [System.Drawing.Font]::new($fontFamily, 9.5, [System.Drawing.FontStyle]::Regular)
    $countFont = [System.Drawing.Font]::new($fontFamily, 30, [System.Drawing.FontStyle]::Bold)
    $countLabelFont = [System.Drawing.Font]::new($fontFamily, 9.5, [System.Drawing.FontStyle]::Regular)
    $senderFont = [System.Drawing.Font]::new($fontFamily, 9, [System.Drawing.FontStyle]::Bold)
    $subjectFont = [System.Drawing.Font]::new($fontFamily, 8.5, [System.Drawing.FontStyle]::Regular)
    $timeFont = [System.Drawing.Font]::new($fontFamily, 8, [System.Drawing.FontStyle]::Regular)

    $pad = 16

    # Attribution row: the app icon and name, as the Board itself draws above a widget.
    $icon = New-AppIcon -Size 18
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
    Save-Asset -Name "$($item.Name).png" -Bitmap (New-AppIcon -Size $item.Base)

    foreach ($scale in $scales) {
        $size = [int][Math]::Round($item.Base * $scale / 100.0)
        Save-Asset -Name "$($item.Name).scale-$scale.png" -Bitmap (New-AppIcon -Size $size)
    }
}

# Target sizes drive the taskbar, the Start list, and Alt-Tab. The unplated variants are used where
# Windows does not draw its own background plate; both are the same self-contained tile here, which
# is what makes the icon look right in either position.
foreach ($target in @(16, 24, 32, 48, 256)) {
    Save-Asset -Name "Square44x44Logo.targetsize-$target.png" -Bitmap (New-AppIcon -Size $target)
    Save-Asset -Name "Square44x44Logo.targetsize-${target}_altform-unplated.png" `
        -Bitmap (New-AppIcon -Size $target)
}

# The widget's attribution icon.
Save-Asset -Name 'WidgetIcon.png' -Bitmap (New-AppIcon -Size 128)

# The picker screenshots. 300x304 with transparent rounded corners, per the documentation.
Save-Asset -Name 'WidgetScreenshot.png' -Bitmap (New-WidgetScreenshot -Theme 'Dark')
Save-Asset -Name 'WidgetScreenshotDark.png' -Bitmap (New-WidgetScreenshot -Theme 'Dark')
Save-Asset -Name 'WidgetScreenshotLight.png' -Bitmap (New-WidgetScreenshot -Theme 'Light')

Write-Output "Wrote $written asset(s), kept $kept existing."
Write-Output "Assets in $OutputDirectory"
Write-Output ''
Write-Output 'Picker screenshots: accepted. App icon: INTERIM - not accepted, being designed separately.'
Write-Output '415 Group branding remains declined; the open question is the design.'
Write-Output 'This script is the source of truth for what it draws: change the code, not the PNGs.'
