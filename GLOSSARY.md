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
- **Payload template** — engineer-entered text that may contain `$PRP`
  property references. It is input to resolution, not necessarily the
  encoded payload.
- **Resolved payload** — the literal text produced by resolving every
  property reference in a payload template against the active document.
  This is the text the QR encodes.
- **Nested ring** — a closed emitted band whose hole contains another
  emitted region (`ring → hole → island`), as in a QR finder pattern.
- **Ring split** — a finite-width channel through a nested ring that
  connects its hole to the surrounding space, so the ring no longer forms
  a closed nested profile.
- **Cell shape** — the per-cell primitive the generator emits (spec #13,
  ticket #38): **Square** (the grown contour path, default) or **Circle**
  (one true circle dot per emitted cell, delivered as a `QrCirclePlan`
  for the host's native linear pattern — ticket #39). Canonical codes:
  `square`, `circle`.
- **Dot size** — the Circle shape's parameter: the dot DIAMETER as a
  percentage of the cell pitch, capped per request so the dot-to-dot gap
  never falls below the 0.05 mm insertion floor. Under Circle the growth
  field is inert — dots are already disjoint — and under Square the dot
  field is inert; inert fields keep their entered text verbatim.
- **Circle-dot pattern** — the sketch emission the `QrCirclePlan`
  describes: one seed circle plus a two-directional sketch linear
  pattern whose non-emitting cells are skipped at create time
  (docs/PATTERN-SPIKE.md). No contours, no block.

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
