# Spec 10 — live block-instance gate results

## UI/document-unit follow-up — fa5d951

**Accepted build:** `SolidWorksQrAddIn-fa5d951-Setup.exe`.
**Manual evidence:** the engineer approved the UI/unit update and reported
"manual qa passes" after installing the updated build.

The accepted scope is the document-unit size label/input and information
refresh, physical-size-preserving unit conversion, single-unit dimensions,
and QR code / quiet zone / overall size wording. Headless Release tests:
**318 passed, 0 failed**.

This closes the UI follow-up's manual verification item. It does not claim
that the automated `units` mode was rerun successfully or close the separate
round-5 block-instance gates below.


## Round 5 — real-size definition, Scale as factor (ADR 0004)

**Driver (interactive, engineer-reported):** with the unit-proportioned
definition (`Scale = 25` carrying the size), selecting the QR sketch and
clicking Extrude produced SolidWorks' "doubly nested contour" prompt and
no cell region could be picked. Control experiments: **explode the
add-in's block and re-make a block by hand (real-size def, `Scale = 1`)
→ extrudes; and on the same pre-fix sketch, lowering `Scale` restores
extrusion.** Verified on the unit def: works at `Scale = 1` and `2`,
broken at `10` and `25` — the cutoff is between 2 and 10, mechanism
unexplained. The fix is to emit the user-made-block shape: definition at
the staged size, instance at `Scale = 1`; the PM Scale field is now a
resize factor (a later Scale edit may re-break picking — explode or
re-insert then).

Updated `blockqr` gates (`Program.cs`):

- `Scale ≈ 25.0000` → `Scale ≈ 1.0` (def carries the real 25 mm size)
- def construction segment lengths: QR square side `0.001` → `0.025` m,
  keep-out side `keepSide` (≈0.0344 m) instead of `keepSide ÷ 25`
- Scale-edit gate drives a factor: `Scale = 1.6` (⇒ ±27.5 mm keep-out,
  same picks) instead of `40.0`
- "size surfaces" gate: the pane cell-size readout only (Scale is a
  factor, not a size)

Live re-verify **pending** (needs a SolidWorks-free window); headless
suite green at the change. Historical rounds below describe the state at
their dates — the `Scale=25`/unit-def evidence was correct then and is
superseded by ADR 0004.

## Round 4 — construction geometry INSIDE the block definition (ADR 0003)

Harness: `tools/LiveProbe` mode `consexp` against **SolidWorks 2023 SP5
(revision 31.5.0)**, spawned session-free (invisible, killed after the
run). Fixture: a ±0.5-doc-unit solid square plus a ±0.7-doc-unit
construction-flagged square folded into ONE block definition — the same
shape the product now emits (unit contours + unit squares).

**Verdict: ALL CHECKS PASSED** — the deciding fact for the round-4 design.

| Gate | Result | Evidence |
|---|---|---|
| fixture drew 4 solid + 4 construction segments | PASS | `solid=4 cons=4 flagged=4` |
| block formed with the construction square inside | PASS | 1 definition, 1 instance |
| def holds 8 segments — exactly 4 construction | PASS | `segs=8 construction=4` |
| def construction square is larger than the solid | PASS | `consLen=1.400mm solidLen=1.000mm ratio=1.400` |
| **construction geometry inside the def RENDERS on the instance** | **PASS** | at Scale=100: picks hit x=52/58/65/70 mm, miss at 74/80/95 mm — solid content ends at ±50 mm, only the construction square renders to ±70 mm |
| Scale edit rescales the def-internal square | PASS | at Scale=40: hits at 24/28 mm, miss at 32/45/70 mm → extent shrank to ±28 mm |
| drag carries the def-internal square | PASS | `InstancePosition(10,-5)` → pick hits the displaced edge at (38,-5) mm |
| explode executes + yields all 8 def segments | PASS | `raw=8` |
| exploded construction lines KEEP the flag | PASS | `flagged=4 of 8` |
| exploded cons square lands at the projected extent | PASS | half ≈ 17.5 mm at Scale=25 (±0.7 def-units × 25) |
| construction content never feeds a profile | PASS | `GetSketchRegionCount` unchanged through explode |

