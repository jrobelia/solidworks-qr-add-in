#requires -Version 5
<#
.SYNOPSIS
  The day-to-day dev loop step: pack the deployment folder and mirror it into
  the registered dev folder. No admin needed.

.DESCRIPTION
  Equivalent of:

    scripts\pack-deploy.ps1
    robocopy deploy\SolidWorksQrAddIn %LOCALAPPDATA%\SolidWorksQrAddIn /MIR

  Refuses to run while SolidWorks is open - the registered DLL is file-mapped
  into SLDWORKS.exe and cannot be overwritten. Close SolidWorks, run this,
  restart SolidWorks.

  Registration (register-addin-dev.ps1 or register-addin-hkcu.ps1) never needs
  re-running after this - the CodeBase path is stable, only the bytes change.

.PARAMETER DeployDir
  Registered dev folder. Default: %LOCALAPPDATA%\SolidWorksQrAddIn.

.PARAMETER SkipBuild
  Passed through to pack-deploy.ps1 - reuse the existing Release build.

.EXAMPLE
  .\sync-dev-deploy.ps1
#>
param(
    [Parameter(Mandatory = $false)][string]$DeployDir = "$env:LOCALAPPDATA\SolidWorksQrAddIn",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) {
    throw 'SolidWorks is running - the registered DLL is locked. Close SolidWorks first.'
}

$packArgs = @{}
if ($SkipBuild) { $packArgs['SkipBuild'] = $true }
& (Join-Path $PSScriptRoot 'pack-deploy.ps1') @packArgs

robocopy (Join-Path $repoRoot 'deploy\SolidWorksQrAddIn') $DeployDir /MIR
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }
$global:LASTEXITCODE = 0   # robocopy 0-7 = success; don't leak it as the script's exit code

Write-Host "`nDeployed to $DeployDir - start SolidWorks to load the new build."
