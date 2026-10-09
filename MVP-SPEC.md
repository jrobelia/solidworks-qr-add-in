# SolidWorks QR Add-In: MVP spec

Status: Confirmed local implementation specification. The engineer approved the corrected playback, defaults, and module seam on 2026-10-08. No add-in code or live modelling prototype has run.
Date: 2026-10-08

This is a standalone SolidWorks tool, separate from the SolidWorks InvenTree Add-In. The current authorization is to create local build tickets, not to execute them or publish remote issues.

## Problem statement

Engineers currently use an online QR generator, export an SVG, and open it in Inkscape. They convert the squares to paths, union them, and grow the outline slightly so diagonal corners overlap. They then hide the fill, show the borders, export a DXF, and import it into a SolidWorks sketch.

An exact grid of touching squares can produce corner-only contacts. These contacts can cause zero-thickness geometry when engineers create an extrusion or cut.

The engineer needs a native sketch without the external conversion steps. The tool creates the sketch only. The engineer selects contours and creates the feature.

## Solution

Provide a Create QR Sketch command inside SolidWorks. Generate the QR locally from text or a URL, apply a small adjustable growth allowance, and create native closed sketch contours on a selected plane or planar face.

Show a simple filled black-on-white QR picture beside the inputs. The picture represents the geometry used for the sketch, including outline growth. It is a visual reference, not an interactive contour selector.

### Confirmed MVP requirements

- Accept text or a URL as the QR content.
- Provide one custom-size field, initially 25 mm. There are no size presets.
- Provide an adjustable outline-growth allowance to resolve QR corner-only contacts.
- Update the black-on-white picture when valid inputs change.
- Create native sketch geometry on the selected plane or planar face.
- Leave the newly generated sketch open in sketch-edit mode so the engineer can manipulate it.
- Preserve enclosed holes and separate filled regions in the QR pattern.
- Leave contour selection, extrusion, cutting, and all feature settings to the engineer.
- Leave manufacturing suitability and scan readability to the engineer.
- Generate locally without an online QR service or an SVG/DXF conversion workflow.

### Engineer workflow

1. Open a part and select a plane or planar face.
2. Open Create QR Sketch.
3. Enter the text or URL.
4. Keep the initial 25 mm size or enter another custom size.
5. Adjust outline growth if needed and review the picture.
6. Select Create Sketch. The tool leaves the new sketch open for editing.
7. Manipulate the sketch as needed, use the picture to identify contours, and create a normal SolidWorks feature.

The MVP supports part documents on Windows x64, with the installed SolidWorks 2023 SP5 generation as the live acceptance baseline. Other versions are not covered by the initial compatibility claim.

## User stories

1. As an engineer, I want to generate a QR sketch inside SolidWorks, so that I avoid the external conversion workflow.
2. As an engineer, I want to enter text or a URL, so that the QR contains the required information.
3. As an engineer, I want a custom size initially set to 25 mm, so that I can keep a starting size or fit the available space.
4. As an engineer, I want to adjust outline growth, so that diagonal squares connect with finite overlap.
5. As an engineer, I want a simple black-on-white picture, so that I can identify the intended feature regions.
6. As an engineer, I want native closed contours, so that I can use normal SolidWorks feature tools.
7. As an engineer, I want holes and separate regions preserved, so that the sketch represents the QR pattern.
8. As an engineer, I want to choose the sketch support, so that I control its placement on the part.
9. As an engineer, I want the generated sketch left open for editing, so that I can manipulate it with normal sketch tools.
10. As an engineer, I want to choose contours and feature settings myself, so that the tool does not decide how I manufacture the QR.

## Implementation decisions

The engineer confirmed this design for ticket planning. Library documentation and the installed SDK support the route, but the live acceptance checks still need to prove host behaviour.

### Geometry generation

1. Encode the entered content exactly into a standard QR cell matrix with automatic version selection and error correction M. Do not browse, resolve, trim, or rewrite URLs or text.
2. Distinguish QR data cells from the encoder's quiet-zone padding.
3. Calculate cell pitch so that the final grown QR outline width equals the requested size.
4. Expand dark-cell squares slightly on all four sides.
5. Union consistently oriented expanded rectangles using a NonZero fill rule before creating any sketch lines.
6. Extract closed outer contours and enclosed-hole contours, including filled regions nested inside holes.
7. Remove redundant edges and validate the final contours.
8. Render the same filled regions into the preview picture.
9. Insert only the final contour edges into SolidWorks and leave the new sketch open for editing.

