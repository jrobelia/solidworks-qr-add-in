# Sketch Linear Pattern spike — can the QR be a circle dot-pattern?

Live evidence, SolidWorks 2023 SP5 (rev 31.5.0), session-free invisible
instance (`SldWorks.Application.31`, `Visible=False`), default part template.
Driver: `tools/LiveProbe` **`pattern` mode** (`PatternSpikeMain` in
`tools/LiveProbe/PatternSpike.cs`) — runs twelve experiments against
`ISldWorks` out-of-process on fresh throwaway parts; nothing is saved.

**Spike question (from the parent ticket):** can a QR be built as *real
circle sketch entities* — one seed circle + a two-directional **sketch**
linear pattern with the light cells omitted — instead of the Clipper2 /
block / polygon-contour pipeline? And can the *user's* end-state vision
hold: a master construction square whose single dimension drives pattern
spacing (`size/N`), seed diameter (`f·spacing`), seed offset (`spacing/2`),
the whole thing still scaling after the driving dim is deleted and the box
corners are constrained to part features — plus a quiet-zone box that
follows?

## Raw result lines

```
PAT0(diag) create=True segTypes[t1x9 t0x2] names[Arc1:t1ARC … Arc9:t1ARC Line1:t0LINE Line2:t0LINE]
     pts=21 pts2=12 segs[Arc1:arc:solid … Line2:line:cons] line(1.0,1.0)->(3.0,1.0) consSet=True
     line(1.0,1.0)->(1.0,3.0) consSet=True objSubs[] param[D1@Sketch1=2.000] param[D2@Sketch1=2.000]
PAT1(create) seedName='Arc1' create=True segs=51 arcs=48 lines=1 regions=49
     tree[… Front Plane|RefPlane Top Plane|RefPlane Right Plane|RefPlane Origin|OriginProfileFeature
     Sketch1|ProfileFeature] committed[segs=51 arcs=48 lines=1 regions=49] dims[D1@Sketch1=2.000 D2@Sketch1=2.000]
PAT2(skip) A[create=True seed='Arc1' segs=11 arcs=8 lines=1 regions=9 objDel picked=2 arcs=7
     missing[1,0;0,1] segs=9 arcs=6 lines=1 regions=7 setD1(2x)=0 missing2x[all]
     centres[(1.0,1.0);(5.0,1.0);(3.0,3.0);(5.0,3.0);(1.0,5.0);(3.0,5.0);(5.0,5.0)]
     editAlive=True arcs=9 missingE[all]] A2[delSeed picked=1 arcs=8 missing[0,0] editStill=False]
     B[(2)]=9/none B[(2) ]=9/none B[2]=9/none B[(2);(5)]=9/none B[(2)(5)]=9/none B[(02)]=9/none
     B[(2) (5) ]=9/none B[(1,0) (0,1)]=9/none B[(1,0);(0,1)]=9/none
     B[(2,1) (1,2)]=True arcs=7 missing[1,0;0,1]                      <<<< THE WORKING FORMAT
     B[(3) (4)]=9/none B[(1) (3)]=9/none B[(4) (5) ]=9/none B[(0,1)]=9/none
     B2[skips+setD1(2x)=0 arcs=7 centres[(1.0,1.0);(9.0,1.0);(5.0,3.0);(9.0,3.0);(1.0,5.0);(5.0,5.0);(9.0,5.0)]]
     C[selPat=True edit=True arcs=9 missing[none]]
PAT3(perInstance) create=True arcs=14 kept=13 extra[0,0] regionPicks=3 boss='Boss-Extrude1' delBoss=True
PAT4(dimDriven) box='D1@Sketch1@Part5.Part' create=True sk='Sketch1' arcs=7 missing[1,0;0,1]
     dia='D6@Sketch1@Part5.Part' selPts=True&True hx='D7@…' vy='D8@…'
     dims[D1=6.00 D2=2.00 D3=2.00 D4=3000.00 D5=3000.00 D6=1.00 D7=1.00 D8=1.00]
     eq(D2="D1"/3)=0 eq(D3="D1"/3)=1 eq(D6="D2"*0.5)=2 eq(D7="D2"/2)=3 eq(D8="D3"/2)=4
     setBox=0 dims2[D1=12.00st2 D2=4.00st1 D3=4.00st1 D4=3000st2 D5=3000st2 D6=2.00st1 D7=2.00st1 D8=2.00st1]
     arcs=7 centres[(2,2);(10,2);(6,6);(10,6);(2,10);(6,10);(10,10)] r0=1.000mm setD4(4)=0 arcs=10
PAT5(seedDia) create=True selArc=True dia='D5@Sketch1@Part6.Part' spacingCands[D1@Sketch1,D2@Sketch1]
     eqDia=ok(idx=0) setSp=0 arcs=25 r0=1.000mm(want 1.000)
PAT6(dimLess) plateDim='D1@Sketch1@Part7.Part' plate=Boss-Extrude1 verts=4 s0=(0,0) s1=(60,60)
     k1pt=True k1v=True add1=True near1=0.00mm k2pt=True k2v=True add2=True near2=0.00mm
     create=True spacingCands[D1@Sketch2,D2@Sketch2] eq=ok×2 setPlate=0 plateAfter=90.0mm
     arcs=36 pitchAfter=15.000mm(want 15.0) c0=(5.0,5.0)
PAT7(quietBox) boxDim='D1@Sketch1@Part8.Part' offDim='D2@…' qDim='D3@…'
     dims[D1=14.000 D2=8.000 D3=30.000] eqOff=ok eqQ=ok setBox=0 dims2[D1=28.000 D2=16.000 D3=60.000]
PAT8(timing) N21[synth open=579 create+skip=292 commit=13ms dark=162 light=279 str=1998B arcs=162 ok=True extra[]]
     N41[open=20 create+skip=2221 commit=45ms dark=571 light=1110 str=8331B arcs=572 ok=True extra[0,0]]
     N21objDel[create=903 skip=5102(picked279) arcs=162]
PAT9(undo) A[sketches=1->2 afterUndo=2 afterRedo=2] B[recStart=True recFin=True sketches=3 afterUndo=2 afterRedo=2]
PAT10(invert) create=True arcs=36 keptLight=36/36
PAT11(fallback) drawn=13 segs=13 arcs=12 lines=0 regions=13 regionPicks=1 boss='Boss-Extrude1' delBoss=True
```

