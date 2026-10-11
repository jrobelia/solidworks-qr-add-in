# Spec 10 — live block-instance gate results

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
taskkill /IM SLDWORKS.exe /F   # before AND after — SLDWORKS lingers
tools\LiveProbe\bin\x64\Release\net48\SolidWorksQrAddIn.LiveProbe.exe blockqr
```

Unit tests at the time of the run: **306 passed** (`dotnet test
tests\SolidWorksQrAddIn.Tests`).
