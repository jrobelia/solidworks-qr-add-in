# Ring-split research — does notching nested rings restore automatic profile selection?

Date: 2026-10-21
Status: Headless evidence survey complete. No SolidWorks run — this document
collects the recorded live evidence, the API-doc semantics, and the geometry
model, then defines the live spike that decides issue #31. "Ring split" and
"nested ring" are glossary terms (GLOSSARY.md:51–55).

## The question

Issue #31 (re-scoped as research): the QR generator preserves
`ring → hole → island` nesting as separate closed contours; SolidWorks
automatic profile selection refuses the result. Does a finite-width notch
through each nested ring — turning each ring band into a non-enclosing
C-shaped contour that connects its hole to the surrounding space — restore
automatic contour/profile selection, and on which carrier: the real-size
block instance the product emits, or only exploded native geometry?

## 1. Prior recorded evidence — which carrier produced which result

| When | Carrier | Route tried | Result | Source |
|---|---|---|---|---|
| Ticket-2 live run (pre-block, native segments) | Native sketch, 252 segs / 27 contours / 16 holes / 3 finder nestings | whole-sketch feature select → `FeatureExtrusion3` | `null` | docs/INSERTION-GUARD.md:74–78 |
| Ticket-2 live run | same native sketch | 11 material `ISketchRegion`s selected (8 outer bodies + 3 finder islands) | `Boss-Extrude2` + `Cut-Extrude1` | docs/INSERTION-GUARD.md:74–81 |
| Block spike BLK1 | Block-bearing sketch, ONE clean square inside a block instance (no nesting) | sketch-feature select / `ISketchBlockInstance.Select` / `SKETCHREGION` coord pick → `FeatureExtrusion3` | `null` every route — selection artifact, not geometry | docs/BLOCK-SPIKE.md:14–16, 39–57 |
| Block spike BLK1 control | Same sketch after `ExplodeSketchBlockInstance` | identical select+extrude | `Boss-Extrude3` — exploded single contour auto-extrudes | docs/BLOCK-SPIKE.md:16–17, 51–57 |
| Spec-10 round 2 | Block-bearing real QR sketch (unit def, Scale=25) | 9 API routes (§3) | all `null` except `ISketchRegion.Select` per-region objects → `Boss-Extrude5`; same route works exploded → `Boss-Extrude9` | docs/BLOCK-GATES.md:235–253 |
| Spec-10 round 5, engineer interactive | Add-in block-bearing QR sketch, unit def at Scale=25 | select sketch → Extrude (interactive) | **"doubly nested contour" prompt; no cell region pickable** | docs/BLOCK-GATES.md:19–32; docs/adr/0004…:16–20 |
| Spec-10 round 5, engineer interactive | Explode → re-make block by hand (real-size def, Scale=1) | interactive extrude | works — the extrude path opens; regions pickable inside cells | docs/adr/0004…:22–24, 47–48 |
| Spec-10 round 5, engineer interactive | Same unit-def sketch, `Scale` lowered | interactive extrude | works at Scale 1 and 2, fails at 10 and 25 — cutoff in (2,10], mechanism unexplained | docs/adr/0004…:25–30 |
| Engineer hand-check | User-made block containing all QR lines | interactive extrude of sketch feature | succeeds | docs/BLOCK-SPIKE.md:199–207 |

**Reading the evidence correctly.**

- The "whole-sketch extrusion returns `null`" claim from #31's brief rests on
  **two distinct recorded events on two different carriers**:
  1. native-segment QR sketch (ticket-2 architecture) — `null`
     (docs/INSERTION-GUARD.md:77);
  2. block-bearing QR sketch, every API selection route (spec-10 round 2,
     docs/BLOCK-GATES.md:240–250) and the single-square block sketch
     (docs/BLOCK-SPIKE.md:41–49).
- On the **block carrier**, `null` is a *selection-path artifact*, not a
  profile rule: it fired even on a non-nested single square (BLK1), while
  the identical geometry extrudes after explode — and interactively, the
  sketch feature containing a block instance extrudes fine
  (docs/BLOCK-SPIKE.md:201–207). A notch cannot fix a selection path that
  cannot see block internals at all.
