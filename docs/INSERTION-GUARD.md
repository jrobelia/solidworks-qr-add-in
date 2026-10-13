# QR insertion guard — recorded limits (ticket 2)

This is an **implementation guard** for what the geometry module is willing to
hand to SolidWorks sketch insertion — not a manufacturing assessment and not a
scan-readability claim (those stay with the engineer per the spec).

Constants live in `src/SolidWorksQrAddIn.Core/Geometry/QrInsertionGuard.cs`:

| Guard | Value | Rationale |
|---|---|---|
| `GridUnitsPerCell` | 10 000 units/cell | Normalized integer grid for Clipper2 topology. |
| `MinGridFeatureUnits` | 10 units | Clipper2 integer imprecision is ±1 unit and edges under ~2 units can be dropped; 10 units keeps every feature ≥10× above that noise floor. |
| `QuantizationMarginUnits` | 2 units | The dot-% cap's margin `M`: grid rounding moves each polygon vertex up to ~0.71 units, so the cap reserves 2 units on top of the floor to keep the *delivered* gap/edge at or above it. |
| `MinInsertableFeatureMm` | 0.05 mm | ~5000× above SolidWorks' ~1e-5 mm zero-length tolerance; the fixed 25 mm/3% request clears it with >20× margin on the *delivered* minimum edge (1.116 mm). |
| `SizeToleranceMm` | 1e-6 mm | Declared tolerance on the final grown outline width; actual conversion error is ~1e-13 mm (pure affine map of integers). |
| `MaxSegmentCount` | 4 400 segments | Sketch-edit-responsiveness ceiling on the delivered contours — measured live by ticket #36 (the provisional 8 000 measured unresponsive at the bound and tightened to ~1.1× the largest live-proven insert, the 3 896–4 022-segment dense square payload of `docs/ACCEPTANCE.md` §F); see the ticket-#35/#36 section below. |
| `MaxFacetsPerCorner` | 8 facets | Bound on the rounded-cell corner-arc polygonization — a 32-vertex cell keeps fidelity without flooding the segment count; the delivered-edge floor bounds it further per request. |

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
- **Segment ceiling**: the total delivered segments — the sum of vertex
  counts over every contour (region outlines, holes, islands; construction
  squares excluded, the same total `QrSketchInfo.SegmentCount` reports and
  the host actually draws) — must be ≤ `MaxSegmentCount`; over it rejects
  (`TooManySegments`). Under Circle the check is even earlier: disjoint
  dots can never union-merge, so the dot contribution is known exactly —
  (emitted non-function cells) × K — right after emission, BEFORE the
  Clipper union that on dense circle sets costs minutes by itself; the
  post-union check right after the contours are flat stays the
  authoritative count for every shape (it also runs BEFORE the quadratic
  self-intersection sweep, so a refused request never pays O(segments²)
  validation on tens of thousands of edges).

## Rounded-square cells — the radius cap (ticket #32)

RoundedSquare replaces each emitted cell's four corners with a polygonized
quarter-arc of radius `ρ = f·h`, where `h = u/2 + g` is the GROWN cell's
half-width and `f ∈ [0, 1]` is the shape parameter (percent of half-width on
the pane). Union, clipping, nesting, validation, conversion and insertion
run identically on either primitive. `f` above the domain rejects
(`InvalidShapeParameter`): past `ρ = h` adjacent corner arcs pass each other
and invert the edge — the "adjacent corners can invert an edge" bound the
ticket names.

Because rounding cuts material, the accepted radius must also preserve the
three guarantees growth buys. In grid units, with the floor
`F = max(10, 0.05 mm / mmPerUnit)`:

