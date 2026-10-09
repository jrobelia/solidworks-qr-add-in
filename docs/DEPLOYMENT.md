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

**Not shipped, on purpose:** `SolidWorks.Interop.*.dll` — interop types are
embedded (`EmbedInteropTypes`), so the installed SDK's assemblies are not
needed beside the add-in and are not redistributed (RESEARCH.md §2;
`redist.txt` would permit unmodified copies, but embedding removes the need).
**Not shipped:** `SolidWorksQrAddIn.LiveProbe.exe`, test assemblies — dev
tooling stays in the repo.

## Install (per-user, no admin) — recommended

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
     Description (the Tools→Add-Ins entry).
   - `HKCU\SOFTWARE\SolidWorks\AddInsStartup\{clsid}` — `(default)`=1
     (load at startup).

4. Start SolidWorks → **Tools → Add-Ins…** — "SolidWorks QR Add-In" is
   listed with both checkboxes ticked (loaded now + at startup). The command
   appears as **QR Sketch → Create QR Sketch** on the menu/command toolbar.

## Loading and unloading inside SolidWorks

- **Tools → Add-Ins…**: the left checkbox loads/unloads for the current
  session; the right checkbox controls startup. Toggling unloads and reloads
  the add-in cleanly — `DisconnectFromSW` removes the command group
  permanently (never runtime-only), so reload never duplicates the command or
  orphans the QR window (the add-in closes it during unload).
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

## Machine-wide alternative (elevated — documented, not run by this repo)

The same registration can be done machine-wide with the 64-bit RegAsm (use
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

- The add-in appends `SolidWorksQrAddIn.debug.log` beside the deployed DLL
  (connection, window, failures).
- Dependencies (`Core`, `QRCoder`, `Clipper2Lib`) resolve from the deployed
  folder via an `AssemblyResolve` handler — all four DLLs must stay together.
- The `IAddInProbe` COM methods (`RunQrSketchProbe`, `RunFailingQrSketchProbe`,
  `RunCleanupFailQrSketchProbe`, the session/window probes) exist for the
  LiveProbe diagnostics harness. They are **not** commands: nothing in the
  menu, toolbar, or window exposes them — the only user-facing action is
  **Create QR Sketch**. Failure injection is reachable only through
  `GetAddInObject` + `IDispatch` from a live-verification harness.
- LiveProbe is never installed: run it from its own build output
  (`tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe
  <deployDir>`). It refuses to touch a visible or document-holding
  SolidWorks session — run it only when no interactive session is open, then
  `taskkill /IM SLDWORKS.exe /F` reaps the invisible instance it spawned.

## Verified record

- Pack + verify + file manifest: headless, this repo (ticket 7).
- COM registration, load/unload/reload, command presence, sketches, and the
  full live checklist: see `ACCEPTANCE.md` beside this file (`docs/` in the
  repo) — items marked *awaiting* are blocked on a session-free SolidWorks
  window; items marked *proven* carry the recorded run.
