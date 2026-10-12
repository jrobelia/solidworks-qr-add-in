# Building, testing, and registering the SolidWorks QR Add-In

Baseline: Windows x64, SolidWorks 2023 SP5 (rev 31.x), .NET Framework 4.8.
Build tools: .NET SDK (tested with 10.0.103); no Visual Studio required.

## Layout

| Path | What it is |
|---|---|
| `src/SolidWorksQrAddIn.Core` | Host-independent logic: the `ISketchHost`/`ISketchInsertion` seam, commands, geometry plans, registration plan, the `IAddInProbe` test hook. **Never references SolidWorks assemblies.** |
| `src/SolidWorksQrAddIn` | The COM-visible add-in (`SwAddin`), the native adapter (`Host/`), the task-pane UI (`UI/` — a WPF view hosted in a WinForms control via `ElementHost` on SolidWorks' UI thread) and COM registration functions. CLSID `{DA932451-66B9-4092-9534-292AD913DEAC}` — unique to this project (NOT the InvenTree GUID). |
| `tests/SolidWorksQrAddIn.Tests` | Headless xunit suite + the in-memory test adapter. References Core only. |
| `tools/LiveProbe` | Live-verification console harness (drives a headless SolidWorks via COM). |
| `scripts/` | `pack-deploy.ps1`/`verify-deploy.ps1` (deployment folder) + per-user HKCU registration/unregistration. |
| `deploy/` | Pack staging output — gitignored; never the registered location. |
| `Installer/` | Inno Setup installer: `Setup.iss`, `Package.ps1`, ISCC resolvers. Compiled `*-Setup.exe` is gitignored. |

## Build

```powershell
dotnet build SolidWorksQrAddIn.sln            # Debug|x64, all four projects
dotnet build SolidWorksQrAddIn.sln -c Release
```

Everything is x64 (`Directory.Build.props` sets `Platforms=x64` and defaults
`Platform` to x64, so single-project builds produce the same `bin\x64\...` path
as the solution build).

Under Git Bash, use MSBuild dash syntax — `/t:Rebuild` is path-mangled to
`t:Rebuild` (MSB1008); write `-t:Rebuild` / `-p:Name=Value`. `> nul` and
`dir /b` are cmd-isms: `> nul` creates a literal file named `nul`, `dir` is
GNU dir — use `>/dev/null` and `ls`.

### Pinned NuGet assets — verified during restore

Package versions are pinned in the project files **and** locked: every project
commits a `packages.lock.json` (`RestorePackagesWithLockFile` in
`Directory.Build.props`) recording the resolved version + content hash of each
package — QRCoder 1.8.0 and Clipper2 2.0.0 for the runtime, the test-SDK
packages for the suite. Verify a checkout resolves exactly the locked assets:

```powershell
dotnet restore SolidWorksQrAddIn.sln --locked-mode
```

`--locked-mode` fails (NU1004/NU140x) when a project file asks for something
the lock file does not contain, instead of silently resolving a different
asset — `pack-deploy.ps1` runs it before every pack build. After an
intentional version change, regenerate the locks with a plain
`dotnet restore --force` and commit the updated `packages.lock.json` files.

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
  beyond QR capacity — the error names the selected ECC level), and direct
  validator rejections (zero area, zero-length edge, self-intersection,
  pinched + pair corner contacts). Error correction (ticket 24): the request
  carries the ECC level 1:1 onto `QRCodeGenerator.ECCLevel` (default M), the
  same payload encodes denser at H than at L, and the insertion guard
  applies at every level. Inverted
  polarity (ticket 25, `QrInvertTests`): the emitted contours are the
  working matrix's light cells — one field bounded at the matrix edge (the
  keep-out square), the code left as shrunken pockets, the same insertion
  guard.