- **Diagonal-overlap cap `ρ ≤ 2g`** (`BelowInsertionLimit`). Two diagonal
  cells overlap in a `2g × 2g` square centred on the shared corner. Each
  cell's corner cap — the part of that square outside the cell — is bounded
  by the polygonized arc, so its deepest point sits on the far side of the
  chord between the arc endpoints `(±(g−ρ), ±g)`. While `ρ ≤ 2g` both caps
  stay on their own side of the overlap square (`x+y ≥ 0` vs `x+y ≤ 0`): the
  union provably covers the square whole **for any polygonization**, the
  same finite overlap squares guarantee — corner-only contacts stay
  impossible. Past `2g` the facing caps meet, the cells pinch to a lens and
  then part — the finite-overlap guarantee is lost, so the radius stops
  there (the disjoint regime stays with circles/#33, where the spec put it).

- **Merge-groove band `g < ρ < g+F`** (`BelowInsertionLimit`). Two
  side-adjacent emitted cells overlap by `2g` across the shared edge. For
  `ρ ≤ g` each corner arc stays inside that overlap band — merges are flush,
  identical to squares. Past `g` a V-groove opens on the shared boundary
  whose edges scale with `ρ−g` (exact on the emitted polygon for the facet
  counts below); under the floor it is a sliver, so the band is refused —
  spec story 9's near-tangent band applied to rounded merges.

- **Primitive floor** (`BelowInsertionLimit`). Every emitted edge must
  resolve: the straight segments `2h−2ρ` and the facet chords
  `2ρ·sin(45°/k)`. A radius that cannot place even one chamfer facet ≥ F is
  refused, like growth that rounds to zero.

**Facet policy.** The corner arc polygonizes at `k` facets/corner:

- Clean regime (`ρ ≤ g`): no arc ever lies on an interior boundary —
  interior corners are covered by the neighbouring grown cells — so the
  finest `k ≤ 8` whose chords clear F is emitted (grid probe: `MaxFacetsPerCorner`).
- Groove regime (`ρ > g`): the corner arcs of side-adjacent cells cross.
  `k ≤ 2` keeps every crossing a clean vertex — the facet touching each
  edge is collinear with the k=1 chamfer, so the crossing angle is the same
  pair of straight lines; finer polygonizations can strand mid-facet stubs
  below F (grid-verified: k=1/k=2 give `ρ−g` groove edges exactly; k=4/k=8
  produce scattered stubs). k=2 is preferred for roundness, k=1 as fallback.

**Feasible domain.** Putting it together, an accepted radius lives in
`ρ ∈ [F_resolvable, g] ∪ [g+F, 2g]`. Both bands need per-side growth ≥ the
physical floor (`g·mmPerUnit ≥ 0.05 mm`); at the window's initial
25 mm / 3% request the growth is 0.0356 mm/side, so **no** rounded radius
resolves there — the pane reports which bound the entered radius hit and
what would clear it. Rounded cells become feasible at ≳4.2% growth
(25 mm) or ≳35 mm size (3% growth) — e.g. 50 mm / 3% accepts the
groove-regime band `f ≈ 9.6–11.3%`, 60 mm / 10% accepts `f ≈ 3–33%`.

Under invert the same emitted cells are light: merged light cells grooved
identically; an un-emitted dark cell's pocket stays a sharp square (the
flanking light cells' grown straight edges cross at each pocket corner and
dominate the diagonal cell's corner arc); isolated emitted cells are
separate rounded islands — the "separate rounded pits" regime.

## Circle cells — disjoint dots only (ticket #33)

Circle emits one **dot** per emitted cell: a polygon circle of diameter
`d = f·p` centred in the cell, where `f` is the shape parameter — the dot
size % of cell pitch. Per spec #13's to-tickets deviation, the merged-blob
regimes (story 7) are deliberately excluded: dots are ALWAYS disjoint, so
the corner-contact failure class disappears entirely and the diagonal
tangency dead zone at `2r = p√2` no longer exists.

**Growth does not apply — the growth-0 layout map.** The dot size is the
only sizing knob, so the affine map runs at `r = 0`: pitch `p = S/N`, the
QR square is the `±S/2` data extent, and the keep-out square is the
working-matrix edge — identical *rules* to squares/rounded cells (the same
formulas evaluated at zero growth), coherent in both polarities. The
growth field is inert under Circle: the pane disables it and the session
keeps its text verbatim without parsing, exactly like the shape parameter
under Square. (The rejected alternative — growth still sizing the pitch —
would have left a second knob changing dot diameter in mm, contradicting
"dot size is the sizing knob".)

**The one-sided dot-% cap.** The `2r = p` tangency of the merged regime
becomes a one-sided bound: neighbouring dots must keep a full-floor gap.
The orthogonal gap `u·(1−f)` is the binding one — the diagonal gap
`u·(√2−f)` is always larger — so the cap is

    f ≤ 1 − (F + M)/u        (f in diameter-fraction of pitch)

with `F = max(10, 0.05 mm / mmPerUnit)` grid units and `mmPerUnit =
S/(N·u)`, plus the quantization margin `M = 2` units: grid rounding moves
each inscribed vertex up to √2/2 units per axis, so two facing dots can
lose ≤ ~1.4 units of the nominal gap — the margin keeps the DELIVERED gap
at or above F. At 25 mm / N = 21 (F ≈ 421) the cap is f ≈ 95.8%; at
50 mm / N = 21 it is f ≈ 97.9%. Values above the cap reject with
`InvalidShapeParameter`, naming the resulting gap and the bound — before
any host mutation; `f ≤ 0` or non-finite and `f > 1` reject on the same
kind (the declared domain is the feasible band).

**Tightened cap under a live function-pattern carve-out.** When
`KeepFunctionPatternsSquare` is on AND the matrix is a real QR version
(the mask is live), emitted function cells are the **bare u×u cell
square** — growth is 0 under Circle, so the square reaches all the way to
its cell edges. A dot orthogonally beside one then keeps only
`u·(1−f)/2` of light channel: the square takes the half of the gap a
second dot would have kept. Holding the same floor+margin against the
halved channel tightens the cap to

    f ≤ 1 − 2(F + M)/u       (keepFn live, e.g. f ≈ 91.6% at 25 mm / N=21)

and rejects above it with `InvalidShapeParameter`, naming the
function-square adjacency. The adjacency is reachable in both polarities
(dark timing/finder cells beside dark data cells normally; the
always-light separator ring emitting square pits beside light data cells
under invert). keepFn×RoundedSquare needs no change: its function squares
are the same GROWN cell every emitted cell is, so both sides of a shared
boundary reach ±g and the narrowest emitted↔emitted channel stays
`u−2g` — a rounded cell's corner arcs only recede inward. `MinLightGapMm`
reports the true minimum either way: `u·(1−f)` dots-only, `u·(1−f)/2`
under a live carve-out.

**Polygon-circle fidelity — K follows cell size.** Each dot is an
inscribed regular K-gon (vertices on the nominal circle, so the polygon
can never reach past the dot's diameter). K is the COARSEST polygon whose
chord sagitta stays under the floor — fewest segments for the same
delivered fidelity:

    sagitta(K) = ρ·(1 − cos(π/K)) ≤ F
    ⟹  K = max(3, ⌈π / arccos(1 − F/ρ)⌉)

The smallest admissible K also has the longest chords, so only that K is
probed: the emitted (grid-rounded) polygon's shortest chord —
`2ρ·sin(π/K)` nominal — must clear F in the same Chebyshev metric the
delivered-edge guard re-measures. If it fails there is no finer
polygonization to fall back on: a dot too small to place even a triangle's
chords (ρ ≲ F/√3 nominally, i.e. f ≲ 1.16·F/u) rejects
`InvalidShapeParameter` — never silently substituted for a square.
Worst case: the largest dot (ρ → u/2) at the bare grid floor (F = 10, a
large QR) needs K ≈ 50 — **K per cell is the segment-count driver** the
MaxSegmentCount guard (#35) bounds. Typical cells resolve at K = 3–15
(e.g. a 50% dot at 25 mm / N = 21 is a hexagon — chord deviation 0.04 mm,
under the floor).

Under invert the emitted polarity flips: every LIGHT cell of the whole
working matrix — including the quiet-zone margin — emits a disjoint dot,
which cuts as a separate circular pit in the field. No merged pocket can
form (nothing ever touches); the keep-out square still marks the matrix
edge the pit field covers.

## Segment ceiling — MaxSegmentCount (ticket #35; tightened by #36's live measurement)

Squares have made the feature floors the only bounds; the shaped cells of
spec #13 add a second dimension: **how many** segments a request draws.
Polygonized circles and rounded corners inflate per-cell segment counts
~4–8× over squares at the same density (the ticket's estimate — measured
below), so a dense shaped QR reaches an uneditable sketch while every
feature floor still passes. `MaxSegmentCount` caps the delivered contour
set at **4 400 segments** (`TooManySegments`), enforced on the generated
contours for every shape, before any host mutation.

**This is a sketch-edit-responsiveness bound, not a hard SolidWorks
limit** — there is no native maximum segment count. The #35 value of
8 000 was provisional until live edit responsiveness could be measured;
ticket #36's gates measured AT the bound and it failed its purpose —
the disposition and evidence are recorded in the "Why 4 400" section
below.

### Worst case per cell, by shape

- **Square: 4** — the grown rectangle; disjoint cells stay 4/cell
  delivered, merged cells strip to ≈1.5–1.7/dark cell on real payloads
  (union + collinear strip).
- **RoundedSquare: 4(k+1) ≤ 36** — k facets per corner, k ≤ 8 in the
  clean regime (ρ ≤ g), k ≤ 2 in the groove regime; isolated cells bill
  all 36. Delivered inflation over the same square run measures
  ~1.7–3.9× (948 vs 252 at v1; 3 682 vs 1 246 at v6 groove regime;
  11 730 vs 4 022 at v14).
- **Circle: K = max(3, ⌈π / arccos(1 − F/ρ)⌉) — the ticket-#33 rule.**
  K follows cell size (finer polygons at larger sizes): measured 15 at
  a 25 mm cell, 21 at 50 mm, 29 at 100 mm, 36 at 250 mm (f = 50%), 48 at
  f = 90–95% on metre-scale cells, 50 worst case — matching the #33
  bound. Dots are ALWAYS disjoint, so **nothing is ever merged away**:
  delivered segments = (emitted cells) × K exactly — and with a live
  carve-out, (emitted non-function cells) × K is a guaranteed lower
  bound (the function squares merge among themselves but every dot
  still delivers its K vertices). Because the dot contribution is
  known before the union runs, a circle request whose dots alone bust
  the budget refuses `TooManySegments` BEFORE the boolean — the union
  no longer gets to spend its minutes first (next paragraph).
  Real-payload inflation over squares measures ~4–8× (1 824 vs 252 at
  v1; 7 848 vs 1 246 at v6/f = 90%; 21 640 vs 4 022 at v14/f = 50%).

### Where real payloads land (measured on this worktree)

Same-payload comparison at ECC M (the `a`×358 row reproduces the
ACCEPTANCE.md §F dense class — version 14, N = 73):

| Payload | Square | RoundedSquare | Circle |
|---|---|---|---|
| v1 `SW-QR-MVP` | 252 | 948 (f 15%) | 1 824 (f 90%) |
| v6 ~101 chars | 1 246 (recorded 1 218) | 3 682 (f 30%) | 7 848 (f 90%) / 5 232 (f 50%) |
| v11 ~250 chars | 3 108 | 8 396 (f 30%) | 15 032 (f 50%) |
| v14 ~358 chars | 4 022 (recorded 3 896) | 11 730 (f 30%) | 21 640 (f 50%) |
| v23 ~1 200 chars | 9 934 | — | — |

Worst-case fixtures: checkerboard N = 73 → 10 660 (square) / 11 244
(rounded) / 29 315 (circle); 2 116 spaced squares → 8 464; 256 spaced
rounded cells at k = 8 → 9 216; an inverted sparse-21 circle fixture →
12 600. On dense circle sets the Clipper union alone used to spend
minutes before the segment count was known (a v34-class request) — the
exact pre-union count above now refuses those requests right after
emission, before the boolean runs at all.

### Why 4 400 — measured live by ticket #36

The provisional 8 000 was measured AT the bound by the `shapes` live gate
(`docs/SHAPES-GATES.md` has the full run record). At the 7 722-segment
just-under rung (v6 circle dot 95% @ 60 mm — 858 disjoint dot regions):

| Timing | 7 722-seg rung (old bound) | 4 290-seg rung (shipped bound) |
|---|---|---|
| module generation (`gen_ms`, best of 5) | 9 031 ms | 3 316 ms |
| create round-trip (`create_ms`) | 59 653 ms | 21 911 ms |
| **native insert** (create−gen) | **≈50 622 ms** | **≈18 595 ms** |
| block-def segment census | 43 623 ms | 24 931 ms |
| sketch exit / re-enter edit | 72 ms / 531 ms | 58 ms / 530 ms |
| **region solve** (`GetSketchRegions`) | **103 109 ms** | **39 965 ms** |
| warm segment enumeration | 1 ms | 1 ms |
| exit + document rebuild | 40 ms / 3 218 ms | 37 ms / 2 763 ms |

- **The insert slope is steeper than projected for disjoint dots** —
  ≈6.6 ms/segment at the 7 722 rung vs the ≈1.4 ms/segment measured on
  merged square payloads (941 ms at 252 → 6 096 ms at 3 896). Dots pay
  per *contour* as well as per segment: a dot field is hundreds of
  separate regions where a merged QR is dozens.
- **The sketch's region solve is the edit-responsiveness wall:** 103 s
  at 858 regions — a region pick on the committed sketch stalls for
  nearly two minutes. At the tightened bound the same 858 dots solve in
  40 s (vertex count matters too: K = 5 vs K = 9 on the identical dot
  set). Enter/exit sketch edit stays sub-second and rebuild is ~3 s —
  the failure is specifically the region-picking path on
  disjoint-region-heavy sketches.
- So the bound **tightened to 4 400**: ~1.1× the largest live-proven
  insert (the 3 896–4 022-segment dense square class, ≈6 s native),
  which keeps **every recorded square payload legal** while refusing
  the measured-unresponsive shaped requests — a v6 circle at dot 90%
  (5 148) is now the cheapest refused rung.
- What it refuses vs the old bound: v6 circle ≥90% (5 148–7 722),
  ≥v9-class circles (5 752+) and ≥v9-class low-growth rounded (6 002+),
  all dense-payload shaped runs — the same classes 8 000 already refused
  a step denser, plus the rungs the measurement convicted. Squares
  still clear through the v14 dense class (≤4 022); the earlier
  "~v18–v20 square" headroom (~6 700) no longer exists — deliberate
  trade of unproven headroom for a proven-responsive top.
- **Honest caveat — segments are a blunt proxy.** The edit cost is
  driven by disjoint regions as much as by vertices: the two measured
  rungs carry the SAME 858 regions and differ 2.6× on solve time purely
  through K. A low-K dot field packs MORE regions per segment — a
  ~4 300-segment triangle-dot field is ~1 400 regions and region picks
  can still stall past a minute, so the tightened bound bounds the
  damage without fully curing it. The sharper instrument is a companion
  ceiling on delivered *contour/region count* — recommended follow-up,
  not folded into this disposition.
- The actionable message names the measured count, the 4 400 budget and
  the levers: shorter content (fewer cells), lighter ECC (smaller N),
  squarer shape (fewer segments per cell).

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