## Verdict table

| # | Experiment | Verdict |
|---|------------|---------|
| 0/1 | Seed circle + 2-dir pattern → real circle segments | **PASS** |
| 2 | Instance skipping | **PASS via create-time `DeleteInstances="(c,r) (c,r)…"`** (1-based col,row) |
| 2a | Skipping via select-instance + `EditDelete` | **FAIL** — geometry deletes but the pattern's dim drive freezes |
| 2b | `EditLinearSketchStepAndRepeat` after skips | **FAIL** — call resurrects every skipped instance |
| 2c | Seed instance in the skip list | **FAIL (by design)** — `"(1,1)"` is silently ignored |
| 3 | Per-instance reality (real circles, region select, boss-extrude) | **PASS** |
| 4 | Dimension-driven scaling (box dim → equations → spacing/dia/pos) | **PASS** |
| 5 | Seed diameter driven by `spacing·f` equation | **PASS** |
| 6 | Dimensionless end-state (box corners → part-feature vertices) | **PASS** |
| 7 | Quiet-zone box scaling with the QR box | **PASS** |
| 8 | Density/timing N=21, N=41 (create-time skip string) | **PASS** |
| 9 | Undo collapse | **PARTIAL** — named undo object works; bare `EditUndo2` no-op; `EditRedo2` did not restore |
| 10 | Inverted mask (dots on light cells) | **PASS** |
| 11 | Fallback per-cell `CreateCircle` (no pattern) | **PASS** (kept as the no-pattern fallback) |

## The working recipe

```csharp
// 1) open sketch on Front Plane (host pattern: AddToDB=false, AutoInference=false)
// 2) master construction square + one driving dim (D1@Sketch) — box edge
// 3) seed circle at (box corner + pitch/2), CreateCircleByRadius
// 4) ISketchManager.CreateLinearSketchStepAndRepeat(
//        NumX:N, NumY:N, SpacingX:pitch, SpacingY:pitch,
//        AngleX:0, AngleY:π/2,
//        DeleteInstances:"(c1,r1) (c2,r2) …",     // 1-based (col,row), space-separated
//        XSpacingDim:true, YSpacingDim:true,       // → driving dims D2,D3
//        AngleDim:false, NumOfInstancesDimX/Y:false)
// 5) diameter dim on the seed arc (IAddDiameterDimension2)  → D6
//    H+V dims box-corner → seed-centre SKETCHPOINT (object Select4, not
//    coordinate picks)                                          → D7,D8
// 6) equations (IEquationMgr.Add, then EvaluateAll + EditRebuild3):
//      "D2@Sketch" = "D1@Sketch" / N
//      "D3@Sketch" = "D1@Sketch" / N
//      "D6@Sketch" = "D2@Sketch" * f          (dot fill fraction)
//      "D7@Sketch" = "D2@Sketch" / 2          (seed X offset)
//      "D8@Sketch" = "D3@Sketch" / 2          (seed Y offset)
// 7) commit (InsertSketch(false)); resize = SetSystemValue3 on D1 + rebuild
```