### Frame caveat (harness, not product)

The def sketch reports segment endpoints in an **offset frame** (observed
at the def bbox's bottom-centre), so def-content verification reads
segment **lengths** and rendered **extents** — never raw coordinates. The
coordinate picks also return a fixed proxy edge (round-2 finding), so
hit-testing extents — not proxy identity — is the render proof.

### Consequences folded into the product

- `SwSketchInsertion` draws the staged unit-space squares
  construction-flagged into the definition fold before
  `MakeSketchBlockFromSelected` — the same `Select4` selection as the
  contours. Host-side squares, coincident binds, display dims, and
  equations are gone.
- `blockqr` gates rewritten for the new world: def seg census + flags,
  rendered-extent pick scans for render/scale/drag tracking, zero dims /
  zero equations checks, explode flag preservation.

### `blockqr` product gates — 2026-10-12 run (route A, in-proc pane path)

Verdict: **54 PASS / 1 FAIL** — the single fail is the long-standing
`EditUndo2`-from-the-harness gate (rounds 1–2 same); the custom undo
object itself verified below.

| Gate group | Result | Evidence |
|---|---|---|
| one block instance, Scale=25, at origin | PASS | `Scale=25.0000` `inst=(0,0)` |
| host sketch carries NO enumerable segments | PASS | `hostSegs=0` |
| def folds contours + 8 construction segs | PASS | `defSegs=260 construction=8` (252+8) |
| construction segs = unit QR + unit keep-out squares | PASS | `short=4/4 long=4/4 lens=[4×1.000, 4×1.377]mm` |
| no display dims / no equations | PASS | `dims=0 eqs=0` |
| construction geometry renders to the keep-out edge | PASS | picks hit 13/15/17 mm, miss 19/22 mm |
| drag carries the keep-out square | PASS | `InstancePosition(10,-5)` → hit at (27,-5) mm |
| Scale=40 resizes the squares | PASS | hits 21/25/27 mm, miss 30 mm (new keep-out ±27.5 mm) |
| extrude on the block-bearing sketch | PASS | `ISketchRegion.Select` → `Boss-Extrude` |
| explode: instance gone, 252 raw + 8 flagged cons | PASS | `raw=252 cons=8 of 260`, extrude on exploded works |
| named undo step via harness `EditUndo2` | **FAIL** | `DeleteSelection2 failed for 'Sketch2'` — the same post-explode-context fail as rounds 1–2; see `undoapi` below |
| prior user work survives undo presses | PASS | marker sketch intact |
| equation manager stays empty | PASS | `eqCount=0` — no litter to dangle |
| supports: planar face, Top Plane 90° | PASS | 1 instance at origin, `hostSegs=0`, `defCons=8`, def squares centred on def content |

### `undoapi` — 2026-10-12 run (in-proc, new build)

**ALL PASSED**: `removed=True restored=True userIntact=True` —
EditUndo removes exactly the QR sketch through the custom
`SetApiUndoObject` handler, a further undo re-fires the toggle and
reinserts the sketch with its def-internal squares, and the pre-existing
user sketch survives everything. The blockqr `EditUndo2` fail is a
harness-context artifact (out-of-proc presses after the explode gate),
not a product defect — the handler's `DoUndo` fires and logs the delete
attempt.

Unit tests at the time of the run: **301 passed**.

### Re-verify after the code-review fixes — 2026-10-11 (commit `1770aaf`)

`blockqr` re-run on the post-review build (authoritative `StagedSpan` scale,
honest construction-flag propagation, sized-metre square staging):
**54 PASS / 1 FAIL — identical to the baseline, no regression.** Def census
`260 segs / 8 construction`, `Scale=25.0000`, squares render/track drag and
`Scale=40`, explode `252 raw + 8 construction`, `ISketchRegion.Select`
extrudes block-bearing and exploded sketches, face/datum supports centred,
`dims=0 eqs=0`. The 1 FAIL is the same `EditUndo2`-from-harness flake —
this run surfaced as "sketch survived 40 presses" rather than the earlier
`DeleteSelection2 failed` text; `undoapi` remains the authoritative
undo check (ALL PASSED). Unit tests: **303 passed**.



## Round 2 — 2026-10-11 (ticket #19, round 2)

Harness: `tools/LiveProbe` modes `blockqr` + `undoapi` + `bindexp` against
**SolidWorks 2023 SP5 (revision 31.5.0)**, spawned session-free (invisible,
killed after the run). Same fixture as round 1: `SW-QR-MVP`, N=21, M=29,
25 mm, pitch ≈ 1.18708 mm, keep-out ≈ 34.4255 mm.

### Verdict: 55 gates — 49 PASS, 6 FAIL

All six remaining fails are the **tracking** gates plus the named-undo step.
Round-1's creation, undo-cleanup, and support regressions are fixed.

| Fail | Evidence |
|---|---|
| drag carries both squares | centroid `(0,0)` after `InstancePosition` edit — squares do not follow |
| `Scale` edit re-sizes squares | `Scale=40` accepted; halves stay `[12.500, 17.213]` |
| driven dim re-measures + keep-out tracks | `D1=25.00`, `D2=34.43` unchanged after scale |
| gap dims keep keep-out centred | `D3=D4=4.71` unchanged |
| host geometry tracks block (coincident verified) | pick@12.5 and pick@20 both select — no corner bound |
| named "Create QR Sketch" undo step | `FinishRecordingUndoObject2` refused in every arrangement (round-1 finding stands); the custom `SetApiUndoObject` step is what actually works — verified below |

### Bug fixes verified live

- **Bug 2 (dangling equations)** — `QrSketchUndoObject` now removes the
  created sketch's equations before deleting the feature.
  `undoapi` gate: `removed=True restored=True userIntact=True`,
  equation count 0 after deletion. ALL PASSED.
- **Bug 3 (redo/reinsert forms no block)** — `RealizeAsBlockInstance` now
  selects each drawn contour segment by object (`Select4`, count-verified)
  instead of `SelectAll`; the custom-undo restore path recreates the full
  block (`restored=True` in `undoapi`).
- **Bug 1 (binding to block base point)** — see the experiment matrix below:
  every public route was tried; a correct bind is **impossible** with the
  current API surface, so the QR square is now drawn at the exact staged
  extents with **no** coincident binds (the round-1 "base-point coincident"
  defect is gone — squares land at `halves=[12.500, 17.213]` exactly).
- **Bug 4 (extrusion)** — 9 API routes were probed (see matrix);
  `ISketchRegion.Select` extrudes both the block-bearing sketch and the
  exploded raw lines. No product redesign needed.

### Supports — all PASS

Front Plane, planar face (`Boss-Extrude1` top), and Top Plane (90° datum)
all create the QR sketch, keep one block instance centred at the sketch
origin, land both squares centred (`centroid=(0,0)`), keep every endpoint on
the support plane (`max |perp|=0`), and resolve the reference entity
(seltype 2/4). The custom 30° ref plane remains a harness gap (all three
plane-creation APIs returned null or wrong planes); Top Plane covers the
rotated-datum gate.

## Host↔block binding — round-2 experiment matrix

Goal: pin the QR square's edges/corners to the block instance's four extent
lines so a `Scale` or `InstancePosition` edit drags the square (spec #10's
tracking requirement). Every route was run live; "binds at" states where the
solver places the host entity.

| # | Route | Result | Verdict |
|---|---|---|---|
| 1 | `ISketchRelationManager.AddRelation` coincident, any entity pair involving block internals | throws `RPC_E_SERVERFAULT` | dead |
| 2 | Stashed block-edge proxy + `Select4` append + `SketchAddConstraints` | silently no-ops — relation never lands | dead |
| 3 | Host corner `Select4` + coordinate `SelectByID2(SKETCHSEGMENT)` pick + `sgCOINCIDENT` (sketch/model coords) | lands an **arbitrary** block edge; coincident binds corner onto that edge's *projected* line — never the extent line (252-seg block) | dead |
| 4 | Same, pick coords transformed into **definition space** | lands the right def edge but the coincident binds at its **raw def coords** — def `y=-0.168 mm` → host `y≈0` | dead |
| 5 | `Select4` on definition-sketch segment object + coincident | selects in host context (E33) but binds at raw def coords (corner → `(0,0)`) | dead |
| 6 | Driving **0-gap dimension** square-edge↔picked edge, self-verified by the dim's own value | pick landed 90°/270° angular or wrong linear dims every attempt; dim-value verification rejected all | dead (works mechanically, can't aim) |
| 7 | Same with `Select4` def-segment object | dim measures the *projected* gap (≈inset) but the solve pins the edge at **raw def** position (`-0.17 mm`) | dead |
| 8 | `SelectByID2` type `EXTSKETCHSEG` | same as SKETCHSEGMENT — arbitrary internal edge, binds its projection | dead |
| 9 | `SelectByRay` (sketch-normal ray, radius 0.2 mm, types 10/24) | selects nothing — block internals not ray-hittable | dead |
| 10 | `ISketchBlockInstance.Select` + dim to the whole instance | dim reads 15 mm (instance centre-ish), not the bbox edge | dead |
| 11 | Pick coords swept just outside the def's raw bbox ("defside" picks, 5 margins × corner quarters) | still arbitrary internal edges | dead |
| 12 | `SketchUseEdge2` on the object-selected extent def segment | converts to a host line at **raw def position** (`-0.34 mm`), not the projected line | dead |
| 13 | `ISketchBlockInstance.GetAttachedEntities` | returns false/null | dead |
| 14 | Inset-vs-exact square draw + pick-order variants, `AddToDB` on/off | no effect on any of the above | — |

