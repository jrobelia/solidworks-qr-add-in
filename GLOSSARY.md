# Glossary

Domain terms for the SolidWorks QR Add-In. Grows lazily — add a term the
moment it is resolved in discussion, not before.

## QR / geometry

- **QR square** — the construction square bounding the grown QR-cell extent.
  Carries the driving dimension when no keep-out square is drawn.
- **Keep-out square** — the construction square representing the QR quiet
  zone; offsets outward from the QR square by `quietCells × cellPitch` per
  side. Drawn only when quiet zone > 0. Carries the driving dimension when
  present (it is always the outermost square).
- **Quiet zone** — the spec-mandated blank margin around a QR code (4
  modules by default) that scanners require; implemented as the keep-out
  square's geometry, never as cuttable profile.
- **Cell pitch** — centre-to-centre spacing of QR modules:
  `finalSize / (dataCells + 2 × growthFraction)`.
- **Driving dimension** — the single editable sketch dimension on the
  outermost square; the parametric handle that resizes the QR (block path).
- **Payload preset** — a `PayloadGenerator` template (Url, WiFi, vCard…)
  that formats fields into the exact string encoded; the content shown is
  always the literal final payload, editable verbatim.

## SolidWorks

- **Block instance** — a `SketchBlockInstance` inserted into a sketch from
  a block definition; scales uniformly via its scale property, and a
  driving dimension inside the definition rescales the instance when
  edited. Candidate container for the QR pattern + squares.
- **Pane** — the add-in's docked `QR Sketch` task-pane tab (hosts the full
  UI; the retired floating window's role).
- **Insertion support** — the selected reference plane or planar face the
  sketch is created on; validated at create time.
