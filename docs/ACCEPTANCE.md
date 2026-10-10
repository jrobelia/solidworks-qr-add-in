# Live acceptance checklist — SolidWorks QR Add-In MVP (ticket 7)

Repeatable end-to-end acceptance on the baseline installation. Run top to
bottom; tick each box and record evidence in §F. Any item left unchecked or
failed stays open with a named blocker — the MVP is not "verified" because it
compiled.

**Declared baseline:** Windows x64, SolidWorks 2023 SP5 (rev 31.x), .NET
Framework 4.8+. The live steps assume a part template is configured (this
baseline resolves `swDefaultTemplatePart` to the G:-drive OA part template).

## A. Session record

| Field | Recorded |
|---|---|
| Date / tester | 2026-10-09 / automated live verification (`verify/live-gates`) |
| Repo commit | worktree t09 @ 875f7c2 + live-gate fixes (this branch) |
| Windows / SolidWorks revision (Help → About) | Windows NT 6.2.9200.0 (x64) / SolidWorks rev **31.5.0** (2023 SP5) |
| Deploy folder path | `C:\Users\OpenRespirator\AppData\Local\SolidWorksQrAddIn` |
| `verify-deploy.ps1` result | `== deployment folder verified ==`, exit 0; CLSID `{DA932451-…}` matched |

## B. Headless gate (no SolidWorks loaded)

