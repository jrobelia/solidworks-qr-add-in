# Building, testing, and registering the SolidWorks QR Add-In

Baseline: Windows x64, SolidWorks 2023 SP5 (rev 31.x), .NET Framework 4.8.
Build tools: .NET SDK (tested with 10.0.103); no Visual Studio required.

## Layout

| Path | What it is |
|---|---|
| `src/SolidWorksQrAddIn.Core` | Host-independent logic: the `ISketchHost`/`ISketchInsertion` seam, commands, geometry plans, registration plan, the `IAddInProbe` test hook. **Never references SolidWorks assemblies.** |
| `src/SolidWorksQrAddIn` | The COM-visible add-in (`SwAddin`), the native adapter (`Host/`), the WPF window (`UI/` — modeless, on a dedicated STA thread) and COM registration functions. CLSID `{DA932451-66B9-4092-9534-292AD913DEAC}` — unique to this project (NOT the InvenTree GUID). |
| `tests/SolidWorksQrAddIn.Tests` | Headless xunit suite + the in-memory test adapter. References Core only. |
| `tools/LiveProbe` | Live-verification console harness (drives a headless SolidWorks via COM). |
| `scripts/` | Dev registration/unregistration (per-user, HKCU only). |

## Build

```powershell
dotnet build SolidWorksQrAddIn.sln            # Debug|x64, all four projects
dotnet build SolidWorksQrAddIn.sln -c Release
```

Everything is x64 (`Directory.Build.props` sets `Platforms=x64` and defaults
`Platform` to x64, so single-project builds produce the same `bin\x64\...` path
as the solution build).

### Configuring the SolidWorks SDK reference location

The interop assemblies (`sldworks`, `swconst`, `swpublished`) come from the
installed SDK and are located via, in order:

1. MSBuild property: `dotnet build ... -p:SolidWorksSdkDir="D:\SW\api\redist"`
2. Environment variable: `SOLIDWORKS_SDK_DIR`
3. Default: `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist`

Interop types are **embedded** (`EmbedInteropTypes=true`) — no
`SolidWorks.Interop.*.dll` is copied to the output or needed beside the add-in.
(One quirk this creates: VARIANT-returning members like `GetSketchSegments()`
surface as `dynamic`; the LiveProbe project references `Microsoft.CSharp` for
the binder. The add-in itself avoids those members.)

## Headless tests — no SolidWorks required

```powershell
dotnet test tests\SolidWorksQrAddIn.Tests
```

Runs on any Windows worker with .NET Framework 4.8 + the .NET SDK. Output goes
only to `tests\...\bin` — never over a deployed/registered DLL. The suite covers:

- **Geometry module** (`QrSketchGenerator`): deterministic cell-matrix fixtures
  (adjacent merge, diagonal finite overlap, finder ring→hole→island nesting),
  the real `SW-QR-MVP` payload (version 1, N=21, exact 25 mm centred outline,
  bit-identical shared endpoints, nonzero-area closed contours), insertion-guard
  rejections (rounding growth, light-gap collapse, sub-0.05 mm features), input
  errors (empty content, non-finite/≤0 size, growth outside (0, 0.5), payload
  beyond QR capacity at ECC M), and direct validator rejections (zero area,
  zero-length edge, self-intersection, pinched + pair corner contacts).
- **Command** (`CreateQrSketchCommand`): eligibility (no doc, assembly/drawing,
  no/other selection, already-editing sketch), insertion request through the
  test adapter, per-content geometry (byte-exact payload, distinct payloads →
  distinct sketches), failure cleanup + honest cleanup-failure reporting, flag
  restoration, redraw.
- **Picture** (`QrPictureRenderer`): 4-cell white margin, grown-union fill
  (diagonal overlap rendered, bounded growth), ring/hole/island nesting,
  orientation, material-area parity, deterministic signature.
- **Session** (`QrSketchSession`): valid↔invalid transitions with stale-picture
  clearing, exact content, current-request insertion, fresh host revalidation
  (no stale cross-document support), create blocked while editing, single-window
  lifecycle (focus-or-recreate, close, release) via the `IQrSketchWindow` seam.
- **Identity**: COM/registration plan (ComIdentityTests, RegistrationPlanTests).

## Deploy + register (per-user, no admin)

```powershell
$deploy = "$env:LOCALAPPDATA\SolidWorksQrAddIn"
mkdir $deploy -Force
$bin = "src\SolidWorksQrAddIn\bin\x64\Release\net48"
copy $bin\SolidWorksQrAddIn.dll $deploy
copy $bin\SolidWorksQrAddIn.Core.dll $deploy
copy $bin\QRCoder.dll $deploy          # shipped dependency (MIT)
copy $bin\Clipper2Lib.dll $deploy      # shipped dependency (BSL-1.0)
.\scripts\register-dev-hkcu.ps1 -DeployDir $deploy
```