**Root cause (round-2 finding):** SolidWorks exposes block-internal entities
to a host sketch **only at their raw definition coordinates**. Object
handles (`Select4`), coordinate picks that land inside the def-raw zone, and
`SketchUseEdge2` all yield definition-space objects whose constraints
resolve at def coords (±0.5 mm here). Coordinate picks outside that zone
yield instance-space wrappers that *do* bind at projected positions — but
the pick evaluator cannot be aimed at a specific internal segment of a
252-segment block (it returns arbitrary internals), so the right extent edge
is unreachable. The projected instance drawing has no bindable identity.

**Fallback implemented:** the QR square is drawn at the exact staged extents
(merges + orientation + equal relations keep it square), the driven size dim
and the three keep-out equations still land, and the squares represent the
footprint correctly — they simply do not follow a later `Scale`/drag edit.
The 5 tracking gates above remain open and would need a SolidWorks API that
exposes instance-projected entities (or an interactive-only path — the same
coincident is believed to work through the UI; not verified this round).

## Extrusion routes — round-2

`TryExtrudeByName` tries nine routes in order; the **first** that returns a
feature wins:

| Route | Result |
|---|---|
| sketch-feature select + `FeatureExtrusion3` | null |
| block-instance object select + `FeatureExtrusion3` | null |
| `SKETCHREGION` coord picks (contour selection on) | regions refused/null |
| sketch-feature + `FeatureExtrusionThin` | null |
| sketch-feature + legacy `FeatureExtrusion` | null |
| `SimpleFeatureBossExtrude` | null |
| `SKETCHCONTOUR` picks + `swSelEXTSKETCHSEGS` marks | null |
| **`ISketchRegion.Select` per-region objects** | **PASS — `Boss-Extrude5`; consumed 8 material regions (faces=30)** |
| sketch-feature + `FeatureCut4` | not reached (route 8 won) |