Overlapping rectangles alone are not the output. Their internal edges must be removed before sketch creation. Growth must preserve finite light gaps and must not leave corner-only contacts in the generated QR geometry.

Use a normalized integer cell grid for topology operations, then convert validated contours to physical coordinates. Preserve shared endpoints exactly. Reject size/growth requests that round away the overlap or fall below a verified insertion limit. Establish and record that limit in the fixed-QR ticket; it is an implementation guard, not a manufacturing assessment.

### Module design

The geometry-generation module accepts QR content, final QR outline size, and growth settings. It returns validated closed contours with their region relationships and size information, or an actionable input error.

QR encoding, expansion, union, contour cleanup, and validation stay inside this module. Its result contains no SolidWorks objects. Preview rendering uses the same result that drives sketch creation.

A SolidWorks adapter places the contours on the selected sketch support. Headless tests use a test adapter at that seam and exercise geometry through the same interface used by the command. A live SolidWorks check verifies native sketch insertion and feature use.

The engineer confirmed this seam. It is specific to the standalone QR tool, not inherited from the InvenTree add-in.

### Technology baseline

- C# and .NET Framework 4.8 for the initial Windows-based add-in and host-independent geometry module.
- Windows x64 and WPF for the SolidWorks-facing window.
- QRCoder 1.8.0 for direct access to the QR cell matrix.
- Clipper2 2.0.0 for polygon union and nested contour results.
- A Windows headless test suite that does not load SolidWorks assemblies. Linux test execution is not part of the initial baseline.

Keep the SDK reference location configurable. Pin package versions and verify the selected NuGet assets during restore/build. Keep SolidWorks DLLs out of the add-in output where embedded interop types are sufficient. Include the required third-party license notices.

Provide an explicit registration/unregistration procedure and deployment folder rather than introducing an MSI project. Registration steps require appropriate user approval and privileges. Do not reuse the InvenTree add-in's identity, registry entries, or integration code.

### Size and growth

Provide one custom-size field, initially 25 mm. Scaling is uniform in both directions. The initial size is a convenience value, not a certified manufacturing or scanning size.

The entered size is the final generated QR outline width, after growth and excluding the clear margin. A 25 mm entry produces a 25 mm-wide QR sketch.

Express growth as a percentage of cell width per side, initially 3%. Require a finite percentage greater than 0 and less than 50%, plus the geometry/insertion checks. This is a prototype starting value, not a manufacturing recommendation. Never silently replace a rejected size or growth value.

For size S, data-cell count N, and per-side growth fraction r, use cell pitch p = S/(N + 2r) and physical growth d = r*p. The final width is N*p + 2d = S. Test it within a declared numeric tolerance.

The QR encoder determines cell count from the content and error-correction settings. A longer payload can therefore produce smaller cells at the same overall size.

A standard QR requires a clear margin of four cells on each side. Show it as blank white space in the picture, measured from the grown QR extents. It is not a dotted box or a rectangle added to the sketch.

The picture is a reference for the generated QR. It does not track later manual sketch edits. The tool leaves successful generation in sketch-edit mode; the engineer must exit that sketch before creating another QR sketch.

## Testing decisions

No code or live SolidWorks tests have run for this specification. The following acceptance checks are required during implementation.

### Geometry checks without SolidWorks

- Adjacent dark squares produce a joined region without internal duplicate edges.
- Diagonal dark squares receive finite overlap rather than point-only contact.
- Outer outlines, enclosed holes, and nested filled regions preserve the intended QR pattern.
- Every output contour is closed and has nonzero area.
- Output contours have no self-intersections, zero-length edges, or unintended corner-only contacts.
- The size field initially shows 25 mm; different custom sizes scale the complete QR uniformly.
- The entered size equals the final grown outline bounds within the declared numeric tolerance; it does not include preview pixels or the clear margin.
- The preview and sketch contours derive from the same filled geometry.
- Representative short and long payloads produce valid output across different QR cell counts.
- Invalid size, invalid growth, empty content, and unsupported payload capacity return useful errors instead of an invalid sketch.

Test observable geometry and error behaviour through the module interface. Avoid assertions tied to a polygon library's vertex ordering or private operations.

### Live SolidWorks acceptance checks

