# Building, testing, and registering the SolidWorks QR Add-In

Baseline: Windows x64, SolidWorks 2023 SP5 (rev 31.x), .NET Framework 4.8.
Build tools: .NET SDK (tested with 10.0.103); no Visual Studio required.

## Layout

| Path | What it is |
|---|---|
| `src/SolidWorksQrAddIn.Core` | Host-independent logic: the `ISketchHost`/`ISketchInsertion` seam, commands, geometry plans, registration plan, the `IAddInProbe` test hook. **Never references SolidWorks assemblies.** |
| `src/SolidWorksQrAddIn` | The COM-visible add-in (`SwAddin`), the native adapter (`Host/`), COM registration functions. CLSID `{DA932451-66B9-4092-9534-292AD913DEAC}` — unique to this project (NOT the InvenTree GUID). |
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
  test adapter, failure cleanup + honest cleanup-failure reporting, flag
  restoration, redraw.
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
`RunFailingQrSketchProbe`, `AddinStateProbe`) through
`IDispatch`/`InvokeMember` — the same mechanism SolidWorks uses for
command callbacks. (Note: the returned RCW does NOT QI our managed
`IAddInProbe` interface; invoke members by name instead.)

It then: checks the no-document/no-selection rejections, creates a part from
the default template, selects **Front Plane**, first runs the command through
`RunFailingQrSketchProbe` (injects a line-3 insertion failure so the real
native abort/cleanup path runs: partial sketch deleted, edit mode exited),
then runs `RunQrSketchProbe` — the fixed `SW-QR-MVP` request at 25 mm / 3% —
and verifies against the module's own expectation (Core is referenced
directly): segment count == generated edge count, native contour count ==
generated contour count, native region count == contour count (SolidWorks
exposes one bounded area per contour, including void hole interiors),
every generated contour matched 1:1 to a native contour by bounding box
(≤1e-9 m), finder ring→hole→island nesting present. It rejects a second
create while the sketch is open, commits the QR sketch, builds a
60×60×5 mm backing plate on Front Plane, selects only the QR's *material*
regions (`ISketchRegion` bounding boxes matched to the module's
body+island contours — selecting the whole sketch returns null for a
multi-contour extrude, and selecting ALL regions fills the holes solid),
then creates a 2 mm Boss-Extrude and a 1 mm Cut-Extrude (`Dir=true` —
the plate grows away from the sketch-normal side) into the plate.
Finally it unloads (callbacks gone) and reloads (connected again — no
duplicated group).

SolidWorks exit: `ExitApp()` on a COM-launched instance does not always
terminate the process — use `taskkill /IM SLDWORKS.exe /F` after verification.

### Verified live on this baseline (SW 2023 SP5, rev 31.5.0, invisible instance)

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