`ISketchRegion.Select` also extrudes the **exploded** raw lines
(`Boss-Extrude9`). The product's extrude-via-region design is unchanged.

## Remaining work / open items

- Tracking gates (5 fails) — blocked on a SolidWorks route that exposes
  instance-projected block entities; the matrix above is the full survey.
- Named undo step — `FinishRecordingUndoObject2` refuses; the custom
  `SetApiUndoObject` path covers delete+restore (`undoapi` green).
- Custom 30° ref plane creation — harness gap, not product.

Unit tests at the time of the run: **307 passed**
(`dotnet test tests\SolidWorksQrAddIn.Tests`).

### Experiment: def-internal dims + last binding probes (round 3)

Harness: `tools/LiveProbe` mode `dimexp` (session-free invisible
SolidWorks 2023 SP5, rev 31.5.0, fresh mm part — `doc.GetUnits()=[0,1,0,2,0]`).
Fixture: inner square ±0.0005 m (the 1×1-doc-unit convention) + outer square
±0.002 m, each corner-merged, H/V, equal-sided, BL corner `sgFIXED`; dims via
`IAddDimension2` (both prompt toggles off). The first dim per square lands
**driving** (`st=2`); every subsequent over-defining dim lands **driven**
automatically (`st=1`) — the prompt-off path works inside a future block def
exactly as in a host sketch. `ext.SelectAll()` → `MakeSketchBlockFromSelected`
carried all five dims into `Block1` (`GetDisplayDimensionCount()=5`).

