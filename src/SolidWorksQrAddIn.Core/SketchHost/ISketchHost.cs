using System;

namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>Document kind as the host reports it (mirrors swDocumentTypes_e).</summary>
    public enum HostDocumentKind
    {
        None,
        Part,
        Assembly,
        Drawing,
        Unknown,
    }

    /// <summary>What the active selection is, in the only terms this seam needs.</summary>
    public enum SupportSelection
    {
        /// <summary>Nothing is selected.</summary>
        None,
        /// <summary>Exactly one baseline datum plane (RefPlane feature) is selected.</summary>
        BaselinePlane,
        /// <summary>A selection exists but is not a single baseline plane.</summary>
        Other,
    }

    /// <summary>Point-in-time host state used for command eligibility decisions.</summary>
    public sealed class SketchHostState
    {
        public SketchHostState(HostDocumentKind document, SupportSelection selection, bool isEditingSketch)
        {
            Document = document;
            Selection = selection;
            IsEditingSketch = isEditingSketch;
        }

        public HostDocumentKind Document { get; }
        public SupportSelection Selection { get; }
        public bool IsEditingSketch { get; }
    }

    /// <summary>
    /// The host seam. The native adapter implements this over the SolidWorks
    /// API; the headless test adapter implements it over an in-memory document.
    /// Commands never see SolidWorks objects.
    /// </summary>
    public interface ISketchHost
    {
        /// <summary>Snapshots document/selection/edit state for eligibility checks.</summary>
        SketchHostState CaptureState();

        /// <summary>
        /// Enters sketch-edit mode on the currently selected baseline plane and
        /// returns a session bound to the newly created sketch. Throws
        /// <see cref="SketchHostException"/> if the host cannot begin (the state
        /// may have changed since <see cref="CaptureState"/>).
        /// </summary>
        ISketchInsertion BeginSketchInsertion();
    }

    /// <summary>
    /// One sketch-insertion session bound to a single newly created sketch.
    /// Contract:
    ///  - <see cref="AddLine"/> appends a native line segment in sketch space.
    ///  - <see cref="KeepOpen"/> commits success: the new sketch stays open in
    ///    edit mode and is never cleaned up.
    ///  - <see cref="Abort"/> exits edit mode and deletes only the sketch this
    ///    session created, reporting the cleanup result honestly.
    ///  - <see cref="IDisposable.Dispose"/> restores every host setting the
    ///    session changed (sketch-manager flags), redraws after a committed
    ///    insertion, and best-effort aborts an uncommitted session.
    /// </summary>
    public interface ISketchInsertion : IDisposable
    {
        /// <summary>Name of the tracked sketch feature ("Sketch1", ...).</summary>
        string SketchName { get; }

        int LineCount { get; }

        /// <summary>Coordinates are sketch-space metres. Throws <see cref="SketchInsertionException"/> on failure.</summary>
        void AddLine(double x1, double y1, double x2, double y2);

        void KeepOpen();

        /// <summary>Aborts the session; returns whether cleanup actually removed the partial sketch.</summary>
        CleanupOutcome Abort();
    }

    /// <summary>Result of cleaning up a failed insertion.</summary>
    public sealed class CleanupOutcome
    {
        private CleanupOutcome(bool succeeded, string? failureReason)
        {
            Succeeded = succeeded;
            FailureReason = failureReason;
        }

        public bool Succeeded { get; }
        public string? FailureReason { get; }

        public static readonly CleanupOutcome Cleaned = new CleanupOutcome(true, null);

        public static CleanupOutcome Failed(string reason) =>
            new CleanupOutcome(false, reason ?? "unknown cleanup error");
    }

    /// <summary>The host could not begin a sketch-insertion session.</summary>
    public class SketchHostException : Exception
    {
        public SketchHostException(string message) : base(message) { }
        public SketchHostException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>A segment could not be inserted into the tracked sketch.</summary>
    public class SketchInsertionException : Exception
    {
        public SketchInsertionException(string message) : base(message) { }
        public SketchInsertionException(string message, Exception inner) : base(message, inner) { }
    }
}
