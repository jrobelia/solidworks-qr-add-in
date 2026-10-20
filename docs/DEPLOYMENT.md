# Deploying the SolidWorks QR Add-In

Baseline environment: **Windows x64**, **SolidWorks 2023 SP5** (rev 31.x),
**.NET Framework 4.8** runtime. SolidWorks is a 64-bit host — every binary is
x64. Other SolidWorks versions are not covered by this procedure (the spec's
compatibility claim is 2023 SP5 only).

This add-in is the standalone **SolidWorks QR Add-In**, CLSID
`{DA932451-66B9-4092-9534-292AD913DEAC}`, ProgId `SolidWorksQrAddIn.SwAddin`.
It shares nothing with the InvenTree add-in (`{A1B2C3D4-…}`) — every command
below touches only this CLSID and its own keys; no other add-in's entries are
read or written.

## Prerequisites

| Requirement | Check |
|---|---|
| Windows x64 | `echo %PROCESSOR_ARCHITECTURE%` → `AMD64` |
| SolidWorks 2023 SP5 installed | `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe` |
| .NET Framework 4.8+ | `reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release` → ≥ 528040 |
| For per-user install: none elevated | Registration writes only under HKCU. |

The add-in is a .NET Framework 4.8 assembly; .NET 4.8 ships with current
Windows 10/11 — only missing on stripped images.

## Deployment folder contents

`scripts\pack-deploy.ps1` builds Release|x64 and assembles exactly the tested
manifest (`SolidWorksQrAddIn.Core`'s `DeploymentPlan` — the same data the
headless suite pins):

```
SolidWorksQrAddIn.dll        COM-visible add-in (CLSID {DA932451-…})
SolidWorksQrAddIn.Core.dll   host-independent logic (loaded beside the add-in)
QRCoder.dll                  pinned dependency 1.8.0 — MIT
Clipper2Lib.dll              pinned dependency 2.0.0 — Boost Software License 1.0
THIRD-PARTY-NOTICES.md       notices, pinned versions, selected NuGet assets
DEPLOYMENT.md                this procedure
ACCEPTANCE.md                live acceptance checklist
register-addin-hkcu.ps1      per-user install (no admin)
unregister-addin-hkcu.ps1    per-user removal
licenses\                    full license texts for the shipped dependencies
```

