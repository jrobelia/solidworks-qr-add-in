# Glossary

Domain terms for the SolidWorks QR Add-In. Grows lazily — add a term the
moment it is resolved in discussion, not before.

## QR / geometry

- **Working matrix** — the `(N+8)×(N+8)` cell grid the generator operates
  on: N×N data cells plus the fixed quiet zone as light cells (ADR 0002).
- **QR code** — the square of marked cells encoding the content, including
  the finder patterns. Does not include the quiet zone.
- **QR code size** — the width and height of the QR code, excluding the
  quiet zone. Cell growth does not change this requested size.
- **Overall size** — the width and height of the QR code including its quiet
  zone, bounded by the keep-out square.
- **QR square** — the inner construction square marking the QR code size.
- **Keep-out square** — the outer construction square marking the overall
  reserved area: the QR code plus its quiet zone.
- **Quiet zone** — the blank four-cell margin on each side of a QR code
  that scanners require. It must remain clear of other markings.
- **Cell pitch** — centre-to-centre spacing of QR modules:
  `finalSize / (dataCells + 2 × growthFraction)`.
- **Size readout** — the numeric size surfaces are the pane's info section
  (cell pitch, footprint, quiet-zone width — shown in the document's
  length unit, refreshed on unit/doc change events) and the block
  instance's PropertyManager **Scale** field, which reads as a resize
  FACTOR of the created size (ADR 0004). No sketch dimensions are emitted
  (ADR 0003 superseded the earlier driven/equation-linked dimension plan —
  host-side dims cannot observe instance Scale).
- **Payload preset** — a `PayloadGenerator` template (Url, WiFi, vCard…)
  that formats fields into the exact string encoded; the content shown is
  always the literal final payload, editable verbatim. The single
  `PayloadComposer` module (Core) owns them all: `compose(presetId, fields)`
  returns the literal payload or an error naming the bad field, and the
  pane enumerates `PayloadComposer.Presets` (id, display name, ordered
  field descriptors) to build preset inputs — never per-preset UI. In the
  session, a direct payload-box edit is a Raw/typed edit — the content
  type reverts to Raw — while entered field values are remembered per
  preset, so re-picking a type recomposes its fields.
- **Polarity / invert** — which working-matrix cells emit contours: dark
  cells normally (black code on a light field); the LIGHT cells under
  `invert` — a white code on a dark field bounded by the keep-out square,
  for marking/engraving (ADR 0002). The same contour result feeds the
  preview — no second renderer.
- **Cell shape** — the per-cell primitive each emitted cell contributes to
  the union (spec #13): `square` (today's grown rectangle, the default),
  `rounded square` (the grown rectangle with each corner cut by a
  polygonized quarter-arc, ticket #32), or `circle` — a disjoint K-sided
  polygon **dot** per emitted cell (ticket #33; dots never touch, so the
  corner-contact failure class disappears). One path per emitted cell
  either way — the union/validate/insert path downstream is shape-blind.
- **Cell-shape parameter** — the single knob a non-square shape takes; for
  rounded square it is the corner radius as a fraction of the GROWN cell's
  half-width (`ρ = f·(u/2+g)`), for circle the **dot size** — the dot
  diameter as a fraction of cell pitch — capped by the bounds recorded in
  `docs/INSERTION-GUARD.md`. Inert while the shape is square — the pane
  field is disabled but its text is kept verbatim.
- **Dot size** — the circle shape's parameter: the emitted dot's diameter
  as a fraction of cell pitch. Under Circle the growth field is inert the
  same way (pitch = S/N — dots are the sizing knob). Dots are disjoint by
  cap: `f ≤ 1 − (F+M)/u` so the orthogonal neighbour gap clears the
  insertion floor.

## SolidWorks

- **Block instance** — a `SketchBlockInstance` inserted into a sketch from
  a block definition; scales uniformly via its `Scale` property — the QR's
  resize handle, a factor of the created size (a dimension inside the
  definition distorts rather than rescales, spike-verified). Container for
  the real-size QR contours AND both construction squares — everything
  tracks `Scale` and the insertion point uniformly (ADR 0003; ADR 0004 —
  the definition draws at real size so SolidWorks' interactive contour
  picker/extrude works, like a user-made block).
- **Pane** — the add-in's docked `QR Sketch` task-pane tab (hosts the full
  UI; the retired floating window's role).
- **Insertion support** — the selected reference plane or planar face the
  sketch is created on; validated at create time.
