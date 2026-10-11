# Package.ps1  Run this (no admin needed) to build the installer exe.
# Output: Installer\SolidWorksQrAddIn-<version>-Setup.exe
# Version is derived from the nearest git tag (e.g. v1.0.0, or
# v1.0.0-3-gabcd123 if not on a tag); falls back to the commit hash.
#
# Steps: scripts\pack-deploy.ps1 (locked-mode restore + Release build +
# verified deploy folder) -> ISCC.exe compiles Setup.iss against that folder.
# When Inno Setup is not installed the script still packs and warns; compile
# by hand with:  iscc /DAppVersion=<version> Installer\Setup.iss

$repoRoot = Split-Path $PSScriptRoot -Parent

$version = & git -C $repoRoot describe --tags --always 2>$null
if (-not $version) { $version = "unknown" }

Write-Host "Packing deployment folder (Release build)..." -ForegroundColor Cyan
& "$repoRoot\scripts\pack-deploy.ps1"
if ($LASTEXITCODE -ne 0) { Write-Host "pack-deploy failed." -ForegroundColor Red; exit 1 }

$iscc = & "$PSScriptRoot\Resolve-ISCC.ps1"

$versionNumber = $version.TrimStart('v')
$exePath = "$repoRoot\Installer\SolidWorksQrAddIn-$versionNumber-Setup.exe"
if ($iscc) {
    Write-Host "Building Setup.exe with Inno Setup..." -ForegroundColor Cyan
    & $iscc "/DAppVersion=$versionNumber" "$PSScriptRoot\Setup.iss"
    if ($LASTEXITCODE -ne 0) { Write-Host "ISCC failed." -ForegroundColor Red; exit 1 }
} else {
    Write-Host "WARNING: ISCC.exe not found - Setup.exe skipped." -ForegroundColor Yellow
    Write-Host "  Install Inno Setup 6 (or run Ensure-ISCC.ps1), then:"
    Write-Host "  iscc /DAppVersion=$versionNumber Installer\Setup.iss"
}

Write-Host ""
Write-Host "Done! Version $version" -ForegroundColor Green
if (Test-Path $exePath) { Write-Host "  $exePath" }
