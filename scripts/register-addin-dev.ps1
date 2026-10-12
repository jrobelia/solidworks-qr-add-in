#requires -Version 5
<#
.SYNOPSIS
  Machine-wide (elevated) registration of the QR add-in pointed at the dev
  deployment folder - the supported dev-machine setup.

.DESCRIPTION
  Registers EXACTLY ONE registration for this add-in, machine-wide, pointing
  at the dev deployment folder (default %LOCALAPPDATA%\SolidWorksQrAddIn):

    1. Removes any HKCU registration (register-addin-hkcu.ps1 route) so no
       per-user CodeBase can shadow the machine one.
    2. Deletes stale HKLM keys - e.g. an installer uninstall leaves
       HKLM\SOFTWARE\Classes\CLSID\{clsid} behind pointing at the deleted
       Program Files path.
    3. Runs elevated RegAsm /codebase on the dev-folder DLL, which rewrites
       HKLM Classes registration plus HKLM AddIns discovery and the
       AddInsStartup flag ([ComRegisterFunction] MachineWide plan) - so the
       add-in appears in Tools > Add-Ins and auto-loads the dev build.

  Run once after setup, and again after every installer test to restore the
  dev registration (the installer's uninstall leaves stale machine keys and
  strips HKCU - this script puts the dev folder back in charge). Idempotent:
  safe to re-run any time SolidWorks is closed.

  The day-to-day code-change loop is sync-dev-deploy.ps1 - registration never
  changes because the deployed path is stable.

.PARAMETER DeployDir
  Folder containing the deployed SolidWorksQrAddIn.dll that registration
  should point at. Default: %LOCALAPPDATA%\SolidWorksQrAddIn.

.EXAMPLE
  .\register-addin-dev.ps1      # relaunches elevated via UAC if needed
#>
param(
    [Parameter(Mandatory = $false)][string]$DeployDir = "$env:LOCALAPPDATA\SolidWorksQrAddIn",
    [switch]$Elevated   # internal: marks the UAC-relaunched instance
)

$ErrorActionPreference = 'Stop'

$Clsid  = '{DA932451-66B9-4092-9534-292AD913DEAC}'   # AddinIdentity.Clsid
$RegAsm = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'

# HKLM writes require admin - relaunch elevated with one UAC prompt. The
# elevated window stays open (see the finally below) so the result is visible.
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Elevation required - relaunching with a UAC prompt...'
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-Elevated', '-DeployDir', "`"$DeployDir`"")
    return
}

try {
    $dll = Join-Path (Resolve-Path $DeployDir).Path 'SolidWorksQrAddIn.dll'
    if (-not (Test-Path $dll)) { throw "Add-in DLL not found: $dll - run sync-dev-deploy.ps1 first." }

    # 1. Clear any HKCU registration so the per-user CodeBase cannot shadow
    #    the machine one (COM prefers HKCU when fully populated).
    $unreg = Join-Path $PSScriptRoot 'unregister-addin-hkcu.ps1'
    if (Test-Path $unreg) { & $unreg }

    # 2. Wipe stale machine keys - an installer uninstall leaves HKLM Classes
    #    entries pointing at the deleted Program Files path. Absent is fine.
    & reg.exe delete "HKLM\SOFTWARE\Classes\CLSID\$Clsid" /f | Out-Null
    & reg.exe delete "HKLM\SOFTWARE\SolidWorks\AddIns\$Clsid" /f | Out-Null

    # 3. Machine-wide registration -> dev folder. RegAsm invokes the add-in's
    #    [ComRegisterFunction] (HKLM AddIns discovery + HKCU AddInsStartup).
    & $RegAsm /codebase "$dll"
    if ($LASTEXITCODE -ne 0) { throw "RegAsm failed with exit code $LASTEXITCODE" }

    Write-Host "Registered $Clsid -> $dll"
    Write-Host 'Start SolidWorks: the add-in is listed in Tools > Add-Ins and auto-loads the dev build.'
} finally {
    if ($Elevated) { Read-Host 'Press Enter to close' }
}
