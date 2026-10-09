# SolidWorks QR Add-In: implementation research

Date: 2026-10-08
Status: Research complete for ticket planning. The engineer confirmed the corrected defaults and module seam on 2026-10-08. The local MVP specification is the authority for current requirements. No add-in code or live modelling prototype has run.

## Summary

The proposed route is supported by the library documentation and the locally installed SolidWorks SDK: encode a QR matrix, expand dark-cell rectangles, union them, preserve nested contours, and insert the resulting lines into a native sketch.

This establishes a route to implementation, not proof that SolidWorks accepts the finished QR profiles. Live sketch closure, selected-contour extrusion/cutting, rotated-plane orientation, and undo behaviour remain acceptance gates during implementation.

Some SolidWorks web-help pages returned HTTP 403 or only a JavaScript shell. Claims below distinguish directly fetched library documentation, indexed excerpts of official help, local SDK signatures, and recommendations. Unread pages are not evidence for behavioural guarantees.

## 1. Local implementation baseline

Read-only checks found:

| Item | Observed value | Evidence |
|---|---|---|
| SolidWorks executable | Product and file version 31.5.0.0052 | File-version metadata from the installed SLDWORKS.exe |
| SolidWorks generation | 2023 SP5 generation | Revision 8 corresponds to 2000; each major year increments the major revision, according to the official RevisionNumber documentation. Revision 31 therefore corresponds to 2023. |
| SolidWorks interop assemblies | sldworks, swconst, and swpublished assemblies available | Installed API redist directory |
| .NET SDK | 10.0.103, Windows x64 | dotnet --info |
| .NET Framework | Release registry value 533509 | Read-only registry query; Microsoft maps this value to .NET Framework 4.8.1 |