- **Command** (`CreateQrSketchCommand`): eligibility (no doc, assembly/drawing,
  no/other selection, already-editing sketch), insertion request through the
  test adapter, per-request geometry (explicit size/growth; byte-exact payload,
  distinct payloads → distinct sketches), guard rejection before mutation,
  failure cleanup + honest cleanup-failure reporting, flag restoration, redraw.
- **Failure handling** (`CreationFailureHandlingTests`, ticket 6): cancel
  before create never consults the host; document switch/close mid-flight
  cannot redirect creation through a cached support (a fresh selection in the
  current part is used; an unselected current part refuses); exceptions at
  every seam boundary — capture, begin, edge insertion (seam and non-seam),
  commit, cleanup, teardown — become honest failure results instead of
  escaping; partial-sketch cleanup asserted on document state at every
  injection point (first line, late edge, after creation, delete-stage and
  edit-exit cleanup failure, throw-on-abort); flags restored on success and
  every failure path; one named undo step ("Create QR Sketch") reverts exactly
  the creation, never prior user work.
- **Picture** (`QrPictureRenderer`): 4-cell white margin, grown-union fill
  (diagonal overlap rendered, bounded growth), ring/hole/island nesting,
  orientation, material-area parity, deterministic signature — plus the
  inverted field through the same render path (no second renderer).
- **Session** (`QrSketchSession`): valid↔invalid transitions with stale-picture
  clearing, exact content, size/growth field parsing (mm / % per side) with
  useful errors and verbatim-kept rejected text, invert toggle → regenerated
  inverted request + picture + polarity info line, a plain Ready status with no
  version/cell/size readouts, current-request insertion, fresh host
  revalidation (no stale cross-document support), create blocked while
  editing, single-surface lifecycle (focus-or-recreate, close, release — the
  task-pane tab) and session→view sync of probe-driven edits via the
  `IQrSketchWindow` seam.
- **Size & growth** (`QrSizeGrowthTests`): several sizes/growths on one
  payload — exact grown bounds within the declared 1e-6 mm tolerance, uniform
  two-direction scaling, preserved ring→hole→island nesting, finite overlap
  and light gaps above the insertion guard; growth changes preserve the
  entered outline size via pitch recalculation; invalid numeric parsing and
  valid↔invalid transitions through the session and command seams.
- **Identity**: COM/registration plan (ComIdentityTests, RegistrationPlanTests).

## Deploy + register

Ticket 7 made this its own deliverable — see **`docs/DEPLOYMENT.md`** for the
full procedure (architecture, prerequisites, folder contents, HKCU install,
the documented-but-not-executed HKLM/RegAsm alternative, load/unload, and the
"close SolidWorks before replacing binaries" rule). The short version:

```powershell
.\scripts\pack-deploy.ps1                          # locked-mode restore, Release build, packs + verifies deploy\
robocopy deploy\SolidWorksQrAddIn "$env:LOCALAPPDATA\SolidWorksQrAddIn" /MIR
cd "$env:LOCALAPPDATA\SolidWorksQrAddIn"
.\register-addin-hkcu.ps1                          # HKCU only, no admin; -DeployDir defaults to the script folder
.\unregister-addin-hkcu.ps1                        # removal — deletes only this add-in's keys
```

The deployment manifest lives in `SolidWorksQrAddIn.Core`'s `DeploymentPlan`
(headless-tested); `pack-deploy.ps1` reads it by reflection and
`verify-deploy.ps1 -DeployDir <folder>` re-checks any folder read-only —
safe on the registered folder, never writes into it.

### Packaging the installer (end-user route)

```powershell
.\Installer\Package.ps1        # pack-deploy + ISCC -> Installer\SolidWorksQrAddIn-<version>-Setup.exe
```

Requires **Inno Setup 6** — `Resolve-ISCC.ps1` checks `$env:INNO_SETUP`,
`%LOCALAPPDATA%\Programs\Inno Setup 6`, and both Program Files locations;
`Ensure-ISCC.ps1` winget-installs it (used by CI, not local builds). With no
compiler the pack still runs and the script prints the manual
`iscc /DAppVersion=<version> Installer\Setup.iss` command. Version comes
from `git describe` (nearest tag, else commit hash). The installer EXE and
its behaviour are documented in `docs/DEPLOYMENT.md` — the install itself
needs admin and a real machine, so only the compile is verified here.

