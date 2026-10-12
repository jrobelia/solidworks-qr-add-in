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
                $"Created sketch '{createdSketchName}' as one QR block instance " +
                $"({segmentCount} contour segments folded into its definition); " +
                "the sketch is open for editing.",
                createdSketchName);

        public static CommandResult Failure(string message) =>
            new CommandResult(false, message, null);
    }
}