**E1 — does a def-internal dim display the SCALED value on the instance? NO.**
With `inst.DimensionDisplay = true`, every read-back of every def dim is
identical at Scale 1, 25 and 40:

| Dim (def-space) | S1 | S25 | S40 |
|---|---|---|---|
| D1 driving, inner side | 1.00 mm | 1.00 mm | 1.00 mm |
| D2 driven, inner side | 1.00 mm | 1.00 mm | 1.00 mm |
| D3 driving, outer side | 4.00 mm | 4.00 mm | 4.00 mm |
| D4 driven, outer side | 4.00 mm | 4.00 mm | 4.00 mm |
| D5 driven, inner-top↔outer-top gap | 1.50 mm | 1.50 mm | 1.50 mm |

`IDimension.SystemValue`, `GetUserValueIn`, `GetSystemValue3`,
`IDisplayDimension.GetText(0/3/4)`, `Scale2`, `GetOverrideValue` — all
def-space, all unchanged. Decisive: `IDisplayData.GetTextAtIndex` (the
rendered text) returns `"1.00mm"`/`"4.00mm"`/`"1.50mm"` (+ dual ".039in" etc.)
at every scale — the dim's value string is baked in def space; the instance
transform scales the glyph GEOMETRY, not the number. Neither the host sketch
feature nor the def sketch feature enumerates the block's dims
(`GetFirstDisplayDimension` chain) — the only reachable dim object is the
def's own. Writing `dd.Scale2 = 25` is silently refused (read-back `1.0000`).