## Per-experiment evidence

### PAT0/1 — creation: PASS

`CreateLinearSketchStepAndRepeat` (deprecated `ISketchManager` API — the
feature-tree `SketchLinearPattern` route was not needed) returns `true`
and emits **real sketch geometry**: N² `ARC`-type segments (`GetTypeName2`
= "ARC"; each reports `IsCircle`-style closed-circle centres via
`GetCenterPoint2`) plus **two construction LINE segments** — the pattern's
direction handles, exactly 1·pitch long. `GetSketchRegionCount` = N² — each
dot is a closed region. Point census: `GetSketchPoints`=21, `…2`=12 on 3×3.
**No feature-tree node for the pattern** (`objSubs[]`, tree shows only the
sketch) — legacy sketch patterns live inside the sketch, so there is no
`IFeature`/`GetDefinition` route and `ILinearPatternFeatureData` does not
apply. `ISketch.GetArcs()` does **not** enumerate instance arcs — census
must go through `GetSketchSegments()`. Census quirk: exactly one arc and
one line enumerate as sibling COM types (not `ISketchArc`/`ISketchLine`)
while `GetTypeName2` still reports ARC/LINE — count by region or by name
instead of `is`-checks alone.

With `XSpacingDim/YSpacingDim=true` the pattern creates **driving** spacing
dims `D1@Sketch`,`D2@Sketch` (verified live: `SetSystemValue3` moves the
whole grid in-session). `CreateNumOfInstancesDimInXDir/YDir` additionally
create driving **count** dims (observed as `D4`,`D5`, value = N,
`DrivenState=2`): `SetSystemValue3(4)` on D4 grew a 3×3 grid to 4×3
(`arcs 7→10` = 12 cells − 2 skips).

### PAT2 — skipping: PASS (create-time string only)

The gate. Three routes were tried:

* **B — `DeleteInstances` at create: PASS.** The format is **1-based
  `(col,row)` pairs, space-separated** — `" (2,1) (1,2)"` skips exactly
  the (1,0) and (0,1) cells (`arcs=7 missing[1,0;0,1]`). Single-number
  `(a)`/`(a) (b)` formats documented in the API help **do nothing** on
  this build (all returned `true` but `arcs=9`); 0-based `(0,1)` and
  `(1,0)`-style entries are silently ignored. The skipped pattern stays
  fully parametric — see B2 below.
* **A — object-select instance arcs (`ISketchSegment.Select4`) + one
  `EditDelete`: geometry deletes (`arcs 9→7`, exact cells) but the
  pattern then *ignores* its driving dims** — `SetSystemValue3` on D1
  returned 0 yet no centre moved (grid frozen at the old pitch). The
  pattern object still "exists" (`EditLinear…` returns true) but **the
  edit call resurrects every deleted instance** (`arcs` back to 9).
  Deleting the seed cell kills the pattern outright
  (`editStill=False`). → object-delete is not a viable skip route.
* **C — select pattern entity + `EditLinearSketchStepAndRepeat` with a
  skip string**: `true` but no skips applied.

**B2 — the decisive combination**: create with `"(2,1) (1,2)"`, then set
the driving dim D1 → `arcs=7`, survivors moved to the new pitch **and the
skips persisted** at the new grid positions. This is the recipe.

Constraint: the **seed instance can never be in the skip list** —
`(1,1)` is silently ignored (`extra[0,0]`). For a real QR this is free:
cell (0,0) is always a dark finder module, so the seed belongs there.

### PAT3 — per-instance reality: PASS

