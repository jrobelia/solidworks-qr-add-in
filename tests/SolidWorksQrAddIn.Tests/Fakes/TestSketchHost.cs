using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Hosting;

namespace SolidWorksQrAddIn.Tests.Fakes
{
    /// <summary>
    /// Selection-type codes mirroring swSelectType_e values, so the fake
    /// exercises the same "can this support a sketch" decision the native
    /// adapter makes.
    /// </summary>
    public static class FakeSelType
    {
        public const int Edge = 1;           // swSelEDGES
        public const int Face = 2;           // swSelFACES
        public const int DatumPlane = 4;     // swSelDATUMPLANES
        public const int Sketch = 9;         // swSelSKETCHES
    }

    /// <summary>One line segment recorded in sketch-space metres.</summary>
    public sealed class FakeLine
    {
        public FakeLine(double x1, double y1, double x2, double y2)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
        }
        public double X1 { get; }
        public double Y1 { get; }
        public double X2 { get; }
        public double Y2 { get; }
    }

    /// <summary>An in-memory sketch: a named feature holding its segments.</summary>
    public sealed class FakeSketch
    {
        public FakeSketch(string name) { Name = name; }
        public string Name { get; }
        public List<FakeLine> Segments { get; } = new List<FakeLine>();
    }

    /// <summary>
    /// One named undo step on the document's undo stack — what
    /// FinishRecordingUndoObject produces and EditUndo2(1) consumes. Reverts
    /// run newest-first, so a step that created then deleted one sketch
    /// reverts to no change at all.
    /// </summary>
    public sealed class FakeUndoStep
    {
        public FakeUndoStep(string name) { Name = name; }
        public string Name { get; }
        public List<Action> Reverts { get; } = new List<Action>();
    }

    /// <summary>A lightweight in-memory part/assembly/drawing.</summary>
    public sealed class FakeDocument
    {
        public FakeDocument(string kind) { Kind = kind; }

        /// <summary>"Part", "Assembly", "Drawing" — mapped to HostDocumentKind by the host.</summary>
        public string Kind { get; }

        /// <summary>Features that already exist in the tree (user geometry we must preserve).</summary>
        public List<string> PreExistingFeatures { get; } = new List<string>();

        /// <summary>Selection as swSelectType_e-like codes, in selection order.</summary>
        public List<int> Selection { get; } = new List<int>();

        /// <summary>
        /// Whether the selected face is planar — the in-memory stand-in for
        /// the native adapter's ISurface.IsPlane() check. Only consulted when
        /// the single selected entity is a Face.
        /// </summary>
        public bool SelectedFaceIsPlanar { get; set; } = true;

        /// <summary>Sketch currently in edit mode, or null. Non-null means "already editing".</summary>
        public FakeSketch? ActiveSketch { get; set; }

        /// <summary>Committed sketch features in the tree.</summary>
        public List<FakeSketch> Sketches { get; } = new List<FakeSketch>();

        /// <summary>Mutable sketch-manager flags, as the real ISketchManager exposes.</summary>
        public Dictionary<string, bool> SketchManagerFlags { get; } = new Dictionary<string, bool>
        {
            ["AddToDB"] = false,
            ["DisplayWhenAdded"] = true,
            ["AutoInference"] = true,
            ["InferenceMode"] = true,
        };

        /// <summary>Number of times the host was asked to redraw.</summary>
        public int RedrawCount { get; set; }

        public int NextSketchNumber { get; set; } = 1;

        // ---------------- undo stack ----------------
        // Models IModelDocExtension.StartRecordingUndoObject /
        // FinishRecordingUndoObject + IModelDoc2.EditUndo2: mutations during
        // an open recording scope land in ONE named undo step; EditUndo2
        // reverts exactly the newest step — never an earlier user action.

        /// <summary>The document's undo steps, oldest first.</summary>
        public List<FakeUndoStep> UndoSteps { get; } = new List<FakeUndoStep>();

        private List<Action>? _recording;

        /// <summary>True while an undo-recording scope is open (never nested).</summary>
        public bool IsRecordingUndo => _recording != null;

        /// <summary>Opens the recording scope (StartRecordingUndoObject).</summary>
        public void BeginUndoRecording()
        {
            _recording ??= new List<Action>();
        }

        /// <summary>Records one undoable mutation inside the open scope.</summary>
        public void RecordUndoable(Action revert) => _recording?.Add(revert);

        /// <summary>Closes the scope as ONE named undo step (FinishRecordingUndoObject).</summary>
        public void FinishUndoRecording(string name)
        {
            var rec = _recording;
            _recording = null;
            if (rec == null) return;
            var step = new FakeUndoStep(name);
            step.Reverts.AddRange(rec);
            UndoSteps.Add(step);
        }

        /// <summary>
        /// Records a completed user operation (a user-drawn sketch, an extrude,
        /// ...) as its own undo step — the prior work a QR undo must never eat.
        /// </summary>
        public void PushUserUndoStep(string name, Action revert)
        {
            var step = new FakeUndoStep(name);
            step.Reverts.Add(revert);
            UndoSteps.Add(step);
        }

        /// <summary>
        /// EditUndo2(1): reverts exactly the newest step, newest recorded
        /// mutation first. Returns false when nothing is undoable.
        /// </summary>
        public bool UndoLastStep()
        {
            if (UndoSteps.Count == 0) return false;
            var step = UndoSteps[UndoSteps.Count - 1];
            UndoSteps.RemoveAt(UndoSteps.Count - 1);
            for (var i = step.Reverts.Count - 1; i >= 0; i--)
                step.Reverts[i]();
            return true;
        }

        public static FakeDocument FreshPart()
        {
            var doc = new FakeDocument("Part");
            doc.PreExistingFeatures.Add("Front Plane");
            doc.PreExistingFeatures.Add("Top Plane");
            doc.PreExistingFeatures.Add("Right Plane");
            doc.PreExistingFeatures.Add("Origin");
            return doc;
        }
    }

    /// <summary>
    /// The test adapter: an ISketchHost over a FakeDocument. Its behaviour
    /// mirrors the native adapter's contract — eligibility decisions, sketch
    /// edit state, segment recording, flag capture/restore, undo recording,
    /// redraw, abort cleanup — rather than just logging calls.
    /// </summary>
    public sealed class TestSketchHost : ISketchHost
    {
        public FakeDocument? Document { get; set; }

        /// <summary>When set, the 1-based AddLine call that throws SketchInsertionException.</summary>
        public int? FailOnLineNumber { get; set; }

        /// <summary>
        /// When set, that 1-based AddLine call throws a non-seam
        /// <see cref="InvalidOperationException"/> — models a host call that
        /// fails in a way the contract didn't anticipate (COM error, crash).
        /// </summary>
        public int? ThrowUnhandledOnLineNumber { get; set; }

        /// <summary>
        /// When set, the delete stage of abort cleanup fails: edit mode is
        /// exited (as the native path does before deleting) but the partial
        /// sketch stays in the feature tree. Persistent across retries.
        /// </summary>
        public bool FailCleanup { get; set; }

        /// <summary>
        /// When set, the earliest abort stage — leaving sketch edit mode —
        /// fails: the partial sketch stays in the tree AND open in edit mode.
        /// Persistent across retries. Takes precedence over
        /// <see cref="FailCleanup"/>.
        /// </summary>
        public bool FailCleanupEditExit { get; set; }

        /// <summary>
        /// When true, Abort itself throws a non-seam exception — the cleanup
        /// can't even produce a result. The command must still report
        /// honestly and never let it escape.
        /// </summary>
        public bool AbortThrowsUnhandled { get; set; }

        /// <summary>When true, Dispose throws a non-seam exception.</summary>
        public bool DisposeThrows { get; set; }

        /// <summary>When true, KeepOpen throws a non-seam exception before committing.</summary>
        public bool KeepOpenThrows { get; set; }

        /// <summary>When set, BeginSketchInsertion throws with this message (selection lost, etc.).</summary>
        public string? BeginFailure { get; set; }

        /// <summary>When true, CaptureState throws a non-seam exception.</summary>
        public bool ThrowOnCapture { get; set; }

        /// <summary>When true, BeginSketchInsertion throws a non-seam exception.</summary>
        public bool BeginThrowsUnhandled { get; set; }

        /// <summary>
        /// When set, the host enters sketch edit mode and creates the sketch,
        /// then fails BEFORE returning the insertion session. The host must
        /// clean up its own half-entered creation (exit edit mode, delete the
        /// sketch it made) before throwing SketchHostException with this
        /// message — the caller has no handle to clean it with.
        /// </summary>
        public string? BeginThrowsAfterSketchCreated { get; set; }

        /// <summary>
        /// With <see cref="BeginThrowsAfterSketchCreated"/>: the begin-path
        /// self-cleanup also fails — the partial sketch stays in the tree
        /// (edit mode exited) and the thrown message must say so honestly.
        /// </summary>
        public bool BeginCleanupFails { get; set; }

        /// <summary>
        /// Invoked inside <see cref="BeginSketchInsertion"/> just before its
        /// re-validation — models a selection that changed between the
        /// command's eligibility capture and the insertion begin.
        /// </summary>
        public Action<FakeDocument>? MutateBeforeBegin { get; set; }

        /// <summary>Times CaptureState was invoked — proves the host was (not) consulted.</summary>
        public int CaptureCount { get; private set; }

        /// <summary>Times BeginSketchInsertion ran — proves no host mutation happened when 0.</summary>
        public int BeginCount { get; private set; }

        public SketchHostState CaptureState()
        {
            CaptureCount++;
            if (ThrowOnCapture)
                throw new InvalidOperationException("Injected: the host could not read its state.");
            var doc = Document;
            var kind = doc == null
                ? HostDocumentKind.None
                : doc.Kind == "Part" ? HostDocumentKind.Part
                : doc.Kind == "Assembly" ? HostDocumentKind.Assembly
                : doc.Kind == "Drawing" ? HostDocumentKind.Drawing
                : HostDocumentKind.Unknown;

            SupportSelection selection;
            if (doc == null || doc.Selection.Count == 0)
                selection = SupportSelection.None;
            else if (doc.Selection.Count > 1)
                selection = SupportSelection.Ambiguous;
            else if (doc.Selection[0] == FakeSelType.DatumPlane)
                selection = SupportSelection.ReferencePlane;
            else if (doc.Selection[0] == FakeSelType.Face)
                selection = doc.SelectedFaceIsPlanar
                    ? SupportSelection.PlanarFace
                    : SupportSelection.NonPlanarFace;
            else
                selection = SupportSelection.Other;

            return new SketchHostState(kind, selection, doc?.ActiveSketch != null);
        }

        public ISketchInsertion BeginSketchInsertion()
        {
            BeginCount++;
            var doc0 = Document;
            if (doc0 != null)
                MutateBeforeBegin?.Invoke(doc0);
            var state = CaptureState();
            if (BeginFailure != null)
                throw new SketchHostException(BeginFailure);
            if (BeginThrowsUnhandled)
                throw new InvalidOperationException("Injected: the host could not begin the session.");
            if (state.Document != HostDocumentKind.Part)
                throw new SketchHostException("BeginSketchInsertion requires an active part document.");
            if (state.IsEditingSketch)
                throw new SketchHostException("A sketch is already open for editing.");
            if (state.Selection != SupportSelection.ReferencePlane &&
                state.Selection != SupportSelection.PlanarFace)
                throw new SketchHostException(
                    "BeginSketchInsertion requires exactly one selected support: " +
                    "a reference plane or a planar face.");

            var doc = Document!;
            // One named undo step per insertion attempt — recording opens
            // before the sketch is created, exactly like the native adapter's
            // StartRecordingUndoObject → FinishRecordingUndoObject scope.
            doc.BeginUndoRecording();
            var sketch = new FakeSketch("Sketch" + doc.NextSketchNumber++);
            doc.Sketches.Add(sketch);
            doc.ActiveSketch = sketch;
            var created = sketch;
            doc.RecordUndoable(() =>
            {
                // Undoing the creation removes exactly this sketch, wherever
                // it sits, and ends its edit session if it is open.
                doc.Sketches.Remove(created);
                if (ReferenceEquals(doc.ActiveSketch, created))
                    doc.ActiveSketch = null;
            });

            if (BeginThrowsAfterSketchCreated != null)
            {
                // The caller never sees this sketch — a thrown begin must
                // leave the document as it found it. The host cleans up the
                // sketch IT created; when that cleanup also fails the thrown
                // message says the partial sketch may remain.
                if (ReferenceEquals(doc.ActiveSketch, created))
                    doc.ActiveSketch = null;
                if (!BeginCleanupFails)
                {
                    // Delete recorded inside the open scope: undoing the
                    // step reverts create+delete to no change at all.
                    doc.RecordUndoable(() => doc.Sketches.Add(created));
                    doc.Sketches.Remove(created);
                }
                doc.FinishUndoRecording(SketchHostContract.CreationUndoName);
                if (BeginCleanupFails)
                    throw new SketchHostException(
                        $"{BeginThrowsAfterSketchCreated} The partial sketch '{created.Name}' " +
                        "could not be removed and may remain in the feature tree.");
                throw new SketchHostException(BeginThrowsAfterSketchCreated);
            }

            return new FakeSketchInsertion(this, doc, sketch);
        }
    }

    public sealed class FakeSketchInsertion : ISketchInsertion
    {
        private readonly TestSketchHost _host;
        private readonly FakeDocument _doc;
        private readonly FakeSketch _sketch;
        private readonly Dictionary<string, bool> _flagSnapshot;
        private bool _committed;
        private bool _aborted;
        private CleanupOutcome? _abortOutcome;

        public FakeSketchInsertion(TestSketchHost host, FakeDocument doc, FakeSketch sketch)
        {
            _host = host;
            _doc = doc;
            _sketch = sketch;
            // Capture the flags a native session would own, then simulate the
            // bulk-insert pattern so tests can observe restoration.
            _flagSnapshot = new Dictionary<string, bool>(doc.SketchManagerFlags);
            doc.SketchManagerFlags["AddToDB"] = true;
            doc.SketchManagerFlags["DisplayWhenAdded"] = false;
            doc.SketchManagerFlags["AutoInference"] = false;
        }

        public string SketchName => _sketch.Name;
        public int LineCount => _sketch.Segments.Count;

        public void AddLine(double x1, double y1, double x2, double y2)
        {
            if (_aborted || _committed)
                throw new SketchInsertionException("The insertion session is already finished.");
            var next = _sketch.Segments.Count + 1;
            if (_host.ThrowUnhandledOnLineNumber == next)
                throw new InvalidOperationException(
                    $"Injected: the host exploded inserting line {next}.");
            if (_host.FailOnLineNumber == next)
                throw new SketchInsertionException(
                    $"CreateLine returned no segment for line {next}.");
            _sketch.Segments.Add(new FakeLine(x1, y1, x2, y2));
        }

        public void KeepOpen()
        {
            if (_host.KeepOpenThrows)
                throw new InvalidOperationException("Injected: the host could not finish the sketch.");
            _committed = true;
            // The creation becomes ONE named undo step on commit.
            _doc.FinishUndoRecording(SketchHostContract.CreationUndoName);
        }

        public CleanupOutcome Abort()
        {
            if (_committed)
                return CleanupOutcome.Cleaned;   // nothing to undo — the sketch stays
            if (_abortOutcome != null)
                return _abortOutcome;            // report the REAL outcome, honestly
            if (_host.AbortThrowsUnhandled)
                throw new InvalidOperationException("Injected: the cleanup mechanism itself failed.");
            _aborted = true;
            _abortOutcome = AbortOnce();
            return _abortOutcome;
        }

        /// <summary>
        /// The staged abort the native adapter performs: exit sketch edit
        /// mode, then delete ONLY this session's sketch feature. A failure at
        /// the earliest stage leaves the sketch in the tree AND in edit mode;
        /// a later failure leaves it in the tree but out of edit mode.
        /// Never a blind Undo — prior user work must survive.
        /// </summary>
        private CleanupOutcome AbortOnce()
        {
            try
            {
                if (_host.FailCleanupEditExit)
                    return CleanupOutcome.Failed(
                        $"could not exit sketch edit mode for '{_sketch.Name}'");
                if (ReferenceEquals(_doc.ActiveSketch, _sketch))
                    _doc.ActiveSketch = null;

                if (_host.FailCleanup)
                    return CleanupOutcome.Failed(
                        $"could not delete sketch '{_sketch.Name}'");

                // The delete is recorded into the still-open undo scope, so
                // undoing the (failed) operation's step reverts to no change.
                _doc.RecordUndoable(() => _doc.Sketches.Add(_sketch));
                _doc.Sketches.Remove(_sketch);
                return CleanupOutcome.Cleaned;
            }
            finally
            {
                RestoreFlags();
                // The recording scope always closes — even when cleanup
                // failed — so nothing later is swallowed into it.
                _doc.FinishUndoRecording(SketchHostContract.CreationUndoName);
            }
        }

        public void Dispose()
        {
            if (_host.DisposeThrows)
                throw new InvalidOperationException("Injected: session teardown failed.");
            if (_committed)
            {
                // Success path: sketch stays in edit mode; restore flags + redraw.
                RestoreFlags();
                _doc.RedrawCount++;
                return;
            }
            if (!_aborted)
                Abort();
        }

        private void RestoreFlags()
        {
            foreach (var kv in _flagSnapshot)
                _doc.SketchManagerFlags[kv.Key] = kv.Value;
        }
    }
}
