# Spec 13 — cell-shape live gate results (ticket #36)

Harness: `tools/LiveProbe` mode `shapes` against the deployed add-in.
Required environment: SolidWorks closed (no `SLDWORKS.exe` process) so the
harness can spawn its own session-free invisible instance, plus a deployed
`SolidWorksQrAddIn.dll` carrying the spec-13 probe surface — `shape=` and
`keepFn=` in `WindowStateProbe` (tickets #32–#34) **and** the `viewStatus=`
field added for these gates (the pane's rendered status line).

## Status — BLOCKED at the occupied-seat refusal (live half pending)

On the recording date a `SLDWORKS.exe` process was already running (the
engineer's interactive session — PID 14248; the refusal is unchanged across
seats, an earlier recording saw PID 51188). Per the harness convention the
run **refused to attach**: every module-side expectation was computed and
printed first, then the refusal landed:

```
# spec-13 live gates — cell shapes (ticket #36)
# gate A (dots): 'SW-QR-MVP' @ 25mm circle dot=50% -> N=21 contours=228 segs=1368 dots=228 minEdge=0.2577mm
# gate B (rounded): 'SW-QR-MVP' @ 60mm growth=10% radius=10% keepFn -> N=21 contours=27 segs=592 minEdge=0.0549mm | refusal at 25mm/3%: Corner radius 10.0% of the grown cell's half-width opens a groove where neighbouring cells merge of about 0.0273 mm — below the recorded 0.05 mm insertion floor. Stay at or under 5.7% (corners merge flush) or raise the radius past 13.6%.
# gate B carve-out discriminator: finder-ring outer verts keepFn=4 all-rounded=20
# gate C candidate table (first accepted rung per combo, MaxSegmentCount=8000):
#   short  square  param=0% growth=3% @ 25mm -> VALID v1 N=21 segs=252
#   short  rounded param=10% growth=3% @ 60mm -> VALID v1 N=21 segs=1108
#   short  rounded param=10% growth=10% @ 25mm -> VALID v1 N=21 segs=484
#   short  rounded param=30% growth=10% @ 25mm -> VALID v1 N=21 segs=1108
#   short  circle  param=50% growth=n/a (inert under Circle) @ 25mm -> VALID v1 N=21 segs=1368
#   short  circle  param=70% growth=n/a (inert under Circle) @ 25mm -> VALID v1 N=21 segs=1596
#   short  circle  param=90% growth=n/a (inert under Circle) @ 25mm -> VALID v1 N=21 segs=1824
#   short  circle  param=95% growth=n/a (inert under Circle) @ 25mm -> VALID v1 N=21 segs=1824
#   medium square  param=0% growth=3% @ 40mm -> VALID v6 N=41 segs=1218
#   medium rounded param=10% growth=3% @ 120mm -> VALID v6 N=41 segs=3664
#   medium rounded param=10% growth=10% @ 40mm -> VALID v6 N=41 segs=1661
#   medium rounded param=30% growth=10% @ 40mm -> VALID v6 N=41 segs=3664
#   medium circle  param=50% growth=n/a (inert under Circle) @ 25mm -> VALID v6 N=41 segs=3432
#   medium circle  param=70% growth=n/a (inert under Circle) @ 25mm -> VALID v6 N=41 segs=4290
#   medium circle  param=90% growth=n/a (inert under Circle) @ 25mm -> VALID v6 N=41 segs=5148
#   medium circle  param=95% growth=n/a (inert under Circle) @ 60mm -> VALID v6 N=41 segs=7722
#   dense  square  param=0% growth=3% @ 80mm -> VALID v14 N=73 segs=3896
#   dense  rounded param=10% growth=3% @ 160mm -> INVALID TooManySegments
#   dense  rounded param=10% growth=10% @ 80mm -> VALID v14 N=73 segs=5350
#   dense  rounded param=30% growth=10% @ 60mm -> INVALID TooManySegments
#   dense  circle  param=50% growth=n/a (inert under Circle) @ 25mm -> INVALID TooManySegments
#   dense  circle  param=70% growth=n/a (inert under Circle) @ 25mm -> INVALID TooManySegments
#   dense  circle  param=90% growth=n/a (inert under Circle) @ 40mm -> INVALID TooManySegments
#   dense  circle  param=95% growth=n/a (inert under Circle) @ 80mm -> INVALID TooManySegments
#   mid    square  param=0% growth=3% @ 60mm -> VALID v9 N=53 segs=2090
#   mid    rounded param=10% growth=3% @ 120mm -> VALID v9 N=53 segs=6002
#   mid    rounded param=10% growth=10% @ 60mm -> VALID v9 N=53 segs=2789
#   mid    rounded param=30% growth=10% @ 40mm -> VALID v9 N=53 segs=6002
#   mid    circle  param=50% growth=n/a (inert under Circle) @ 25mm -> VALID v9 N=53 segs=5752
#   mid    circle  param=70% growth=n/a (inert under Circle) @ 25mm -> VALID v9 N=53 segs=5752
#   mid    circle  param=90% growth=n/a (inert under Circle) @ 40mm -> INVALID TooManySegments
#   mid    circle  param=95% growth=n/a (inert under Circle) @ 60mm -> INVALID TooManySegments
# gate C rung: medium circle param=95% @ 60mm -> segs=7722 (bound 8000)
# gate D rung: dense circle param=50% @ 25mm -> The generated QR would draw 8040 sketch segments — over the recorded 8000-segment insertion guard (the provisional sketch-edit-responsiveness bound of docs/INSERTION-GUARD.md). Shorten the content, pick a lighter error-correction level, or use a squarer cell shape — square cells deliver the fewest segments per cell.
# gate C module generation: gen_ms=8948.4 (best of 5)
# FATAL: the spec-13 shape gates require SolidWorks closed — a SLDWORKS.exe
process is already running. Refusing to attach; the probe never mutates or
kills an occupied seat.
```

(The `# FATAL` line is wrapped here for readability — the harness prints it
as one line.)

## What the live half will check (check list of the `shapes` mode)

To re-run: close SolidWorks, refresh the deploy folder
(`robocopy deploy\SolidWorksQrAddIn "$env:LOCALAPPDATA\SolidWorksQrAddIn" /MIR`),
then

```powershell
& tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe shapes $deploy
```

Exit code 0 = every check passed; the run ends by closing its document,
unloading the add-in, and `ExitApp` on the spawned instance only.

**Surface plumbing** — the answering build must report `shape=`/`keepFn=`/
`viewStatus=` in `WindowStateProbe` (stale registered builds are unloaded and
replaced with the deployed DLL, the xsettings route). Then: pane opens
through the command entry; a fresh session reports `shape=square`,
`keepFn=False`, `shapeParam=10`; `SetQrCellShapeProbe("bogus")` returns
`ERROR unknown cell shape` and the selection stays `square`.

**Gate A — disjoint dots** (`SW-QR-MVP` @ 25 mm, Circle, dot 50% — module:
228 disjoint dots / 1368 segments / minEdge 0.258 mm):

- `SetQrContentProbe`, `SetQrCellShapeProbe("circle")`,
  `SetQrShapeParamProbe("50")` — each live verdict asserted against the
  module's computed verdict (never a guessed response).
- `WindowStateProbe` reports `shape=circle`, the rendered pane follows
  (`viewShape=circle`, growth field disabled, parameter relabelled "Dot
  size"); `PreviewSignatureProbe` equals the module's dotted render.
- Create on Front Plane succeeds; block census (one instance at Scale 1,
  host sketch carries no enumerable segments, def folds the 1368 module
  edges + 8 construction edges, material span = module extent, 1:1 edge
  lengths); sketch exits edit mode.
- **Per-cell region selection** on the committed sketch: a native
  `ISketchRegion` census must find all 228 dot regions and each must be
  *individually* selectable (each region selected alone). If the block-form
  sketch does not enumerate regions, the harness explodes the block as a
  diagnostic fallback and re-censuses — the record then says which route
  held.
- On a 60×60×5 mm backing plate, a subset (a quarter of the dots) is
  appended-selected and `FeatureExtrusion3` produces a boss — no contact
  failures.
- The committed sketch is re-entered, the block exploded, and every
  exploded material contour must match a module contour's bounding box 1:1.

**Gate B — rounded square** (`SW-QR-MVP` @ 60 mm, growth 10%, radius 10%,
keep-function-squares on — module: 27 contours / 592 segments / minEdge
0.055 mm):

- The shape-parameter text is first reset to "10" under Circle (it persists
  verbatim across shape switches — a 50% leftover would carry radius 50%).
- `SetQrCellShapeProbe("rounded")` at the still-initial 25 mm/3% request is
  refused exactly as the module says — `BelowInsertionLimit`, naming the
  merge-groove bound and what clears it (the #32 feasible-domain note made
  live: no radius resolves at 25 mm/3%).
- Size 60 mm → growth 10% → radius 10% → keepFn on: each step's live
  verdict asserted module-identical; a malformed `bogus` parameter is
  refused `InvalidShapeParameter`.
- `WindowStateProbe` reports `keepFn=True shape=rounded` and the rendered
  checkbox `viewKeepFn=True`; the preview equals the module's
  rounded+carve-out render.
- Create succeeds; block census vs the module's keepFn output; sketch
  exits.
- The material regions enumerate on the committed sketch (explode fallback
  recorded if not); on a fresh backing plate the material regions drive a
  2 mm `Boss-Extrude` and a 1 mm shallow `Cut-Extrude` — both must land,
  no contact failures.
- **Carve-out, natively**: after explode, the finder ring's outer contour
  must carry exactly the square primitive's 4 edges — the module's
  all-rounded counterfactual emits 20 for the same extent, so the check
  proves function cells emitted square.
- Module-identical 1:1 exploded contour census as in gate A.

**Gate C — dense rung at the bound** (auto-picked: `medium` @ 60 mm, Circle
dot 95% — **7722 native segments**, just under `MaxSegmentCount=8000`):

- Drives shape/param/size/content through the pane; the final verdict is
  asserted module-identical.
- `CreateQrFromWindowProbe` timed end-to-end; the native block-definition
  census must carry the module's segment count exactly (7722 material
  segments).
- Timings recorded with exactly what each measured:
  `gen_ms` (module generation, best of 5 — measured 8948/10446 ms on the
  two blocked-run recordings of this rung),
  `create_ms` (pane create round-trip), `native_insert_ms≈create−gen`,
  `def_census_ms`, `exit_ms` (committing the open sketch), `enter_edit_ms`
  (re-selecting the feature + `InsertSketch`), `region_solve_ms`
  (`GetSketchRegionCount`/`GetSketchRegions` in edit — the solve region
  picking waits on), `seg_enum_ms` (`GetSketchSegments`), `exit2_ms`,
  `doc_rebuild_ms` (`EditRebuild3` after exit).
- These are API-level timings on an invisible instance — a proxy for
  interactive edit feel, not a literal drag test; that is what the
  INSERTION-GUARD disposition below is based on.

**Gate D — one rung over the bound** (`dense` @ 25 mm, Circle dot 50% —
module: **8040 segments**, 0.5% over 8000):

- The final field edit returns `INVALID TooManySegments` with the
  actionable message; `hasRequest=False`; the pane's rendered status line
  (`viewStatus=`) carries the refusal verbatim — the "8000-segment" text.
- `CreateQrFromWindowProbe` returns `ERROR`, the feature tree gains no
  sketch, and no sketch is left open — rejection without host mutation.

## Headless evidence (ran green)

- `dotnet build SolidWorksQrAddIn.sln -c Release` — clean, 0 errors
  (2 pre-existing CS8602 warnings in `Program.cs` DimExp code + 1
  pre-existing CS8600 in `QrIconLocations.cs`, all pre-dating this ticket).
- `dotnet test tests\SolidWorksQrAddIn.Tests` — **561 passed, 0 failed**
  (unchanged — this ticket adds no new Core logic; the harness computes its
  expectations through the existing `QrSketchRequest`/`QrSketchGenerator`
  seam and the only add-in change is the probe-string field below).

Add-in/harness additions this ticket:

- `QrPaneView.InfoSnapshot` gained two trailing fields: `viewKeepFn=` (the
  rendered carve-out checkbox) and `viewStatus=` (the pane's status line
  verbatim — last because it is free text). This is how gate D proves the
  `TooManySegments` rejection surfaces on the pane, and how the
  discriminator detects a stale deployed build.
- `tools/LiveProbe` gained the `shapes` mode described above. Every live
  verdict is asserted against the module's own computed verdict; every
  module-side expectation (fixture censuses, the candidate table, the
  picked rungs, generation timing) prints BEFORE the occupied-seat refusal.

## MaxSegmentCount evidence — what the candidate table established

The table above is measured module truth (not estimates) for the deployed
generation code: `SegmentCount` per (payload, shape, size, parameter) over
the real perf payloads plus a ~v9 "mid" payload. It confirms the #35
analysis where they overlap and sharpens it:

- The just-under rung is a **v6 circle at dot 95% / 60 mm → 7722**; the
  just-over rung is a **v14 circle at dot 50% / 25 mm → 8040** — the bound
  sits where intended: between a hundred-char URL with fat dots (legal) and
  a dense shaped matrix (refused).
- A ~v9-class rounded payload lands **6002** segments (~mid rounded rows);
  squares stay under through v14 (3896) — matching the ~4400 headroom
  estimate; the provisional bound keeps every recorded square run legal.
- Circle dots at fixed payload **shrink in count when the polygon coarsens**
  (K follows cell size: 25 mm v6 dots are K=6…8 hexagon-ish → 3432–5148; at
  60 mm the same payload needs K≈14 → 7722) — size, not just payload,
  moves a circle request across the bound.

The **live half remains**: native insertion time at 7722 segments and the
sketch-edit timings (enter/exit/region-solve/rebuild). Only those numbers
decide whether 8000 stands, rises (re-admitting ~v23 squares at 9934), or
tightens. See `docs/INSERTION-GUARD.md` §"For ticket #36".

## Limitations / carry-overs

- Not yet run live: this doc records the harness and the module-side
  expectations; the PASS/FAIL table above fills in on the first
  session-free run.
- Region selection through the Scale-1 block sketch is asserted first
  (the engineer's route); if SolidWorks does not enumerate `ISketchRegion`s
  for block-internal geometry the run records the explode-fallback route
  explicitly rather than hiding it — either outcome is informative.
- The dense rung's `gen_ms≈9–10.4s` is module cost (the disjoint-dot union
  is O(cells) with no merge wins); `create_ms` includes it, so insertion
  time is reported both end-to-end and net.
