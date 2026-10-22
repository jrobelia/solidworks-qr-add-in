# Sketch Block Instance spike — can a block carry the QR pattern?

> **Harness retired post-v1.0; code preserved at tag `v1.0.0`.** The
> `block` mode (`BlockSpikeMain`) and the in-proc `BlockSpikeProbe` member
> referenced below were removed in the #65 probe-surface reshape; this
> document remains the design-evidence record.

Live evidence, SolidWorks 2023 SP5 (rev 31.5.0), session-free invisible
instance (`SldWorks.Application.31`, `Visible=False`). Driver:
`tools/LiveProbe` **`block` mode** (`BlockSpikeMain`), which runs the five
experiments directly against `ISldWorks` out-of-process on fresh throwaway
part documents. The in-proc variant exists too
(`IAddInProbe.BlockSpikeProbe(int)` in `SwAddin.Probes.cs`, STA via
`_pane.Invoke`) but was unreachable on this box — see "Harness note" below.

Raw result lines (one run each, doc names vary by run):

```
BLK1 selCount=4 def=ok instInSketch=1 host='Sketch1' instName='Block1-1'
     extrudeSketchSel=null selInst=True selType=114 extrudeInstSel=null
     selRegion=False type=-999 extrudeRegionSel=null
     extrudeExploded='Boss-Extrude3' cut=none
BLK2 A[selCount=4 def=ok e1[segs=8 edgeNearIns=10.00mm]
      scale→2.00/2.00 angle→0.50 e2arg[segs=8 edgeX=19.80 edgeY=19.80]
      dimDisp→True]
     B[selLine=True dim=ok selDim=False(D1@Sketch2) selCount=5 def=ok
       defDims=1 eBefore[edgeNearIns=10.00mm]
       dim='D1@Block2' v0=10.00 set=0 v=30.00 reenter=True
       eAfter[segs=24 edgeNearIns=30.00mm]]
BLK3 selCount=4 def=ok selHostPt=True selBlockPt=True hostPtAtOrigin=True
     pts[n=2:(0.0,0.0)] ptStill050=False ptAtCorner=True
     hostLine=noHostLine selHostSeg=False selBlockSeg=True rels 0→0
     hostBlockDim=null
BLK4 selCount=4 def=ok defs 0→1 instInSketch=1 insert2=ok
     inst2 name='Block1-2' pos=(30.0,30.0) scale=1.00 scale2=1.00 angle=1.00
     instInSketch=2 xform=ok defInstCount=2
BLK5 selLine=True dim=ok dimName='D1@Sketch1' selName='D1@Sketch1'
     v=10.00mm selDim=False(D1@Sketch1) selCount=5 def=ok defDims=1
     defDim0='' v=mm
```

## Verdict per question

### Q1 — Block-instance geometry as an extrude/cut profile: **FAILS**

`FeatureManager.FeatureExtrusion3` returned `null` (no exception, no feature)
for every selection route tried, on a sketch whose only contents are a block
instance containing a clean closed square:

| Route | Selection | Result |
|---|---|---|
| A | host sketch feature via `SelectByID2(name,"SKETCH")` | `null` |
| B | `ISketchBlockInstance.Select(true,null)` — selects fine (`selType=114`, swSelSKETCHBLOCKINSTANCE) | `null` |
| C | `EnableContourSelection` + `SelectByID2("","SKETCHREGION",pt)` — **can't even select** (`selRegion=False`) | `null` |

Control on the same doc: `ExplodeSketchBlockInstance` → identical
select+extrude sequence → **`'Boss-Extrude3'` created**. The geometry is
fine; regions inside a block instance simply do not exist for the
feature system. (`FeatureCut4` produced no cut-type feature either — same
root cause assumed; cut was exercised only after the explode control, so the
independent cut verdict is "untested but implied dead" since no profile can
be built from unexploded block contents.)

**This alone kills the "QR pattern lives inside a block instance" design** —
the pattern must extrude/cut, so it must be exploded before the engineer (or
the feature) can use it.

### Q2 — Driving-dimension rescale: **WORKS**

Inside the definition: `IAddDimension2(x,y,z)` on a selected square edge →
driving `DisplayDimension` (`D1@Block2`). On the definition object:

```
defI.GetDisplayDimensionCount() == 1
dim = defI.GetDisplayDimensions()[0].GetDimension()    // IGetDisplayDimensions(0) returned null — use the array
dim.SetSystemValue3(0.03, 2 /* swSetValue_InAllConfigurations */, null) → returns 0
doc.EditRebuild3()
```

