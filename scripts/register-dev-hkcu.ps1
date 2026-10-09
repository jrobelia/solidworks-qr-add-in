#requires -Version 5
<#
.SYNOPSIS
  Per-user (HKCU-only, no admin) dev registration of the SolidWorks QR add-in.

.DESCRIPTION
  Writes exactly the entries SolidWorks needs, all under HKCU — never HKLM:

    HKCU\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32   (mscoree.dll + CodeBase)
    HKCU\SOFTWARE\Classes\CLSID\{clsid}\ProgId         (-> SolidWorksQrAddIn.SwAddin)
    HKCU\SOFTWARE\Classes\SolidWorksQrAddIn.SwAddin    (+ \CLSID subkey)
    HKCU\SOFTWARE\SolidWorks\AddIns\{clsid}            (default=1, Title, Description)
    HKCU\SOFTWARE\SolidWorks\AddInsStartup\{clsid}     (default=1)

  Equivalent in effect to `RegAsm /codebase` + the add-in's
  [ComRegisterFunction], but usable without elevation. Touches ONLY this
  add-in's GUID — never the InvenTree add-in ({A1B2C3D4-...}) or any other key.

.PARAMETER DeployDir
  Folder containing the built SolidWorksQrAddIn.dll (CodeBase points here).
  Keep this folder stable — COM resolves the DLL at this exact path.

.EXAMPLE
  .\register-dev-hkcu.ps1 -DeployDir "$env:LOCALAPPDATA\SolidWorksQrAddIn"
#>
param(
    [Parameter(Mandatory = $true)][string]$DeployDir
)

$ErrorActionPreference = 'Stop'

$Clsid   = '{DA932451-66B9-4092-9534-292AD913DEAC}'   # AddinIdentity.Clsid
$ProgId  = 'SolidWorksQrAddIn.SwAddin'
$Class   = 'SolidWorksQrAddIn.SwAddin'
$Title   = 'SolidWorks QR Add-In'
$Descrip = 'Creates native QR-code sketches from text or URLs inside SolidWorks.'

$dll = Join-Path (Resolve-Path $DeployDir).Path 'SolidWorksQrAddIn.dll'
if (-not (Test-Path $dll)) { throw "Add-in DLL not found: $dll" }

$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($dll).FullName
$codebase = ([System.Uri]$dll).AbsoluteUri   # file:///C:/...

function Reg {
    param([string]$Key, [string]$Name, $Value, [string]$Type = 'REG_SZ')
    if ($Name -eq '(default)') {
        & reg.exe add "HKCU\$Key" /ve /t $Type /d "$Value" /f | Out-Null
    } else {
        & reg.exe add "HKCU\$Key" /v $Name /t $Type /d "$Value" /f | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "reg add failed: HKCU\$Key $Name" }
}

# --- COM class registration (per-user CLSID hive) ---
Reg "SOFTWARE\Classes\CLSID\$Clsid"                    '(default)' $Class
Reg "SOFTWARE\Classes\CLSID\$Clsid\InprocServer32"     '(default)' "$env:SystemRoot\System32\mscoree.dll"
Reg "SOFTWARE\Classes\CLSID\$Clsid\InprocServer32"     'ThreadingModel' 'Both'
Reg "SOFTWARE\Classes\CLSID\$Clsid\InprocServer32"     'Class'         $Class
Reg "SOFTWARE\Classes\CLSID\$Clsid\InprocServer32"     'Assembly'      $assemblyName
Reg "SOFTWARE\Classes\CLSID\$Clsid\InprocServer32"     'RuntimeVersion' 'v4.0.30319'
Reg "SOFTWARE\Classes\CLSID\$Clsid\InprocServer32"     'CodeBase'      $codebase
Reg "SOFTWARE\Classes\CLSID\$Clsid\ProgId"             '(default)' $ProgId
Reg "SOFTWARE\Classes\$ProgId"                          '(default)' $Title
Reg "SOFTWARE\Classes\$ProgId\CLSID"                    '(default)' $Clsid

# --- SolidWorks discovery + startup (per-user hives) ---
Reg "SOFTWARE\SolidWorks\AddIns\$Clsid"                '(default)' 1 'REG_DWORD'
Reg "SOFTWARE\SolidWorks\AddIns\$Clsid"                'Title'       $Title
Reg "SOFTWARE\SolidWorks\AddIns\$Clsid"                'Description' $Descrip
Reg "SOFTWARE\SolidWorks\AddInsStartup\$Clsid"         '(default)' 1 'REG_DWORD'

Write-Host "Registered $Clsid -> $dll"
Write-Host "  (per-user HKCU only; run unregister-dev-hkcu.ps1 to remove)"
