#requires -Version 5
<#
.SYNOPSIS
  Packs the SolidWorks QR add-in deployment folder (ticket 7).

.DESCRIPTION
  Builds the add-in in Release|x64 (unless -SkipBuild), then assembles the
  deployment folder described by SolidWorksQrAddIn.Core's DeploymentPlan:

    <OutputDir>\
      SolidWorksQrAddIn.dll        own assemblies +
      SolidWorksQrAddIn.Core.dll
      QRCoder.dll                  pinned dependency (MIT)
      Clipper2Lib.dll              pinned dependency (BSL-1.0)
      THIRD-PARTY-NOTICES.md       notices + pinned versions + selected assets
      DEPLOYMENT.md                install / register / unregister procedure
      ACCEPTANCE.md                live acceptance checklist
      register-addin-hkcu.ps1      per-user install (no admin)
      unregister-addin-hkcu.ps1
      licenses\                    full license texts
        QRCoder-LICENSE.txt
        Clipper2-LICENSE.txt

  DeploymentPlan is read by reflection from the freshly built Core.dll so the
  script can never drift from the tested manifest. After copying it runs
  DeploymentPlan.VerifyFolder and fails if any problem is reported.

  The output folder is STAGING, not the registered location - install by
  copying it somewhere stable (e.g. %LOCALAPPDATA%\SolidWorksQrAddIn) and
  running register-addin-hkcu.ps1 there. Packing never touches a registered
  folder; see docs/DEPLOYMENT.md.

.PARAMETER OutputDir
  Where the deployment folder is written. Default: <repo>\deploy\SolidWorksQrAddIn.

.PARAMETER SkipBuild
  Use the existing src\SolidWorksQrAddIn\bin\x64\Release\net48 output without
  rebuilding.

.EXAMPLE
  .\pack-deploy.ps1
  .\pack-deploy.ps1 -OutputDir "$env:LOCALAPPDATA\SolidWorksQrAddIn"
#>
param(
    [Parameter(Mandatory = $false)][string]$OutputDir,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path $scriptDir -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'deploy\SolidWorksQrAddIn' }

# --- build (release, x64) ----------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "== build: dotnet build src\SolidWorksQrAddIn -c Release =="
    & dotnet build (Join-Path $repo 'src\SolidWorksQrAddIn\SolidWorksQrAddIn.csproj') -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
}

$binDir = Join-Path $repo 'src\SolidWorksQrAddIn\bin\x64\Release\net48'
$coreDll = Join-Path $binDir 'SolidWorksQrAddIn.Core.dll'
if (-not (Test-Path $coreDll)) {
    throw "Build output not found: $coreDll - run without -SkipBuild."
}

# --- read the tested manifest from Core --------------------------------------
$core = [Reflection.Assembly]::LoadFrom($coreDll)
$plan = $core.GetType('SolidWorksQrAddIn.Core.Deployment.DeploymentPlan')
if ($plan -eq $null) { throw 'DeploymentPlan type not found in SolidWorksQrAddIn.Core.dll' }
$binaries = @($plan.GetProperty('RuntimeBinaries').GetValue($null))
$shipped  = @($plan.GetProperty('ShippedFiles').GetValue($null))

# --- assemble the folder ------------------------------------------------------
Write-Host "== pack -> $OutputDir =="
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($name in $binaries) {
    $src = Join-Path $binDir $name
    if (-not (Test-Path $src)) { throw "expected build output missing: $src" }
    Copy-Item $src (Join-Path $OutputDir $name)
    Write-Host "  + $name"
}
foreach ($file in $shipped) {
    $src = Join-Path $repo ($file.Source -replace '/', '\')
    if (-not (Test-Path $src)) { throw "planned source missing from repo: $($file.Source)" }
    $dst = Join-Path $OutputDir ($file.DeployedPath -replace '/', '\')
    New-Item -ItemType Directory -Path (Split-Path $dst -Parent) -Force | Out-Null
    Copy-Item $src $dst
    Write-Host "  + $($file.DeployedPath)"
}

# --- verify the result against the plan ---------------------------------------
$problems = @($plan.GetMethod('VerifyFolder').Invoke($null, @($OutputDir)))
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "  PROBLEM: $_" }
    throw ("deployment folder failed verification: {0} problem(s)" -f $problems.Count)
}
Write-Host "== verify: clean =="

# --- evidence -----------------------------------------------------------------
Get-ChildItem $OutputDir -Recurse -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16)
    Write-Host ("  {0,-45} {1,8} bytes  sha256:{2}..." -f
        $_.FullName.Substring($OutputDir.Length + 1), $_.Length, $hash)
}
Write-Host "Packed -> $OutputDir"
Write-Host "Install: copy this folder somewhere stable, then run register-addin-hkcu.ps1 inside it (see DEPLOYMENT.md)."
