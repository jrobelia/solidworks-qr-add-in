#requires -Version 5
<#
.SYNOPSIS
  Removes the per-user (HKCU) dev registration written by register-dev-hkcu.ps1.

.DESCRIPTION
  Deletes ONLY this add-in's keys:
    HKCU\SOFTWARE\Classes\CLSID\{clsid}
    HKCU\SOFTWARE\Classes\SolidWorksQrAddIn.SwAddin
    HKCU\SOFTWARE\SolidWorks\AddIns\{clsid}
    HKCU\SOFTWARE\SolidWorks\AddInsStartup\{clsid}

  Never touches the InvenTree add-in ({A1B2C3D4-...}) or any other add-in.
#>
param()

$ErrorActionPreference = 'Stop'

$Clsid  = '{DA932451-66B9-4092-9534-292AD913DEAC}'   # AddinIdentity.Clsid
$ProgId = 'SolidWorksQrAddIn.SwAddin'

foreach ($key in @(
    "SOFTWARE\Classes\CLSID\$Clsid",
    "SOFTWARE\Classes\$ProgId",
    "SOFTWARE\SolidWorks\AddIns\$Clsid",
    "SOFTWARE\SolidWorks\AddInsStartup\$Clsid")) {
    & reg.exe delete "HKCU\$key" /f | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "deleted HKCU\$key" }
    else { Write-Host "absent  HKCU\$key" }
}
