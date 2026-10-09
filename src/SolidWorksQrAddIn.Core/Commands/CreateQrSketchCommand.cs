using System;
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
    ///
    /// Failure contract: nothing this command calls can throw back across the
    /// seam into SolidWorks. Any host-side failure — declared seam exception
    /// or an unexpected error — becomes a <see cref="CommandResult"/> failure
    /// with a useful, honest message: a removed partial sketch is reported as
    /// removed; a cleanup failure is reported as possibly leaving the partial
    /// sketch behind, never as a complete rollback.
    /// </summary>
    public sealed class CreateQrSketchCommand
    {
        /// <param name="sizeMm">Final grown QR outline width (mm), excluding the clear margin.</param>
        /// <param name="growthFractionPerSide">Per-side outline growth as a fraction of cell width.</param>
        public CommandResult Execute(
            ISketchHost host, string content, double sizeMm, double growthFractionPerSide)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));

            SketchHostState state;
            try
            {
                state = host.CaptureState();
            }
            catch (Exception ex)
            {
                return CommandResult.Failure(
                    $"Could not read the SolidWorks document state: {ex.Message}");
            }

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
            catch (Exception ex)
            {
                // The seam contract: a thrown begin leaves the document as the
                // host found it — when the host had to abandon a half-entered
                // sketch it could not remove, its message says so.
                return CommandResult.Failure(
                    $"Could not start the sketch: {ex.Message}");
            }

            try
            {
                foreach (var edge in sketch.Edges)
                    insertion.AddLine(edge.Start.X, edge.Start.Y, edge.End.X, edge.End.Y);
                insertion.KeepOpen();
            }
            catch (Exception ex)
            {
                // ANY failure — a refused edge, a host error mid-contour, a
                // commit that could not finish — takes the same path: abort
                // the session (a committed insertion reports Cleaned without
                // touching the sketch), then tear down.
                var cleanup = TryAbort(insertion);
                var name = SafeSketchName(insertion);
                var cleanupText = cleanup.Succeeded
                    ? $" The partial sketch '{name}' was removed."
                    : $" Cleanup failed: {cleanup.FailureReason}. The partial sketch '{name}' " +
                      "may remain in the feature tree.";
                TryDispose(insertion);
                return CommandResult.Failure(
                    $"Failed to create the sketch: {ex.Message}{cleanupText}");
            }

            try
            {
                insertion.Dispose();
            }
            catch (Exception ex)
            {
                // The sketch was created and committed — teardown (flag
                // restore, redraw) is what failed. Report it, never throw
                // back into the host.
                return CommandResult.Failure(
                    $"The sketch was created but closing the operation failed: {ex.Message}");
            }

            return CommandResult.Success(SafeSketchName(insertion), SafeLineCount(insertion));
        }

        /// <summary>
        /// Best-effort abort that cannot throw — a cleanup that itself throws
        /// is reported as a failed cleanup, not let loose.
        /// </summary>
        private static CleanupOutcome TryAbort(ISketchInsertion insertion)
        {
            try
            {
                return insertion.Abort()
                    ?? CleanupOutcome.Failed("the host returned no cleanup result");
            }
            catch (Exception ex)
            {
                return CleanupOutcome.Failed($"the cleanup itself failed: {ex.Message}");
            }
        }

        /// <summary>Teardown on the failure path — must never mask the real outcome.</summary>
        private static void TryDispose(ISketchInsertion insertion)
        {
            try { insertion.Dispose(); }
            catch { /* the abort outcome already says what happened */ }
        }

        private static string SafeSketchName(ISketchInsertion insertion)
        {
            try { return insertion.SketchName; }
            catch { return "the partial sketch"; }
        }

        private static int SafeLineCount(ISketchInsertion insertion)
        {
            try { return insertion.LineCount; }
            catch { return 0; }
        }
    }
}