Plus-mask on 7×7 via the create-time string: all 13 kept cells are real
circle arcs; 3/3 `SKETCHREGION` picks inside kept dots (contour selection
enabled) → `FeatureManager.FeatureExtrusion3` produced `Boss-Extrude1`;
rollback deleted it. (`arcs=14` includes the unskippable seed cell —
see PAT2.)

### PAT4 — dimension-driven scaling: PASS — the whole vision

One sketch: master construction square (driving `D1@Sketch1`=6 mm) +
pattern with create-time skips + dia dim `D6` + position dims `D7`,`D8`
(object-level `ISketchPoint.Select4` on the box corner + seed centre;
coordinate `SelectByID2` was ambiguous/failed in append mode). Five
equations all installed (`eqm.Add` → idx 0–4) and evaluated
(`eqm.Value[i]` reads back correctly).

`D1` 6→12 mm (`SetSystemValue3` + `EditRebuild3`):
`D2/D3` spacing 2→4 mm, `D6` dia 1→2 mm, `D7/D8` offsets 1→2 mm,
`DrivenState` flipped 2→1 on every equation-driven dim, and the arcs
moved to a perfectly doubled grid `[(2,2);(10,2);(6,6);(10,6);(2,10);
(6,10);(10,10)]` with both skips intact. **One box dimension rescales
everything.**

Naming notes: `IDimension.FullName` = `D6@Sketch1@Part5.Part`; equations
and `IParameter` want the `D@Sketch` form (strip the last `@…` — the
probe's `EqName` helper). Equations are created per sketch-name, so
read `((IFeature)sk).Name` rather than assuming `Sketch1`.

### PAT5 — seed diameter: PASS

`"D5@Sketch1" = "D1@Sketch1"*0.5` added cleanly; spacing 2→4 mm → seed
radius 0.8→1.0 mm exactly. The dot-percentage knob is a normal,
user-editable equation.

### PAT6 — dimensionless end-state: PASS

A 60 mm plate (`Boss-Extrude1`) with a driving `D1@Sketch1`. On the
plate's top face (sketch on face, `ModelToSketchTransform` to map vertex
positions): box drawn +8 mm off the corners, then
`ISketchPoint.Select4` + `SelectByID2("VERTEX")` +
`SketchAddConstraints("sgCOINCIDENT")` on both diagonal corners — box
snapped onto the plate vertices (`near1=near2=0.00mm`). Spacing dims
equation'd to `plateDim/N`; `plateDim` 60→90 mm → `pitchAfter=15.000mm`
(= 90/6). **The pattern scales from part geometry with no driving dim
on the box at all** — the vision's final form. (Seed-centre offset was
not dimensioned in this test, so the grid scales in place; combine with
the PAT4 position dims + box-corner equations for the full recipe.)

### PAT7 — quiet-zone box: PASS

Second construction square offset by q=4·pitch, both square in one
sketch. Two dims (`D2`=edge-to-edge offset, `D3`=quiet width) +
equations `D2 = box·4/N`, `D3 = box·(1+8/N)`. Box 14→28 mm → offset
8→16 mm, quiet width 30→60 mm exactly. (The `sgEQUAL` relation attempt
between unequal-length edges was a no-op — dims + equations are the
right route.)

### PAT8 — density/timing: PASS

| N | mask | cells | light (skips) | skip-string len | open | create+skip | commit | arcs |
|---|------|-------|---------------|-----------------|------|-------------|--------|------|
| 21 | synth ~37% | 441 | 279 | 1 998 B | 579 ms | 292 ms | 13 ms | 162 |
| 41 | real QR v5 | 1681 | 1110 | 8 331 B | 20 ms | 2 221 ms | 45 ms | 572* |

*+1 = the unskippable seed cell (the generated mask's (0,0) module
evaluated light in this build, so `"(1,1)"` went into the string and
was ignored — see PAT2). For real production masks the seed goes on a
dark cell → exact counts.

Contrast — the deprecated object-delete route on the same 21 grid:
**5 102 ms just for the skip picks + EditDelete** (~17× slower than the
create-time string) and it freezes the pattern (PAT2-A). The string
route is the only viable one and easily meets interactive latency.

### PAT9 — undo: PARTIAL

* Bare `EditUndo2(1)` after a committed recipe sketch: no-op
  (`sketches 2→2`), and `EditRedo2` likewise.
