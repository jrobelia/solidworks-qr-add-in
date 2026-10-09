using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Core.Commands
{
    /// <summary>
    /// "Create QR Sketch" — encodes the given <paramref name="content"/>
    /// exactly (no trim/resolve/rewrite) at the requested
    /// <paramref name="sizeMm"/> final grown outline width with the requested
    /// <paramref name="growthFractionPerSide"/> outline growth, then inserts
    /// every validated contour as native sketch lines on the currently
    /// selected support — one reference plane or one planar face — and leaves
    /// the new sketch open for editing.
    ///
    /// Host state is captured fresh at call time — the active part, the
    /// selected support, and the no-sketch-being-edited rule are always
    /// revalidated, never cached. Geometry is generated and validated BEFORE
    /// any host mutation — a rejected request never touches the part. No
    /// margin geometry, no features. Works only through the
    /// <see cref="ISketchHost"/> seam.
    /// </summary>
    public sealed class CreateQrSketchCommand
    {
        /// <param name="sizeMm">Final grown QR outline width (mm), excluding the clear margin.</param>
        /// <param name="growthFractionPerSide">Per-side outline growth as a fraction of cell width.</param>
        public CommandResult Execute(
            ISketchHost host, string content, double sizeMm, double growthFractionPerSide)
        {
            var state = host.CaptureState();

            switch (state.Document)
            {
                case HostDocumentKind.None:
                    return CommandResult.Failure(
                        "No active document. Open a part document, select a plane or planar face, and run the command again.");
                case HostDocumentKind.Assembly:
                    return CommandResult.Failure(
                        "The active document is an assembly. The QR sketch command requires a part document.");
                case HostDocumentKind.Drawing:
                    return CommandResult.Failure(
                        "The active document is a drawing. The QR sketch command requires a part document.");
                case HostDocumentKind.Unknown:
                    return CommandResult.Failure(
                        "The active document type is not supported. The QR sketch command requires a part document.");
            }

            if (state.IsEditingSketch)
                return CommandResult.Failure(
                    "A sketch is already open for editing. Exit the current sketch, select a plane or planar face, and run the command again.");

            switch (state.Selection)
            {
                // The two supported supports — proceed to generation.
                case SupportSelection.ReferencePlane:
                case SupportSelection.PlanarFace:
                    break;
                case SupportSelection.None:
                    return CommandResult.Failure(
                        "No sketch support is selected. Select one reference plane or one planar face " +
                        "and run the command again.");
                case SupportSelection.Ambiguous:
                    return CommandResult.Failure(
                        "More than one entity is selected. Select exactly one reference plane or " +
                        "planar face and run the command again.");
                case SupportSelection.NonPlanarFace:
                    return CommandResult.Failure(
                        "The selected face is not planar. Select a flat (planar) face or a reference " +
                        "plane and run the command again.");
                case SupportSelection.Other:
                    return CommandResult.Failure(
                        "The selected entity cannot support a sketch. Select one reference plane or " +
                        "one planar face and run the command again.");
                default:
                    return CommandResult.Failure(
                        "The current selection is not a usable sketch support. Select one reference " +
                        "plane or one planar face and run the command again.");
            }

            // Generate + validate all geometry before the host is touched.
            var generated = QrSketchGenerator.Generate(
                content, sizeMm, growthFractionPerSide);
            if (!generated.Succeeded)
                return CommandResult.Failure(
                    $"QR geometry could not be generated: {generated.Error!.Message}");

            var sketch = generated.Sketch!;

            ISketchInsertion insertion;
            try
            {
                insertion = host.BeginSketchInsertion();
            }
            catch (SketchHostException ex)
            {
                return CommandResult.Failure(
                    $"Could not start the sketch: {ex.Message}");
            }

            try
            {
                foreach (var edge in sketch.Edges)
                    insertion.AddLine(edge.Start.X, edge.Start.Y, edge.End.X, edge.End.Y);
            }
            catch (SketchInsertionException ex)
            {
                var cleanup = insertion.Abort();
                insertion.Dispose();
                var cleanupText = cleanup.Succeeded
                    ? $" The partial sketch '{insertion.SketchName}' was removed."
                    : $" Cleanup failed: {cleanup.FailureReason}. The partial sketch '{insertion.SketchName}' may remain in the feature tree.";
                return CommandResult.Failure(
                    $"Failed to create the sketch: {ex.Message}{cleanupText}");
            }

            using (insertion)
            {
                insertion.KeepOpen();
            }

            return CommandResult.Success(insertion.SketchName, insertion.LineCount);
        }
    }
}