Only this add-in's CLSID `{DA932451-66B9-4092-9534-292AD913DEAC}` is ever
touched — never the InvenTree add-in ({A1B2C3D4-…}) or any other key. The
live acceptance checklist is `docs/ACCEPTANCE.md`.

## Live verification (requires SolidWorks)

LiveProbe is dev tooling — it never ships in the deployment folder; run it
from its own build output, passing the deployed folder:

```powershell
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe $deploy
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe perf $deploy   # timings
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe units $deploy  # native unit events + rendered pane
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe xsettings $deploy  # spec-11 settings gates
```

The `xsettings` mode (ticket #28) is the spec-11 live gate — it drives the
extended-settings probes (`SetQrContentTypeProbe`, `SetQrPresetFieldProbe`,
`SetQrEccProbe`, `SetQrInvertProbe`) through the pane path, performs one create
with a non-default preset + ECC level + inverted polarity, verifies the landed
block instance module-identically (definition census plus an exploded 1:1
contour-bounding-box match), and replays the insertion-guard floor on a denser
payload at the same settings (a refused rung first, then the cleared rung's
create). Like `units` it **refuses to run while any `SLDWORKS.exe` process
exists** — an occupied seat can be an interactive session it must never
mutate — and it prints its module-side expectations (composed payload, rung
table, accepted size) before that refusal so a blocked run still shows what
the live half would check. Gate results are recorded in
`docs/XSETTINGS-GATES.md`.

The `units` mode uses a session-free instance and checks the native document
watcher plus the actual pane's rendered size label, input, and cell-size value.
It changes the document unit system, calls `SetUnits`, and switches documents
without invoking the QR menu command or Create. The pending QR's physical size
stays fixed while its input and details convert to the active document's unit.
On SW 2023 SP5, assigning `LengthUnit` directly does not emit
`UnitsChangeNotify`; the probe reports that API-only route diagnostically,
not as an interactive event test. Use `SetUnits` or the unit-system preference
when an API-driven change must notify listeners. Pass the folder actually used
by COM registration: specifying a different DLL path does not override its
`CodeBase`. An installer-managed setup uses Program Files, so install the
updated installer before checking the rendered pane.

The probe launches/attaches SolidWorks via `SldWorks.Application.31` +
`Activator.CreateInstance` (PowerShell late binding does **not** work — verified),
loads the add-in with `LoadAddIn`, fetches it via `GetAddInObject("{clsid}")`,
and invokes its public probe methods (`RunQrSketchProbe`,
`RunFailingQrSketchProbe`, `AddinStateProbe`, plus the session/window
probes `OpenQrWindowProbe`, `CloseQrWindowProbe`, `WindowStateProbe`,
`SetQrContentProbe`, `SetQrSizeProbe`, `SetQrGrowthProbe`, `SetQrEccProbe`,
`SetQrInvertProbe`, `SetQrContentTypeProbe`, `SetQrPresetFieldProbe`,
`CreateQrFromWindowProbe`, `PreviewSignatureProbe`,
`RunCleanupFailQrSketchProbe`)
through `IDispatch`/`InvokeMember` — the same mechanism SolidWorks uses for
command callbacks. (Note: the returned RCW does NOT QI our managed
`IAddInProbe` interface; invoke members by name instead.)

Ticket-4 flow: open the modeless window → the fresh fields read 25 mm / 3%
→ reopen (same window, `windows=1` — no duplicate) → create a part and
select Front Plane **while the window is open** (proves the host stays
interactive) → empty content rejected with no picture → injected line-3
insertion failure cleans up only its partial sketch → `SW-QR-MVP` accepted;
the shown picture's SHA-256 signature equals a local `QrPictureRenderer`
render of the same module result → Create leaves sketch A open and the
native outline measures the entered 25 mm → a second create is blocked while
the sketch is being edited → after exiting, size/growth probes drive the
request: unparseable, zero, negative and ≥50% values are rejected (request
invalidated, text kept verbatim, no part mutation), 40 mm is accepted and
growth 8% preserves it → the custom-size sketch measures 40 mm natively
while input edits never resize the open or earlier sketches → fields
restored to 25/3 → payload B produces a different picture signature and a
second sketch matching payload B's contours while sketch A is untouched →
close/reopen recreates the window with session state intact → unload
releases everything and a reload connects fresh. Retained ticket-2 checks
then verify each native sketch against the module's expectation (segment
count, contour count, 1:1 contour bounding boxes at ≤1e-9 m,
ring→hole→island nesting) and build a 60×60×5 mm backing plate + 2 mm
Boss-Extrude + 1 mm Cut-Extrude (`Dir=true`) from the QR's material regions.