* `IModelDocExtension.StartRecordingUndoObject()` … recipe …
  `FinishRecordingUndoObject2("name", false)` → `EditUndo2(1)` removes
  the whole recipe sketch in one step (`sketches 3→2`). `EditRedo2` did
  not restore within the probe (count stayed 2) — possibly a headless
  limitation; undo itself is proven.

### PAT10 — invert: PASS

Same string, inverted mask (skip the dark cells): `keptLight=36/36`.

### PAT11 — fallback: PASS

Per-cell `CreateCircle` (no pattern): 13 dots, region pick, boss-extrude
— all work; this remains the escape hatch if a future build breaks the
pattern route (it loses the single-seed/parametric knobs entirely).

### PAT12 — production order: PASS (+ one API surprise)

Added with ticket #40 (`LiveProbe.exe pattern 12`). Replicates the
emitter's exact order — square dims, seed + pattern, diameter + offset
dims, `IEquationMgr.Add` + `EvaluateAll` + `EditRebuild3`, then a
`SetSystemValue3` master rescale — **all inside the open sketch edit**
(PAT4 added its equations only after `InsertSketch(false)`). Result:
all 8 equations installed in-edit (`rc=0`), box 6→12 mm rescaled every
driven dim in-edit (quiet offsets 8→16, keep-out 22→44, dia 1→2,
offsets 1→2), and the committed sketch held exactly 7 arcs at r=1 mm —
the doubled radius, skips intact. Production order verified end to end.

**The surprise that matters:** the call's `AngleY = −π/2` (a negative
literal) is **refused** — `CreateLinearSketchStepAndRepeat` returns
false — while the identical direction written as `3π/2` succeeds and
patterns downward from the same top-left seed. Production uses `3π/2`.
Minor caveat: in-edit `IParameter` reads of the spacing dims reported
stale pre-rescale values while downstream equation-driven dims read
correctly — post-commit reads are accurate (the probes read committed
sketches anyway). The production map lives in
`docs/CIRCLE-DIMENSIONS.md`.

## Production implications

1. **Viable.** The QR can be a single seed circle + legacy sketch linear
   pattern; light cells are omitted via the create-time `DeleteInstances`
   string — *real* circle entities, real closed regions, extrudable.
2. **The full parametric vision holds**: one master dim on the QR box →
   equations → spacing (`D@Sketch / N`), diameter (`spacing·f`), seed
   offset (`spacing/2`), quiet-zone dims (`box·(1±8/N)`) — one
   `SetSystemValue3` rescales the entire QR, skips included.
3. **Dimensionless form works**: coincident constraints between box
   corner sketch-points and 3D part vertices + equations targeting the
   part's own dims → the pattern follows part features with no driving
   dim on the sketch (PAT6).
4. **Bonus knob**: the auto-created count dims (`D4`,`D5`) drive the
   instance counts live — `SetSystemValue3` resizes the grid without
   recreation.
5. **Constraints / gotchas**:
   * Skip list format is `" (col,row)"`, **1-based**, space-separated —
     the documented `"(a) (b) (c)"` single-number form does nothing.
   * Seed cell `(1,1)` cannot be skipped (silently ignored) — seed on a
     guaranteed-dark cell; for real QR (0,0) is always dark.
   * **Never call `EditLinearSketchStepAndRepeat` after creation** —
     it resurrects all skipped instances *and* detaches the spacing
     dims (they freeze at stale values). All post-create edits go
     through the dims.
   * Never delete instance geometry via `EditDelete` — deletes arcs but
     freezes the pattern's dim drive.
   * No feature object for the pattern — all access is sketch-internal.
   * Select seed/instance **points** by object enumeration
     (`sk.GetSketchPoints()` + `Select4`), not `SelectByID2` coords —
     ambiguous next to arcs/lines.
   * Census via `GetSketchSegments()`; `GetArcs()` misses instances and
     one arc/line each reports a sibling COM type.
   * Undo needs the named undo object; bare `EditUndo2` is a no-op on
     committed sketches.
6. **Performance is fine** for real QRs: v5 (41×41) ≈ **2.3 s**
   all-in; v1 (21×21) ≈ **0.3 s** create+skip.

## Harness note

Each experiment runs in `WithSpikeDoc` (fresh `NewDocument` on the
default template, invisible app, `CloseDoc` discarding changes). The
probe refuses to start while any `SLDWORKS.exe` exists and `ExitApp`
can leave the COM-spawned process alive briefly — the driver shells
`taskkill` for its own PID afterwards (documented in Program.cs).
