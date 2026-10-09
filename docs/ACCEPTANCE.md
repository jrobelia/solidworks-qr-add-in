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
| Date / tester | |
| Repo commit | |
| Windows / SolidWorks revision (Help → About) | |
| Deploy folder path | |
| `verify-deploy.ps1` result | |

## B. Headless gate (no SolidWorks loaded)

- [ ] `dotnet build SolidWorksQrAddIn.sln -c Release` — clean, 0 errors.
- [ ] `dotnet test tests\SolidWorksQrAddIn.Tests` — all tests pass (count: ____).
- [ ] `.\scripts\pack-deploy.ps1` — packs `deploy\SolidWorksQrAddIn\`, ends
      `== verify: clean ==`.
- [ ] `.\scripts\verify-deploy.ps1 -DeployDir deploy\SolidWorksQrAddIn` —
      `PASS`, exit 0; manifest shows the 4 binaries + notices + licenses +
      scripts and **no** `SolidWorks.Interop.*`, no LiveProbe/test binaries.
- [ ] Packing/verifying never touched a registered folder: if the add-in is
      already installed, its `deploy\` output differs from the registered
      path and both are intact.

## C. Install / load / unload / reload

Run with SolidWorks **closed**.

- [ ] Copy `deploy\SolidWorksQrAddIn\` → `%LOCALAPPDATA%\SolidWorksQrAddIn\`
      (stable path — `CodeBase` points here).
- [ ] `.\register-addin-hkcu.ps1` inside the installed folder — registers
      HKCU-only entries; no admin prompt.
- [ ] Registry spot check: `HKCU\SOFTWARE\SolidWorks\AddIns\{DA932451-66B9-4092-9534-292AD913DEAC}`
      exists with Title/Description; `HKCU\SOFTWARE\SolidWorks\AddInsStartup\{…}`=1;
      `HKCU\SOFTWARE\Classes\CLSID\{…}\InprocServer32\CodeBase` points at the
      installed DLL. **No other add-in's keys changed** — InvenTree
      `{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}` entries untouched.
- [ ] Start SolidWorks → Tools → Add-Ins… — "SolidWorks QR Add-In" listed,
      both boxes ticked → loads at startup.
- [ ] Command present once: QR Sketch → **Create QR Sketch** (menu and/or
      toolbar). Only that one command — no probe/failure-injection entries.
- [ ] Untick the session box → add-in unloads; retick → reloads: command
      appears exactly once, no duplicate menu items, no orphaned QR window.
- [ ] Restart SolidWorks → add-in loads at startup again.
- [ ] Binary replacement: with the add-in loaded the DLL is locked — exit
      SolidWorks, replace files, restart. Documented requirement confirmed.

## D. Interactive workflow (user drives SolidWorks; window stays modeless)

Setup: open a fresh part document.

- [ ] **D1** Select **Front Plane** → run the command → the modeless window
      opens: fields read **25** (mm) and **3** (%), no size presets.
- [ ] **D2** Enter content `SW-QR-MVP` → a black-on-white QR picture appears;
      margin is blank white space — **no dotted box, no margin rectangle**.
- [ ] **D3** Empty the content → picture clears, status explains; blank/invalid
      inputs never create geometry. Restore `SW-QR-MVP`.
- [ ] **D4** SolidWorks stays interactive with the window open — select
      entities, rotate the model.
- [ ] **D5** Select Front Plane → **Create Sketch** → one new sketch opens
      **in edit mode** on the plane.
- [ ] **D6** Measure the native grown outline: **exactly 25.000 mm** wide on
      both axes — the entered size is the grown outline excluding margin.
      (Probe check `CheckOutline` covers this automatically; manual evidence:
      Tools → Measure across outermost sketch edges, or a Smart Dimension on
      the overall extents.)
- [ ] **D7** Drag a contour / add a line with normal sketch tools — the open
      sketch is fully editable.
- [ ] **D8** Window + picture remain available after Create — the picture is
      the *generated reference*: edit the sketch and confirm the picture does
      **not** track the manual change (it reflects the generated geometry).
- [ ] **D9** While the sketch is open, press Create Sketch again → refused
      ("exit the current sketch"), nothing created. Exit the sketch.
- [ ] **D10** Nested finder regions: the three finder rings show as
      ring→hole→filled-island contours in sketch and picture.
- [ ] **D11** Contour usability (evidence only — the command ships sketch-only):
      select the QR material contours by region pick and create a
      **Boss-Extrude ~2 mm** on a 60×60×5 mm backing plate, and a shallow
      **Cut-Extrude ~1 mm** — both complete with **no zero-thickness /
      corner-contact failures**. Visual check: holes open, islands raised/etched.
- [ ] **D12** Rotated support: create a reference plane at ~30° to a plate
      face (or reuse the probe's), select it, Create → QR lands on it with
      correct orientation (not mirrored) and 25.000 mm outline.
- [ ] **D13** Planar face: select a flat face of the plate → Create → QR on
      the face, correct size/orientation.
- [ ] **D14** Negative selections: a cylindrical face, a lone edge, two
      supports at once, and nothing selected — each refused with a useful
      status message and no partial sketch.
- [ ] **D15** Cancel: set valid inputs then close the window without Create →
      part unchanged.
- [ ] **D16** Document switch: open a second part, select its Front Plane,
      Create → the sketch lands in the **second** document; the first is
      untouched.
- [ ] **D17** Undo: after a successful create, exit the sketch and Undo once —
      the generated sketch is removed by the one "Create QR Sketch" step;
      prior user work stays.
- [ ] **D18** Different content → different picture and different sketch;
      earlier sketches unchanged.
- [ ] **D19** Close/reopen the window → fresh window keeps session inputs.
- [ ] **D20** Tools → Add-Ins unload/reload again → single command, no
      orphaned window; restart works.

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

Then reap the spawned instance if needed: `taskkill /IM SLDWORKS.exe /F`
(only ever the instance the run spawned — never an interactive session).

## F. Performance record — `LiveProbe perf`

```powershell
.\tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe perf "$env:LOCALAPPDATA\SolidWorksQrAddIn"
```

Generation timing always runs (no SolidWorks needed); native-insertion timing
runs only on a session-free instance. Record:

| Payload | chars | QR version | cells N | contours | segments | gen ms | create ms (incl. gen) |
|---|---|---|---|---|---|---|---|
| short  | 9   | 1  | 21 | 27  | 252  | 10.3   | awaiting live window |
| medium | 101 | 6  | 41 | 125 | 1218 | 215.5  | awaiting live window |
| dense  | 358 | 14 | 73 | 361 | 3896 | 2629.5 | awaiting live window |

Recorded 2026-10-09 (this baseline): .NET 4.0.30319.42000, Windows NT
6.2.9200.0, 12 cores. Generation times are best-of-5 via `perf` mode.
**Guard-driven sizes:** at the fixed 3% growth, the 0.05 mm insertion floor
pushes the smallest accepted size up with density — the perf run used
25 mm / 40 mm / 80 mm for short / medium / dense (25 mm is refused for the
denser payloads; that refusal is the documented insertion guard, not a crash).
Native-insertion `create_ms` is awaiting a session-free SolidWorks window —
an interactive session (visible, 200 open docs) correctly triggered the
refuse-if-visible guard during this run.

Environment: SW revision / CPU / RAM — record with §A. If a payload is too
slow for practical use, report the measured wall-clock numbers — do not
invent a hidden QR-capacity limit.

## G. Result record

| Area | Result | Evidence |
|---|---|---|
| Headless gate (B) | | |
| Install/load/unload/reload (C) | | |
| Interactive workflow (D1–D20) | | |
| LiveProbe suite (E) | | |
| Performance (F) | | |
| Named undo live | | |
| Rotated plane / planar face | | |
| Boss/cut without corner-contact failure | | |

**Unrun items:** (name blocker — e.g. "interactive SolidWorks session
running; awaiting session-free window")
