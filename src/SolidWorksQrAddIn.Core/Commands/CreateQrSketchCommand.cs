using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Core.Commands
{
    /// <summary>
    /// "Create QR Sketch" — the fixed-request command of ticket 2. Encodes the
    /// fixed content <see cref="FixedContent"/> at <see cref="FixedSizeMm"/>
    /// with <see cref="FixedGrowthFractionPerSide"/> outline growth, then
    /// inserts every validated contour as native sketch lines on the selected
    /// baseline plane and leaves the new sketch open for editing.
    ///
    /// Geometry is generated and validated BEFORE any host mutation — a
    /// rejected request never touches the part. No margin geometry, no
    /// features. Works only through the <see cref="ISketchHost"/> seam.
    /// </summary>
    public sealed class CreateQrSketchCommand
    {
        /// <summary>Fixed QR payload for this slice.</summary>
        public const string FixedContent = "SW-QR-MVP";

        /// <summary>Final grown QR outline width (mm), excluding the clear margin.</summary>
        public const double FixedSizeMm = 25.0;

        /// <summary>Per-side outline growth as a fraction of cell width (3%).</summary>
        public const double FixedGrowthFractionPerSide = 0.03;

        public CommandResult Execute(ISketchHost host)
        {
            var state = host.CaptureState();

            switch (state.Document)
            {
                case HostDocumentKind.None:
                    return CommandResult.Failure(
                        "No active document. Open a part document, select a baseline plane, and run the command again.");
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
                    "A sketch is already open for editing. Exit the current sketch, select a baseline plane, and run the command again.");

            switch (state.Selection)
            {
                case SupportSelection.None:
                    return CommandResult.Failure(
                        "No baseline plane is selected. Select one plane (e.g. Front Plane) and run the command again.");
                case SupportSelection.Other:
                    return CommandResult.Failure(
                        "The current selection is not a single baseline plane. Select exactly one plane and run the command again.");
            }

            // Generate + validate all geometry before the host is touched.
            var generated = QrSketchGenerator.Generate(
                FixedContent, FixedSizeMm, FixedGrowthFractionPerSide);
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
