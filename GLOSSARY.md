# Glossary

Domain terms for the SolidWorks QR Add-In. Grows lazily — add a term the
moment it is resolved in discussion, not before.

## QR / geometry

- **Working matrix** — the `(N+8)×(N+8)` cell grid the generator operates
  on: N×N data cells plus the fixed quiet zone as light cells (ADR 0002).
- **QR square** — the construction square bounding the grown QR-cell extent
  (side = final size S). Carries the driven dimension — the live size
  readout.
- **Keep-out square** — the construction square marking the quiet-zone
  clearance: the working-matrix boundary, `(N+8) × cellPitch` per side,
  always present. Its side dimension is equation-linked to the QR-square
  dimension.
- **Quiet zone** — the spec-mandated blank margin around a QR code that
  scanners require: always the standard 4 modules per side (ISO 18004) — a
  fixed constant, never a setting. Lives in the working matrix as light
  cells; shows on the sketch as the keep-out square, never as cuttable
  profile.
- **Cell pitch** — centre-to-centre spacing of QR modules:
  `finalSize / (dataCells + 2 × growthFraction)`.
- **Driven dimension** — the reference-only sketch dimension on the QR
  square that reads the current size. It drives nothing — resize is the
  block instance's Scale property.
- **Payload preset** — a `PayloadGenerator` template (Url, WiFi, vCard…)
  that formats fields into the exact string encoded; the content shown is
  always the literal final payload, editable verbatim.

## SolidWorks

- **Block instance** — a `SketchBlockInstance` inserted into a sketch from
  a block definition; scales uniformly via its `Scale` property — the QR's
  resize handle (a dimension inside the definition distorts rather than
  rescales, spike-verified). Container for the unit-proportioned QR
  contours; the squares live in the host sketch.
- **Pane** — the add-in's docked `QR Sketch` task-pane tab (hosts the full
  UI; the retired floating window's role).
- **Insertion support** — the selected reference plane or planar face the
  sketch is created on; validated at create time.
