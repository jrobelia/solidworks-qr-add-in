using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;

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

    /// <summary>
    /// The document's length unit as the host reports it — a host fact, never
    /// something the core may invent. <see cref="MetresPerUnit"/> converts
    /// between document units and sketch-space metres (0.001 for mm docs,
    /// 0.0254 for inch docs); <see cref="Symbol"/> is the display label.
    /// The block-instance Scale reads as a size in these units, and the
    /// pane's cell-size readout is expressed in them.
    /// </summary>
    public sealed class HostLengthUnit
    {
        public HostLengthUnit(double metresPerUnit, string symbol)
        {
            MetresPerUnit = metresPerUnit;
            Symbol = symbol;
        }

        /// <summary>Sketch-space metres per document length unit.</summary>
        public double MetresPerUnit { get; }

        /// <summary>The unit's display label ("mm", "in", …).</summary>
        public string Symbol { get; }

        /// <summary>
        /// Millimetres — the add-in's baseline unit, used when the host
        /// cannot report a real unit (no document, unreadable property).
        /// </summary>
        public static readonly HostLengthUnit Millimetres = new HostLengthUnit(0.001, "mm");
    }

    /// <summary>Point-in-time host state used for command eligibility decisions.</summary>
    public sealed class SketchHostState
    {
        public SketchHostState(
            HostDocumentKind document, SupportSelection selection, bool isEditingSketch,
            HostLengthUnit? lengthUnit = null)
        {
            Document = document;
            Selection = selection;
            IsEditingSketch = isEditingSketch;
            LengthUnit = lengthUnit ?? HostLengthUnit.Millimetres;
        }

        public HostDocumentKind Document { get; }
        public SupportSelection Selection { get; }
        public bool IsEditingSketch { get; }

        /// <summary>
        /// The document's length unit at capture time — mm when the host
        /// could not report one.
        /// </summary>
        public HostLengthUnit LengthUnit { get; }
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
    ///  - <see cref="AddLine"/> STAGES one contour edge at its final size in
    ///    sketch space; the host may buffer edges and realize them at commit
    ///    (the adapters draw them unit-proportioned inside one sketch block
    ///    instance whose Scale reads the requested size in document units —
    ///    spec #10 — so nothing lands as raw segments until
    ///    <see cref="KeepOpen"/>).
    ///  - <see cref="AddConstructionSquares"/> STAGES the definition-internal
    ///    construction squares the same way — realized at commit INSIDE the
    ///    block definition, so they scale and move with the instance.
    ///  - <see cref="KeepOpen"/> commits success: the staged geometry is
    ///    realized, the new sketch stays open in edit mode and is never
    ///    cleaned up.
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

        /// <summary>Staged contour edges so far.</summary>
        int LineCount { get; }

        /// <summary>
        /// Stages one contour edge; coordinates are sketch-space metres at the
        /// final size. Throws <see cref="SketchInsertionException"/> on failure.
        /// </summary>
        void AddLine(double x1, double y1, double x2, double y2);

        /// <summary>
        /// Stages the model's construction squares — the QR square and the
        /// keep-out square in sketch-space metres at the requested size —
        /// for commit-time emission INSIDE the block definition (ADR 0003).
        /// Like <see cref="AddLine"/> this only stages: <see cref="KeepOpen"/>
        /// normalizes the squares into the same unit-proportioned definition
        /// space as the contours, flagged construction, BEFORE the selection
        /// fold that forms the block — so the footprint and the quiet-zone
        /// boundary are definition content that scales and moves with the
        /// instance, with no host-side constraints, dimensions, or
        /// equations. A null or empty list stages nothing. Throws
        /// <see cref="SketchInsertionException"/> on failure.
        /// </summary>
        void AddConstructionSquares(IReadOnlyList<QrConstructionSquare> squares);

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
