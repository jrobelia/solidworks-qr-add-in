using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Core.Commands
{
    /// <summary>Result of a command execution — success detail or a useful, actionable error.</summary>
    public sealed class CommandResult
    {
        private CommandResult(bool succeeded, string message, string? createdSketchName)
        {
            Succeeded = succeeded;
            Message = message;
            CreatedSketchName = createdSketchName;
        }

        public bool Succeeded { get; }
        public string Message { get; }
        public string? CreatedSketchName { get; }

        public static CommandResult Success(string createdSketchName, int segmentCount) =>
            new CommandResult(true,
                $"Created sketch '{createdSketchName}' with {segmentCount} segments; the sketch is open for editing.",
                createdSketchName);

        public static CommandResult Failure(string message) =>
            new CommandResult(false, message, null);
    }

    /// <summary>
    /// "Create Test Sketch" — the temporary command that proves the host path.
    /// Creates exactly one closed square sketch on a selected baseline plane and
    /// leaves the new sketch open for normal editing. Works only through the
    /// <see cref="ISketchHost"/> seam, so it is fully testable headlessly.
    /// </summary>
    public sealed class CreateTestSketchCommand
    {
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
                        "The active document is an assembly. The test sketch command requires a part document.");
                case HostDocumentKind.Drawing:
                    return CommandResult.Failure(
                        "The active document is a drawing. The test sketch command requires a part document.");
                case HostDocumentKind.Unknown:
                    return CommandResult.Failure(
                        "The active document type is not supported. The test sketch command requires a part document.");
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
                foreach (var edge in TestSquareSketch.Edges)
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