Measured via insert+explode: exploded edge **10.00 mm → 30.00 mm**. The def's
dimension is a real driving dim — one `SetSystemValue3` rescales **every**
instance of that def. `IDimension.SystemValue` read-back confirms 30.00 mm.

Also verified: `inst.Scale = 2.0` / `inst.Angle = 0.5` /
`inst.DimensionDisplay = true` are all settable (read-backs confirm). The
insert call's two trailing doubles are **(scale, angle)**, NOT scaleX/scaleY:
`InsertSketchBlockInstance(def, pt, 2.0, 3.0)` exploded to 20 mm edges with
X-axis projected at cos(3 rad)≈0.99 (`edgeX=19.80`).

### Q3 — Relations from host sketch to block geometry: **WORKS**

`SelectByID2` accepts block-instance entities by coordinate — block corner as
`"SKETCHPOINT"` at (0,0): `selBlockPt=True`; block edge as `"SKETCHSEGMENT"`
at (0.005,0): `selBlockSeg=True`. Then:

```
doc.SketchAddConstraints("sgCOINCIDENT")   // host pt (0.05,0.05) + block corner (0,0)
```

Proof it applied (not just executed silently): afterwards a SKETCHPOINT
select at the host's old position fails (`ptStill050=False`) while the corner
selects (`ptAtCorner=True`) — the host endpoint moved onto the block corner.
Caveats observed: host-sketch line selection by `"SKETCHSEGMENT"` coords was
flaky in this test (`selHostSeg=False`, twice — possibly tolerance/moved-
geometry related, worth a second look if the design needs it);
`IAddDimension2` between a host segment and a block edge was not exercised
successfully; `sketch.RelationManager.GetRelationsCount(-1)` returned 0 even
with the applied coincident (so don't trust it for verification).

### Q4 — Block creation API: **WORKS**

Exact live sequence (all inside an open sketch edit on Front Plane):

```csharp
mgr.CreateLine(...);                       // draw the closed contours
doc.ClearSelection2(true);
ext.SelectAll();                           // selects everything incl. dims (selCount=5 with dim)
var def = mgr.MakeSketchBlockFromSelected(mathPoint);   // converts the selection into instance #1
// → defs 0→1, sketch.GetSketchBlockInstanceCount() = 1

var inst2 = mgr.InsertSketchBlockInstance(def, mathPoint2, scale, angle);
// → instance 'Block1-2', GetSketchBlockInstanceCount() = 2, def.GetInstanceCount() = 2
```

Interop signatures (SW 2023 SP5, `SolidWorks.Interop.sldworks`):

- `SketchBlockDefinition ISketchManager.MakeSketchBlockFromSelected(MathPoint insertionPoint)`
- `SketchBlockInstance ISketchManager.InsertSketchBlockInstance(SketchBlockDefinition def, MathPoint insertPt, double scale, double angle)`
- `ISketchBlockInstance`: `InstancePosition`, `Scale`, `Scale2`, `Angle`, `Name`, `Definition`, `DimensionDisplay`, `BlockToSketchTransform` (non-null), `Select(bool, SelectData)`
- `ISketchBlockDefinition`: `GetInstanceCount()`, `IGetInstances(i)`, `GetDisplayDimensionCount()`, `GetDisplayDimensions()` (array works; `IGetDisplayDimensions(0)` returned null here)
- `ISketchManager.EditSketchBlock()` / `EndEditSketchBlock(bool)` (edits the SELECTED instance), `ExplodeSketchBlockInstance(SketchBlockInstance)` — verified working
- `ISketch.GetSketchBlockInstanceCount()` / `IGetSketchBlockInstances(i)`

### Q5 — Dimension inside the block via API: **WORKS**

While still in the definition sketch (before `MakeSketchBlockFromSelected`):

```csharp
ext.SelectByID2("", "SKETCHSEGMENT", edgeMidX, edgeMidY, 0, false, 0, null, opt); // selLine=True
var dd = doc.IAddDimension2(x, y, z);        // → 'D1@Sketch1', 10.00 mm
ext.SelectAll();                            // dims ride along automatically — explicit dim selection is NOT needed
var def = mgr.MakeSketchBlockFromSelected(pt);
// defI.GetDisplayDimensionCount() == 1 — the dim travelled into the def
```

(Selecting the dim explicitly by `dim.FullName`/`"DIMENSION"` failed —
`selDim=False` — and didn't matter.)

## Surprises / traps hit (all verified live)

- **`IAddDimension2` hangs an invisible session** when
  `swInputDimValOnCreate` (pref toggle 10) is on — set
  `sw.SetUserPreferenceToggle(swInputDimValOnCreate, false)` +
  `swSketchOverdefiningDimsPromptToSetState` (100) off first. Both hangs
  reproduced and fixed.
- **`EditRebuild3()` exits sketch edit mode** — the first
  `InsertSketchBlockInstance` after a rebuild returned null until the sketch
  was re-entered (`reenter=True` then `eAfter` worked).
- Block-edit (`EditSketchBlock`) exposes the def's points via
  `GetSketchPointsCount()=8` but **`GetSketchPoints()` returns null** and
  `GetSketchPointsCount2()=0` — measure geometry by insert+explode
  (`ExplodeSketchBlockInstance` then `GetSketchSegments`), which is reliable.
- `IFeature.GetBox` on the host sketch feature returned null — not usable
  as a measurement path either.
- Host-sketch `GetSketchSegments`/`GetSketchPointsCount2` do not enumerate
  block-instance internals (nor, oddly, the host line in this test) —
  selection-by-coordinates is the workable route to block entities.
- **Harness note:** the add-in probe route is currently shadowed on this
  box — the CLSID is ALSO registered machine-wide by the installer
  (`HKLM\SOFTWARE\Classes\CLSID\{DA932451-…}\InprocServer32` →
  `C:\Program Files\SolidWorks QR Add-In\`, read-only), and HKLM wins over
  the HKCU registration, so `GetAddInObject` only ever returns the stale
  installed build. `LiveProbe block` therefore runs the experiments
  out-of-process; `BlockSpikeProbe` remains in the add-in for when the
  conflict is resolved.

## GO / NO-GO

**NO-GO for "the QR pattern lives inside a block instance"** — block-instance
regions cannot feed `FeatureExtrusion3`/`FeatureCut4` by any selection route
(Q1), and profiles are the whole point.

**Partial GO — the block is still useful as a *temporary* parametric
container**, i.e. an "insert → explode" pattern:

1. Draw the QR contours + one driving dim in a scratch sketch →
   `MakeSketchBlockFromSelected`.
2. `InsertSketchBlockInstance(def, targetPt, scale, angle)` on the user's
   sketch — the engineer sees one tidy, movable, rotatable, scalable object
   (and `SetSystemValue3` on the def dim rescales parametrically — Q2 works;
   host-sketch relations to the instance work — Q3 works).
3. `ExplodeSketchBlockInstance(inst)` before feature time → normal segments →
   extrude/cut verified (`Boss-Extrude3`).

Value over the status quo (plain sketch): grouping/movability and a
parametric size handle during the edit session. Cost: the QR ends up as
plain sketch segments anyway after explode — the same end state the add-in
produces today. If NO-GO-for-carrying is accepted and explode-on-commit is
acceptable UX, the block route buys parametric scaling "for free"; if the
goal was a *persistent* parametric block the engineer never explodes, it is
dead and the fallback is the current plain-sketch design (static geometry,
resize by re-generation).

## UPDATE — engineer's interactive counter-evidence (2026-10-10)

The engineer reproduced the block workflow BY HAND on a real QR sketch:
selected all QR lines → Make Sketch Block → block instance inside the
sketch → **extruded the sketch feature successfully — no problem.** The
spike's "regions don't exist for the feature system" verdict was too broad:
what fails is every API *selection* route to block-internal contours — but
extruding THE SKETCH FEATURE (the interactive workflow) works. The
selection-route failure was a probe artifact, not a capability limit.

The corrected kill test — replicate the exact interactive sequence via API:
create lines → MakeSketchBlockFromSelected → exit sketch → select the
sketch FEATURE → FeatureExtrusion3 — is queued for the next session-free
window (user session was open). Provisional verdict moves toward GO.

Engineer's other observations (consistent with spike findings):
- Cannot dimension a block instance from outside → the driving dim must
  live INSIDE the block definition (confirms squares+dim-inside design).
- Constraints to block geometry DO work (host sketch point coincident to
  the block origin — matches probe's coincident-to-corner result).

Note: `IAddDimension2` can HANG an invisible session (documented trap) —
if this spike interacted with a session that became visible mid-run, an
in-flight hang + spawned-process cleanup could have contributed to a crash.
The probe guard refuses visible/doc sessions; timing on this box made a
collision possible in the window between the user's session exiting and
the spike's own invisible instance starting.
