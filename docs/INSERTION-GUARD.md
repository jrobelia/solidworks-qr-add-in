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
| `MaxSegmentCount` | 4 400 segments | Sketch-edit-responsiveness ceiling on the delivered contour set (spec-13 tickets #35/#36, ported by #38). Measured live: the 7 722-segment disjoint-dots rung took ≈50.6 s to insert and ≈103 s to region-solve — past the wall; 4 400 sits just above the largest live-proven square insert (~4 000 segments, ≈6.1 s). Circle-cell output emits a dot pattern, not segments, so it never pays this budget — its sibling bound is `MaxCircleInstances`. |
| `MaxCircleInstances` | 3 600 instances | Sketch-edit-responsiveness ceiling on the delivered circle-dot count (spec-13 ticket #41). Under real circles each kept instance is one ARC entity and one closed sketch region, so responsiveness scales with emitted dots, not contour segments — the bound counts the plan's post-invert `EmittedCount`, never N². Measured live on the production path (ladder below): the largest rung inside the responsiveness budget emitted 3 232 dots (v12 invert, ≈71 s create / ≈62 s sketch re-solve) and the next rung, 4 045 dots (v18), ran ≈122 s to create and ≈97 s to re-solve — past the wall. 3 600 sits ~1.1× over the largest proven insert, the same methodology that sized `MaxSegmentCount`. |

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
- **Segment ceiling**: the flattened post-union contour total is counted
  before the quadratic self-intersection sweep; `> 4 400` rejects
  (`TooManySegments`) so a refused request never pays the O(n²) check.
- **Circle dot bounds** (spec #13, ticket #38): under the Circle cell shape
  the dots are disjoint by construction and carry no contour machinery — the
  guard reduces to the per-request dot-size cap `f < 1 − 0.05/pitch`
  (dot-to-dot gap ≥ 0.05 mm) plus the same 0.05 mm floor on the dot diameter
  itself; both reject as `InvalidShapeParameter` before any host mutation.
- **Circle instance ceiling** (spec #13, ticket #41): the circle plan's
  post-invert emitted-dot count — the instances the host's sketch linear
  pattern actually delivers — is counted inside generation; `> 3 600`
  rejects (`TooManyInstances`) before any host mutation, with the actionable
  message (shorter content / lighter error-correction level / Square cells).

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

## Circle instance bound — live density ladder (ticket #41)

LiveProbe `circleladder`, production path only (`IAddInProbe` →
`CreateQrFromWindowProbe` → native sketch linear pattern), SW 2023 SP5 rev
31.5.0, invisible COM-launched instance, dot size 90%, live size N×2 mm,
one throwaway part per rung (a second circle create in a document whose
earlier QR sketch was deleted trips an `IEquationMgr.Add` refusal — a
multi-sketch/equation-manager interaction noted for ticket #42, unrelated
to the bound). Payloads are `'a'`×k version-rungs plus inverted twins.
Create ≈ full round trip; committed-solve = the sketch's region solve at
commit; edit re-solve = the same solve paid on every sketch edit — the
responsiveness driver this bound guards.

| Rung | N | Emitted dots | Create | Committed solve | Edit re-solve | Master rescale |
|---|---|---|---|---|---|---|
| v1 | 21 | 232 | 4.3 s* | 0.3 s | 0.6 s | 0.15 s |
| v5 | 37 | 696 | 12.3 s | 2.1 s | 4.5 s | 0.28 s |
| v6 | 41 | 860 | 16.4 s | 3.1 s | 7.4 s | 0.33 s |
| v9 | 53 | 1 399 | 29.1 s | 7.9 s | 15.9 s | 0.57 s |
| v6 invert | 41 | 1 541 | 26.5 s | 9.4 s | 18.3 s | 0.50 s |
| v12 | 65 | 2 097 | 49.5 s | 17.4 s | 28.9 s | 0.94 s |
| v15 | 77 | 2 862 | 74.9 s | 32.3 s | 49.5 s | 1.47 s |
| v12 invert | 65 | 3 232 | 70.9 s | 41.9 s | 61.9 s | 1.30 s |
| v18 | 89 | 4 045 | **122.2 s** | 66.6 s | **97.4 s** | 2.24 s |

\* v1 carries first-create warm-up. Exit/re-enter (~0.6–1.0 s), segment
enumeration (≤2.5 s at 4 045 dots), rebuild (~0.1 s) and sketch delete
(~0.1 s) stayed flat across the ladder; the census read scaled with dots
(0.35 s → 8.6 s).

- **Largest proven insert inside the budget**: 3 232 dots (v12 invert) —
  ≈71 s to create, ≈62 s to re-solve on edit.
- **First rung past the wall**: 4 045 dots (v18) — create broke two
  minutes and every edit re-solves ≈97 s.
- **Shipped bound**: `MaxCircleInstances = 3 600` ≈ 1.1× the largest
  proven insert — the methodology that sized `MaxSegmentCount`.
  Deeper rungs (v21 ≈5.2 k, v25 ≈7.2 k, v29 ≈8.8 k, v33 ≈10.4 k,
  v38 ≈14.3 k dots) restate the same verdict and were not re-measured;
  the shipped build's `circleladder refuse` pass verified every
  over-bound rung — v18, v15-invert (4 363 dots), v21, v25, v29, v33,
  v38 — answering `INVALID TooManyInstances` through the production
  session probes with the shared part's feature tree untouched (0
  sketches, no active sketch).

- **Headroom band now measured** (ticket #42 `circles` gate, same
  environment): the largest rung under the bound is **v17, 3 579 dots** —
  create 90.7 s, committed region-solve 53.5 s, edit re-solve **77.9 s**,
  master rescale 2.0 s. The edit re-solve — the responsiveness this bound
  guards — sits inside the 120 s wall with ~1.5× margin and interpolates
  cleanly on the ladder (62 s @ 3 232 → 97 s @ 4 045 over the bound).
  Create latency is one-time and was never the bound's purpose; the gate
  walls only the solve phases at 120 s and keeps a 150 s create figure as
  a regression alarm. The first over-bound rung re-measured is v14-invert
  (3 872 dots): `INVALID TooManyInstances` on the session probe, `ERROR`
  on create, feature tree untouched — refusal happens before any host
  mutation.
- **Orphan-purge status**: the multi-sketch `IEquationMgr.Add` refusal
  noted above is fixed by the create-start orphan purge
  (`SketchEquationHygiene.PurgeOrphanedEquations`) — the `circles` gate
  deletes a circle sketch via the raw `EditDelete` path (orphaning its 8
  equations) and verifies the next create succeeds with only the new
  sketch's table installed.
