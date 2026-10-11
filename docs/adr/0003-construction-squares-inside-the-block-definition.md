# ADR 0003 — Construction squares live inside the block definition

**Status:** accepted (engineer decision, spec-10 round 4; supersedes the
host-sketch square/dimension plan in ADR 0001 — the unit block instance
itself is unchanged)

## Context

ADR 0001 put the QR square and keep-out square in the *host* sketch as
construction geometry: the QR square coincident-bound to the block's
outer edges carrying a driven size dim, the keep-out square centred and
sized by equation-linked dims. Two rounds of live evidence
(`docs/BLOCK-GATES.md` rounds 1–2, `bindexp`/`dimexp`) showed the design
cannot work:

- Host-sketch relations to block-internal geometry silently no-op — the
  coincident bound the *base point*, pinning the square at y=0 instead of
  the grown extent; every tracking gate failed.
- Host-side dimensions cannot observe `inst.Scale` — no API/solver route
  links a DisplayDimension value to the instance property.
- A dimension placed *inside* the definition reads definition space — it
  renders the unit value and does not track the instance Scale
  (`dimexp` round 3), so it can never be the size readout either.
- Equation-linked dims left dangling names in the equation manager when
  the sketch was deleted, refusing the next create's `Add2(solve:true)`.

The one uncertainty that remained: does construction geometry *inside* a
block definition render on its instances at all? If not, none of this is
achievable without host geometry.

## Spike evidence (live, SW 2023 SP5, `consexp`)

A fixture block — a solid ±0.5-unit square plus a larger
construction-flagged square, folded into one definition — showed:

- **Def-internal construction geometry renders on the instance** and
  extends its hit-tested footprint: at Scale=100 solid content ended at
  ±50 mm while picks hit the construction edge out to ±70 mm and missed
  beyond.
- **It tracks `Scale`**: Scale=40 shrank the rendered construction extent
  to ±28 mm.
- **It tracks position**: dragging `InstancePosition` carried the
  rendered construction edge.
- **Explode preserves the flag**: `ExplodeSketchBlockInstance` returned
  all 8 segments; the 4 construction lines kept
  `ConstructionGeometry=true` at the instance-projected extent.
- **It never feeds a profile**: `GetSketchRegionCount` unchanged.
- The def sketch reports endpoints in an offset frame — read segment
  *lengths* and *extents*, never raw coordinates, when verifying def
  content.

## Decision

- The **QR square** and **keep-out square** are drawn as construction
  geometry *inside* the block definition, in the same unit space as the
  normalized contours: QR square = the ±0.5 unit box, keep-out square =
  the working-matrix boundary `M·u/(N·u+2g)` of that span (ADR 0002).
- Both fold into the definition in the *same* `Select4` + 
  `MakeSketchBlockFromSelected` call as the contours — no host-side
  squares, no coincident binding, no display dims, no equations.
- The **size readout** is the block PropertyManager **Scale** field plus
  the pane's cell-size readout — both already exist.
- `ISketchInsertion` carries `AddConstructionSquares(squares)`; the old
  `AddConstructionLayout(squares, dimensionSpec)` seam and the
  `QrDimensionSpec` model are deleted.

## Consequences

- The squares are *definition geometry*: they render, they track every
  Scale/position edit for free, they explode with the flag, they never
  feed a profile — all verified live.
- Nothing in the host sketch needs binding: the block is self-contained
  and stays constrainable to model geometry as a unit.
- There is no on-sketch numeric size dim — accepted: the PM Scale field
  is the resize handle *and* the readout, matching the "edit Scale to
  resize" model the engineer already accepted in ADR 0001.
- Undo/redo simplify: deleting the sketch removes everything (no
  equation litter to sweep); a redo re-stages the squares into the
  definition through the same insertion seam.