**E2 — a def-internal dim driven by the def's own geometry: same answer.**
The gap dim (def-internal, driven by two def entities' distance) is `1.50 mm`
at Scale 1, 25, 40 — def-space, driven, never scaled.

**E3 — `SketchUseEdge2`: the converted edge is a FROZEN def-space copy.**
(a) coordinate pick on the instance-projected edge (y=4 mm at Scale 2)
selects a block edge (`seltype=10`, reads def coords `(±2,2) mm`) and
`SketchUseEdge2(false)` returns `True` but enumerates **zero** new host
segments (`segs 0→0`). (b) `Select4` on the def-sketch's line object
(`def.GetSketch().GetSketchSegments()` — `def.GetLines()`/`sketch.GetLines()`
return `Double[]` coordinates, not objects) + `SketchUseEdge2` **does** create
a host segment — at raw def coords `(2,2)→(−2,2) mm` — and it stays at those
coords through `Scale=2→4→1`. A host line `sgCOLINEAR` onto it rides at
y=2 mm, equally frozen. The copy is static geometry, not a live projection.

**E4 — base-point anchors bind position, do not track.** With the instance
displaced to (10,10) mm, `SKETCHPOINT` pick @(10,10) returns the base point
(`seltype=11`, reports `(0,0)` def coords); pick @(0,0) returns **nothing** —
the earlier (0,0) hits were the base point all along. `sgCONCENTRIC` on a
host circle ↔ base point snaps the circle onto (10,10); a coincident on a
square-centre midpoint ↔ base point slides the whole square onto it
(BL 20,20 → 0,0). Re-dragging the instance to (20,10) leaves both at (10,10)
even after `EditRebuild3` + sketch re-entry — **no live drag tracking** — but
a `Scale` write re-solves and re-snaps them to the base point's current
position (centre/BL jump to (0,0)/(-10,-10) at `Scale=3` after restoring the
position). Position-only re-anchoring on solve, never scale tracking.

**Verdict for the fallback design:** there is no spec-compatible "live
readout of Scale on the sketch". Def-internal dims render def-space values
(the user sees `1.00mm` on a 25 mm instance); converted edges are frozen
def-space copies; base-point anchors re-snap on solve but carry no scale
information. The only surfaces that reflect Scale remain the block instance's
own `Scale` PropertyManager field and the add-in pane's cell-size readout.
The round-2 fallback stands: squares + dims drawn at the staged extents are a
create-time footprint; tracking `Scale`/drag edits is not achievable through
the current API surface.

---

# Round 1 — earlier run (kept for history)


Ticket #19. Harness: `tools/LiveProbe` mode `blockqr` against **SolidWorks
2023 SP5 (revision 31.5.0)**, spawned session-free (`Activator.CreateInstance`,
invisible, killed after the run). Fixture `SW-QR-MVP`: N=21, working matrix
M=29, requested size 25 mm, pitch ≈ 1.18708 mm, keep-out side ≈ 34.4255 mm,
keep-out ratio ≈ 1.37702.

## Route that actually ran

**ROUTE A — in-process.** The deployed per-user build
(`%LOCALAPPDATA%\SolidWorksQrAddIn`) answers the `BlockSpikeProbe(-1)`
discriminator (`PING … apt=MTA`) and drives `SetQrContentProbe` +
`CreateQrFromWindowProbe` — the pane's real path. The earlier blocker was the
stale machine-wide (`HKLM`) registration shadowing the per-user one; adding a
versioned `InprocServer32\1.0.0.0` subkey under `HKCU` makes the new build win
— **no elevated/machine-wide registration was needed**. The out-of-process
reflective route (ROUTE B) still exists as a fallback and shows the same
failures, so they are product-level, not harness artifacts.

## Verdict: 55 gates — 45 PASS, 10 FAIL

PASS means the interactive-equivalent evidence was observed live.

### Creation and block instance — all PASS

| Gate | Evidence |
|---|---|
| add-in connected | `CONNECTED group=20906` |
| prior-work marker sketch exists | `Sketch1` |
| Front Plane selected / create succeeds | `OK Sketch2 … 252 segments` |
| sketch left open in edit mode | — |
| exactly ONE block instance | `count=1` |
| instance readable | `Block1-1` |
| `Scale` = requested 25 mm | `Scale=25.0000 Scale2=25.0000 Angle=0` |
| instance at sketch origin | `(0.000,0.000) mm` |
| block definition reachable, unit-sized | `1×1 doc unit, 252 segments` |
| only construction squares enumerable | `segs=8 construction=8` — block internals hidden |
| four display dims | `D1=25.000 st=2 (driven) · D2=34.425 · D3=D4=4.713` |
| dim naming | `D1@Sketch2@Part1.Part` |
| 3 keep-out equations in manager | `D2/D3/D4 = D1 × ratio / × growth` |
| equations reference QR dim multiplicatively | verified in text |

### Geometry tracking — FAIL (1 root cause)

| Gate | Verdict | Evidence |
|---|---|---|
| squares land at staged extents | FAIL | landed `halves=[17.213, 25.000]` expected `[12.500, 17.213]`; the QR square sits at `(-25,0)→(0,0)→(0,25)→(-25,25)` — its corner pinned to the origin |
| drag carries both squares | FAIL | centroid `(-3.894, 3.894)` after `InstancePosition` edit |
| Scale edit re-sizes squares | FAIL | `Scale=40` accepted; halves unchanged `[17.213, 25.000]` |
| driven dim re-measures + keep-out tracks | FAIL | `D1=25.00` still after `Scale=40`; `D2=34.43` unchanged |
| gap dims keep keep-out centred | FAIL | `D3=D4=4.71` unchanged |
| host geometry tracks block (coincident) | FAIL | corner pick@12.5 still selects after scale |

**Root cause (confirmed by in-probe experiments):**
`SwSketchInsertion.BindEdgeToBlock` (~line 401) picks block boundary vertices
with `SelectByID2("", "SKETCHPOINT", x, y)`. The pick returns `True` but the
object is the block instance's **base point** — read back at
`(0.165, 0.332) mm` in definition space (≈ the instance origin) — not the
vertex at the picked coordinates. Every square edge's coincident therefore
lands on the single anchor point, which drags the whole 25 mm square so that
a corner sits at the origin while H/V relations preserve its size. The
keep-out square's binds land differently (its ±17.213 mm extents are correct).

Experiments also showed:
- `SelectByID2("", "SKETCHSEGMENT", edge-mid)` **does** select a block edge
  (`seltype=10`), but `sgCOLINEAR` and `sgCOINCIDENT` against it silently
  no-op — a host line/corner point did not move.
- A coincident to the block base point does apply (probe line moved) — the
  base point is the only bindable block geometry found.
- A square's own corner point coincident to a block edge (`SKETCHSEGMENT`
  pick + `Select4` point) did not move — block-internal segments are not
  constrainable this way.

