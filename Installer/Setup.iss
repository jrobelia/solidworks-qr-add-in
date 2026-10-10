; SolidWorksQrAddIn Inno Setup script
;
; Build:  iscc /DAppVersion=1.0.0 Installer\Setup.iss
; Output: Installer\SolidWorksQrAddIn-<version>-Setup.exe
;
; Replaces the pack-deploy + copy + register-addin-hkcu.ps1 flow with a
; standard Windows installer for end users. Admin rights are required —
; Tools -> Add-Ins enumerates HKLM\SOFTWARE\SolidWorks\AddIns and COM
; registration lands in HKLM\Software\Classes — but the user sees a normal
; wizard, a UAC prompt, and a real entry in Settings > Apps. The manual
; per-user route in docs/DEPLOYMENT.md remains the dev fallback.
;
; COM registration still goes through RegAsm so the registry layout stays
; owned by [ComRegisterFunction]/[ComUnregisterFunction] in SwAddin.cs
; (elevated RegAsm takes the MachineWide plan: HKLM AddIns discovery +
; HKCU AddInsStartup flag).

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif

#define AppName "SolidWorks QR Add-In"
#define DeployDir "..\deploy\SolidWorksQrAddIn"
#define RegAsm "{win}\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

[Setup]
; Freshly generated uninstall identity ({EFF08964-…}) — deliberately NOT the
; add-in's COM CLSID {DA932451-66B9-4092-9534-292AD913DEAC} and NOT the
; InvenTree add-in's GUID. A stable AppId lets Inno recognise previous
; installs and upgrade them in place.
AppId={{EFF08964-4543-45AC-9CDC-867500E50276}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=SolidWorks QR Add-In Contributors
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Prompt to close SolidWorks when it has the DLL loaded — the usual
; "install while SolidWorks is open" failure becomes a guided close.
CloseApplications=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\THIRD-PARTY-NOTICES.md
OutputDir=.
OutputBaseFilename=SolidWorksQrAddIn-{#AppVersion}-Setup
UninstallDisplayName={#AppName}

[Files]
; Ship exactly the pack-deploy output — the same folder DeploymentPlan
; verifies headlessly: the four runtime DLLs, notices, install docs, the
; HKCU registration scripts (still useful for a per-user fallback), and the
; bundled license texts. LiveProbe and interop DLLs are excluded by the
; plan, so they can never appear here.
Source: "{#DeployDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
const
  // NDP Release value Microsoft assigned to .NET Framework 4.8
  DotNet48MinRelease = 528040;

// .NET Framework 4.8 is a hard prerequisite. Check the NDP Release value —
// RegAsm ships with every 4.x, so a FileExists gate would let 4.6/4.7
// machines through and then fail mid-install at the RegAsm step.
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release)
    and (Release >= DotNet48MinRelease);
  if not Result then
    MsgBox('SolidWorks QR Add-In requires .NET Framework 4.8 or later.' + #13#10 +
           'Install it from Microsoft and run this installer again.',
           mbError, MB_OK);
end;

// The failure messages quote the RegAsm command back as the manual-repair
// hint, so the hint and the Exec params must be built in one place — if
// they drifted, the hint would tell the user to run the wrong command.
function RegAsmArgs(const Switches: String): String;
begin
  Result := '"' + ExpandConstant('{app}\SolidWorksQrAddIn.dll') + '" ' + Switches;
end;

// [Run]/[UninstallRun] entries never look at the exit code, so a failed
// RegAsm would still end the wizard "successfully" with the add-in absent
// from SolidWorks — the same silent failure register-addin-hkcu.ps1 avoids
// via $LASTEXITCODE. Exec gives the code back so we can surface it.
function RunRegAsm(const Switches: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{#RegAsm}'), RegAsmArgs(Switches),
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
  if Result <> 0 then
    Log('RegAsm ' + Switches + ' failed with exit code ' + IntToStr(Result));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    ExitCode := RunRegAsm('/codebase /s');
    if ExitCode <> 0 then
      SuppressibleMsgBox(
        'RegAsm could not register the add-in (exit code ' +
          IntToStr(ExitCode) + ').' + #13#10 +
        'The QR Sketch command will not appear in SolidWorks.' + #13#10 +
        'Re-run this installer, or register by hand as admin:' + #13#10 +
        ExpandConstant('{#RegAsm}') + ' ' + RegAsmArgs('/codebase'),
        mbError, MB_OK, IDOK);
  end;
end;

// usUninstall fires before file removal so the DLL still exists when RegAsm
// unregisters it and [ComUnregisterFunction] can clean the SolidWorks keys.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExitCode := RunRegAsm('/u /s');
    if ExitCode <> 0 then
      SuppressibleMsgBox(
        'RegAsm could not unregister the add-in (exit code ' +
          IntToStr(ExitCode) + ').' + #13#10 +
        'Files are still removed, but SolidWorks may keep listing the add-in.' + #13#10 +
        'To finish by hand as admin:' + #13#10 +
        ExpandConstant('{#RegAsm}') + ' ' + RegAsmArgs('/u'),
        mbError, MB_OK, IDOK);
  end;
end;