This writes, under HKCU only (never HKLM, never touches other add-ins' keys):

- `HKCU\SOFTWARE\Classes\CLSID\{clsid}\InprocServer32` — mscoree.dll, ThreadingModel=Both, Class, Assembly, RuntimeVersion, **CodeBase** (the DLL location; there is no `Path` value in the AddIns keys)
- `HKCU\SOFTWARE\Classes\CLSID\{clsid}\ProgId` + `HKCU\SOFTWARE\Classes\SolidWorksQrAddIn.SwAddin\CLSID`
- `HKCU\SOFTWARE\SolidWorks\AddIns\{clsid}` — `(default)`=1, Title, Description
- `HKCU\SOFTWARE\SolidWorks\AddInsStartup\{clsid}` — `(default)`=1

To remove: `.\scripts\unregister-dev-hkcu.ps1` — deletes only this add-in's
CLSID/ProgId/AddIns/AddInsStartup keys.

### Machine-wide registration (admin; not run by this ticket)

The COM class + SW entries can also be registered machine-wide:

```powershell
# elevated prompt — writes HKLM\SOFTWARE\Classes\CLSID + calls our
# [ComRegisterFunction] which writes HKLM\SolidWorks\AddIns + HKCU AddInsStartup
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe /codebase <path>\SolidWorksQrAddIn.dll
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe /unregister <path>\SolidWorksQrAddIn.dll
```

`RegisterFunction` falls back to HKCU automatically when HKLM is unavailable.

## Live verification (requires SolidWorks)

```powershell
copy tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe $deploy
& "$deploy\SolidWorksQrAddIn.LiveProbe.exe" $deploy
```

The probe launches/attaches SolidWorks via `SldWorks.Application.31` +
`Activator.CreateInstance` (PowerShell late binding does **not** work — verified),
loads the add-in with `LoadAddIn`, fetches it via `GetAddInObject("{clsid}")`,
and invokes its public probe methods (`RunQrSketchProbe`,
`RunFailingQrSketchProbe`, `AddinStateProbe`, plus the ticket-3 session/window
probes `OpenQrWindowProbe`, `CloseQrWindowProbe`, `WindowStateProbe`,
`SetQrContentProbe`, `CreateQrFromWindowProbe`, `PreviewSignatureProbe`)
through `IDispatch`/`InvokeMember` — the same mechanism SolidWorks uses for
command callbacks. (Note: the returned RCW does NOT QI our managed
`IAddInProbe` interface; invoke members by name instead.)

Ticket-3 flow: open the modeless window → reopen (same window, `windows=1` —
no duplicate) → create a part and select Front Plane **while the window is
open** (proves the host stays interactive) → empty content rejected with no
picture → injected line-3 insertion failure cleans up only its partial sketch
→ `SW-QR-MVP` accepted; the shown picture's SHA-256 signature equals a local
`QrPictureRenderer` render of the same module result, so the picture is
exactly the grown contour geometry → Create leaves sketch A open, the window
and picture stay up → a second create is blocked while the sketch is being
edited → after exiting, payload B produces a different picture signature and
a second sketch matching payload B's contours while sketch A is untouched →
close/reopen recreates the window with session state intact → unload releases
everything and a reload connects fresh. Retained ticket-2 checks then verify
each native sketch against the module's expectation (segment count, contour
count, 1:1 contour bounding boxes at ≤1e-9 m, ring→hole→island nesting) and
build a 60×60×5 mm backing plate + 2 mm Boss-Extrude + 1 mm Cut-Extrude
(`Dir=true`) from the QR's material regions.

WPF threading note (verified): probe calls arrive on MTA COM worker threads,
so the add-in creates its window on a dedicated STA thread
(`AddInWpfThread`) with its own dispatcher; the window marshals every call.
A menu-command invocation takes the same path.

SolidWorks exit: `ExitApp()` on a COM-launched instance does not always
terminate the process — use `taskkill /IM SLDWORKS.exe /F` after verification.

### Verified live on this baseline (SW 2023 SP5, rev 31.5.0, invisible instance)

- Ticket 3 windowed run — **ALL CHECKS PASSED** (57): modeless window open →
  focus-not-duplicate → part + selection while window open → empty-content
  refusal with no stale picture → injected-failure cleanup → payload A picture
  signature == module render, sketch A = 252 segs/27 contours 1:1 bbox-matched,
  left open, window + picture stay available → create blocked while editing →
  payload B picture/sketch (384 segs/41 contours) with sketch A unchanged →
  close/reopen (windows=2, session state kept) → unload releases, reload fresh
  → plate + Boss-Extrude2 + Cut-Extrude1 on the QR regions.
- Ticket 2 full QR run — **ALL CHECKS PASSED**: LoadAddIn → IDispatch probe →
  no-doc/no-selection rejections → injected line-3 failure cleanup →
  `SW-QR-MVP` sketch created and left open → 252 native segments / 27 native
  contours / 27 native regions, all 27 contour bounding boxes matched at
  1e-9 m, 3 finder ring→hole→island nestings verified → second-create-while-
  editing rejected → clean commit → 60×60×5 mm plate + 2 mm QR material-
  region Boss-Extrude (`Boss-Extrude2`) + 1 mm QR material-region
  Cut-Extrude (`Cut-Extrude1`, `Dir=true`) → clean unload + reload.
- Insertion requires bulk mode: `ISketchManager.AddToDB = true` +
  `DisplayWhenAdded = false` (interactive mode rejected valid coincident-
  endpoint segments with "CreateLine returned no segment"; bulk mode accepts
  all 252). Flags are restored after insertion.
- A committed sketch feature's RCW does **not** QI to `ISketch` — use
  `IFeature.GetSpecificFeature2()` to reach the sketch object.
- **Not observed headlessly:** startup auto-load via `AddInsStartup` did not
  fire in the COM-launched invisible instance (even with the flag=1 and our
  HKCU discovery key — SW did read/manage the key; it rewrote the flag to 0
  after runtime unload). Whether an interactive session enumerates the HKCU
  `AddIns` key for Tools→Add-Ins/startup is **awaiting human verification**;
  the HKLM AddIns route via RegAsm is the documented standard.
- Visual confirmation that the boss/cut reproduces the QR pattern on the
  plate (holes open, islands raised/etched) is **awaiting human
  verification** — the headless run verifies region selection and feature
  creation, not the rendered result.
