# ADR 0001 — QR pattern as a sketch block instance with unit-scale sizing

**Status:** accepted, **partially superseded by ADR 0003 and ADR 0004**
(2026-10-12): the host-sketch squares with coincident binding, driven dim,
and equation-linked keep-out dim were abandoned — the squares moved INSIDE
the block definition as construction geometry after live evidence showed
host geometry cannot bind to instance-projected edges or observe
`inst.Scale`. The unit-proportioned definition + `Scale = size` readout
was abandoned by ADR 0004 after interactive evidence showed SolidWorks'
contour picker cannot resolve a unit-tiny def — the definition now draws
at real size and `Scale` is a resize factor.

## Context

The MVP inserts the QR as static sketch lines at the requested size. The
engineer wants an *editable* size afterwards — a driving dimension on a
construction square that resizes the QR, a proportional keep-out (quiet
zone) square, and the ability to constrain the result to model geometry
("attached to things").

The naive reading — per-cell sketch constraints so the pattern re-solves —
is impractical at QR densities (~250–4,000 segments would need thousands
of constraints).

## Spike evidence (live, SW 2023 SP5)

- Block-instance regions **cannot** be consumed by `FeatureExtrusion3` via
  any API selection route — but a sketch feature *containing* a block
  instance extrudes fine **interactively** (engineer-verified twice; the
  API result was a selection-path artifact).
- A driving dimension *inside* a block definition does **not** rescale an
  instance uniformly — under an under-defined def the solver distorts the
  dimensioned entity (bbox 24→30 mm horizontally, 28 mm vertically).
- `SketchBlockInstance.Scale`/`Scale2`/`Angle` are settable, interactively
  and via API; scaling is uniform over everything in the definition.
- Host-sketch entities take relations to block geometry (coincident to a
  block corner proven; engineer confirmed coincident-to-origin by hand).
- Block instances cannot be dimensioned from outside (engineer-confirmed).

## Decision

- The QR cells are drawn at **unit size** (1 × 1 document unit) and made a
  **sketch block definition**; creation inserts one **block instance**
  whose `Scale` equals the requested final size in document units —
  the PropertyManager scale field reads as the literal size.
  *(superseded by ADR 0004: the definition draws at the real size and the
  instance lands at Scale 1 — the field is a resize factor; the unit-tiny
  def broke the interactive contour picker.)*
- **QR square** and **keep-out square** live in the *host* sketch as
  construction geometry — fully constrainable, positionable, dimensionable.
  The keep-out square is drawn only when quiet zone > 0; its side is
  equation-linked to the QR square's dimension so the total footprint is a
  live readout (`GLOSSARY.md` terms: QR square, keep-out square, quiet
  zone). *(superseded by ADR 0003: the squares moved inside the block
  definition as construction geometry; no dims or equations are emitted.)*
- The QR square carries a **driven** dimension showing the current size.
  *(superseded by ADR 0003: no sketch dims — the PM Scale field and the
  pane readout are the size surfaces.)*
- Resizing = editing the block instance **Scale** — the pane states this
  ("to resize, edit the block's Scale"). No dimension drives anything.
- Explode is optional: `ExplodeSketchBlockInstance` yields ordinary lines
  when the user wants raw geometry.

## Alternatives rejected

- Per-cell constraint graph — unmanageable at QR segment counts.
- Dimension inside the block driving instance rescale — distorts rather
  than scales (spike evidence).
- Permanent parametric *dimension* (dim drives block scale via equations) —
  no API/solver path links a DisplayDimension to instance Scale.
- A doc-event watcher rewriting `inst.Scale` on a watched dimension —
  fragile, works only while the add-in is loaded.

## Consequences

- The QR is one tidy object that attaches to model geometry and resizes as
  a unit; the squares + dims give a live size/footprint readout.
- "Edit the dim to resize" is degraded to "edit Scale" — accepted by the
  engineer as the achievable version of the vision.
- Verification gate before build: confirm the host-sketch squares/dims
  track block-scale changes live (constraints to block edges), and that
  equation-linked quiet-zone sizing survives scale edits.