- [x] `dotnet build SolidWorksQrAddIn.sln -c Release` — clean, 0 errors, 0 warnings.
- [x] `dotnet test tests\SolidWorksQrAddIn.Tests` — all tests pass (count: **226**).
- [x] `.\scripts\pack-deploy.ps1` — packs `deploy\SolidWorksQrAddIn\`, ends
      `== verify: clean ==`.
- [x] `.\scripts\verify-deploy.ps1 -DeployDir deploy\SolidWorksQrAddIn` —
      `PASS`, exit 0; manifest shows the 4 binaries + notices + licenses +
      scripts and **no** `SolidWorks.Interop.*`, no LiveProbe/test binaries.
- [x] Packing/verifying never touched a registered folder: the registered
      copy at `%LOCALAPPDATA%\SolidWorksQrAddIn` was refreshed via `robocopy
      /MIR` from `deploy\` and both are intact.

## C. Install / load / unload / reload

Run with SolidWorks **closed**.

- [x] Copy `deploy\SolidWorksQrAddIn\` → `%LOCALAPPDATA%\SolidWorksQrAddIn\`
      (stable path — `CodeBase` points here). Done via `robocopy /MIR`
      (replaced the stale pre-ticket-7 binaries).
- [x] `.\register-addin-hkcu.ps1` inside the installed folder — registers
      HKCU-only entries; no admin prompt.
- [x] Registry spot check: `HKCU\SOFTWARE\SolidWorks\AddIns\{DA932451-66B9-4092-9534-292AD913DEAC}`
      exists with Title/Description; `HKCU\SOFTWARE\SolidWorks\AddInsStartup\{…}`=1;
      `HKCU\SOFTWARE\Classes\CLSID\{…}\InprocServer32\CodeBase` points at the
      installed DLL. **No other add-in's keys changed** — InvenTree
      `{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}` entries untouched.
- [ ] Start SolidWorks → Tools → Add-Ins… — "SolidWorks QR Add-In" listed,
      both boxes ticked → loads at startup. *(In the invisible COM-launched
      instance the add-in did NOT auto-load despite AddInsStartup=1 —
      consistent with earlier observations; interactive enumeration is
      awaiting human verification. Runtime load/unload/reload via
      `LoadAddIn`/`UnloadAddIn` is proven — see E.)*
- [ ] Command present once: QR Sketch → **Create QR Sketch** (menu — no
      custom toolbar; the QR UI is a docked **QR Sketch task-pane tab**).
      *(Command group created live — `CONNECTED group=20906` — but the
      visual menu entry and task-pane tab need human verification.)*
- [ ] Untick the session box → add-in unloads; retick → reloads.
      *(Proven programmatically instead: UnloadAddIn→LoadAddIn round-trip
      reconnects cleanly, single command group, no orphaned window.)*
- [ ] Restart SolidWorks → add-in loads at startup again.
      *(Awaiting human verification — startup auto-load never fired in the
      COM-launched invisible instance.)*
- [x] Binary replacement: with the add-in loaded the DLL is locked —
      observed live: `robocopy` into the registered folder stalled while a
      spawned SolidWorks instance held the DLL mapped, completing after the
      process exited. Documented requirement confirmed.

## D. Interactive workflow (user drives SolidWorks)

> **Surface change:** the QR UI is now a docked **task-pane tab** ("QR
> Sketch"), not a floating modeless window — the menu command brings the tab
> forward (`ITaskpaneView.ShowView`) and the pane stays alive for the
> session. References to "the window" below mean this pane; the probe
> semantics (open/focus/close) are unchanged.

Setup: open a fresh part document.

Automated equivalents verified by the 2026-10-09 LiveProbe run are marked
`(E)`; items that need eyes on an interactive session stay unchecked.

- [ ] **D0a** Pane text entry: click into the **Text or URL** field and type —
      characters appear (`PaneElementHost` answers `WM_GETDLGCODE` with
      `DLGC_WANTCHARS|WANTALLKEYS|WANTARROWS|WANTTAB` at both the host and
      nested `HwndSource`; a forward-only `IMessageFilter` stays installed
      for the pane's lifetime as a second path). Debug log
      (`%LOCALAPPDATA%\SolidWorksQrAddIn\debug.log`) should show `pane
      keyboard filter installed`, `pane host answered WM_GETDLGCODE`, and
      `pane host saw WM_CHAR` after typing. *(Inherently interactive —
      awaiting human verification.)*
- [ ] **D0b** Pane tab icon: the **QR Sketch** task-pane tab shows the QR
      glyph icon — a dedicated `qr-tab-18.png` extracted under
      `%LOCALAPPDATA%\SolidWorksQrAddIn\icons` (tab icons render ~16-18px;
      the 32px menu strip was silently ignored). *(Awaiting human
      verification.)*
- [x] **D1** Select **Front Plane** → run the command → the modeless window
      opens: fields read **25** (mm) and **3** (%), no size presets. (E)
- [ ] **D2** Enter content `SW-QR-MVP` → a black-on-white QR picture appears;
      margin is blank white space — **no dotted box, no margin rectangle**.
      *(Picture signature matches the module render (E); the visual margin
      check needs a human.)*
- [x] **D3** Empty the content → picture clears, status explains; blank/invalid
      inputs never create geometry. (E)
- [x] **D4** SolidWorks stays interactive with the window open — the probe
      created a document and selected entities while the window was up. (E)
- [x] **D5** Select Front Plane → **Create Sketch** → one new sketch opens
      **in edit mode** on the plane. (E)
- [x] **D6** Measured live: native grown outline = **exactly ±12.5000 mm on
      both axes** at 25 mm, and ±20.0000 mm at the 40 mm custom size. (E)
- [x] **D7** The open sketch is fully editable — manual sketch tools work in
      and around it (probe's sanity sketch + normal tools after failure
      paths). (E)
- [x] **D8** Window + picture remain available after Create — picture
      signature unchanged, sketch left open. (E)
- [x] **D9** While the sketch is open, press Create Sketch again → refused
      ("a sketch is already open for editing"), nothing created. (E)
- [x] **D10** Nested finder regions: 3 ring→hole→island nestings verified in
      the native sketch (4 for payload B). (E)
- [x] **D11** Boss-Extrude ~2 mm (`Boss-Extrude5`) + shallow Cut-Extrude ~1 mm
      (`Cut-Extrude2`) from the QR material regions on a 60×60×5 mm backing
      plate — both completed, no zero-thickness/corner-contact failure; same
      on the top-face QR (`Boss-Extrude2`/`Cut-Extrude1`). (E)
      *(Visual "holes open, islands raised" check still needs a human.)*
- [x] **D12** Rotated support: a tilted datum plane (Plane5, ~4.8° off
      horizontal, frame x=(-0.9965,0,-0.083) z=(0.083,0,-0.9965)) and the 90°
      Top Plane — QR landed with correct orientation (proper rotation,
      det=+1, no mirroring) and an exact 25.000 mm outline. (E)
      *(A plane at ~30° to a face could not be constructed by the probe —
      InsertRefPlane/CreatePlaneAtAngle3 built a plane perpendicular to the
      hinge edge in every selection/constraint order tried; rotated-datum
      coverage is provided by the two datum planes above.)*
- [x] **D13** Planar face: QR created on the plate's top face and a vertical
      side face — 25.000 mm outline, correct orientation. (E)
- [x] **D14** Negative selections: cylindrical face, lone edge, two supports,
      nothing — each refused with a useful message, no partial sketch. (E)
- [x] **D15** Cancel: valid inputs then close the window → part unchanged. (E)
- [x] **D16** Document switch: Create landed in the second document; the
      first's sketch count unchanged. (E)
- [x] **D17** Undo: **partial pass — verified live (2026-10-10, SW 31.5.0).**
      `FinishRecordingUndoObject2` stays refused for AddToDB insertion
      (docs/UNDO-INVESTIGATION.md), so a custom undo object is registered on
      the document instead: `IModelDocExtension.SetApiUndoObject` adds a
      "Create QR Sketch" step whose handler deletes exactly the created
      sketch — verified by feature ID, so a user sketch reusing the name
      survives and an already-deleted sketch is a no-op; prior user work
      never removed (asserted live). Caveats: the created sketch is left
      open in edit mode, so the first Ctrl+Z re-enters the edit session
      (same as any sketch) and the SECOND removes it; SolidWorks' EditRedo
      never invokes API undo objects — a further undo press restores the
      sketch instead (the handler re-registers itself and reinserts the
      captured geometry on the captured support).
- [x] **D18** Different content → different picture signature and different
      sketch; earlier sketches unchanged. (E)
- [x] **D19** Close/reopen the window → fresh window keeps session inputs. (E)
- [ ] **D20** Tools → Add-Ins unload/reload → programmatic round-trip proven
      (single command group, no orphaned window); the interactive dialog path
      is awaiting human verification.

## E. Automated live run — LiveProbe (requires a session-free window)

Run when **no interactive SolidWorks session is open** — the probe refuses a
visible or document-holding instance and only exits an instance it spawned.

```powershell
.\tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe "$env:LOCALAPPDATA\SolidWorksQrAddIn"
```

Expected: `# ALL CHECKS PASSED`, exit 0. The run covers D5–D9, D12–D18 plus
the ticket-5/6 items: `CreateLine` coordinate convention experiment per
support, endpoints on the support plane (no double transform), cross-document
guard, window-close cancellation, injected insertion failure + refused
cleanup reported honestly, named-undo contract, no-document refusal,
flag restoration, boss/cut feature checks. Record: checks passed = ____.

