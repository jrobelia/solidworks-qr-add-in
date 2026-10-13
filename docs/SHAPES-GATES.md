# Spec 13 — cell-shape live gate results (ticket #36)

Harness: `tools/LiveProbe` mode `shapes` against the deployed add-in.
Required environment: SolidWorks closed (no `SLDWORKS.exe` process) so the
harness can spawn its own session-free invisible instance, plus a deployed
`SolidWorksQrAddIn.dll` carrying the spec-13 probe surface — `shape=` and
`keepFn=` in `WindowStateProbe` (tickets #32–#34) **and** the `viewStatus=`
field added for these gates (the pane's rendered status line).

## Status — ALL 95 CHECKS PASSED, two runs

Ran live against the deployed build (`%LOCALAPPDATA%\SolidWorksQrAddIn`) on
SolidWorks 2023 SP5 (revision 31.5.0, invisible COM-launched instance, no
prior `SLDWORKS.exe`). Exit code 0; final line `# SHAPES ALL CHECKS PASSED`.

Two runs are recorded because the run measured the provisional segment
bound and tightened it on its own evidence:

- **Run 1 — `MaxSegmentCount=8 000`** (the value under test): all 95 checks
  passed; the dense rung at the bound was the v6 circle dot-95% @ 60 mm →
  **7 722 segments / 858 regions**. Its timings are the evidence the
  `MaxSegmentCount` disposition below is based on.
- **Run 2 — `MaxSegmentCount=4 400`** (the tightened, shipped bound): all
  95 checks passed again on an identical check list; the dense rung at the
  bound became the v6 circle dot-70% @ 25 mm → **4 290 segments** (the same
  858 disjoint dots at a coarser K = 5 polygon) and gate D's cheapest
  over-bound rung moved to v6 circle dot-90% @ 25 mm → 5 148 refused.

The candidate tables below are printed by the harness before it touches
SolidWorks — measured module truth through the real `QrSketchRequest`/
`QrSketchGenerator` seam.

## Module expectations (printed before the run)

Run 1 (bound 8 000):

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
# gate C module generation: gen_ms=9031.0 (best of 5)
```

Run 2's table is the same shape with `MaxSegmentCount=4400`: the rows now
refused by count are v6 circle 90%/95%, every `mid` circle (5 752),
`mid` rounded 3% (6 002) and 30% (6 002), `dense` rounded 10% (5 350) —
everything else is unchanged — and the picked rungs are
`gate C: medium circle param=70% @ 25mm -> segs=4290 (bound 4400)` and
`gate D: medium circle param=90% @ 25mm -> 5148 over the recorded
4400-segment insertion guard`.

## Results — run 1 @ bound 8 000 (run 2 identical but for the rung sizes)

Surface plumbing:

| # | Check | Result |
|---|-------|--------|
| 1 | Answering build exposes the spec-13 surface | PASS — `WindowStateProbe` reports `shape=`/`keepFn=`/`viewStatus=` (registered build lacked it → unloaded + `LoadAddIn(deploy)` → 0) |
| 2 | `AddinStateProbe` | PASS — `CONNECTED group=20906` |
| 3 | `OpenQrWindowProbe` | PASS — `open=True windows=1` |
| 4 | Fresh session defaults | PASS — `shape=square keepFn=False shapeParam=10` |
| 5 | `SetQrCellShapeProbe("bogus")` | PASS — `ERROR unknown cell shape 'bogus'`; `shape=square` kept |
| 6 | Part template + document | PASS — `OA Part [mm].prtdot` |

Gate A — disjoint dots (`SW-QR-MVP` @ 25 mm, Circle dot 50%; module:
228 dots / 1 368 segments / minEdge 0.2577 mm):

| # | Check | Result |
|---|-------|--------|
| 7 | content / `circle` / param `50` verdicts | PASS — each live `VALID` module-identical (684 segs at the initial 10% → 1 368 at 50%) |
| 8 | Pane reports Circle | PASS — `shape=circle viewShape=circle viewGrowthEnabled=False viewShapeParamLabel=Dot size` |
| 9 | Preview signature | PASS — sha `afdbcece…` == module's dotted render |
| 10 | Create on Front Plane | PASS — `OK Sketch1`, sketch open |
| 11 | Block census | PASS — 1 instance `Block1-1`, Scale 1.0000, at origin, host sketch 0 enumerable segs |
| 12 | Def census | PASS — `defSegs=1376` = 1 368 material + 8 construction; QR square 25.000 mm + keep-out 34.524 mm centred on content; material span == module extent; **all 1 368 edges module-identical** (maxLenErr 0.000000 mm) |
| 13 | Sketch exits edit mode | PASS |
| 14 | **Per-cell region census on the committed sketch** | PASS — `nativeRegions=228 matched=228 selectable=228` — every dot region individually selectable **through the block sketch** (no explode fallback needed) |
| 15 | Backing plate + 57-dot subset boss | PASS — `Boss-Extrude2` on a ¼ subset of the dot regions, no contact failures |
| 16 | Explode + module-identical contours | PASS — 228/228 material contours matched module bboxes 1:1 |

Gate B — rounded square @ 60 mm, growth 10%, radius 10%, keepFn (module:
27 contours / 592 segments / minEdge 0.0549 mm):

| # | Check | Result |
|---|-------|--------|
| 17 | Param reset to "10" under Circle | PASS — verbatim-field discipline verified live |
| 18 | Rounded refused at initial 25 mm/3% | PASS — `BelowInsertionLimit` naming the 0.0273 mm groove and both escapes (≤5.7% or >13.6%) — the #32 feasible-domain note made live |
| 19 | size 60 → growth 10% → radius 10% → keepFn | PASS — each verdict module-identical |
| 20 | `SetQrShapeParamProbe("bogus")` | PASS — `INVALID InvalidShapeParameter` |
| 21 | Pane reports carve-out | PASS — `keepFn=True shape=rounded viewKeepFn=True` |
| 22 | Preview signature | PASS — sha `14226746…` == module's rounded+carve-out render |
| 23 | Create + block/def census | PASS — `OK Sketch3`; `defSegs=600` = 592+8; 592/592 edges module-identical |
| 24 | Material regions enumerate | PASS — `nativeRegions=27 matched=11 expected=11` on the committed sketch (block route held again) |
| 25 | Boss + shallow cut | PASS — `Boss-Extrude3` (2 mm) + `Cut-Extrude1` (1 mm), no contact failures |
| 26 | Explode + module-identical contours | PASS — 27/27 matched 1:1 |
| 27 | **Carve-out, natively** | PASS — exploded finder-ring outer contour carries the square primitive's **4** edges; the all-rounded counterfactual emits 20 for the same extent — function cells emitted square in the native sketch |

Gate C — dense rung at the bound (run 1: v6 circle dot-95% @ 60 mm,
**7 722 segments**, just under 8 000):

| # | Check | Result |
|---|-------|--------|
| 28 | Rung drives through the pane | PASS — every field response `VALID`, final verdict module-identical (`cells=41 contours=858 segs=7722 size=60.000`) |
| 29 | Session reports the rung | PASS — `shape=circle shapeParam=95 viewShapeParamLabel=Dot size` |
| 30 | Timed create | PASS — `OK Sketch4` |
| 31 | **Native segment census 1:1** | PASS — `nativeMaterialSegs=7722` in the block def |
| 32 | Sketch exits / re-selects / re-enters | PASS — `enter_edit_ms=531` |
| 33 | Edit-responsiveness timings | recorded — see PERF below |

Gate D — one rung over the bound (run 1: dense circle dot-50% @ 25 mm →
**8 040 segments**, 0.5% over):

| # | Check | Result |
|---|-------|--------|
| 34 | Pane rejection | PASS — `INVALID TooManySegments` with the actionable message |
| 35 | `hasRequest=False` | PASS |
| 36 | Status line verbatim | PASS — `viewStatus=` carries the "8 040 … over the recorded 8000-segment" text |
| 37 | Refused create | PASS — `ERROR`, feature tree unchanged, no sketch left open |

PERF — the dense-rung timings, both runs (invisible instance, API-level —
a proxy for interactive edit feel, not a literal drag test):

| timing | run 1 — 7 722 segs / 858 regions (bound 8 000) | run 2 — 4 290 segs / 858 regions (bound 4 400) |
|---|---|---|
| `gen_ms` (module, best of 5) | 9 031 | 3 316 |
| `create_ms` (pane create round-trip) | 59 653 | 21 911 |
| `native_insert_ms` ≈ create−gen | **≈50 622** | **≈18 595** |
| `def_census_ms` (`GetSketchSegments` on the block def) | 43 623 | 24 931 |
| `exit_ms` / `enter_edit_ms` | 72 / 531 | 58 / 530 |
| `region_solve_ms` (`GetSketchRegions`, 858 regions) | **103 109** | **39 965** |
| `seg_enum_ms` (sketch `GetSketchSegments`, warm) | 1 | 1 |
| `exit2_ms` / `doc_rebuild_ms` | 40 / 3 218 | 37 / 2 763 |

## MaxSegmentCount disposition — TIGHTENED 8 000 → 4 400

The live half decided the provisional bound. At the 7 722-segment rung the
native insert took ≈50.6 s — 4× over the ~12 s the ~1.4 ms/segment square
slope projected, because disjoint dots pay per *contour* (≈6.6 ms/segment)
as well as per segment — and the region solve the sketch does for region
picking stalled **≈103 s** on the 858 disjoint dots. That is past the
responsiveness wall the bound exists to hold, so **the bound is tightened
to 4 400** — just above the largest live-proven insert class (the
3 896–4 022-segment dense square runs, ≈6 s native) — keeping every
recorded square payload legal while refusing the dense shaped requests the
measurement convicts. The re-run at the shipped bound recorded the just-
under rung at 4 290 segments: insert ≈18.6 s, region solve ≈40 s — still
the heaviest single interaction by an order of magnitude, and the reason
the doc now names a region/contour-count ceiling as the sharper follow-up
instrument. Full reasoning and tables: `docs/INSERTION-GUARD.md` §"Segment
ceiling — MaxSegmentCount".

## Headless evidence (ran green)

- `dotnet build SolidWorksQrAddIn.sln -c Release` — clean, 0 errors
  (2 pre-existing CS8602 warnings in `Program.cs` DimExp code + 1
  pre-existing CS8600 in `QrIconLocations.cs`, all pre-dating this ticket).
- `dotnet test tests\SolidWorksQrAddIn.Tests` — **567 passed, 0 failed**
  at the tightened bound (was 561 before the integration-branch merge; the
  two fixtures that straddled the new bound — the v7 inverted-dot carve-out
  run at ~6 312 segments and the v2 inverted composition at ~4 564 — were
  re-aimed at sizes/versions that stay legal; the bound's refusal of the
  old rung is itself the disposition working, recorded in the test
  comments).

## Limitations / carry-overs

- The `region_solve_ms` figure is `GetSketchRegions` on a re-entered
  sketch — the API cost a region pick waits on. It is the honest proxy the
  disposition was scoped for ("enter/exit sketch edit, drag a segment,
  rebuild" of #35), but it is not a literal interactive drag test.
- Region solve tracks vertex count on top of region count: the same 858
  disjoint dots solved 103 s at K = 9 (7 722 segs) and 40 s at K = 5
  (4 290 segs). Segment count bounds it — loosely.
- `seg_enum_ms=1` is the warm-call figure (the def census already paid the
  cold enumeration — that 43.6 s/24.9 s number is the true cold cost).
- Both runs left a spawned `SLDWORKS.exe` alive after `ExitApp` (known
  quirk — `BUILDING.md`); killed with `taskkill /IM SLDWORKS.exe /F` after
  verifying it was the harness-spawned instance.
- The `icons/` folder `/MIR` purges from the registered directory is
  runtime-reextracted by `QrToolbarIcons.EnsureExtracted` — not part of the
  deployment manifest, so harmless.
