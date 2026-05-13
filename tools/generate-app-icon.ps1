# Renders the AzureTray app icon at the standard Windows icon sizes
# (16/32/48/64/128/256 px) and packs them into a single multi-resolution
# .ico file at src/AzureTray/AzureTray.ico.
#
# Visual matches the dynamic tray-badge icon (Azure-blue rounded square
# with white "Az" glyph) so the EXE/titlebar identity is the same one
# the user already sees in the system tray.
#
# Re-run after tweaking the colour/glyph/radius to regenerate.

#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not $OutputPath) {
    $OutputPath = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'src/AzureTray/AzureTray.ico'
}

$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = New-Object System.Collections.Generic.List[object]

# Azure-blue (#0066CC) matches BadgeState.Normal in TrayIcon.CreateIcon.
$bg = [System.Drawing.Color]::FromArgb(0xFF, 0x00, 0x66, 0xCC)

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap(
        $size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        # Rounded square. Radius ~1/6 of edge so the 16 px shape stays
        # visibly rounded but doesn't go fully circular.
        $radius = [int][math]::Max(2, [math]::Round($size / 6.0))
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        try {
            $d = $radius * 2
            $path.AddArc(0, 0, $d, $d, 180, 90)
            $path.AddArc($size - $d, 0, $d, $d, 270, 90)
            $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
            $path.AddArc(0, $size - $d, $d, $d, 90, 90)
            $path.CloseFigure()

            $brush = New-Object System.Drawing.SolidBrush($bg)
            try { $g.FillPath($brush, $path) } finally { $brush.Dispose() }
        } finally {
            $path.Dispose()
        }

        # "Az" glyph in white. Font size ~46 % of the icon edge centres the
        # text vertically within the rounded square; 16 px falls back to 7.5 pt
        # so the tray-icon rendering stays pixel-identical at small sizes.
        $fontSize = if ($size -le 16) { 7.5 } else { [math]::Round($size * 0.46, 1) }
        $unit = if ($size -le 16) {
            [System.Drawing.GraphicsUnit]::Point
        } else {
            [System.Drawing.GraphicsUnit]::Pixel
        }
        $font = New-Object System.Drawing.Font(
            'Segoe UI', [single]$fontSize, [System.Drawing.FontStyle]::Bold, $unit)
        try {
            $glyph = 'Az'
            $glyphSize = $g.MeasureString($glyph, $font)
            $x = [single](($size - $glyphSize.Width) / 2.0 + 0.5)
            $y = [single](($size - $glyphSize.Height) / 2.0 - 0.5)
            $g.DrawString($glyph, $font, [System.Drawing.Brushes]::White, $x, $y)
        } finally {
            $font.Dispose()
        }
    } finally {
        $g.Dispose()
    }

    $ms = New-Object System.IO.MemoryStream
    try {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs.Add([pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() })
    } finally {
        $ms.Dispose()
        $bmp.Dispose()
    }
}

# Assemble the ICO container. Each entry stores a PNG payload — modern
# Windows reads PNG-encoded ICO entries natively, and PNG gives clean
# alpha at every size without the per-image AND-mask pain of legacy BMP.
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
try {
    # ICONDIR
    $bw.Write([uint16]0)             # Reserved
    $bw.Write([uint16]1)             # Type = 1 (ICO)
    $bw.Write([uint16]$pngs.Count)   # Image count

    $offset = 6 + ($pngs.Count * 16)
    foreach ($p in $pngs) {
        $w = if ($p.Size -ge 256) { 0 } else { [byte]$p.Size }
        $bw.Write([byte]$w)              # Width  (0 = 256)
        $bw.Write([byte]$w)              # Height (0 = 256)
        $bw.Write([byte]0)               # ColorCount (0 = >256 colours)
        $bw.Write([byte]0)               # Reserved
        $bw.Write([uint16]1)             # Planes
        $bw.Write([uint16]32)            # Bits per pixel
        $bw.Write([uint32]$p.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $p.Bytes.Length
    }

    foreach ($p in $pngs) { $bw.Write($p.Bytes) }
    $bw.Flush()

    $outDir = Split-Path -Parent $OutputPath
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
    [System.IO.File]::WriteAllBytes($OutputPath, $ico.ToArray())
} finally {
    $bw.Dispose()
    $ico.Dispose()
}

$len = (Get-Item $OutputPath).Length
Write-Host "Wrote $OutputPath ($($pngs.Count) sizes, $len bytes)" -ForegroundColor Green