At first load the add-in also writes `qr-cmd-{20,32,40,64,96,128}.png` and a
dedicated `qr-tab-18.png` into
`%LOCALAPPDATA%\SolidWorksQrAddIn\icons` (falling back to `icons\` beside the
DLL when the per-user folder is unavailable — the unpacked dev case) — the QR
command group carries icons (`ICommandGroup.IconList`/`MainIconList`, one PNG
strip per SolidWorks resolution; the single command is `ImageIndex` 0). The
task-pane tab does NOT reuse a menu strip: SolidWorks draws tab icons at
~16-18px and silently ignores a 32px image (the API spec historically calls
for 16x18), so `qr-tab-18.png` is rendered at 18x18 solely for
`CreateTaskpaneView2`. Per-user-first matters
under the Inno installer: the deployed DLL lives in Program Files, which is
read-only. The write is idempotent and best-effort: a failed extraction
leaves the command reachable by name and the tab unadorned.

**Not shipped, on purpose:** `SolidWorks.Interop.*.dll` — interop types are
embedded (`EmbedInteropTypes`), so the installed SDK's assemblies are not
needed beside the add-in and are not redistributed (RESEARCH.md §2;
`redist.txt` would permit unmodified copies, but embedding removes the need).
**Not shipped:** `SolidWorksQrAddIn.LiveProbe.exe`, test assemblies — dev
tooling stays in the repo.

## Install (recommended for all users) — the Inno Setup installer

`Installer\Package.ps1` packs the deployment folder and compiles
`Installer\Setup.iss` into `Installer\SolidWorksQrAddIn-<version>-Setup.exe`
— a normal Windows installer built with Inno Setup 6 (a scripted setup EXE,
not an MSI).

1. Close SolidWorks if it is running (the installer can prompt to close it
   for you — `CloseApplications=yes`).
2. Run the `-Setup.exe`. The wizard requires admin (one UAC prompt) because
   the **Tools → Add-Ins** dialog enumerates
   `HKLM\SOFTWARE\SolidWorks\AddIns` and COM registration lands under
   `HKLM\SOFTWARE\Classes`.
3. Files land in `%ProgramFiles%\SolidWorks QR Add-In\` — exactly the
   verified pack-deploy contents (the four runtime DLLs, notices, docs, the
   HKCU fallback scripts, `licenses\`). The wizard then runs
   `Framework64\v4.0.30319\RegAsm.exe /codebase /s` on the installed DLL;
   elevated RegAsm takes the `MachineWide` registration plan —
   `HKLM\SOFTWARE\SolidWorks\AddIns\{clsid}` (default=1, Title,
   Description) plus the per-user `AddInsStartup` flag — so the add-in
   appears in **Tools → Add-Ins** and auto-loads at startup. The
   `AddInsStartup` flag is written only when absent, so re-registering
   never overrides an existing Start-Up checkbox.
4. A failed RegAsm aborts the "success" ending with an error box that
   quotes the exact manual-repair command — the exit code is checked, never
   silently swallowed.
5. **Uninstall** via Settings → Apps / Add or Remove Programs: the
   uninstaller runs `RegAsm /u /s` *before* file removal so
   `[ComUnregisterFunction]` can clean this add-in's SolidWorks keys, then
   deletes the install folder and the Apps entry.

Prerequisite gate: the wizard refuses to start on machines without .NET
Framework 4.8 (NDP `Release` ≥ 528040) — RegAsm itself ships with 4.x, so
a bare file check would let 4.6/4.7 hosts through and fail mid-install.

## Install (per-user, no admin) — dev/fallback route

> Note (verified 2026-10-09): this route registers the add-in so COM
> automation and File→Open can load it, but it does **not** list the add-in
> in Tools → Add-Ins on the baseline install — that dialog reads HKLM. For
> durable Tools → Add-Ins visibility use the machine-wide route further down.

1. Pack the folder (from the repo):

   ```powershell
   .\scripts\pack-deploy.ps1
   ```

   Output lands in `deploy\SolidWorksQrAddIn\` and is verified against the
   plan automatically (fail = do not install).

2. Copy the folder to a **stable location** — the registered path is written
   into the COM `CodeBase` entry and resolved on every SolidWorks start:

   ```powershell
   robocopy deploy\SolidWorksQrAddIn "$env:LOCALAPPDATA\SolidWorksQrAddIn" /MIR
   ```

   `%LOCALAPPDATA%` is user-writable and survives rebuilds. Any stable path
   works — but never point registration at a `bin\` or `deploy\` staging
   folder that builds/packs overwrite.

3. Close SolidWorks if it is running, then register (from the installed
   folder — the script defaults `-DeployDir` to its own location):

   ```powershell
   cd "$env:LOCALAPPDATA\SolidWorksQrAddIn"
   .\register-addin-hkcu.ps1
   ```

   Writes under HKCU only — no admin required:

   - `HKCU\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32` — `mscoree.dll`,
     `ThreadingModel=Both`, `Class`, `Assembly`, `RuntimeVersion`, and
     `CodeBase` = the deployed DLL path (SolidWorks locates the add-in purely
     through this CLSID→CodeBase; there is no `Path` value in the AddIns keys).
   - `HKCU\SOFTWARE\Classes\CLSID\{clsid}\ProgId` and
     `HKCU\SOFTWARE\Classes\SolidWorksQrAddIn.SwAddin\CLSID`.
   - `HKCU\SOFTWARE\SolidWorks\AddIns\{clsid}` — `(default)`=1, Title,
     Description.
   - `HKCU\SOFTWARE\SolidWorks\AddInsStartup\{clsid}` — `(default)`=1
     (load at startup). Written only when the key is absent — re-running
     the script preserves the user's existing Start-Up checkbox.

4. **Verified 2026-10-09 on the baseline install:** the interactive
   **Tools → Add-Ins…** dialog enumerates `HKLM\SOFTWARE\SolidWorks\AddIns` —
   HKCU `AddIns` entries are per-user load-state mirrors and do **not** make
   the add-in appear in the dialog, even though the per-user CLSID/CodeBase
   lets `LoadAddIn` and COM automation load it fine. To load per-session
   without admin, use **File → Open** on `SolidWorksQrAddIn.dll` (the Open
   dialog has an "Add-Ins (*.dll)" filter). For end-user installs, use the
   machine-wide (elevated) route below.

## Dev-machine setup (one-time elevated) — the supported dev workflow

`scripts\register-addin-dev.ps1` registers the add-in **once, machine-wide,
pointed at the dev deployment folder** — the same RegAsm step the installer
runs, but with `CodeBase` at `%LOCALAPPDATA%\SolidWorksQrAddIn` instead of
Program Files. It also clears any HKCU registration (which would otherwise
shadow the machine one) and deletes stale HKLM keys an installer uninstall
leaves behind. After it, the add-in is listed in Tools → Add-Ins, auto-loads,
and the loaded build is always the dev folder's.

The day-to-day code-change loop is then a single unelevated command — the
registration never changes because the deployed path is stable:

```powershell
# close SolidWorks first (the registered DLL is file-mapped and locked)
.\scripts\sync-dev-deploy.ps1     # pack-deploy + robocopy /MIR to the dev folder
# restart SolidWorks
```

Testing the Inno installer against this setup is a bounded event, not a
per-change tax: close SolidWorks, install (elevated — it repoints the HKLM
keys to Program Files so the *installed* build is what loads), verify,
uninstall, then re-run `register-addin-dev.ps1` once to repoint the machine
keys back at the dev folder — including the stale `Classes\CLSID` keys the
uninstaller leaves behind. Once loaded, the
   command appears as **QR Sketch → Create QR Sketch** on the menu, and the
   QR UI lives in a docked **QR Sketch task-pane tab** (created via
   `CreateTaskpaneView2`, hosting the WPF view through a WinForms
   `ElementHost`). There is no custom toolbar; engineers who want a button
   can add the command via Tools → Customize.

   *Keyboard note:* without mitigation the pane's text boxes accept
   Backspace/Delete/paste but no typed characters. The root cause is
   `WM_GETDLGCODE`: the native task-pane container asks each child window
   which keys it wants before dispatching, and the default `ElementHost`/
   `HwndSource` answer lacks `DLGC_WANTCHARS`, so `WM_CHAR` is never
   produced for the WPF window. The pane therefore hosts its WPF view in a
   `PaneElementHost` that answers `WM_GETDLGCODE` with the full wanted set
   (`WANTCHARS|WANTALLKEYS|WANTARROWS|WANTTAB`) at both HWND levels — the
   ElementHost `WndProc` and a hook on the nested `HwndSource`. A
   forward-only WinForms `IMessageFilter` that re-raises every thread
   message through `System.Windows.Interop.ComponentDispatcher` stays
   installed for the pane's lifetime (ConnectToSW → DisconnectFromSW) as a
   belt-and-braces second path; it never eats messages, so SolidWorks and
   other add-ins are unaffected. Everything is logged to the debug log
   (`pane keyboard filter installed on thread N`, `…saw first message`,
   `pane host HwndSource hook installed`, `pane host answered
   WM_GETDLGCODE`, `pane host saw WM_CHAR`) and is best-effort, never fatal.

## Loading and unloading inside SolidWorks

- **Tools → Add-Ins…**: the left checkbox loads/unloads for the current
  session; the right checkbox controls startup. Toggling unloads and reloads
  the add-in cleanly — `DisconnectFromSW` removes the command group
  permanently (never runtime-only), so reload never duplicates the command or
  orphans the QR pane (the add-in deletes the task-pane view during unload).
- While loaded, the DLL is file-mapped into `SLDWORKS.exe`.

## Replacing binaries — always close SolidWorks first

A loaded add-in DLL is mapped into the running SolidWorks process — it cannot
be overwritten in place, and unregister/re-register while loaded leaves stale
entries. To update:

1. Tools → Add-Ins → untick both boxes **and** exit SolidWorks fully.
2. Replace the files in the installed folder (or `robocopy` the fresh pack).
3. Start SolidWorks.

No re-registration is needed when only the binaries change — `CodeBase` still
points at the same path. Re-register only if the folder moves.

`pack-deploy.ps1`, `dotnet build`, and `dotnet test` never write into the
registered folder — builds land in `src\…\bin\`, packing lands in `deploy\`,
and verification is read-only. Headless verification can therefore run at any
time, including while the installed add-in is registered and loaded.

## Verify a deployment folder headlessly

```powershell
.\scripts\verify-deploy.ps1 -DeployDir "$env:LOCALAPPDATA\SolidWorksQrAddIn"
```

Read-only; safe on the registered folder. Reports the plan check, SHA-256
fingerprints, and the add-in's COM CLSID (must be
`{DA932451-66B9-4092-9534-292AD913DEAC}`). Exit code 0 = deployable.

## Uninstall (per-user)

1. Exit SolidWorks.
2. From the installed folder:

   ```powershell
   .\unregister-addin-hkcu.ps1
   ```

   Deletes only this add-in's `CLSID`, ProgId, `AddIns` and `AddInsStartup`
   keys under HKCU.
3. Delete the installed folder.

## Machine-wide alternative (elevated — what the installer runs)

The installer above performs this registration automatically; done by hand,
the same registration can be applied machine-wide with the 64-bit RegAsm (use
`Framework64` — SolidWorks is x64):

```powershell
# elevated prompt — REQUIRES admin rights and the user's explicit approval.
# Writes HKLM\SOFTWARE\Classes\CLSID and invokes the add-in's
# [ComRegisterFunction], which writes HKLM\SOFTWARE\SolidWorks\AddIns\{clsid}
# (+ HKCU AddInsStartup). Falls back to HKCU automatically when HKLM is denied.
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe /codebase "C:\path\to\SolidWorksQrAddIn.dll"
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe /unregister "C:\path\to\SolidWorksQrAddIn.dll"
```

Neither route is executed by the build, pack, or test scripts — elevated or
machine-wide changes are always an explicit user action.

## Diagnostics

- The add-in appends `debug.log` under
  `%LOCALAPPDATA%\SolidWorksQrAddIn\` (connection, `task pane created`,
  `pane keyboard filter installed`, `pane host …` keyboard-interop events,
  icon-extraction target, failures). LocalAppData is always writable — the
  previous beside-the-DLL location silently produced no log under a Program
  Files install; that location remains only as the dev/unpacked fallback
  (`SolidWorksQrAddIn.debug.log`). All logging is best-effort — never fatal.
- Dependencies (`Core`, `QRCoder`, `Clipper2Lib`) resolve from the deployed
  folder via an `AssemblyResolve` handler — all four DLLs must stay together.
- The `IAddInProbe` COM methods (`RunQrSketchProbe`, `RunFailingQrSketchProbe`,
  `RunCleanupFailQrSketchProbe`, the session/window probes) exist for the
  LiveProbe diagnostics harness. They are **not** commands: nothing in the
  menu or task pane exposes them — the only user-facing action is
  **Create QR Sketch**. Failure injection is reachable only through
  `GetAddInObject` + `IDispatch` from a live-verification harness.
- LiveProbe is never installed: run it from its own build output
  (`tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe
  <deployDir>`). It refuses to touch a visible or document-holding
  SolidWorks session — run it only when no interactive session is open. It
  reaps its spawned instance by PID at teardown; if one lingers,
  `taskkill /PID <pid> /F` — never `taskkill /IM SLDWORKS.exe`, which kills
  an interactive session too.
- **(Historical — the add-in no longer ships a toolbar)** A persisted
  command-group toolbar could render as a tiny empty sliver: SolidWorks keeps
  command-group toolbar geometry per user under
  `HKCU\Software\SolidWorks\SOLIDWORKS <year>\User Interface` — a toolbar
  first shown without icons can be cached collapsed (observed 2026-10-09:
  a 39×8 px docked rect). With SolidWorks closed, delete the toolbar's
  records — the `Custom API Toolbars\<ToolbarID>` key (match `ModuleName`
  to this add-in's CLSID), its `General-Bar<N>` record
  (`BarID` = that ToolbarID), and every `uiCustomApiToolbars_c<ToolbarID>`
  value — then reload; it regenerates at proper size.

## Verified record

- Pack + verify + file manifest: headless, this repo (ticket 7) and re-run
  on the deployed folder 2026-10-09 (clean, exit 0).
- COM registration (HKCU), LoadAddIn/UnloadAddIn/reload round-trip, sketches
  on all support types, failure handling, and perf: **verified live
  2026-10-09** (185 LiveProbe checks passed, SW rev 31.5.0) — the recorded
  evidence and the two remaining human-visual items are in `ACCEPTANCE.md`
  (`docs/` in the repo).
