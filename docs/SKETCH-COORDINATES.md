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
| Baseline Front Plane | **sketch-space metres, verbatim — PROVEN** | 2026-10-09, SW 2023 SP5 rev 31.5.0 invisible instance (LiveProbe, ALL CHECKS PASSED, 185 checks): under the seam's bulk-insertion mode the asymmetric marker (0.011,0.007)→(0.023,0.013) read back exactly, and mapped through the sketch→model frame recovered exactly. |
| Rotated reference plane ('Plane5', ~4.8° tilted, frame x=(-0.9965,0,-0.083) z=(0.083,0,-0.9965)) | **sketch-space metres, verbatim — PROVEN** | Same run: marker read back verbatim; the 252-segment QR sketch recovered its sketch coordinates exactly (`< 1e-9` in-plane error), every endpoint on the plane (`|perp| = 0`), reference entity = the selected RefPlane. |
| Top Plane (datum, 90° from Front, frame x=(1,0,0) y=(0,0,-1) z=(0,1,0)) | **sketch-space metres, verbatim — PROVEN** | Same run — verbatim marker read-back and exact in-plane round-trip on the QR sketch. |
| Planar part face — plate top face (offset from all default planes) | **sketch-space metres, verbatim — PROVEN** | Same run — verbatim marker; QR outline measured exactly 25.000 mm; all endpoints on the face plane; 28 sketch regions (27 contours + 1 face-boundary region). |
| Planar part face — vertical side face (frame x=(1,0,0) y=(0,0,-1) z=(0,1,0)) | **sketch-space metres, verbatim — PROVEN** | Same run — verbatim marker; QR on-plane and exact round-trip; 42 regions because the 60×5 mm face clips the 25 mm QR (expected, floor check only). |

**The insertion seam needs no transform** — `SwSketchInsertion.AddLine`
passes the module's sketch-space metres to `CreateLine` verbatim, on every
support orientation measured. One caveat proven by the same runs: this holds
under the seam's bulk-insertion mode (`AddToDB=true`,
`DisplayWhenAdded=false`, `AutoInference=false`). In *interactive* mode the
same marker came back shifted ~0.24 mm on X — inference snapping, not a
coordinate-frame difference — which is also why the seam uses bulk mode.

## What the seam sends

The module emits closed contour edges in sketch-space metres, centred on
`(0, 0)`, `y = up` (QR row 0 maps to +Y — no vertical mirror), sized so the
grown outline measures exactly the entered width. `ISketchInsertion.AddLine`
receives those coordinates verbatim; the contract is documented as
"sketch-space metres" in `ISketchHost.cs`. Headless tests
(`SketchSupportTests`) assert bit-identical pass-through on both a
reference-plane and a planar-face selection — no adapter-side transform is
permitted to exist, so there can be no double transform. **Live-verified
correct on rotated and face supports (2026-10-09).**

Other layout facts proven in the same run:

- `ISurface.PlaneParams` returns the **normal first**, then a point on the
  plane (detected by unit length — the earlier assumed order gave unitless
  "normals"). `IRefPlane.GetRefPlaneParams` does not return a usable
  [point,normal] layout here (Top Plane parsed as +Z while its real frame is
  +Y); read a datum plane's geometry from the feature's own face or the
  sketch's `ModelToSketchTransform` instead.
- `IVertex.GetPoint` returns **model-space** coordinates — map through
  `sketch.ModelToSketchTransform` before comparing with sketch-space data.
- `GetSketchRegionCount` on a face support adds the face's own boundary
  region(s) (28 regions / 27 contours on the contained top face; more when
  the sketch overflows the face).

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
session is open; the probe reaps its spawned instance by PID at teardown
(`taskkill /PID <pid> /F` if one lingers — never `taskkill /IM`).
