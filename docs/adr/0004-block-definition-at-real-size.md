# ADR 0004 — Block definition at real size; Scale is a resize factor

**Status:** accepted (engineer-reported interactive evidence, spec-10
round 5; supersedes the unit-proportioned-definition part of ADR 0001 —
the block-instance design itself and ADR 0003's def-internal squares
stand)

## Context

ADR 0001 drew the block definition **unit-proportioned** (1 × 1 document
unit) and carried the requested size on `instance.Scale` — the
PropertyManager field read as the literal size. Every API gate passed
(`blockqr` 54/55): regions enumerate, `ISketchRegion.Select` extrudes,
squares track Scale/drag.

Interactive use then showed what the harness could not: selecting the
committed sketch and clicking Extrude produced SolidWorks'
"**doubly nested contour**" prompt, and no region inside the block could
be picked. Block-internal entities were already known to be locked
against entity picks; the decisive experiments were the engineer's own:

- **Explode the add-in's block, re-make a block from the same geometry
  (Tools > Blocks > Make) → the sketch extrudes normally.** A user-made
  block is definition at real size + instance `Scale = 1`.
- **On a pre-fix sketch, setting `Scale = 1` (shrinking the rendered QR
  to 1 mm) also restores extrusion** — the interactive picker refuses at
  `Scale = 25`, not at `Scale = 1`. Whether intermediate factors (0.5,
  1.6, 2 …) break picking was not isolated; `Scale = 1` is the only
  verified-working non-trivial state.

## Decision

- The block definition is drawn **at the staged (real) size** — sketch-
  space metres, no normalization — and the instance stays at `Scale = 1`
  after `MakeSketchBlockFromSelected`, matching the shape SolidWorks
  itself produces interactively.
- The instance **Scale field is a resize FACTOR** of the created size
  (2 = double), not a size readout. The pane's cell-size readout remains
  the numeric size surface; the pane note now says so.
- Everything else stands: one instance, def-internal QR square +
  keep-out square (construction, ADR 0003), no dims, no equations,
  explode preserves flags.

## Consequences

- The interactive extrude path is restored to what a user-made block
  does: region-pick inside a cell, or explode → select-all → extrude.
- **Caveat, same mechanism:** a later `Scale` edit may re-break
  interactive region-picking — only `Scale = 1` is verified-working and
  `Scale = 25` verified-broken; intermediate factors are untested. If
  picking fails after a resize, explode, or re-insert at the new size
  instead.
- "Edit Scale to resize" survives, now in ratio units — slightly less
  direct than typing the mm size (the ADR 0001 readout elegance), which
  is the accepted cost of an extrudable sketch.
- The `blockqr` gates updated: `Scale = 1`, def construction segments
  sized at real values (25 mm / 34.43 mm sides), the Scale-edit gate
  drives a factor (1.6 ⇒ ±27.5 mm keep-out) instead of a size (40 mm).
- Headless fakes/tests model the same contract: def = staged geometry,
  instance Scale = 1.
