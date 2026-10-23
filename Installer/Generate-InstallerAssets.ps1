# Generate-InstallerAssets.ps1  Regenerates every checked-in raster asset
# Setup.iss references from the add-in's own QrIconGlyph — the ico and
# small images reuse RenderPng's 17x17 icon motif (identical to runtime);
# the left banner draws the real repo-URL QR matrix, large enough to scan —
# so installer chrome, the .ico, and product icons never drift.
#
#   qr-addin.ico                       SetupIconFile + UninstallDisplayIcon +
#                                    the DLL ApplicationIcon (multi-size ICO)
#   wizard-image-{100,150,200,250}.png   left banner, aspect 164:314
#   wizard-small-{100,150,200,250}.png   upper-right square image
#
# The wizard pixel sizes are Inno's published per-DPI image areas for
# WizardImageFile / WizardSmallImageFile; Setup.iss lists each set
# comma-separated and Inno picks the best match per display scale.
#
# Needs a built SolidWorksQrAddIn.Core.dll (probes the deploy folder, then
# Release/Debug bin output - run scripts\pack-deploy.ps1 or build first).
# Re-run whenever QrIconGlyph (payload/ECC) or the layout changes.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

Add-Type -AssemblyName System.Drawing

$core = @(
    "$repoRoot\deploy\SolidWorksQrAddIn\SolidWorksQrAddIn.Core.dll",
    "$repoRoot\src\SolidWorksQrAddIn.Core\bin\x64\Release\net48\SolidWorksQrAddIn.Core.dll",
    "$repoRoot\src\SolidWorksQrAddIn.Core\bin\x64\Debug\net48\SolidWorksQrAddIn.Core.dll"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $core) {
    throw "SolidWorksQrAddIn.Core.dll not found - run scripts\pack-deploy.ps1 or build the solution first."
}
# Load QRCoder first: QrIconGlyph.Build() resolves it at JIT time from the
# same folder as the Core assembly (LoadFrom context).
[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $core) 'QRCoder.dll')) | Out-Null
$coreAsm = [Reflection.Assembly]::LoadFrom($core)
$glyph = $coreAsm.GetType('SolidWorksQrAddIn.Core.Icons.QrIconGlyph')
$icons = $coreAsm.GetType('SolidWorksQrAddIn.Core.Icons.QrToolbarIcons')
$modules = $glyph::Size

$black = [Drawing.Color]::Black
$white = [Drawing.Color]::White

# ---------------- qr-addin.ico ----------------
# Frames reuse QrToolbarIcons.RenderPng - the identical white-quiet-zone
# render the runtime extracts for SolidWorks - wrapped as PNG-encoded ICO
# entries (same encoding the previous hand-built ico used; Vista+ native).
function Write-Ico {
    param([int[]]$sizes, [string]$path)
    $pngs = $sizes | ForEach-Object { ,$icons::RenderPng($_) }
    $ms = New-Object IO.MemoryStream
    $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = [byte]($sizes[$i] -band 0xFF)   # 256 encodes as 0
        $bw.Write($dim); $bw.Write($dim)       # width, height
        $bw.Write([byte]0); $bw.Write([byte]0) # palette, reserved
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($png in $pngs) { $bw.Write($png) }
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
    Write-Host "  $([IO.Path]::GetFileName($path))  sizes: $($sizes -join ', ')"
}

# ---------------- wizard images ----------------
function New-GlyphBitmap {
    # QR modules as integer cells -> perfectly uniform, crisp at any size.
    param([int]$cellPx)
    $side = $cellPx * $modules
    $bmp = New-Object Drawing.Bitmap $side, $side, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([Drawing.Color]::Transparent)
        $brush = New-Object Drawing.SolidBrush $black
        for ($r = 0; $r -lt $modules; $r++) {
            for ($c = 0; $c -lt $modules; $c++) {
                if ($glyph::IsDark($r, $c)) {
                    $g.FillRectangle($brush, $c * $cellPx, $r * $cellPx, $cellPx, $cellPx)
                }
            }
        }
    } finally { $g.Dispose() }
    $bmp
}

function New-Banner {
    # Opaque white panel (164:314 aspect) with the glyph centered - opaque so
    # the strip reads the same on light and dark wizard chrome.
    param([int]$w, [int]$h, [string]$path)
    $cell = [Math]::Max(1, [int]($w * 0.75 / $modules))
    $glyphBmp = New-GlyphBitmap $cell
    $bmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear($white)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $side = $glyphBmp.Width
        $g.DrawImageUnscaled($glyphBmp, [int](($w - $side) / 2), [int](($h - $side) / 2))
        $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        Write-Host "  $([IO.Path]::GetFileName($path))  ${w}x${h} (glyph ${side}px)"
    } finally { $g.Dispose(); $glyphBmp.Dispose(); $bmp.Dispose() }
}

function New-SmallImage {
    # Transparent canvas with a white badge square - the black modules stay
    # legible on both light and dark wizard headers (dark mode is automatic
    # in Inno 6.6+). The glyph comes from RenderPng, identical to runtime.
    param([int]$s, [string]$path)
    $inset = [Math]::Max(1, [int]($s * 0.05))
    $tile = $s - 2 * $inset
    $glyphBmp = New-Object Drawing.Bitmap (New-Object IO.MemoryStream (,$icons::RenderPng($tile)))
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear([Drawing.Color]::Transparent)
        $g.FillRectangle((New-Object Drawing.SolidBrush $white), $inset, $inset, $tile, $tile)
        $g.DrawImageUnscaled($glyphBmp, $inset, $inset)
        $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        Write-Host "  $([IO.Path]::GetFileName($path))  ${s}x${s} (tile ${tile}px)"
    } finally { $g.Dispose(); $glyphBmp.Dispose(); $bmp.Dispose() }
}

Write-Host "Rendering installer assets from $core (glyph ${modules}x$modules)"
Write-Ico @(16, 24, 32, 48, 64, 128, 256) "$PSScriptRoot\qr-addin.ico"
# Inno image areas per DPI (docs: WizardImageFile keeps 164:314;
# WizardSmallImageFile is square). Name suffix = display scale.
@(
    @{ Pct = 100; W = 202; H = 386 },
    @{ Pct = 150; W = 336; H = 643 },
    @{ Pct = 200; W = 430; H = 824 },
    @{ Pct = 250; W = 534; H = 1022 }
) | ForEach-Object {
    New-Banner $_.W $_.H "$PSScriptRoot\wizard-image-$($_.Pct).png"
}
@(
    @{ Pct = 100; S = 58 },
    @{ Pct = 150; S = 97 },
    @{ Pct = 200; S = 124 },
    @{ Pct = 250; S = 159 }
) | ForEach-Object {
    New-SmallImage $_.S "$PSScriptRoot\wizard-small-$($_.Pct).png"
}
Write-Host "Done."
