#requires -Version 5
<#
.SYNOPSIS
  Verifies a SolidWorks QR add-in deployment folder (ticket 7).

.DESCRIPTION
  Read-only check - safe on the registered folder too; never writes into or
  deletes from it. Runs the tested DeploymentPlan.VerifyFolder contract from
  the folder's own SolidWorksQrAddIn.Core.dll: required binaries/docs/licenses
  present, no SolidWorks interop DLLs, no dev/test tooling, no unexpected
  binaries. Then reports SHA-256 fingerprints and the add-in's COM identity as
  install evidence.

  Exit code 0 = deployable, 1 = problems found.

.PARAMETER DeployDir
  The deployment folder (e.g. deploy\SolidWorksQrAddIn or the installed
  %LOCALAPPDATA%\SolidWorksQrAddIn).

.EXAMPLE
  .\verify-deploy.ps1 -DeployDir "deploy\SolidWorksQrAddIn"
  .\verify-deploy.ps1 -DeployDir "$env:LOCALAPPDATA\SolidWorksQrAddIn"
#>
param(
    [Parameter(Mandatory = $true)][string]$DeployDir
)

$ErrorActionPreference = 'Stop'
$DeployDir = (Resolve-Path $DeployDir -ErrorAction SilentlyContinue).Path
if (-not $DeployDir) {
    Write-Host "FAIL  deployment folder not found"
    exit 1
}

# --- plan-driven file check (from the folder's own Core.dll) ------------------
$coreDll = Join-Path $DeployDir 'SolidWorksQrAddIn.Core.dll'
if (-not (Test-Path $coreDll)) {
    Write-Host "FAIL  missing required file: SolidWorksQrAddIn.Core.dll"
    exit 1
}
$core = [Reflection.Assembly]::LoadFrom($coreDll)
$plan = $core.GetType('SolidWorksQrAddIn.Core.Deployment.DeploymentPlan')
if ($plan -eq $null) {
    Write-Host "FAIL  SolidWorksQrAddIn.Core.dll predates DeploymentPlan - repack with pack-deploy.ps1"
    exit 1
}
$problems = @($plan.GetMethod('VerifyFolder').Invoke($null, @($DeployDir)))

if ($problems.Count -eq 0) {
    Write-Host "PASS  deployment contents match DeploymentPlan"
} else {
    $problems | ForEach-Object { Write-Host "FAIL  $_" }
}

# --- evidence: fingerprints + identity ----------------------------------------
Write-Host "-- files -------------------------------------------------------"
Get-ChildItem $DeployDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16)
    Write-Host ("       {0,-45} {1,8} bytes  sha256:{2}..." -f
        $_.FullName.Substring($DeployDir.Length + 1), $_.Length, $hash)
}

$addinDll = Join-Path $DeployDir 'SolidWorksQrAddIn.dll'
if (Test-Path $addinDll) {
    try {
        # Reflection-only context requires dependencies preloaded.
        foreach ($dep in 'SolidWorksQrAddIn.Core.dll', 'QRCoder.dll', 'Clipper2Lib.dll') {
            $p = Join-Path $DeployDir $dep
            if (Test-Path $p) { [void][Reflection.Assembly]::ReflectionOnlyLoadFrom($p) }
        }
        $asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom($addinDll)
        Write-Host "       assembly: $($asm.GetName().FullName)"

        # The expected CLSID comes from the folder's own Core.dll
        # (AddinIdentity.Clsid, braced "{...}") — never a literal repeated here.
        $expectedClsid = $null
        $identity = $core.GetType('SolidWorksQrAddIn.Core.Registration.AddinIdentity')
        if ($identity -ne $null) {
            $field = $identity.GetField('Clsid', [Reflection.BindingFlags]'Public,Static')
            if ($field -ne $null) { $expectedClsid = [string]$field.GetValue($null) }
        }

        $t = $asm.GetType('SolidWorksQrAddIn.SwAddin')
        $guid = [Reflection.CustomAttributeData]::GetCustomAttributes($t) |
            Where-Object { $_.AttributeType.FullName -eq 'System.Runtime.InteropServices.GuidAttribute' } |
            ForEach-Object { $_.ConstructorArguments[0].Value } | Select-Object -First 1
        if ($guid -and $expectedClsid) {
            # GuidAttribute.Value is UNBRACED; AddinIdentity.Clsid is braced —
            # normalise both before comparing.
            $match = ($guid.Trim('{}') -ieq $expectedClsid.Trim('{}'))
            Write-Host ("       add-in CLSID: {0}  ({1})" -f $guid,
                $(if ($match) { 'matches AddinIdentity.Clsid' } else { "MISMATCH - expected $expectedClsid" }))
            if (-not $match) { $problems += "deployed add-in CLSID $guid does not match AddinIdentity.Clsid $expectedClsid" }
        } elseif ($guid -and -not $expectedClsid) {
            Write-Host "       add-in CLSID: $guid  (AddinIdentity.Clsid unreadable - not compared)"
        }
    } catch {
        Write-Host "       (identity read skipped: $($_.Exception.Message))"
    }
}

if ($problems.Count -eq 0) {
    Write-Host "== deployment folder verified =="
    exit 0
} else {
    Write-Host ("== {0} problem(s) - do not install ==" -f $problems.Count)
    exit 1
}