- On the **native carrier**, the failure driver is the contour nesting
  itself — SolidWorks' own error names it: *"The sketch contains a doubly
  nested contour. Pick a sketch entity to define an open or closed contour.
  To define a region, pick inside an area bounded by sketch geometry."*
  (verbatim UI string, public record:
  https://forum.solidworks.com/thread/210587 — and observed verbatim on this
  product's own sketch at Scale=25, docs/BLOCK-GATES.md:22–24).
- Notably, the doubly-nested prompt fired **through** the block instance
  (Scale=25 unit def): SolidWorks' interactive region solver does resolve
  block-internal contours — at real-size def (Scale=1) regions inside the
  block are pickable, and `GetSketchRegions`/`ISketchRegion.Select` see
  them via API too (§3, §5).

## 2. SolidWorks profile-selection semantics (primary sources)

**The object model.** `ISketchContour` = "a collection of end-point
connected sketch segments that might, or might not, form a face";
`ISketchRegion` = "white space on a sketch bordered by sketch entities…
basically forming a face". An extrude feature's `IExtrudeFeatureData2::
Contours` array "can contain both ISketchContour and ISketchRegion objects"
— the feature's Selected Contours list.

Sources (read from the installed API help CHM,
`C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\apihelp.chm`; canonical URLs):
- https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISketchContour.html
- https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISketchRegion.html
- https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IExtrudeFeatureData2~Contours.html

**`FeatureExtrusion3`/`FeatureCut4` have no contour-scope argument.**
`UseFeatScope` and `UseAutoSelect` govern **body scope** ("the bodies that
the feature affects", mark 8 per body) — not profile selection. The profile
is whatever the current selection is: the sketch feature (auto-solve), or
explicit SKETCHCONTOUR / SKETCHREGION picks, or `ISketchRegion`/`ISketchContour`
objects. The official "Create Extrude Feature Using Sketch Contours" example
selects the sketch + `SKETCHCONTOUR` points with `EnableContourSelection=true`
and mark 4 before calling `FeatureExtrusion3`; the "Enable Contour Selection"
example does the same with `SKETCHREGION`. `FeatureCut4`'s `Flip` removes
material *outside* the profile ("flip side to cut") — cut-only semantics,
same selection model.

Sources:
- https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFeatureManager~FeatureExtrusion3.html
- https://help.solidworks.com/2023/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IFeatureManager~FeatureCut4.html
- https://help.solidworks.com/2023/english/api/sldworksapi/Create_Extrude_Feature_Using_Sketch_Contours_Example_VB.htm
- https://help.solidworks.com/2023/english/api/sldworksapi/enable_contour_selection_example_csharp.htm

**The documented nesting limit.** When a whole sketch is selected for a
feature, SolidWorks auto-resolves the sketch's regions: multiple disjoint
closed contours are all consumed as profiles, a contour inside a profile is
its hole — but **a contour nested inside a hole (island) is "doubly
nested"** and the auto-solve refuses the whole sketch with the prompt quoted
in §1. The user-facing fix SolidWorks documents is to pick regions/contours
("select a contour for Selected Contours", or the Contour Select tool).
The QR's finder pattern (`ring → hole → island`) is exactly doubly nested;
data-area rings nest deeper still.

Sources:
- https://my.solidworks.com/reader/onlinehelp/2021%252Fenglish%252Fsolidworks%252Fsldworks%252Ft_selecting_contours.htm/selecting-contours (Selecting Contours help topic)
- https://forum.solidworks.com/thread/210587 (verbatim error text)
- https://www.eng-tips.com/threads/doubly-nested-contour.222374/
- In-repo corroboration: docs/BLOCK-GATES.md:22–32, docs/adr/0004…:16–30

**Do block instances participate in feature profile selection?** The API
help is silent; the recorded product evidence says **the region solver sees
through them, selection routes do not**:
- `ISketch::GetSketchRegions`/`GetSketchRegionCount` on the block-bearing
  sketch return the block-internal regions — `ISketchRegion.Select` on them
  extrudes (`Boss-Extrude5`, tools/LiveProbe/Program.cs:2617–2632;
  docs/BLOCK-GATES.md:249, 108) even though the same sketch enumerates zero
  segments (`hostSegs=0`, Program.cs:2290–2294) and coordinate picks of
  block-internal entities fail (docs/BLOCK-SPIKE.md:159–162).
- Interactive region-pick inside cells works on the real-size block
  (docs/adr/0004…:47–48).
- But the sketch-feature selection route (`SelectByID2(name,"SKETCH")` →
  `FeatureExtrusion3`) returned `null` even on a non-nested single-square
  block (docs/BLOCK-SPIKE.md:41–49) — the API whole-sketch route does not
  resolve block-internal profiles.

## 3. The nine tried feature-creation routes (tools/LiveProbe/Program.cs)

`TryExtrudeByName` (Program.cs:2496–2659) runs these in order on the
block-bearing QR sketch; shared calls: `Extrude` = `FeatureExtrusion3`
(sd,blind 2 mm, `UseFeatScope=true`, `UseAutoSelect=true` — body-scope args,
Program.cs:2471–2488), `ExtrudeThin` = `FeatureExtrusionThin`
(Program.cs:2661–2678), cut = `FeatureCut4` (Program.cs:2637–2655).

| # | Route (Program.cs) | Result on block-bearing QR | Why |
|---|---|---|---|
| 1 | sketch-feature `SelectByID2(name,"SKETCH")` → `FeatureExtrusion3` (2500–2508) | `null` | API whole-sketch route cannot resolve block-internal profiles (BLK1 single-square proved it isn't the nesting) |
| 2 | `ISketchBlockInstance.Select` → `FeatureExtrusion3` (2510–2523) | `null` | instance object is not a profile selection |
| 3 | `EnableContourSelection` + `SelectByID2("","SKETCHREGION",pt)` at material centres (2525–2547) | refused — no region picks land | coordinate picks cannot hit block-internal regions |
| 4 | sketch-feature → `FeatureExtrusionThin` (2554–2562) | `null` | thin path still needs a resolvable profile |
| 5 | sketch-feature → legacy `FeatureExtrusion` (2565–2581) | `null` | same selection artifact |
| 6 | `SimpleFeatureBossExtrude(sketchName)` (2583–2591) | `null` | same selection artifact |
| 7 | `EnableContourSelection` + `SKETCHCONTOUR` picks, `swSelEXTSKETCHSEGS` marks (2596–2614) | `null` | contour picks don't reach block internals |
| 8 | `sketch.GetSketchRegions()` → `ISketchRegion.Select(true)` per region object (2616–2632) | **PASS → `Boss-Extrude5` (8 material regions, faces=30)** | region objects exist and are selectable through the block |
| 9 | sketch-feature → `FeatureCut4` (2637–2655) | not reached (route 8 won first) | cut shares the boss's selection model |

On **exploded** geometry only two of these were exercised: the
sketch-feature route (works on the one-square control, BLK1) and
`ISketchRegion.Select` (`Boss-Extrude9`, docs/BLOCK-GATES.md:252–253).
Nobody has yet run "whole-sketch select → `FeatureExtrusion3`" on the
exploded **QR** geometry — that is precisely the experiment #31 needs.

## 4. The generator's contour model — where a notch would hook in

Pipeline (`src/SolidWorksQrAddIn.Core/Geometry/QrSketchGenerator.cs`,
`Build` at :154):

1. cells → grown rects on the integer grid (`U = GridUnitsPerCell = 10 000`,
   g = `round(r·U)` units/side) (:219–236);
2. `Clipper.BooleanOp(Intersection, subject, clipSquare, tree, NonZero)` →
   `PolyTree64` with nesting preserved (:257–258);
3. `ReadRegion` (:566–578) walks the tree into `GridRegion`/`GridHole`
   (:550–560): a region = boundary `Path64` + `List<GridHole>`; a hole =
   boundary `Path64` + `List<GridRegion> Islands` — the nesting is a strict
   alternating tree, holes' children are filled islands, islands can carry
   deeper holes recursively;
4. `Flatten` (:580–591) emits every loop — outer, hole, island — as a
   `Path64` in `flatPaths` (+`isHole` flag);
5. segment ceiling (:283–288) → `ContourValidator.FindViolation`
   (:290–293) → affine map to sketch metres (:299–305) →
   `QrRegion`/`QrHole`/`QrContour`/`QrSketch` (QrSketch.cs:12–55, 161–217);
6. `CreateQrSketchCommand` stages `sketch.Edges` via `insertion.AddLine`
   (CreateQrSketchCommand.cs:146–147) → `SwSketchInsertion.
   RealizeAsBlockInstance` draws every buffered edge at real size, folds
   contours + construction squares into one block definition at
   `Scale = 1` (SwSketchInsertion.cs:201–280).

**Natural hook:** between steps 3 and 5, on the integer grid. Two shapes:

- **Tree rewrite** — for each `GridHole` with `Islands.Count > 0`, splice
  its boundary into the parent region's boundary as a single C-shaped
  `Path64` and promote the islands into the enclosing collection;
  recurse. Keeps the existing `Flatten`/`ConvertRegion` machinery.
- **Second Clipper pass (cleaner)** — collect the notching rectangles for
  every hole-with-islands, run `BooleanOp(Difference, firstPassPaths,
  notchRects, tree2, NonZero)`, then feed `tree2` into the *unchanged*
  `ReadRegion → Flatten → validate → convert` path. Clipper re-derives all
  nesting: the former hole space merges with the exterior, islands surface
  as top-level regions automatically, and the validator sees the true
  delivered geometry.

Either way the option is `QrSketchRequest`-level (QrSketchRequest.cs:13–66
is where settings grow), `QrSketchInfo` (QrSketch.cs:66–155) gains split
count/effective width, and the preview needs no new code — it fills
`sketch.Contours` even-odd, so a notched C-band plus promoted island
renders exactly what the sketch gets (QrPictureRenderer.cs:97–134).

**Validator invariants the notch must satisfy**
(`ContourValidator.cs:20–36`): nonzero area per contour, no zero-length
edges, no self-touch/self-intersection, **no shared vertices or
vertex-on-edge contacts between distinct contours** (:79–105 — a notch
channel whose walls land on an island edge, or whose overhang clips a
neighbouring region, produces exactly these). Floors:
`MinGridFeatureUnits = 10` units and `MinInsertableFeatureMm = 0.05` mm
(QrInsertionGuard.cs:31–37), plus `MaxSegmentCount = 4 400`
(QrInsertionGuard.cs:56) — notching adds ~4 vertices per split, negligible.

## 5. The notch treatment space

**What exactly to cut.** A nested ring's band is the material between an
outer loop `O` and a hole loop `H` that has islands. The notch is a
rectangular channel spanning `O`'s outer edge → `H`'s inner edge through
the band: it must cross *both* boundaries (crossing only `O` leaves `H`
closed → still doubly nested; crossing only `H` is impossible). After the
cut, `O`+`H` splice into one simple C-polygon; the islands inside `H` sit
in space now connected to the exterior and become disjoint top-level
contours.

**Which loops need it — and how many.** SolidWorks tolerates **one level**
of nesting (profile + holes); only holes that *contain islands* are
"doubly nested". So: **one notch per `GridHole` with `Islands.Count > 0`,
at every depth** — not per island (a hole containing 3 islands still needs
one channel into it). For the v1 payload that's the 3 finder holes
(docs/INSERTION-GUARD.md:63–64). Partial treatment is worthless: any
remaining doubly-nested hole still trips the prompt — this is itself a
live-matrix claim (E5 below). Splitting "only finder rings" is therefore
**not** a viable option — dense payloads nest rings elsewhere in the
matrix (the brief's "arbitrary nested rings" requirement).

**Placement.** On the rectilinear grid, pick a minimum-band-thickness
locus between `O` and `H` (adjacent-parallel-edge gap = grown one-cell
band, `u+2g` units wide for a finder ring) and centre an axis-aligned
channel rect there. Constraints: inner end must land inside `H`'s area
without reaching any island — the `H↔island` clearance is the un-emitted
channel `u−2g` (≥ `MinGridFeatureUnits`, typically ~9 400 units ≈ 1.1 mm at
25 mm/3%) so an inner overhang of ~`(u−2g)/2` is safe at the min-distance
locus; outer overhang must stay < `u−2g` to avoid nicking a neighbouring
disjoint region. Placement must re-check clearance against *all* contours
(islands, deeper bands, neighbours) — a degenerate locus gets the
fallback: try the next nearest-approach pair.

**Width.** The channel is a light corridor through a dark band — the
smaller the better for scannability, but its walls must clear the
insertion floor: `w ≥ max(MinGridFeatureUnits, ⌈0.05 mm/mmPerUnit⌉)`. At
25 mm/3% (mmPerUnit ≈ 0.0001187, pitch 1.187 mm) that's ≈421 units ≈
0.05 mm ≈ **4.2 % of one cell** — a hairline. It also must not *heal shut*
in SolidWorks: 0.05 mm sits ~5000× above the ~1e-5 mm zero-length
tolerance (QrInsertionGuard.cs:21–23), so the opening is real geometry.

**Scannability.** The finder pattern's 1:1:3:1:1 run ratio is the
decode-critical structure and three of the cuts necessarily land there
(unless placement deliberately avoids finders — then payload rings still
need theirs). ECC M repairs ~15 % codeword damage, which thin channels
should stay inside, but finder damage degrades *detection*, not codewords
— the issue's acceptance criteria already disclaim a scannability promise
("Claiming the notch preserves scanner readability" is out of scope);
record measured widths so QA can judge.

**The two routes cost differently here, and this is the honest
trade.** A hairline contour-notch (~4 % of one module) is nearly
invisible to a scanner — the cell still reads dark under the sampling
grid, so even a finder-ring cut barely registers. The grid-space bridge
flips a whole module: on a data-area site that's an ordinary ECC-
correctable module error (nearly free at M level), but on a finder ring
it's a *structural* flip at a signature-bearing position — real cost, and
unavoidable since the all-or-nothing rule forces a channel through every
nested site. Per-site cost is bounded (one module per 1-cell band
regardless of code size — a v1 matrix pays the same ~4 cells total as a
v10), but for small codes the finders dominate more of the matrix and the
relative damage is highest exactly where it hurts most. Mitigations:
place the channel at a ring **corner** — off the horizontal/vertical
centre-lines the detection runs sample — and remember the feature is
opt-in: an engineer choosing extrudability accepts a readability margin
trade, and marking workflows often weight permanence over scan margin.
Whether that trade is acceptable, or whether the finder sites justify the
harder hairline route, is a product call the spike's width measurements
should inform. **Update — the hybrid resolves it** (live-proven in the
third interactive run, §6): hairline cuts at the three regular finder
rings — the easy, repeatable subset of contour-cutting — and whole-cell
drops at arbitrary data-area sites, keeping the hard geometry confined to
exactly three known orthogonal bands.

**Invert.** The same rule applies to whatever the emitted polarity
produces: under invert the *light* cells emit, so the ring sites move
(finder centres become non-emitting holes *without* islands — no notch —
while light rings around dark blobs containing light islands become the
nested sites). The hole-with-islands rule is polarity-agnostic by
construction since the tree is built on the emitted set either way
(QrSketchGenerator.cs:216–236, 566–591). **Circle mode** emits disjoint
dots with no contour machinery at all — nothing to notch
(QrSketchGenerator.cs:167–174; circle cells trivially have no nesting,
which is also a *different answer* to #31's user goal).

**Alternative: cut in grid space, not contour space.** `Build` constructs
`subject` as one grown rect *per emitted cell* before the BooleanOp
(QrSketchGenerator.cs:219–236) — so a channel can be opened by simply
dropping the right cells from `subject` and re-running the union, no
corridor geometry at all:

- For each hole-with-islands, BFS the band's dark cells to find the
  minimal cell path spanning hole-side light → containing void, then drop
  those cells. A one-cell-thick finder band flips **one cell**; iterate
  until no doubly-nested hole remains (deep nesting = a light path
  through multiple band levels; each pass removes ≥1 cell, so it
  converges).
- **Zero sliver/validator risk**: the opened channel's walls are the
  grown edges of the surviving neighbours — a `u−2g` light gap, exactly
  the geometry the whole pattern already emits and the guard already
  floors (`minGridFeature = min(2g, u−2g) ≥ MinGridFeatureUnits`,
  QrSketchGenerator.cs:199–204). No sub-cell feature is ever created.
- **Scannability may actually be better** than the hairline slit: a
  module-aligned flip is the damage shape QR error correction is designed
  for, whereas a sub-module slit produces partial-module edges under the
  scanner's sampling grid.
- **Arbitrary shapes come along free** where they still ride the cell
  grid: a "flipped" rounded/shaped cell is just its paths dropped from
  `subject`. True free-form contour cutting is only needed if the
  treatment must survive in a regime without a per-cell representation —
  which this pipeline never produces.

The real difficulty moves to **attribution**: which light cells belong to
each hole (point-in-polygon or grid-flood bookkeeping) and which dark
cells form its band — graph work on the matrix, not polygon surgery. For
the feasibility spike neither route is needed: hand-drawn fixtures answer
the solver question first, and if the channel doesn't restore
auto-selection the cutting algorithm is moot either way.

## 6. Answer

**Interactive evidence (2026-10-21, engineer runs on the live sample QR
block) — the mechanism and the product claim are now proven
interactively.** First run: deleting the enclosing ring contour at every
nested site (the three finder rings plus one small data-area ring at
matrix centre — see screenshot on #31) made SolidWorks auto-select all
remaining contours on the real-size block instance, no prompt. Second
run: **finite-width cuts** through each enclosing ring — narrow channels
leaving essentially all ring material in place — produced the same
result: auto-selection engaged across the whole sketch. **Third run —
the hybrid, live-proven end-to-end**: thin cuts at the three finder
rings (the repeatable structural sites) *plus* whole-cell deletions for
the internal island sites — auto-selection again engaged across the
sketch. This confirms:
(a) the interactive solver **does** consume block-internal contours; (b)
the trigger is exactly "an island completely surrounded by a filled
region" (the doubly-nested condition); (c) **all-or-nothing is
corroborated twice** — every nested site had to be opened, including the
data-area site the finder-only fixture list undercounted; and (d) **the
solver treats a narrow channel as a real opening** — the finite-width
variant preserves the ring's material and still restores auto-selection.

The engineer's third run also fixes the hybrid design the trade-off
below hinted at: **hairline cuts at the three (regular, repeatable)
finder rings; whole-cell drops at arbitrary data-area sites.** Two
corollaries fall out of the run:

- The data-area channel path must reach **exterior-connected void**, not
  merely tunnel into a differently-enclosed volume — it "might be many
  cells" when sites nest inside other holes. Implement as BFS whose
  target set is the matrix's *flood-filled exterior light region*
  (flood from the border over un-emitted cells), iterating until no
  doubly-nested hole survives.
- Since data-site drops spend real ECC budget and finder cuts spend
  detection margin, the option should be **gated on a minimum
  error-correction level** (e.g. refuse at L; the pane already exposes
  the ECC choice, so the session/composer seam can validate it).

What the interactive runs do not measure: the *minimum* effective channel
width (the test cuts were comfortably visible, not hairline) — i.e.
whether a `MinInsertableFeatureMm`-scale channel still reads as open to
the solver, or whether the width floor is set by solver visibility rather
than insertion validity. The grid-bridge route makes this moot for the
product (its channel is `u−2g`, far above either floor), but a width
sweep stays on the live matrix for the contour-notch variant — which the
hybrid now needs at the three finder sites.

**Does the notch restore automatic profile selection? — Probably yes on
exploded/native geometry; interactively, the enclosure-removal mechanism
is proven (deletion variant).** Removing every hole-with-
islands leaves only disjoint loops and singly-nested empty holes — the
documented "doubly nested" refusal condition no longer exists, and the
BLK1 control already proved whole-sketch `FeatureExtrusion3` works on
exploded non-nested content. The open risk is whether SolidWorks'
auto-solve consumes *all* remaining top-level contours the way interactive
multi-contour sketches behave — plausible and standard, but the live spike
must show it on real QR density.

**On which carrier — the honest split answer:**

- **API-driven auto-selection works only on exploded/native geometry.**
  On the block carrier, `SelectByID2(sketch,"SKETCH") → FeatureExtrusion3`
  fails even for a non-nested single square — the API whole-sketch route
  does not see into block instances, a selection-layer dead end that no
  geometry treatment can fix (docs/BLOCK-SPIKE.md:39–57).
- **Interactive auto-selection on the real-size block — proven, both
  variants (see above).** The interactive solver resolves block-internal
  contours; deleting enclosing rings restored auto-selection, and narrow
  finite-width cuts did the same while preserving ring material. The
  user-facing claim — engineer clicks the sketch and extrudes without
  picking — holds interactively on the block carrier.
- **`ISketchRegion.Select` keeps working on both carriers** — the notch
  must not regress the only route that works today; islands simply
  promote to top-level regions.

**Decision consequence.** If #31's goal is "user clicks the sketch and
extrudes without picking", the deciding experiment is the *interactive*
one on the real-size block — and it probably succeeds. If the goal is
"*API* feature creation without per-region selection", the block carrier
is dead regardless of geometry; the option only buys anything on exploded
geometry — i.e. it couples with a deliver-exploded (or explode-at-commit)
product decision, which ADR 0001/0003/0004 currently reject.

## 7. Live-experiment matrix

One session-free run per row; fixtures drawn natively (no add-in needed)
so the harness isolates SolidWorks semantics from generator changes.
`FX-RING` = concentric-squares ring fixture (7-cell band square + 5-cell
hole + 3-cell island square — a hand-drawn finder pattern);
`FX-QR` = the real `SW-QR-MVP` v1 contour set (27 contours, 3 ring sites).

| # | Fixture | Carrier | Route | Expected signal | Proves |
|---|---|---|---|---|---|
| E1 | FX-QR nested, no notch | native segments | sketch-feature select → `FeatureExtrusion3` | `null` + doubly-nested prompt | baseline: native QR fails auto-select (re-proves INSERTION-GUARD ticket-2 on today's build) |
| E2 | FX-RING notched | native segments | sketch-feature select → `FeatureExtrusion3` | `Boss-Extrude` created, 2 bodies (C-band + island) | minimal case: one notch removes the refusal |
| E3 | FX-QR all-3-rings notched | native segments | sketch-feature select → `FeatureExtrusion3` | `Boss-Extrude`, bodies ≈ material regions | **the core claim on real density** |
| E4 | FX-QR notched | native segments | sketch-feature select → `FeatureCut4` (needs plate body) | `Cut-Extrude` | cut path shares the fix |
| E5 | FX-QR, only 2 of 3 rings notched | native segments | sketch-feature select → `FeatureExtrusion3` | still refused | all-or-nothing: every nested hole must be notched |
| E6 | FX-QR notched | real-size block (Scale=1) | sketch-feature select → `FeatureExtrusion3` (API) | predicted `null` | block carrier dead at the selection layer, notch can't fix it |
| E7 | FX-QR notched | real-size block (Scale=1) | `GetSketchRegions` census + `ISketchRegion.Select` | regions enumerate, extrude still works | no regression of the working route; islands promoted to selectable top-level regions |
| E8 | FX-QR notched | native segments | `GetSketchRegionCount` + validator-style sweep | region census matches headless prediction; no sliver/short-edge entities | geometry-quality signal the validator models |
| E9 | FX-QR notched | exploded block (product path: insert block → `ExplodeSketchBlockInstance`) | sketch-feature select → `FeatureExtrusion3` | `Boss-Extrude` | the *deliverable* carrier if "explode at commit" is chosen |
| E10 (manual) | FX-QR notched | real-size block, interactive | engineer clicks sketch → Extruded Boss/Base | no doubly-nested prompt; PM previews all profiles without picks | **the user-facing claim — interactive only, cannot be driven headless** |

E10 is now **proven interactively** (2026-10-21): auto-selection engages
on the real-size block once enclosures are removed — deletion, uniform
finite-width cuts, AND the hybrid (thin finder cuts + data-site cell
drops) all worked. The remaining rows bound the API route and the
native-geometry claim.

Minimum decisive set: **E1, E2, E3, E6** — E1/E3 decide the native claim,
E2 isolates the mechanism, E6 bounds the carrier claim. E10 is done.

## 8. Open risks the spike must measure

- **Sliver/short-edge creation** — the channel adds wall edges of length =
  local band thickness and width edges `w`; residual slivers between the
  channel and adjacent loops/islands must stay ≥ `MinInsertableFeatureMm`
  (validator `CheckPair`/`CheckEdgePair`, ContourValidator.cs:79–141).
- **Validator rejection** — a channel wall tangent to an island edge or an
  overhang touching a neighbour produces shared-vertex/vertex-on-edge
  violations (`InvalidGeometry`); placement needs the clearance re-check
  and a fallback locus before host mutation.
- **`Scale`-edit re-break** — even if E10 passes at `Scale=1`, a later
  resize may re-break region resolution exactly like the unit-def picking
  did (docs/adr/0004…:49–54); the matrix's `Scale=1.6` re-pick is worth a
  line in the live record.
- **Invert-mode rings** — the nested sites differ under invert (§5); the
  fixture set needs one inverted variant to prove the polarity-agnostic
  rule, since the brief requires both.
- **Region-pick point robustness (harness, not product)** —
  `MaterialPoints` picks region bbox centres (Program.cs:2681–2689); a
  C-shaped band's bbox centre can land in the notch mouth — region
  verification needs interior-point picks, or the probe misreads a good
  contour as a failure.
- **Multi-body semantics** — auto-selection consuming all top-level
  contours yields one feature with disjoint bodies; verify the body count
  and that nothing material is silently dropped (compare with the
  `ISketchRegion.Select` result).
- **Undo/redo parity** — `SketchUndoRegistration` replays `sketch.Edges`
  (CreateQrSketchCommand.cs:206–211); notched geometry reinserts
  identically, but the live gates should re-run the blockqr undo gate on
  the option-on path.

## Appendix — cited in-repo anchors

- Pipeline: `src/SolidWorksQrAddIn.Core/Geometry/QrSketchGenerator.cs`
  (:154 Build, :257–258 union/clip, :264–276 tree read/flatten,
  :283–293 segment ceiling + validation, :299–305 metre conversion,
  :550–591 GridRegion/GridHole/ReadRegion/Flatten/ConvertRegion)
- Contour model: `src/SolidWorksQrAddIn.Core/Geometry/QrSketch.cs:12–55`
  (QrContour/QrHole/QrRegion), :161–217 (QrSketch)
- Validator: `src/SolidWorksQrAddIn.Core/Geometry/ContourValidator.cs:20–36`,
  pair checks :79–141
- Guard: `src/SolidWorksQrAddIn.Core/Geometry/QrInsertionGuard.cs:31–37`,
  ceilings :56, :75
- Request/info surfaces: `src/SolidWorksQrAddIn.Core/Geometry/QrSketchRequest.cs:13–66`;
  `src/SolidWorksQrAddIn.Core/Geometry/QrSketch.cs:66–155` (QrSketchInfo)
- Insertion: `src/SolidWorksQrAddIn.Core/Commands/CreateQrSketchCommand.cs:135–152`;
  `src/SolidWorksQrAddIn/Host/SwSketchInsertion.cs:201–280`
  (RealizeAsBlockInstance), :95–107 (AddLine staging)
- Extrusion routes: `tools/LiveProbe/Program.cs:2471–2659`
  (`Extrude`/:2496 `TryExtrudeByName`/:2661 `ExtrudeThin`), region census
  :2281, :2617–2632, pick-point strategy :2681–2689
- Preview parity: `src/SolidWorksQrAddIn.Core/Preview/QrPictureRenderer.cs:97–134`
- Recorded evidence: docs/INSERTION-GUARD.md:63–84; docs/BLOCK-SPIKE.md
  (BLK1 :13–17, Q1 :39–61, update :199–225); docs/BLOCK-GATES.md
  (round 5 :19–47, extrusion routes :235–253); docs/adr/0004…:16–54