Ticket-5 flow (supports + coordinate convention — see
`docs/SKETCH-COORDINATES.md`): on Front Plane, a datum plane rotated 30°
(`InsertRefPlane`, Coincident-on-edge + Angle-to-face), the plate's top
planar face, and a vertical side planar face the probe (a) runs the
`CreateLine` convention experiment — asymmetric marker segment, endpoint
read-back, sketch→model frame recovered from `ModelToSketchTransform`
with a det=+1 no-mirroring assert — and (b) creates the QR through the
add-in, then verifies module-identical contours, the entered 25 mm outline
centred at the sketch origin, every endpoint on the support plane (no
double transform), exact in-plane round-trip, the sketch's reference entity
is the selected support, and the sketch left open. The top-face QR then
gets the Boss-Extrude + shallow Cut-Extrude feature checks on the plate.
Negative selections — cylindrical (non-planar) face, edge, two supports at
once, nothing — must each fail with a useful error and no partial sketch.
Finally a second part document is activated and its Front Plane selected:
Create must land in the second document with the first unchanged
(cross-document/stale-support guard).

Ticket-6 flow (failure handling): closing the window with a valid pending
request leaves the part unchanged (cancel is safe) → injected
insertion-failure + refused-cleanup (`RunCleanupFailQrSketchProbe`) must
report honestly ("may remain", never claimed rollback) and leave nothing
stuck in edit mode → a manual sketch must still open/draw/exit normally
(flags restored; that sketch doubles as the prior-work marker) → a fresh QR
create is exited then undone once (`EditUndo2(1)`): the named
"Create QR Sketch" step must remove the whole generated sketch without
removing the marker — a second undo is tried only as diagnosis → finally all
documents close and Create must fail cleanly with "no active document".

WPF threading note: the QR surface is a task-pane tab — task-pane controls
live on SolidWorks' own STA UI thread, so the WPF view (inside `ElementHost`)
runs on that dispatcher and no dedicated WPF pump exists anymore. Probe calls
still arrive on MTA COM worker threads; the `QrTaskPaneControl` adapter
marshals every session-facing view call (`ShowModeless`/`FocusWindow`/`Close`/
`SyncFromSession`) onto the UI thread via WinForms `Invoke`.

SolidWorks exit: `ExitApp()` on a COM-launched instance does not always
terminate the process — use `taskkill /IM SLDWORKS.exe /F` after verification.

**Attached-session guard:** `Activator.CreateInstance` returns the *running*
instance when one exists — so if the engineer's SolidWorks is already open,
the probe would attach to their interactive session instead of spawning an
invisible one. The probe therefore refuses to run when the attached instance
is visible or already holds documents, and only calls `ExitApp` on an
instance it spawned fresh. A second ProgID argument selects the version
(`SolidWorksQrAddIn.LiveProbe.exe <deployDir> SldWorks.Application.30`) —
useful when another SolidWorks release is already running. This box has
only SW 2023 installed — `SldWorks.Application.30`/`.31` both resolve to
the same server (the unqualified ProgID belongs to the last installed).