Sources:
- [SolidWorks RevisionNumber documentation, indexed official excerpt](https://help.solidworks.com/2012/english/api/sldworksapi/solidworks.interop.sldworks~solidworks.interop.sldworks.isldworks~revisionnumber.html)
- [Microsoft .NET Framework release-value table](https://learn.microsoft.com/en-us/dotnet/framework/install/how-to-determine-which-versions-are-installed)
- Local executable: `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe`
- Local SDK: `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist`

Recommendation: target this installed SolidWorks 2023 SP5 generation first. Later SolidWorks compatibility is a separate validation claim, not an automatic guarantee.

## 2. Runtime, libraries, and registration

### QR encoder

QRCoder exposes ModuleMatrix as rows of dark/light cells. The published example states that the matrix includes the quiet zone. Read the cell matrix directly rather than converting SVG or tracing a bitmap. Standard QR only is sufficient for this MVP; Micro QR has different rules and is not required.

QRCoder 1.8.0 was published on 2026-04-04. Its NuGet metadata includes .NET Framework assets, including a .NET Framework 4.0 target without package dependencies. Its license is MIT. Both the version and date are well outside the seven-day release-age guard.

Source: [QRCoder 1.8.0 package, matrix documentation, frameworks, dependencies, and release history](https://www.nuget.org/packages/QRCoder/1.8.0).

### Polygon operations

Clipper2 2.0.0 was published on 2025-12-17. Its package targets .NET Standard 2.0 and has no package dependencies. Its license is Boost Software License 1.0. Microsoft documents .NET Framework 4.8 as supporting .NET Standard 2.0.

Sources:
- [Clipper2 package metadata and release history](https://www.nuget.org/packages/Clipper2)
- [Clipper2 2.0.0 license](https://www.nuget.org/packages/Clipper2/2.0.0/License)
- [Microsoft .NET Standard compatibility guidance](https://learn.microsoft.com/en-us/dotnet/standard/net-standard)

Recommendation: C#, .NET Framework 4.8, x64, and WPF for the SolidWorks-facing add-in. Use a separate host-independent geometry module targeting .NET Framework 4.8 for the initial Windows-based build. Pin QRCoder 1.8.0 and Clipper2 2.0.0. Confirm the selected NuGet assets during the first restore/build rather than assuming them.

Headless tests must not load SolidWorks assemblies. They can run on a Windows worker with .NET Framework and the build tools, without SolidWorks installed. This recommendation does not promise that the same tests run on a Linux worker.

### Registration and distribution

The official C# add-in example uses ISwAddin, a COM-visible identity, ConnectToSW/DisconnectFromSW, and separate SolidWorks add-in discovery/startup registry entries. This is distinct from PDM add-in registration.

Source: [Official SolidWorks C# add-in example, indexed source excerpt](https://help.solidworks.com/2021/english/api/swpublishedapi/Custom_Colorize_a_Model_Example_CSharp.htm).

The installed redist.txt permits redistribution of listed interop DLLs unmodified, subject to the software license. It is not unconditional permission to redistribute SolidWorks itself.

Local source: `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\redist.txt`.

Recommendation: reference the installed SDK for development, keep its path configurable, and keep SolidWorks DLLs out of the add-in output where embedded interop types are sufficient. Ship the add-in's own assemblies and required library dependencies, with third-party license notices. Registration/install instructions are a deliverable; do not run elevated registration or edit machine-wide settings during drafting. The first ticket must verify COM registration and SDK-reference choices on the baseline installation.

## 3. Geometry: union, nesting, and precision

### Expand rectangles before union

For cell pitch p and per-side growth d, two diagonal dark cells overlap by 2d in each direction. A one-cell-wide light channel between expanded dark areas retains width p - 2d. These are geometric derivations, not scanner or manufacturing recommendations.

Use consistently oriented dark-cell rectangles and a NonZero union. Do not use EvenOdd to combine the overlapping input rectangles: an even overlap count can classify the overlap as empty. A final nested contour result can be rendered using its proper nesting/fill rule.

Clipper2 provides C# union operations. PolyTree64 records outer contours, holes, and filled regions nested inside holes. Preserve this hierarchy rather than flattening every loop into an independent filled polygon.

Sources:
- [Clipper2 overview and filling rules](https://angusj.com/clipper2/Docs/Overview.htm)
- [Clipper2 Union](https://angusj.com/clipper2/Docs/Units/Clipper/Functions/Union.htm)
- [Clipper2 PolyTree64 nesting rules](https://angusj.com/clipper2/Docs/Units/Clipper.Engine/Classes/PolyTree64/_Body.htm)

Recommendation: construct the expanded axis-aligned rectangles directly, then union them. This avoids using polygon-offset operations on already intersecting input paths. QR encoding, expansion, union, contour cleanup, and geometry validation belong behind one geometry-generation interface.

### Use sufficient integer precision

Clipper2 uses integer coordinates internally. Its robustness guidance warns that integer rounding is significant for tiny geometry and can remove near-zero regions. The overview also notes that touching contours can remain in some solutions. A library union alone is therefore not a sufficient validity check.

Sources:
- [Clipper2 numeric robustness](https://angusj.com/clipper2/Docs/Robustness.htm)
- [Clipper2 coordinate ranges and touching solutions](https://angusj.com/clipper2/Docs/Overview.htm)

Recommendation: perform topology operations on a normalized integer cell grid, then convert the validated result to physical sketch coordinates. Preserve shared endpoints exactly. Check for nonzero area, zero-length edges, self-intersections, and residual corner-only contacts after clipping. Reject requests where growth rounds away or creates geometry below the implementation's verified insertion limit.

The research does not establish a universal SolidWorks minimum line length or a universal safe growth value. Establish a conservative insertion guard during the fixed-QR ticket and record the tested size/growth range. Do not describe that guard as a manufacturing limit.

## 4. Native sketch insertion

Reflection against the installed SolidWorks.Interop.sldworks.dll confirms the presence and types of:

- ISketchManager.InsertSketch(Boolean).
- ISketchManager.CreateLine(Double X1, Double Y1, Double Z1, Double X2, Double Y2, Double Z2), returning SketchSegment.
- ISketchManager.AddToDB and DisplayWhenAdded, both Boolean properties.
- ISketch.ModelToSketchTransform, returning MathTransform.
- IModelDocExtension.StartRecordingUndoObject(), returning void.
- IModelDocExtension.FinishRecordingUndoObject(String UndoObjectName), returning Boolean.
- IModelDocExtension.FinishRecordingUndoObject2(String UndoObjectName, Boolean MakeHidden), returning Boolean.
- IModelDocExtension.DeleteSelection2(Int32), returning Boolean.
- IModelDoc2.EditUndo2(Int32), returning void.

Source: read-only metadata inspection of the installed SDK assembly. These signatures prove availability, not transaction or coordinate semantics.

An official example explains that sketch-point coordinates are relative to the sketch origin, and that the inverse of ModelToSketchTransform maps sketch coordinates into model coordinates. That example concerns point transformation; it does not by itself establish which coordinate frame every creation operation consumes.

Source: [Official sketch-to-model transform example, indexed excerpt](https://help.solidworks.com/2014/english/api/sldworksapi/Transform_Sketch_to_Model_Example_VB.htm).

The official DisplayWhenAdded excerpt states that AddToDB must be true for its display setting to apply. Suppressed display requires a redraw, and the setting persists after the program ends.

Source: [Official DisplayWhenAdded documentation, indexed excerpt](https://help.solidworks.com/2013/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISketchManager~DisplayWhenAdded.html).

Recommendations:
- Generate and validate the complete geometry before mutating the part.
- Create a new sketch, not an addition to a sketch the user is already editing.
- Capture and restore sketch-manager flags on success and failure; redraw at the end.
- Treat identical endpoint coordinates as necessary, but prove native contour closure in SolidWorks.
- Verify the CreateLine coordinate convention on a rotated plane and planar face. Apply the sketch/model transform only where the consuming operation requires it.
- Group successful creation into a named undo operation if the baseline live check confirms its behaviour.
- Do not assume an undo recording scope provides automatic rollback. Track only the newly created sketch and clean up that sketch on insertion failure. Never use a blind Undo that could undo the user's preceding action.

Live acceptance gates: a closed square on a selected plane; a fixed QR with nested holes; selected-contour boss and shallow cut on a backing plate; rotated-plane/face orientation; and injected mid-insertion failure with no user geometry removed.

## 5. Simple picture and clear margin

DENSO WAVE specifies a clear margin of four cells on each side of a standard QR. Growth extends the dark geometry, so reserve that margin from the grown outer extents. QRCoder's matrix already includes padding; explicitly distinguish QR data cells from its padding when calculating physical size.

Sources:
- [DENSO WAVE clear-margin guidance](https://www.qrcode.com/en/howto/code.html)
- [QRCoder matrix example](https://www.nuget.org/packages/QRCoder/1.8.0)

For nominal QR-grid width W, data-cell count N, pitch p = W/N, and per-side growth d, the picture footprint with a four-cell margin is W + 2d + 8p. This formula follows from the proposed expanded-square geometry.

Following the user's size/margin question, the revised recommendation is to make the size field the final grown QR outline width S, excluding the clear margin. For growth fraction r per side, choose p = S/(N + 2r) and d = r*p. The nominal grid width is N*p, the final QR width is exactly S, and the picture footprint is S + 8p. This avoids a 25 mm size entry producing a sketch slightly wider than 25 mm. The engineer confirmed this revised definition.

Render a simple filled black-on-white picture from the final contours. Do not use an unmodified encoder bitmap if growth changes the sketch profile. Preview pixels do not determine physical sketch dimensions.

Microsoft distinguishes modeless WPF windows from modal dialogs: a modeless window allows interaction with the host. WindowInteropHelper can assign a Win32 owner handle.

Sources:
- [Microsoft modeless WPF windows](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/windows/how-to-open-window-dialog-box)
- [Microsoft WPF/Win32 ownership](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.windowinterophelper?view=netframework-4.8.1)

Recommendation: one modeless window owned by SolidWorks. Keep the picture available after Create Sketch so the engineer can refer to it while choosing contours. Reopening the command focuses the existing window or recreates a closed one. No preview interaction, inserted sketch picture, colour overlay, or extra preview mode is needed.

User correction: successful creation leaves the newly generated sketch open for normal sketch editing. The picture remains a reference to the generated geometry, not a live view of later manual edits. Require the engineer to exit that sketch before creating another QR sketch. The clear margin is blank white space in the picture, not a dotted box or additional sketch lines.

## 6. Confirmed defaults after research

The engineer confirmed the following corrected defaults. They are product/implementation decisions, not findings dictated by a standard. The MVP specification remains the authority for current requirements:

| Item | Recommended MVP decision |
|---|---|
| Compatibility claim | Windows x64; SolidWorks 2023 SP5 baseline; parts only. Broader versions require separate checks. |
| Runtime/UI | C#/.NET Framework 4.8; WPF modeless window owned by SolidWorks. |
| Libraries | QRCoder 1.8.0 and Clipper2 2.0.0, pinned. |
| Content | Encode the entered text exactly; do not browse, resolve, or rewrite URLs. Reject empty input and capacity errors. |
| QR type | Standard QR; automatic version selection; fixed error correction M for the MVP. |
| Size input | Confirmed: millimetres; final grown QR outline width, excluding the clear margin. Label that meaning. |
| Custom size | Confirmed correction: one custom-size field, initially 25 mm; no presets. Allow positive finite size subject to geometric checks. |
| Growth | Label as Outline growth (% of cell width per side); initially 3%; require greater than 0 and less than 50%, plus geometric/insertion checks. |
| Clear margin | Four cells of blank white space from the grown QR extents in the picture; no dotted box or margin rectangle in the sketch. |
| Preview | One black-on-white picture; retain it after creation as a reference to the generated geometry. No extra readouts or tracking of manual sketch edits. |
| After generation | Confirmed correction: leave the new sketch open for editing with normal SolidWorks tools. |
| Placement | Centre the nominal QR grid at the new sketch origin; selected support determines orientation. No click-to-place interaction. |
| Constraints | Native contour lines without a driving QR-size dimension. Do not rely on automatic inferencing to reshape geometry. |
| Existing sketch | Require the user to exit sketch editing before creation; never silently close or alter an existing sketch. |
| Document selection | Revalidate the active part and selected support at Create; stale selections cannot target a different document. |
| Repeated creation | Each successful Create produces a new sketch. Exit the currently open sketch before creating another. Later input changes do not edit existing sketches. |
| Failure/undo | Preserve prior user work; clean up only the sketch created by the failing operation. Prove named undo behaviour live. |
| Packaging | Start with an explicit registration/unregistration procedure and a self-contained deployment folder, not a new MSI project. |

The 3% value is a starting overlap setting for the prototype, not a validated manufacturing allowance. If a request cannot maintain valid geometry, return a useful error rather than silently substituting another growth or size.

## 7. Ticket-planning implications

The approved seven-ticket sequence remains suitable. Each ticket should carry one end-to-end result, its blockers, tests, non-goals, and a clear stopping condition. A ticket is not complete merely because a model wrote the code.

Two live gates deserve explicit treatment:

1. The first ticket must prove standalone loading, registration, and a native test sketch on the baseline installation.
2. The fixed-QR ticket must prove closure, nested regions, and the absence of QR corner-contact failures before later tickets build on its geometry.

Keep the host-dependent checks separate from the headless suite. If an implementation worker cannot run SolidWorks, it must report the live gate as awaiting human verification rather than claiming it passed.

The rotated-plane ticket resolves creation-coordinate conventions with a live result. The failure-handling ticket must verify rollback and successful undo semantics rather than inferring them from the presence of method names.

The engineer approved the seven-ticket breakdown and confirmed the corrected defaults and seam: one host-independent geometry-generation module returning validated contours, with a separate SolidWorks sketch-insertion adapter. Seven local build tickets were authored after that confirmation; implementation is not part of this session.
