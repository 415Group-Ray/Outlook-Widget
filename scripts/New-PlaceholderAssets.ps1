#Requires -Version 7.0
<#
.SYNOPSIS
    Generates the placeholder PNG assets the package manifest requires.

.DESCRIPTION
    An MSIX manifest references logo files by path, and makeappx fails validation when they are
    missing. Phase 0 needs a package that installs, not a package that looks good, so these are
    deliberately plain solid-colour images with the correct dimensions and nothing else.

    The PNGs are written by hand rather than with an imaging library. That is not cleverness for
    its own sake: this machine has no image tooling, System.Drawing.Common is a separate NuGet
    package on modern .NET, and adding a dependency to Directory.Packages.props purely to draw
    a square would be a real cost for a placeholder. A solid-colour RGBA PNG is a small, fully
    specified format, so it is cheaper to emit directly.

    Replace these with real artwork in Phase 2, which owns the widget experience.

.PARAMETER OutputDirectory
    Where to write the assets. Defaults to the package project's Assets folder.

.PARAMETER Force
    Overwrite existing files. Without this, existing assets are left alone so real artwork is
    never silently replaced by placeholders.

.EXAMPLE
    pwsh -File scripts/New-PlaceholderAssets.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\OutlookWidget.Package\Assets'
}

# Outlook's familiar blue, so a placeholder tile is at least recognisable in the Start menu
# while Phase 0 work is in progress.
$colour = @(0x00, 0x5A, 0x9E, 0xFF)

# CRC-32, as PNG requires for every chunk.
#
# All CRC arithmetic is done in UInt64 with an explicit 32-bit mask after every step.
# PowerShell's -bxor and -shr promote operands to signed types, so accumulating directly in
# UInt32 lets an intermediate result go negative and the subsequent cast then throws. Masking
# a wider type is the only reliable way to express unsigned 32-bit arithmetic here.
$mask = [uint64]4294967295

$crcTable = [uint32[]]::new(256)
for ($n = 0; $n -lt 256; $n++) {
    $c = [uint64]$n
    for ($k = 0; $k -lt 8; $k++) {
        if ($c -band 1) {
            $c = ([uint64]3988292384 -bxor ($c -shr 1)) -band $mask
        }
        else {
            $c = ($c -shr 1) -band $mask
        }
    }
    $crcTable[$n] = [uint32]$c
}

function Get-Crc32 {
    param([AllowEmptyCollection()][byte[]]$Bytes)

    $localMask = [uint64]4294967295
    $c = [uint64]4294967295

    foreach ($b in $Bytes) {
        $index = [int](($c -bxor [uint64]$b) -band 0xFF)
        $c = ([uint64]$crcTable[$index] -bxor ($c -shr 8)) -band $localMask
    }

    return [uint32](($c -bxor $localMask) -band $localMask)
}

function Get-BigEndianBytes {
    param([uint32]$Value)

    $bytes = [System.BitConverter]::GetBytes($Value)
    if ([System.BitConverter]::IsLittleEndian) {
        [array]::Reverse($bytes)
    }
    return $bytes
}

function New-PngChunk {
    param(
        [Parameter(Mandatory)][string]$Type,
        [Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Data
    )

    $typeBytes = [System.Text.Encoding]::ASCII.GetBytes($Type)
    $payload = $typeBytes + $Data

    return (Get-BigEndianBytes ([uint32]$Data.Length)) + $payload + (Get-BigEndianBytes (Get-Crc32 $payload))
}

function Get-ZlibStream {
    param([Parameter(Mandatory)][byte[]]$Raw)

    # PNG's IDAT carries a zlib stream, which is a two-byte header, a raw deflate body, and a
    # big-endian Adler-32 of the uncompressed data. DeflateStream produces only the body.
    $memory = [System.IO.MemoryStream]::new()
    $deflate = [System.IO.Compression.DeflateStream]::new(
        $memory, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $deflate.Write($Raw, 0, $Raw.Length)
    $deflate.Dispose()
    $body = $memory.ToArray()
    $memory.Dispose()

    $a = [uint64]1
    $b = [uint64]0
    foreach ($byte in $Raw) {
        $a = ($a + $byte) % 65521
        $b = ($b + $a) % 65521
    }
    $adler = [uint32]((($b -shl 16) -bor $a) -band [uint64]4294967295)

    # 0x78 0x01: deflate, 32K window, no preset dictionary.
    return [byte[]]@(0x78, 0x01) + $body + (Get-BigEndianBytes $adler)
}

function New-SolidPng {
    param(
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height,
        [Parameter(Mandatory)][string]$Path
    )

    # Each scanline is prefixed with a filter byte of 0, meaning no filtering.
    $stride = ($Width * 4) + 1
    $raw = [byte[]]::new($stride * $Height)

    for ($y = 0; $y -lt $Height; $y++) {
        $rowStart = $y * $stride
        $raw[$rowStart] = 0

        for ($x = 0; $x -lt $Width; $x++) {
            $offset = $rowStart + 1 + ($x * 4)
            $raw[$offset]     = $colour[0]
            $raw[$offset + 1] = $colour[1]
            $raw[$offset + 2] = $colour[2]
            $raw[$offset + 3] = $colour[3]
        }
    }

    $ihdr = (Get-BigEndianBytes ([uint32]$Width)) +
            (Get-BigEndianBytes ([uint32]$Height)) +
            [byte[]]@(8, 6, 0, 0, 0)   # 8-bit depth, RGBA, deflate, no filter, non-interlaced

    $bytes = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) +
             (New-PngChunk -Type 'IHDR' -Data $ihdr) +
             (New-PngChunk -Type 'IDAT' -Data (Get-ZlibStream -Raw $raw)) +
             (New-PngChunk -Type 'IEND' -Data ([byte[]]@()))

    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

# The set the manifest references. Sizes are the standard MSIX asset dimensions.
$assets = @(
    @{ Name = 'StoreLogo.png';        Width = 50;  Height = 50 }
    @{ Name = 'Square44x44Logo.png';  Width = 44;  Height = 44 }
    @{ Name = 'Square150x150Logo.png'; Width = 150; Height = 150 }
    @{ Name = 'WidgetIcon.png';       Width = 128; Height = 128 }
    @{ Name = 'WidgetScreenshot.png'; Width = 480; Height = 480 }
)

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

foreach ($asset in $assets) {
    $path = Join-Path $OutputDirectory $asset.Name

    if ((Test-Path -LiteralPath $path) -and -not $Force) {
        Write-Output "Kept existing $($asset.Name)"
        continue
    }

    New-SolidPng -Width $asset.Width -Height $asset.Height -Path $path
    Write-Output "Wrote $($asset.Name) ($($asset.Width)x$($asset.Height))"
}

Write-Output ''
Write-Output "Assets written to $OutputDirectory"
Write-Output 'These are placeholders. Phase 2 owns real artwork.'