`NewDocument` requires an explicit template path — an empty one pops a
template dialog that hangs a headless run. The probe passes the configured
`swDefaultTemplatePart`; on this box that resolves to the `G:` shared drive
(`OA SW Document Templates\...prtdot`, verified mounted). The stock
`C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2023\templates\` has no plain
`Part.PRTDOT`, only `MBD\*.prtdot`.

### Verified live on this baseline (SW 2023 SP5, rev 31.5.0, invisible instance)

- **Full-suite live run 2026-10-09 — `# ALL CHECKS PASSED`, 185 checks,
  exit 0.** Window lifecycle, modeless host, invalid-input rejections,
  payload A (252 segs/27 contours, exact 25 mm) + payload B (384/41),
  40 mm/8% custom size, convention probe + QR creates on Front Plane, a
  tilted datum plane (Plane5, ~4.8°), Top Plane (90° datum), plate top and
  side planar faces (all verbatim sketch-space, on-plane, no mirroring),
  negative selections, cross-document guard, cancel safety, unload/reload,
  plate + face boss/cut, injected cleanup failure, flag restoration, and the
  no-document refusal.
- **Named-undo contract — LIMITATION verified live:** the "Create QR Sketch"
  undo step never reaches the undo stack — `FinishRecordingUndoObject2`
  returns false in every arrangement tried (recording opened before/after
  `InsertSketch`, finished inside/outside sketch edit mode); the AddToDB
  bulk insertion the contour set requires bypasses the undo system. Two
  `EditUndo2` presses left the generated sketch in place and acted on older
  history; prior user work was never removed (asserted). Reported in
  `docs/ACCEPTANCE.md` D17 — the recording attempt remains best-effort.
- **Insertion perf measured live:** create_ms = 951 / 1930 / 8905 for
  252 / 1218 / 3896-segment payloads (at the guard-minimum sizes 25/40/80 mm);
  see `docs/ACCEPTANCE.md` §F.
- Probe-side facts learned live: `ISurface.PlaneParams` returns normal-first;
  `IRefPlane.GetRefPlaneParams` is not a usable [point,normal] layout (read
  the plane feature's face or the sketch frame instead); face supports add
  the face-boundary region to `GetSketchRegionCount`; `IVertex.GetPoint` is
  model-space; interactive-mode `CreateLine` applies ~0.24 mm inference
  snapping (bulk mode is verbatim); InsertRefPlane/CreatePlaneAtAngle3 would
  not produce a face-angled datum plane in this session (a 3-point plane
  supplied the rotated support instead).
- Ticket 7 packaging run — deploy folder packs and verifies clean; per-user
  HKCU registration installed and verified (`verify-deploy.ps1` on the
  registered folder). Runtime load/unload/reload is proven; interactive
  Tools→Add-Ins enumeration and startup auto-load remain *awaiting human
  verification* (auto-load has never fired in a COM-launched instance — SW
  may not enumerate the HKCU `AddIns` key for invisible sessions).
- Ticket 4 size/growth run — **ALL CHECKS PASSED** (87): fresh-window fields
  read 25 mm / 3% → payload-A sketch native outline measured 25.000 mm →
  unparseable/zero/negative size and unparseable/0%/50% growth rejected
  (request invalidated, verbatim text kept, no part mutation) → 40 mm custom
  size accepted (request `size=40.000`, picture re-rendered) → growth 8%
  preserved the entered 40 mm outline (pitch recalculation) → custom sketch
  native outline measured 40.000 mm and stayed open → input edits while the
  sketch was open changed nothing → earlier sketch kept its 25 mm outline →
  defaults restored, payload-B sketch + close/reopen + unload/reload +
  plate/Boss/Cut regressions all still green.
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
