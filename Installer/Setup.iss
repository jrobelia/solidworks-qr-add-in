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
; Freshly generated uninstall identity — deliberately NOT the add-in's COM
; CLSID {DA932451-66B9-4092-9534-292AD913DEAC} and NOT the InvenTree add-in's
; GUID. A stable AppId lets Inno recognise previous installs and upgrade
; them in place; it is also the Uninstall registry key name ({...}_is1).
#define AppIdGuid "{EFF08964-4543-45AC-9CDC-867500E50276}"

[Setup]
AppId={{#AppIdGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Jon Robelia
AppCopyright=Copyright (c) 2026 Jon Robelia
; Publisher/support/update links surface in Settings > Apps and appwiz.
AppPublisherURL=https://github.com/jrobelia/solidworks-qr-add-in
AppSupportURL=https://github.com/jrobelia/solidworks-qr-add-in/issues
AppUpdatesURL=https://github.com/jrobelia/solidworks-qr-add-in
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
; Inno's license page is a plain-text control, so LICENSE.txt beside this
; script carries the add-in's own MIT license (same text as ..\LICENSE) plus
; a plain-text rendering of ..\THIRD-PARTY-NOTICES.md — keep them in sync.
LicenseFile=LICENSE.txt
; Installer exe + Settings > Apps icon — a multi-size ICO of the QR glyph,
; checked in beside this script (same convention as the InvenTree add-in).
; Inno re-embeds it into the generated unins000.exe as well.
SetupIconFile=qr-addin.ico
; Wizard chrome — the QR glyph rendered by Generate-InstallerAssets.ps1 from the
; same QrIconGlyph matrix the add-in draws at runtime. Comma lists let Inno
; pick the closest size per display scale (PNG, no BMP fallback needed on
; Inno >= 6.5). The banner is an opaque white panel and the small image a
; white badge, so both stay legible under automatic dark mode.
WizardImageFile=wizard-image-100.png,wizard-image-150.png,wizard-image-200.png,wizard-image-250.png
WizardSmallImageFile=wizard-small-100.png,wizard-small-150.png,wizard-small-200.png,wizard-small-250.png
OutputDir=.
OutputBaseFilename=SolidWorksQrAddIn-{#AppVersion}-Setup
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\qr-addin.ico

[Files]
Source: "qr-addin.ico"; DestDir: "{app}"; Flags: ignoreversion
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

// The generated uninstaller is always named unins000.exe — and because it is
// unsigned, UAC shows that raw filename. The stub resolves its data/message
// files via PathChangeExt(ParamStr(0)), so renaming the exe+dat+msg trio
// keeps the pair intact; the Apps entry is then repointed at the new name.
// The uninstaller and its registry key exist by ssPostInstall (verified:
// Inno writes the key before the step fires). Any failure leaves the stock
// unins000.* names in place rather than breaking uninstall.
procedure UndoUninstallerRename(const OldExe, NewExe: String);
begin
  RenameFile(NewExe, OldExe);
  if FileExists(ChangeFileExt(NewExe, '.dat')) then
    RenameFile(ChangeFileExt(NewExe, '.dat'), ChangeFileExt(OldExe, '.dat'));
  if FileExists(ChangeFileExt(NewExe, '.msg')) then
    RenameFile(ChangeFileExt(NewExe, '.msg'), ChangeFileExt(OldExe, '.msg'));
end;

procedure RenameUninstaller;
var
  UninstKey, OldExe, NewExe, S: String;
begin
  OldExe := ExpandConstant('{uninstallexe}');
  if not FileExists(OldExe) then Exit;
  NewExe := ExtractFilePath(OldExe) + '{#AppName} Uninstall.exe';
  if not RenameFile(OldExe, NewExe) then Exit;
  if FileExists(ChangeFileExt(OldExe, '.dat')) then
    RenameFile(ChangeFileExt(OldExe, '.dat'), ChangeFileExt(NewExe, '.dat'));
  if FileExists(ChangeFileExt(OldExe, '.msg')) then
    RenameFile(ChangeFileExt(OldExe, '.msg'), ChangeFileExt(NewExe, '.msg'));
  // AppId renders as {GUID}} — {{...}} in [Setup] yields a literal { and
  // the trailing }} stays literal — so the real key name ends "}}_is1"
  // (verified 2026-10-23). Keeping the quirky-but-stable AppId preserves
  // upgrade identity with 1.0.0 installs.
  UninstKey :=
    'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdGuid}}_is1';
  if not RegQueryStringValue(HKLM, UninstKey, 'UninstallString', S) then
  begin
    Log('Uninstall key missing - reverting uninstaller rename');
    UndoUninstallerRename(OldExe, NewExe);
    Exit;
  end;
  StringChangeEx(S, OldExe, NewExe, True);
  if not RegWriteStringValue(HKLM, UninstKey, 'UninstallString', S) then
  begin
    Log('UninstallString rewrite failed - reverting uninstaller rename');
    UndoUninstallerRename(OldExe, NewExe);
    Exit;
  end;
  if RegQueryStringValue(HKLM, UninstKey, 'QuietUninstallString', S) then
  begin
    StringChangeEx(S, OldExe, NewExe, True);
    RegWriteStringValue(HKLM, UninstKey, 'QuietUninstallString', S);
  end;
  Log('Uninstaller renamed to ' + NewExe);
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
  if CurStep = ssPostInstall then
    RenameUninstaller;
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
