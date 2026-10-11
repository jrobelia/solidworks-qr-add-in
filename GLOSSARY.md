# Glossary

Domain terms for the SolidWorks QR Add-In. Grows lazily — add a term the
moment it is resolved in discussion, not before.

## QR / geometry

- **Working matrix** — the `(N+8)×(N+8)` cell grid the generator operates
  on: N×N data cells plus the fixed quiet zone as light cells (ADR 0002).
- **QR square** — the construction square bounding the grown QR-cell extent
  (side = final size S). Lives INSIDE the block definition in unit space
  (ADR 0003) — renders and tracks the instance's Scale/position.
- **Keep-out square** — the construction square marking the quiet-zone
  clearance: the working-matrix boundary, `(N+8) × cellPitch` per side,
  always present. Same def-internal construction geometry as the QR square.
- **Quiet zone** — the spec-mandated blank margin around a QR code that
  scanners require: always the standard 4 modules per side (ISO 18004) — a
  fixed constant, never a setting. Lives in the working matrix as light
  cells; shows on the sketch as the keep-out square, never as cuttable
  profile.
- **Cell pitch** — centre-to-centre spacing of QR modules:
  `finalSize / (dataCells + 2 × growthFraction)`.
- **Size readout** — the numeric size surfaces are the block instance's
  PropertyManager **Scale** field and the pane's cell-size readout. No
  sketch dimensions are emitted (ADR 0003 superseded the earlier
  driven/equation-linked dimension plan — host-side dims cannot observe
  instance Scale).
- **Payload preset** — a `PayloadGenerator` template (Url, WiFi, vCard…)
  that formats fields into the exact string encoded; the content shown is
  always the literal final payload, editable verbatim.

## SolidWorks

- **Block instance** — a `SketchBlockInstance` inserted into a sketch from
  a block definition; scales uniformly via its `Scale` property — the QR's
  resize handle (a dimension inside the definition distorts rather than
  rescales, spike-verified). Container for the unit-proportioned QR
  contours AND both construction squares — everything tracks `Scale` and
  the insertion point uniformly (ADR 0003).
- **Pane** — the add-in's docked `QR Sketch` task-pane tab (hosts the full
  UI; the retired floating window's role).
- **Insertion support** — the selected reference plane or planar face the
  sketch is created on; validated at create time.