**Fix direction for the next ticket:** do not rely on block-internal
vertex/segment selection. Alternatives: bind to the instance base point only
(tracks drag, not scale), or drop coincident binding and drive square
position/size through the equation/dimension path.

### Feature use — mixed

| Gate | Verdict | Evidence |
|---|---|---|
| block-instance sketch extrudes | FAIL | `route=all-routes-failed(regions=1)` — sketch-feature select, `ISketchBlockInstance.Select`, and `EnableContourSelection`+`SKETCHREGION` (1 region selected!) all yield `FeatureExtrusion3 = null` |
| sketch re-enters edit with block | PASS | — |
| `ExplodeSketchBlockInstance` executes | PASS | — |
| explode removes the instance | PASS | `remaining=0` |
| explode yields raw QR lines | PASS | `raw=252` |
| exploded lines carry QR extent | PASS | bbox `[-12.50,-12.50]→[12.50,12.50]` mm — exactly the 25 mm QR |
| exploded lines extrude | FAIL | `route=all-routes-failed(regions=1)` — even exploded, `FeatureExtrusion3` returns null (a region does select) |

`FeatureExtrusion3` itself works in the same session — the backing plate
`Boss-Extrude1` was created with it. The spike (`BLOCK-SPIKE.md` Q1)
verified explode→extrude for a single square. The 252-segment QR sketch —
block or exploded — produces no boss through any API route tried; whether
the interactive workflow succeeds needs a manual check in the SolidWorks UI
(the QR region topology may need per-contour selection or a thin-feature
flag the route doesn't cover).

### Undo — PASS with caveats

| Gate | Verdict | Evidence |
|---|---|---|
| named "Create QR Sketch" undo removes the sketch | PASS | gone after `presses=14` (walks back through post-create edits) |
| prior user work survives | PASS | marker `Sketch1` intact |
| undo-toggle restore | caveat | `restored=False` — the handler re-registers and a further `EditUndo2` **does** re-fire `DoUndo → RecreateSketch`, but reinsert fails in the undo callback: `MakeSketchBlockFromSelected formed no block definition` (debug log) |
| equations after the undo | caveat | the deleted sketch's 3 equations **linger** in the equation manager (`"D2@Sketch2…" = "D1@Sketch2…" × 1.377…`) — they dangle and make the next create's `EquationMgr.Add2(-1, eq, solve:true)` refuse: `The equation manager refused '"D2@Sketch4…" = …'` — the undo step should delete its equations |

### Supports — mixed

| Gate | Verdict | Evidence |
|---|---|---|
| backing plate / top face selected | PASS | `Boss-Extrude1` |
| QR create on planar face + left open | PASS | `OK Sketch2` |
| one block instance, at origin, on face | PASS | `count=1 (0.000,0.000) mm` |
| squares centred on face | FAIL | centroid `(-0.4234, 2.9899) mm` — same binding cause |
| every endpoint lands ON the face plane | PASS | `max |perp|=0` — no double transform |
| reference entity resolves to a face | PASS | `seltype=2` |
| Top Plane (90° datum) available/selected/create | PASS | `OK Sketch3` |
| one block instance, at origin, on datum | PASS | `count=1 (0.000,0.000) mm` |
| squares centred on datum | FAIL | centroid `(0.0000, 3.8936) mm` — QR square bottom edge pinned at y=0 |
| every endpoint lands ON the datum | PASS | `max |perp|=0` |
| reference entity resolves to RefPlane | PASS | `seltype=4` |
| custom 30° ref plane | harness gap | `InsertRefPlane`, `CreatePlaneAtAngle3`, `CreatePlaneThru3Points3` all returned null or a wrong plane; Top Plane (90°) covered the rotated-datum gate |

## API facts — confirmed this run

- `GetAddInObject` + `BlockSpikeProbe` discriminator works in-process for
  the deployed build (after the versioned `HKCU` `InprocServer32\1.0.0.0`
  entry — a one-time manual registry step, **not** elevated registration).
- `MakeSketchBlockFromSelected`, `InsertSketchBlockInstance(def, pt, scale,
  angle)`, `ISketchBlockInstance.{InstancePosition, Scale, Scale2, Angle,
  Name, Definition, BlockToSketchTransform}`,
  `ISketch.{GetSketchBlockInstanceCount, IGetSketchBlockInstances}`,
  `ISketchBlockDefinition.GetDisplayDimensions`,
  `ISketchManager.ExplodeSketchBlockInstance` — all work as in the spike.
- `EquationMgr.Add2(-1, eq, true)` adds equations at create; **refuses**
  once the manager holds dangling equations from a deleted sketch.
- `EditUndo2` pops the custom `SetApiUndoObject` step → `DoUndo` fires and
  deletes the created sketch (feature-ID verified); the handler's
  re-register makes a *further* undo press re-fire `DoUndo`
  (`EditRedo` never calls the handler).
- `SelectByID2("", "SKETCHPOINT", …)` on block internals returns the
  instance **base point**, never arbitrary boundary vertices.
- `SelectByID2("", "SKETCHSEGMENT", …)` selects block edges (`seltype=10`)
  but coincident/collinear relations to them silently no-op.
- `SelectByID2("", "SKETCHREGION", …)` can pick a contour region even inside
  the block-bearing sketch, yet `FeatureExtrusion3` still returns null.
- `EditRebuild3()` exits sketch edit mode in this SolidWorks setup.
- `ISketchBlockDefinition` has no `Name`; `ISketchBlockInstance` has no
  `GetTransform`/`IGetTransform` (use `BlockToSketchTransform`).

## Product bugs filed by evidence (for follow-up tickets)

1. **`SwSketchInsertion.BindEdgeToBlock` binds to the block base point**
   (~`src/SolidWorksQrAddIn/Host/SwSketchInsertion.cs:365-423`): squares
   land off-centre and never track `Scale`. All six tracking FAILs trace here.
2. **`QrSketchUndoObject.DeleteCreatedSketch` leaves the sketch's
   equations dangling** (`src/SolidWorksQrAddIn/Host/QrSketchUndoObject.cs:162`):
   the next create's `Add2(solve:true)` refuses. Delete the created
   equations in `DoUndo`.
3. **Undo-toggle reinsert fails in the undo callback** —
   `MakeSketchBlockFromSelected` forms no definition during `DoUndo`
   (`QR undo step failed`, debug log). Either make `RecreateSketch`
   block-safe in-callback or accept undo-only semantics.
4. **No extrusion route for the QR sketch**, block or exploded: needs the
   interactive-UI check or a per-contour/thin-feature route.

## Reproduce

```powershell
dotnet build tools\LiveProbe\LiveProbe.csproj -c Release
scripts\pack-deploy.ps1
robocopy deploy\SolidWorksQrAddIn "$env:LOCALAPPDATA\SolidWorksQrAddIn" /MIR
taskkill /PID <pid> /F   # before AND after — a lingering SPAWNED instance only, never an interactive session
tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe blockqr
```

Unit tests at the time of the run: **306 passed** (`dotnet test
tests\SolidWorksQrAddIn.Tests`).
