# Sketch coordinate conventions (`CreateLine`, units, frames)

What coordinate space `ISketchManager.CreateLine(x1, y1, z1, x2, y2, z2)`
consumes — and what space `ISketchPoint.X/Y/Z` reports — decides whether the
insertion seam can pass the module's sketch-space metres through verbatim on
an arbitrary support (rotated reference plane, planar face) or must transform
them itself. This file records what is **proven**, what is **measured**, and
how the measurement is made.

## Status

| Support | `CreateLine` consumes | Evidence |
|---|---|---|
| Baseline Front Plane | sketch-space metres, verbatim | Proven live (tickets #2–#4): the module's contours were passed un-transformed and the native sketch reproduced them 1:1 — 252 segments / 27 contours matched at ≤1e-9 m, outline measured exactly the entered size. On Front Plane the sketch frame coincides with model X/Y, so this evidence alone cannot distinguish "sketch space" from "model space". |
| Rotated reference plane | **awaiting live run** | The convention probe (below) is coded and compiles; it could not run while the engineer's interactive SolidWorks session was up (the probe refuses attached visible sessions by design). |
| Planar part face | **awaiting live run** | Same pending status. |

Until the rotated-plane and planar-face lines read "proven", the native
adapter deliberately performs **no transform**: `SwSketchInsertion.AddLine`
calls `CreateLine(x1, y1, 0, x2, y2, 0)` with the module's sketch-space
metres. If the probe later shows a different consuming frame, the seam — and
only the seam — gains the proven transform, documented here with the
evidence.

## What the seam sends

The module emits closed contour edges in sketch-space metres, centred on
`(0, 0)`, `y = up` (QR row 0 maps to +Y — no vertical mirror), sized so the
grown outline measures exactly the entered width. `ISketchInsertion.AddLine`
receives those coordinates verbatim; the contract is documented as
"sketch-space metres" in `ISketchHost.cs`. Headless tests
(`SketchSupportTests`) assert bit-identical pass-through on both a
reference-plane and a planar-face selection — no adapter-side transform is
permitted to exist, so there can be no double transform.

## The convention probe (tools/LiveProbe → `ConventionProbe`)

On each selected support the probe:

1. `ISketchManager.InsertSketch(true)` — opens a fresh sketch on the
   currently selected entity.
2. `CreateLine(0.011, 0.007, 0, 0.023, 0.013, 0)` — an asymmetric marker
   (non-square, non-symmetric so orientation flips and frame mix-ups are
   visible in the numbers).
3. Reads the created segment's `ISketchPoint.X/Y/Z` endpoints back.
4. Recovers the sketch→model frame from
   `ISketch.ModelToSketchTransform.Inverse()` by mapping `(0,0,0)`,
   `(1,0,0)`, `(0,1,0)`, `(0,0,1)` and differencing — no `ArrayData` layout
   assumption. Asserts `det = +1` (a proper rotation — no mirroring).
5. Prints: sketch origin in model space, the three sketch axes in model
   space, the marker endpoints mapped into model space, and the read-back
   endpoints recovered into sketch space.
6. Deletes exactly that probe sketch (selected by its own feature name —
   never "the last sketch", never Undo).

Reading the evidence:

- **read-back == passed** → points are stored in sketch space *and*
  `CreateLine` consumed sketch space.
- **read-back ≠ passed, but mapped through sketch→model the marker lands at
  the passed coordinates** → `ISketchPoint` reports model space *and*
  `CreateLine` still consumed sketch space.
- **neither** → `CreateLine` consumed model space (or something else) — the
  printed frame numbers identify the true convention; the seam would then
  apply the proven transform once.

`VerifySketchOnSupport` independently re-proves the convention end-to-end on
every QR create: each committed segment endpoint, mapped sketch→model, must
lie on the selected support plane (`|perp distance| < 1e-7 m` — a double
transform or wrong frame would lift them off it) and must recover its sketch
coordinates exactly in the sketch frame (`< 1e-9`).

## Supports exercised live

- Baseline Front Plane (regression control).
- A datum plane rotated 30° about a plate edge
  (`IFeatureManager.InsertRefPlane`, Coincident-on-edge + Angle-to-face) —
  verified genuinely rotated (|normal·Z| < 0.95) before use.
- The plate's top planar face (offset from all default planes) and a
  vertical side planar face (normal ⊥ Z — a frame rotated ~90° from Front).
- Negative selections: cylindrical (non-planar) face, edge, two supports at
  once, nothing — each must error with no partial sketch.
- Cross-document: while the QR window is open a second part is activated
  and its Front Plane selected; Create must land in the second document and
  leave the first untouched.

## Units

Everything at the seam and on the wire is **metres** (SolidWorks's API
unit); millimetres appear only at the UI field boundary. The convention
probe's marker coordinates are literals in metres (11 mm / 7 mm /
23 mm / 13 mm) — non-round values chosen so a mm↔m mix-up is obvious.

## Running it

See `docs/BUILDING.md` → *Live verification*. The probe refuses to touch a
visible or document-holding SolidWorks session; run it when no interactive
session is open, then `taskkill /IM SLDWORKS.exe /F` to reap the spawned
invisible instance.
