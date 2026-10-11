# ADR 0002 — The quiet zone is always in the working matrix

**Status:** accepted (design converged with engineer, 2026-10-10;
extends ADR 0001 — nothing in it is reversed)

## Context

ADR 0001 made the QR a block instance and put the quiet zone in the host
sketch as a **construction** keep-out square — a clearance marker, never
geometry. The generator currently *strips* the encoder's 4-cell quiet-zone
padding, so the pipeline only ever sees N×N data cells.

Issue #11's `invert` option exposes the flaw in that model: for engraving
the engineer cuts the **light** cells (engineer-confirmed semantics,
2026-10-10), and an untouched margin has the same appearance as untouched
dark modules — an inverted field bounded at the nominal square would have
no readable quiet zone. The light cells *of the quiet zone* must emit
geometry too — which means they must exist in the matrix.

## Decision

- **The working cell matrix always includes the quiet zone as light
  cells** — `N×N` data cells plus `q` light cells on each side (the
  encoder's fixed 4-cell padding is simply the `q=4` instance; #11's
  quiet-zone field parameterizes `q`, including `q<4` by keeping fewer
  padding cells).
- Shape primitives emit at **dark** positions in normal mode, at **light**
  positions under `invert` — the polarity select is the *only* mode
  switch. There is no separate "include the margin when inverted" rule;
  it falls out of the matrix.
- The **keep-out square is the working-matrix boundary** in both modes:
  construction clearance marker in normal mode, the engraved field's real
  outer edge under invert — same square, same equation linkage (ADR 0001).
- All geometry is clipped to the matrix square (one
  `BooleanOp(Intersection)`): a guaranteed no-op in normal mode (grown
  dark cells cannot reach the q-cell border), a clean rectangular field
  edge under invert.
- **Size semantics unchanged**: `S` still measures the N-cell data area;
  the footprint under invert is `S + 2q·p` — the `S + 8p` picture
  footprint already documented in `docs/RESEARCH.md`.
- Dark cells are interior holes/islands of an inverted field; isolated
  light cells enclosed by dark cells become separate pockets (existing
  PolyTree island-in-hole nesting — no new machinery).
- `invert` + `q = 0` lands the field edge on the QR boundary — warn via
  the status line; scannability remains the engineer's call.

## Consequences

- `invert` becomes a one-line semantic: which polarity emits shapes.
  #11's invert definition simplifies accordingly — no special extent
  handling.
- The quiet-zone value (#11's field) gains geometric meaning under invert
  (it sizes the engraved field) while keeping its construction-marker
  role in normal mode.
- The cell-shapes spec inherits this matrix model unchanged — shapes at
  post-inversion emit-positions, clip at matrix bounds.