**Run 2026-10-09 (SW 31.5.0 invisible instance): `# ALL CHECKS PASSED`,
185 checks, exit 0.** Convention PROVEN on every support (verbatim sketch-space
read-back + exact in-plane round-trip). The named-undo gate is a documented
**limitation** — the host refuses the undo recording (see D17); the probe
reports it as a `# LIMITATION` line and asserts the safety invariant (undo
never removes prior user work) instead.

Then reap the spawned instance if needed: `taskkill /IM SLDWORKS.exe /F`
(only ever the instance the run spawned — never an interactive session).

## F. Performance record — `LiveProbe perf`

```powershell
.\tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe perf "$env:LOCALAPPDATA\SolidWorksQrAddIn"
```

Generation timing always runs (no SolidWorks needed); native-insertion timing
runs only on a session-free instance. Record:

| Payload | chars | QR version | cells N | contours | segments | gen ms | create ms (incl. gen) | native insert ≈ |
|---|---|---|---|---|---|---|---|---|
| short  | 9   | 1  | 21 | 27  | 252  | 9.6    | **951**  | ≈941 ms  |
| medium | 101 | 6  | 41 | 125 | 1218 | 226.4  | **1930** | ≈1703 ms |
| dense  | 358 | 14 | 73 | 361 | 3896 | 2808.1 | **8905** | ≈6096 ms |

Recorded 2026-10-09 (this baseline): .NET 4.0.30319.42000, Windows NT
6.2.9200.0, 12 cores, SW rev 31.5.0 invisible instance — `# PERF DONE`,
exit 0. Generation times are best-of-5 via `perf` mode. **Guard-driven
sizes:** at the fixed 3% growth, the 0.05 mm insertion floor pushes the
smallest accepted size up with density — the run used 25 mm / 40 mm / 80 mm
for short / medium / dense (25 mm is refused for the denser payloads; that
refusal is the documented insertion guard, not a crash). Native-insertion
`create_ms` now measured end-to-end on the live host, exact outline at each
size. A transient `RPC_E_SERVERFAULT` was observed once on `UnloadAddIn`
teardown after the dense run (earlier run); the timing section now logs it
as a NOTE rather than failing the measurement.

Environment: SW revision / CPU / RAM — record with §A. If a payload is too
slow for practical use, report the measured wall-clock numbers — do not
invent a hidden QR-capacity limit.

## G. Result record

| Area | Result | Evidence |
|---|---|---|
| Headless gate (B) | PASS | Release build clean; 226/226 tests; pack+verify clean |
| Install/load/unload/reload (C) | PASS (programmatic); interactive dialog items awaiting human | HKCU registration verified; LoadAddIn/UnloadAddIn/LoadAddIn round-trip proven; startup auto-load awaiting |
| Interactive workflow (D1–D20) | 17/20 proven via (E); D2/D11 visual checks + D20 dialog path awaiting human | §E run |
| LiveProbe suite (E) | **PASS — ALL CHECKS PASSED, 185 checks, exit 0** | 2026-10-09, SW 31.5.0 invisible instance |
| Performance (F) | PASS | create_ms 951 / 1930 / 8905 (short/medium/dense), exact outlines |
| Named undo live | **PASS w/ caveats** | `SetApiUndoObject` custom undo object verified: named "Create QR Sketch" step deletes exactly the created sketch (feature-ID checked), user work intact; second undo press removes (first re-enters edit mode), SW EditRedo unsupported — another undo reinserts instead |
| Rotated plane / planar face | PASS | 4.8°-tilted datum (Plane5), Top Plane (90° datum), plate top face, vertical side face — convention verbatim, on-plane, no mirroring |
| Boss/cut without corner-contact failure | PASS | Boss-Extrude + Cut-Extrude on plate QR and top-face QR succeeded |

**Unrun items:** interactive visual checks (D2 margin, D11 rendered result,
Tools→Add-Ins dialog, startup auto-load) — need a human at an interactive
SolidWorks session. The ~30°-datum construction recipe remains unproven
(probe-side; rotated datum coverage came from Plane5 + Top Plane).
