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
        /// <summary>
        /// Exactly one reference (datum) plane is selected — a built-in plane
        /// or a user-created one, at any orientation. (swSelDATUMPLANES.)
        /// </summary>
        ReferencePlane,
        /// <summary>
        /// Exactly one model face is selected and it is planar — it can
        /// support a 2D sketch.
        /// </summary>
        PlanarFace,
        /// <summary>
        /// Exactly one face is selected but its surface is not planar
        /// (cylinder, cone, sphere, B-surface, ...) — it cannot support a
        /// 2D sketch.
        /// </summary>
        NonPlanarFace,
        /// <summary>
        /// More than one entity is selected — the intended support is
        /// ambiguous. (Any combination, even two valid supports.)
        /// </summary>
        Ambiguous,
        /// <summary>
        /// Exactly one entity of a kind that cannot support a sketch is
        /// selected (edge, vertex, axis, sketch, ...).
        /// </summary>
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
        /// Enters sketch-edit mode on the currently selected support — one
        /// reference plane or one planar face — and returns a session bound to
        /// the newly created sketch. The selection is re-read at this point:
        /// a stale or changed support must throw <see cref="SketchHostException"/>
        /// rather than steer insertion onto a wrong entity.
        /// </summary>
        ISketchInsertion BeginSketchInsertion();

        /// <summary>
        /// Post-commit hook: the command calls this once a creation has
        /// fully succeeded, so the host can register a custom undo step for
        /// the sketch it just made (the native adapter hands SolidWorks a
        /// SetApiUndoObject handler — AddToDB insertion bypasses native undo
        /// recording, so this IS the named step). Best-effort plumbing on
        /// top of a completed creation: a host that cannot register must
        /// simply return — never throw, never mutate the sketch.
        /// </summary>
        void RegisterSketchUndo(SketchUndoRegistration registration);
    }

    /// <summary>
    /// One sketch-insertion session bound to a single newly created sketch.
    /// Contract:
    ///  - <see cref="AddLine"/> appends a native line segment in sketch space.
    ///  - <see cref="KeepOpen"/> commits success: the new sketch stays open in
    ///    edit mode and is never cleaned up.
    ///  - <see cref="Abort"/> exits edit mode and deletes only the sketch this
    ///    session created, reporting the cleanup result honestly. It is a
    ///    no-op on a committed session and reports the same REAL outcome on
    ///    every call — a failed cleanup may not be claimed as clean on retry.
    ///  - <see cref="IDisposable.Dispose"/> restores every host setting the
    ///    session changed (sketch-manager flags), redraws after a committed
    ///    insertion, and best-effort aborts an uncommitted session.
    /// Hosts that support named undo steps group the whole creation into one
    /// step named <see cref="SketchHostContract.CreationUndoName"/>.
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

    /// <summary>Contract-level constants every host adapter honours.</summary>
    public static class SketchHostContract
    {
        /// <summary>
        /// The undo-step name a creation attempt records: a committed
        /// insertion produces exactly ONE undo step with this name (grouping
        /// sketch creation + every segment), so undoing it removes the QR
        /// sketch and nothing else. Hosts without undo-record support must
        /// still behave correctly — the name is a best-effort nicety.
        /// </summary>
        public const string CreationUndoName = "Create QR Sketch";
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