- The command creates one native sketch on a selected plane or planar face and leaves that new sketch open for editing.
- The engineer can manipulate the generated contours with normal SolidWorks sketch tools.
- A non-default, rotated support produces the correct orientation and physical size without mirroring the QR.
- The user can select contours and create a Boss-Extrude on a simple backing plate.
- The user can select contours and create a shallow Cut-Extrude on a simple backing plate.
- Finder-pattern rings and their inner filled regions remain identifiable in the picture and usable in the sketch.
- The sample features have no failures caused by QR corner-only contacts.
- Creation performs acceptably for representative QR densities; establish a measurable limit during the prototype.
- Cancelling before creation leaves the part unchanged. Invalid input does not create a partial sketch.

These checks do not guarantee feature success against arbitrary existing part geometry. They do not assess manufactured scan readability.

## Out of scope

- Automatic Boss-Extrude, Cut-Extrude, or other feature creation.
- Automatic contour selection or interactive contour highlighting in the preview.
- Outline-preview toggles, colour overlays, and additional preview modes.
- Inserting a sketch picture into the part or placing a reference image beside the generated sketch.
- Automatic bridges for through-cut islands or stencil generation.
- Automatic sizing based on printer, cutter, material, lighting, or surface finish.
- Scan testing, readability certification, and manufacturing validation.
- Size presets, a dotted margin box, and margin sketch geometry.
- Tool-provided one-dimension resizing, parametric QR features, or automatic regeneration after sketch creation. Normal manual sketch editing remains in scope.
- SVG/DXF import or export as part of the MVP workflow.
- InvenTree integration or changes to the existing InvenTree add-in.
- Remote issue publication and implementation execution during this local ticket-planning step.

## Further notes

### User responsibilities

The engineer uses the picture to choose contours. The engineer controls feature direction, depth, merge settings, and all other feature choices.

Through-cuts can leave loose islands, including centres enclosed by finder-pattern rings. The engineer decides whether to use backing material, add bridges, or choose another feature strategy.

An internally valid QR sketch can still produce zero-thickness geometry where a feature meets an existing hole or part edge. The engineer handles those model interactions.

### Confirmed interaction defaults

- One modeless window owned by SolidWorks. The user can continue to interact with SolidWorks while the window is open.
- The command focuses the existing window or recreates a closed one. Keep the picture available after creation.
- The UI contains the content input, custom-size input, outline-growth input, simple picture, and Create Sketch action. No additional size readouts or error-correction selector are required.
- Centre the nominal QR grid at the new sketch origin. The selected support determines orientation; there is no click-to-place interaction.
- Use native contour lines without a driving QR-size dimension. Allow normal manual sketch editing without tool-provided parametric resizing.
- Require the engineer to exit any currently edited sketch before creating another. Never silently close or modify an existing sketch.
- Revalidate the active part and selected support at Create. A cached selection must never target a different document.
- Each successful Create makes a new sketch and leaves it open. Later input changes do not edit existing sketches.
- Preserve prior user work on failure. Clean up only the sketch created by the failing operation; report any cleanup failure accurately.
- Verify named undo behaviour live, including the generated sketch remaining open. A recording scope is not assumed to provide rollback, and cleanup never uses a blind Undo of prior user work.

### Evidence still required during implementation

The first native-sketch ticket verifies standalone loading and registration on the baseline installation. The fixed-QR ticket verifies closure, nested regions, feature use, and a conservative insertion limit. Later tickets verify rotated-plane/face orientation, failure cleanup, and undo behaviour.

These are implementation acceptance gates, not unresolved product questions. A worker without SolidWorks records a live gate as awaiting human verification. It must not report that gate passed or close the ticket as fully verified.

### Confirmed playback

The engineer confirmed the corrected playback: one custom-size field initially 25 mm; size measured across the final grown QR outline and excluding the white margin; no presets or margin box; the generated sketch stays open for manipulation. The remaining proposed defaults and the separate geometry-generation/SolidWorks-insertion approach were also confirmed.

This specification is the authority for the current requirements. The research note records evidence and rationale.

### Reference material

- QRCoder, including direct access to the cell matrix: https://github.com/Shane32/QRCoder
- Clipper2 polygon operations: https://angusj.com/clipper2/Docs/Overview.htm
- Clipper2 offset rules and intersection handling: https://angusj.com/clipper2/Docs/Units/Clipper.Offset/Classes/ClipperOffset/_Body.htm
- DENSO WAVE guidance on the four-cell clear margin: https://www.qrcode.com/en/howto/code.html
