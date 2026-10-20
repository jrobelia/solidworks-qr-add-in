# Circle-mode master geometry — dimensions & equations

Ticket #40 (spec #13): make the circle-dot sketch parametric — one driving
dimension on the QR construction square resizes the whole pattern. The
recipe is the one `docs/PATTERN-SPIKE.md` proved live (PAT4/PAT5/PAT7);
this document is the production map of what the emitter installs.

Scope: **circle mode only.** Square mode is untouched — its contours ride
inside a block instance and carry no sketch dimensions (ADR 0003's "no
sketch dims" rule still stands for the block path; #13 sanctions the
deviation for circle mode, where the construction squares are plain
host-sketch entities).

## What the sketch contains after create

Each construction square emits as one `CreateCornerRectangle` — the
canonical rectangle primitive, four construction edges sharing ONE sketch
point per corner (the genuinely closed profile the UI rectangle tool
produces). Per-edge `CreateLine` never merges endpoints — it left two
stacked sketch points at every corner, so a corner grab (the seed-offset
dims' corner point, the dimensionless coincident binds below) held only
one of the two edges meeting there — the PR-#43 QA defect. The returned
segments carry no contractual order, so the emitter re-orders them into
the square's canonical loop order by bound (`QrSquareEdges.OrderFor`):
bottom = the horizontal edge at min-Y, left = the vertical at min-X.

All inside one open sketch, created in this order (dims number `D1..` in
creation order — a typical N=21 sketch looks exactly like this):

| dim (typical) | on | value | role |
|---|---|---|---|
| `D1` | QR-square bottom edge | QR code size | **master — driving, user-edited** |
| `D2` | QR left edge ↔ keep-out left edge | 4·pitch | quiet-zone offset, driven |
| `D3` | QR bottom edge ↔ keep-out bottom edge | 4·pitch | quiet-zone offset, driven |
| `D4` | keep-out bottom edge | N+8 pitches | keep-out size, driven |
| `D5` | pattern between-axes angle | 90° (π/2 rad) | **driving** — created by `CreateLinearSketchStepAndRepeat` (`AngleDim=true`), emitted FIRST of the pattern's own dims; without it the direction lines are underdefined and the pattern can tilt once a square corner is pinned |
| `D6`,`D7` | pattern X/Y spacing | pitch | driven — created by `CreateLinearSketchStepAndRepeat` (`spacingDims=true`) |
| `D8` | seed arc | pitch·f | seed diameter, driven (`IAddDiameterDimension2`) |
| `D9`,`D10` | pattern-anchor corner → seed centre | pitch/2 | seed position, driven |

The pattern may also emit count dims (`D11`,`D12` — instance counts,
`CreateNumOfInstancesDimIn*Dir` off in our call so usually absent; see
#41 for the count knob). Names are **never assumed**: sketch dims are not sub-features, so
the emitter discovers each `D{n}@<sketch>` name through `IParameter`
scans, and equations reference `IDimension.FullName` minus the trailing
`@<Part>.Part` (`EqName` in `SwSketchInsertion`). The sketch name comes
from `((IFeature)sketch).Name` — never `"Sketch1"`.

## Equation table

`QrCircleEquations.For(plan, scheme)` (Core,
`Geometry/QrCircleDimensions.cs`) emits, in dependency order:

```
spacingX   = "box"/N
spacingY   = "box"/N
diameter   = "spacingX" * f        (dot fraction, e.g. *0.9)
seedOffX   = ±"spacingX" / 2
seedOffY   = ±"spacingY" / 2
quietOffX  = ±"spacingX" * 4       (= box·4/N — the four-cell margin)
quietOffY  = ±"spacingY" * 4
quietSize  = "spacingX" * (N+8)    (= box·(1+8/N))
```

`spacing·4` / `spacing·M` are the ticket's `box·4/N` and `box·(1+8/N)`
rewritten through the spacing dims — identical values, and the whole
quiet-zone wiring survives a master retarget for free. The ± mirror on
the offset dims copies the sign the dim measured at creation (SolidWorks
can sign point/edge distance dims by direction), so an installed
equation reproduces the drawn side instead of flipping the geometry.

Install sequence (both in the emitter and PAT12): `IEquationMgr.Add(-1,
'"lhs" = rhs')` per row, then `EvaluateAll()` + `EditRebuild3()` — all
**inside the open sketch edit**, before `InsertSketch(false)` commits.

## Dimensionless mode (the part-constrained end state)

The emitted state is *dimensioned*: the master `D1` drives everything.
The PAT6 end-state — pattern scaling off part geometry with no sketch
driving dim — is reachable as a transformation, not a second emission:

1. Delete the `D1` master dim (the square keeps its shape on the
   remaining constraints/geometry).
2. Coincident-bind the QR square's corner sketch points to the part's
   vertices (`GetSketchPoints()` + `ISketchPoint.Select4`, then
   `SketchAddConstraints("sgCOINCIDENT")` — PAT6). Each corner is the
   single sketch point the closed rectangle's two edges share, so one
   bind constrains both edges meeting there.
3. Re-point the **two spacing equations only** at the part's driving
   dim: `"D5" = "D1@PlateSketch"/N`, `"D6" = "D1@PlateSketch"/N`.
   Diameter, seed offsets and both quiet-zone dims all follow through
   the spacing names — nothing else needs editing.

That retarget is why every equation roots at the spacing pair rather
than repeating `"box"` — exactly two lines change. The transition is a
documented manual/API step for now (the emitter always emits the
dimensioned state); a dedicated conversion command is out of scope for
#40 and can ride #42's live-verified surfaces later.

**Why the delete is not automatic (measured live, 2026-10-20).** A driven
"echo" dimension between the square's two bottom corners was tried as the
equation root — the equations would then read whatever geometry sizes the
square after a master delete, no retarget needed. It works, but
SolidWorks' solve order makes it unusable as a source: **equations
evaluate before the solve refreshes driven dims**, so every master
rescale lagged one rebuild behind — `D1` 25→50 mm left spacing at the
old pitch until a second rebuild. Only a driving dim is a single-solve
equation source, and any driving dim is deletable; the two-line retarget
above is the cost of instant live rescale, and retargeting at the plate's
own driving dim keeps the dimensionless path lag-free too.

## Probe surface (the #42 live gates)

* `PatternCensusProbe(sketch)` — segment/region census + plan echo
  (ticket #39).
* `PatternDimsProbe(sketch)` — `PATDIMS sketch='..' dims=n
  D1=25.0000mm(s0) ... eqs=8 '"D5@Sketch1" = "D1@Sketch1"/21' ...
  mode=circle` — name, mm value, `DrivenState` per `D{n}@sketch` dim
  (creation order), then the equation manager verbatim.
* `SetPatternDimProbe(dimName, valueM)` — `SetSystemValue3(valueM,
  swSetValue_InThisConfiguration)` + `EditRebuild3`, echoes the stored
  value (`PATSET 'D1@Sketch1'=50.0000mm rc=0`). The gate rescales the
  master and re-reads `PatternDimsProbe`/`PatternCensusProbe`.

DrivenState codes seen in the spike: `s0`/`s2` driving/undriven,
`s1` = equation-driven (`DrivenState` flipped 2→1 when an equation took
over — PAT4).

## PAT12 — production-order spike: PASS (+ one API fix it caught)

`LiveProbe.exe pattern 12` replicates the emitter's exact order: dims on
the squares, seed + pattern, diameter + offset dims, equation install,
`EvaluateAll`/`EditRebuild3`, **and a `SetSystemValue3` master rescale —
all inside the open sketch edit** — then exits and re-censuses. Result:
8 equations accepted in-edit, box 6→12 mm rescaled every driven dim,
committed sketch = 7 arcs at the doubled 1 mm radius.

It also caught a real defect: `CreateLinearSketchStepAndRepeat` refuses
`AngleY = −π/2` (returns false). The identical direction as `3π/2`
works — that is what the emitter calls.

## Gotchas carried from the spike (all still apply)

* `AngleY` must be written `3π/2`, not `−π/2` — the negative literal is
  refused (PAT12); same descending direction, accepted form.
* Point selection is object enumeration + `Select4` — never
  `SelectByID2` coordinates (ambiguous next to arcs and direction
  lines).
* Never `EditDelete` pattern instances; never
  `EditLinearSketchStepAndRepeat` after creation — both freeze/resurrect
  the pattern's drive. All post-create edits go through the dims.
* The keep-out equal-relation route is a no-op (PAT7) — dims +
  equations are the mechanism.
* Seed cell (1,1) is unskippable — the plan puts the seed on a
  guaranteed-emitting cell.
