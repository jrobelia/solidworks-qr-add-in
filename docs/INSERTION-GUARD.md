# QR insertion guard — recorded limits (ticket 2)

This is an **implementation guard** for what the geometry module is willing to
hand to SolidWorks sketch insertion — not a manufacturing assessment and not a
scan-readability claim (those stay with the engineer per the spec).

Constants live in `src/SolidWorksQrAddIn.Core/Geometry/QrInsertionGuard.cs`:

| Guard | Value | Rationale |
|---|---|---|
| `GridUnitsPerCell` | 10 000 units/cell | Normalized integer grid for Clipper2 topology. |
| `MinGridFeatureUnits` | 10 units | Clipper2 integer imprecision is ±1 unit and edges under ~2 units can be dropped; 10 units keeps every feature ≥10× above that noise floor. |
| `MinInsertableFeatureMm` | 0.05 mm | ~5000× above SolidWorks' ~1e-5 mm zero-length tolerance; the fixed 25 mm/3% request clears it with >20× margin on the *delivered* minimum edge (1.116 mm). |
| `SizeToleranceMm` | 1e-6 mm | Declared tolerance on the final grown outline width; actual conversion error is ~1e-13 mm (pure affine map of integers). |

## What is guarded

- **Growth rounds to zero**: per-side growth `g = round(r·U)` grid units;
  `g < 1` rejects (`GrowthRoundsToZero`).
- **Growth closes light gaps**: `2g ≥ U` rejects (`GrowthClosesGaps`).
- **Grid resolution floor**: `min(2g, U−2g) < 10` units rejects
  (`BelowInsertionLimit`).
- **Physical floor**: `min(2d, p−2d) = min(2g, U−2g)·k mm < 0.05 mm` rejects
  (`BelowInsertionLimit`), where `k = S/(N·U+2g)` mm/unit — i.e.
  `min feature = S·min(2r, 1−2r)/(N+2r)`.
- **Delivered edges**: the actual smallest contour edge is re-measured after
  union and must also clear 0.05 mm.
- **Contour validity**: zero area, zero-length edges, self-intersections, and
  residual corner-only contacts reject (`InvalidGeometry`) before any host
  mutation.

## Tested range — payload `SW-QR-MVP` (version 1, N = 21)

Since ticket 4 the size and growth are engineer-entered fields — the
25 mm / 3% request below is the window's *initial* value, and the same
guards apply to every entered value before any host mutation.
Empirically bisected on the generated result:

- At **size 25 mm**: accepted growth ≈ **2.10% … 47.80%** per side
  (rejects below/above; analytical bounds 2.104% and 47.8% match).
- At **growth 3%**: accepted size ≈ **≥ 17.55 mm** (rejects smaller).
- Grid representability: growth fraction resolves to integer units of 1/10 000
  cell — effective growth is reported in `QrSketchInfo.GrowthFractionPerSide`
  (the requested 3% maps to exactly 0.03 at U = 10 000).

The window's initial request: 25 mm / 3% → pitch 1.1871 mm, growth 0.0356 mm/side,
final outline exactly 25 mm, smallest delivered contour edge 1.116 mm,
narrowest one-cell light channel 1.116 mm, 27 contours / 252 segments /
8 regions / 16 holes / 3 nested finder islands.

## Live verification record

Ticket-2 live run (LiveProbe `FeatureChecks`, SW 2023 SP5 rev 31.5.0,
invisible COM-launched instance) — all checks passed:

- QR sketch on Front Plane: 252 native segments / 27 native contours /
  27 native regions; all 27 contour bounding boxes match the module at
  1e-9 m; 3 finder ring→hole→island nestings verified; sketch left open.
- Boss-Extrude 2 mm on a 60×60×5 mm plate created `Boss-Extrude2` —
  selection was the 11 *material* regions only (8 outer bodies + 3 finder
  islands, matched by bounding box to `ISketchRegion`s). Caveats verified
  empirically: selecting the whole sketch feature returns null for a
  multi-contour extrude; selecting all 27 regions would fill holes solid.
- Cut-Extrude 1 mm shallow into the plate created `Cut-Extrude1` with
  `Dir=true` (the plate extrusion grows away from the sketch-normal side;
  `Dir=false` returned null).
- No corner-only-contact feature failures observed: growth guarantees
  finite overlap between diagonal cells; validator rejects residual point
  contacts.
