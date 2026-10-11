using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
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

        /// <summary>
        /// Raw line segments drawn directly in this sketch. The QR contours
        /// no longer land here — they are wrapped in a block instance at
        /// commit — so a created QR sketch leaves this empty. User sketches
        /// and failure paths still use it.
        /// </summary>
        public List<FakeLine> Segments { get; } = new List<FakeLine>();

        /// <summary>Block instances placed in this sketch (the QR is exactly one).</summary>
        public List<FakeBlockInstance> BlockInstances { get; } = new List<FakeBlockInstance>();

        /// <summary>
        /// Construction squares drawn in this sketch — the QR square lives
        /// here, bound to the block instance by coincident relations. Never
        /// part of <see cref="Segments"/> and never inside a block.
        /// </summary>
        public List<FakeConstructionSquare> ConstructionSquares { get; } =
            new List<FakeConstructionSquare>();

        /// <summary>Display dimensions placed in this sketch (the QR square's driven readout).</summary>
        public List<FakeDisplayDimension> Dimensions { get; } = new List<FakeDisplayDimension>();

        /// <summary>
        /// The geometry the sketch actually shows: raw drawn segments plus
        /// every block instance's effective (scale/angle/position-resolved)
        /// edges — the world-state a test should assert on when it asks
        /// "what did the create leave in the sketch". Construction geometry
        /// (squares) is deliberately NOT part of this enumeration — it is
        /// reference geometry, asserted separately.
        /// </summary>
        public IEnumerable<FakeLine> EffectiveSegments =>
            Segments.Concat(BlockInstances.SelectMany(i => i.EffectiveEdges));
    }

    /// <summary>
    /// A construction square in a sketch — the in-memory stand-in for the
    /// host-sketch construction geometry the QR square emits as. Its edges
    /// are coincident-bound to a block instance's outer edges
    /// (<see cref="CoincidentBindings"/> relations), so a Scale edit on the
    /// instance drags the square: <see cref="EffectiveEdges"/> is derived
    /// from the bound instance's CURRENT effective extent, recomputed on
    /// every read exactly like <see cref="FakeBlockInstance.EffectiveEdges"/>.
    /// </summary>
    public sealed class FakeConstructionSquare
    {
        public FakeConstructionSquare(QrSquareRole role, List<FakeLine> drawnEdges, FakeBlockInstance? bound)
        {
            Role = role;
            DrawnEdges = drawnEdges;
            BoundInstance = bound;
        }

        /// <summary>Which square of the QR layout this is (QR square, keep-out).</summary>
        public QrSquareRole Role { get; }

        /// <summary>The edges as drawn, in sketch-space metres.</summary>
        public List<FakeLine> DrawnEdges { get; }

        /// <summary>
        /// The block instance the square's edges are coincident-bound to —
        /// null when no instance exists to constrain to (degraded: the square
        /// then stands at its drawn position and does not track).
        /// </summary>
        public FakeBlockInstance? BoundInstance { get; }

        /// <summary>
        /// How many coincident relations the emission applied between the
        /// square's edges and the block's outer-edge points — the world-state
        /// stand-in for the sketch's relation set.
        /// </summary>
        public int CoincidentBindings { get; set; }

        /// <summary>Always true — squares emit as construction geometry.</summary>
        public bool IsConstruction => true;

        /// <summary>
        /// The square's four edges as the sketch currently shows them: the
        /// bound block instance's effective bounding box (recomputed live —
        /// a Scale edit moves them) or, unbound, the drawn edges.
        /// </summary>
        public IEnumerable<FakeLine> EffectiveEdges
        {
            get
            {
                var bound = BoundInstance;
                if (bound == null)
                {
                    foreach (var e in DrawnEdges) yield return e;
                    yield break;
                }
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                foreach (var e in bound.EffectiveEdges)
                {
                    minX = Math.Min(minX, Math.Min(e.X1, e.X2));
                    maxX = Math.Max(maxX, Math.Max(e.X1, e.X2));
                    minY = Math.Min(minY, Math.Min(e.Y1, e.Y2));
                    maxY = Math.Max(maxY, Math.Max(e.Y1, e.Y2));
                }
                yield return new FakeLine(minX, minY, maxX, minY);
                yield return new FakeLine(maxX, minY, maxX, maxY);
                yield return new FakeLine(maxX, maxY, minX, maxY);
                yield return new FakeLine(minX, maxY, minX, minY);
            }
        }
    }

    /// <summary>
    /// A DisplayDimension in a sketch — the in-memory stand-in for the
    /// IDisplayDimension the driven readout lands as. Its value is MEASURED
    /// from the square it targets, recomputed on every read: a Scale edit
    /// drags the bound square and the driven dim reads the new size.
    /// </summary>
    public sealed class FakeDisplayDimension
    {
        private readonly FakeDocument _doc;

        public FakeDisplayDimension(FakeConstructionSquare target, FakeDocument doc)
        {
            Target = target;
            _doc = doc;
        }

        /// <summary>The construction square the dimension measures.</summary>
        public FakeConstructionSquare Target { get; }

        /// <summary>True for a driven (reference readout) dimension — it never drives.</summary>
        public bool IsDriven { get; set; }

        /// <summary>
        /// Whether the two dimension-prompt user toggles were suppressed at
        /// the moment the dimension was created — the world-state stand-in
        /// for the guards the real adapter applies around IAddDimension2
        /// (which hangs an invisible session when they are on).
        /// </summary>
        public bool PromptsSuppressedAtCreation { get; set; }

        /// <summary>
        /// The live readout in document units — the target square's side.
        /// Recomputed on every read, so a bound square that tracks a Scale
        /// edit shows the new size.
        /// </summary>
        public double ValueDocUnits
        {
            get
            {
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                foreach (var e in Target.EffectiveEdges)
                {
                    minX = Math.Min(minX, Math.Min(e.X1, e.X2));
                    maxX = Math.Max(maxX, Math.Max(e.X1, e.X2));
                    minY = Math.Min(minY, Math.Min(e.Y1, e.Y2));
                    maxY = Math.Max(maxY, Math.Max(e.Y1, e.Y2));
                }
                return Math.Max(maxX - minX, maxY - minY) / _doc.MetresPerLengthUnit;
            }
        }
    }

    /// <summary>
    /// A sketch block definition: the QR contours drawn unit-proportioned —
    /// one document length unit across — in sketch-space metres. In the real
    /// host the definition lives in the document, shared by its instances.
    /// </summary>
    public sealed class FakeBlockDefinition
    {
        public FakeBlockDefinition(string name) { Name = name; }

        public string Name { get; }

        /// <summary>The definition's contours in sketch-space metres (one document unit across).</summary>
        public List<FakeLine> Contours { get; } = new List<FakeLine>();

        /// <summary>Instances of this definition placed in sketches.</summary>
        public List<FakeBlockInstance> Instances { get; } = new List<FakeBlockInstance>();
    }

    /// <summary>
    /// A block instance placed in a sketch: a definition handle plus the
    /// placement — insertion point, uniform <see cref="Scale"/> (the resize
    /// handle — it reads as the QR size in document units) and rotation.
    /// </summary>
    public sealed class FakeBlockInstance
    {
        public FakeBlockInstance(FakeBlockDefinition definition, double x, double y)
        {
            Definition = definition;
            X = x;
            Y = y;
        }

        public string Name { get; set; } = "";

        public FakeBlockDefinition Definition { get; }

        /// <summary>Insertion point, sketch-space metres.</summary>
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>
        /// Uniform scale — what the PropertyManager Scale field shows. Editing
        /// it rescales the effective geometry uniformly, exactly like the
        /// real instance property.
        /// </summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>Rotation, radians.</summary>
        public double Angle { get; set; }

        /// <summary>
        /// The geometry the instance currently contributes to the sketch, in
        /// sketch-space metres: definition contours × Scale, rotated by
        /// <see cref="Angle"/>, offset to the insertion point. Recomputed on
        /// every read — a Scale edit rescales it immediately.
        /// </summary>
        public IEnumerable<FakeLine> EffectiveEdges
        {
            get
            {
                double cos = Math.Cos(Angle);
                double sin = Math.Sin(Angle);
                foreach (var e in Definition.Contours)
                {
                    yield return new FakeLine(
                        X + Scale * (e.X1 * cos - e.Y1 * sin),
                        Y + Scale * (e.X1 * sin + e.Y1 * cos),
                        X + Scale * (e.X2 * cos - e.Y2 * sin),
                        Y + Scale * (e.X2 * sin + e.Y2 * cos));
                }
            }
        }
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

        /// <summary>
        /// The redo half for steps registered by an API undo object — the
        /// single-callback toggle modelled after ISwUndoAPIHandler.DoUndo.
        /// Null for recorded steps (no redo support).
        /// </summary>
        public Action? DoRedo { get; set; }
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

        /// <summary>Block definitions created in this document (doc-level registry).</summary>
        public List<FakeBlockDefinition> BlockDefinitions { get; } = new List<FakeBlockDefinition>();

        /// <summary>Sequence number for generated block names ("Block1", "Block1-1").</summary>
        public int NextBlockNumber { get; set; } = 1;

        /// <summary>
        /// The document's length unit in metres — the in-memory stand-in for
        /// the native adapter's ModelDoc2.LengthUnit read (swLengthUnit_e).
        /// 0.001 = millimetres, 0.0254 = inches. The block definition spans
        /// exactly one of these units and the instance Scale reads as the
        /// size in them.
        /// </summary>
        public double MetresPerLengthUnit { get; set; } = 0.001;

        /// <summary>Display symbol for the document's length unit ("mm", "in", …).</summary>
        public string LengthUnitSymbol { get; set; } = "mm";

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

        /// <summary>
        /// Closes the recording scope WITHOUT producing a step — mirrors the
        /// live-verified verdict that FinishRecordingUndoObject2 is refused
        /// for AddToDB insertion: a committed creation's recording leaves no
        /// trace on the undo list; the named step now comes from
        /// RegisterSketchUndo instead.
        /// </summary>
        public void DiscardUndoRecording()
        {
            _recording = null;
        }

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

        // Redo stack — an undone step lands here, newest last.
        private readonly List<FakeUndoStep> _redoSteps = new List<FakeUndoStep>();

        /// <summary>Steps waiting to be redone (EditRedo), oldest first.</summary>
        public IReadOnlyList<FakeUndoStep> RedoSteps => _redoSteps;

        /// <summary>
        /// EditUndo2(1): reverts exactly the newest step, newest recorded
        /// mutation first, and moves it to the redo stack. Returns false
        /// when nothing is undoable.
        /// </summary>
        public bool UndoLastStep()
        {
            if (UndoSteps.Count == 0) return false;
            var step = UndoSteps[UndoSteps.Count - 1];
            UndoSteps.RemoveAt(UndoSteps.Count - 1);
            for (var i = step.Reverts.Count - 1; i >= 0; i--)
                step.Reverts[i]();
            _redoSteps.Add(step);
            return true;
        }

        /// <summary>
        /// EditRedo2(1): re-applies the most recently undone step — for an
        /// API-undo step, the same handler toggles back (reinsert). Returns
        /// false when nothing is redoable.
        /// </summary>
        public bool RedoLastStep()
        {
            if (_redoSteps.Count == 0) return false;
            var step = _redoSteps[_redoSteps.Count - 1];
            _redoSteps.RemoveAt(_redoSteps.Count - 1);
            step.DoRedo?.Invoke();
            UndoSteps.Add(step);
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

        /// <summary>
        /// When true, the first definition draw inside the commit's block
        /// step fails (the CreateLine stage) — the sketch may hold
        /// partially-drawn definition lines, then abort cleans it up.
        /// </summary>
        public bool FailBlockDraw { get; set; }

        /// <summary>
        /// When true, the commit's MakeSketchBlockFromSelected stage fails —
        /// the drawn definition lines were already in the sketch but no block
        /// formed. Abort still removes the whole sketch.
        /// </summary>
        public bool FailBlockMake { get; set; }

        /// <summary>
        /// When true, the commit's instance step fails — the block definition
        /// and its instance exist but the Scale could not be applied. Abort
        /// removes the whole sketch (block inside it).
        /// </summary>
        public bool FailBlockInstance { get; set; }

        /// <summary>
        /// When true, the commit's construction-square draw stage fails —
        /// the block instance is already in the sketch but the square could
        /// not be drawn. Abort removes the whole sketch.
        /// </summary>
        public bool FailSquareDraw { get; set; }

        /// <summary>
        /// When true, the square's coincident-binding stage fails — the
        /// square was drawn but could not be constrained to the block's
        /// outer edges. Abort removes the whole sketch.
        /// </summary>
        public bool FailSquareConstraint { get; set; }

        /// <summary>
        /// When true, the dimension emission fails — IAddDimension2 returned
        /// no dimension. Abort removes the whole sketch; the prompt toggles
        /// must still be restored.
        /// </summary>
        public bool FailDimension { get; set; }

        /// <summary>
        /// Application-level user-preference toggle — the in-memory stand-in
        /// for ISldWorks.SetUserPreferenceToggle(swInputDimValOnCreate). On by
        /// default: the dangerous state the spike documented (IAddDimension2
        /// hangs an invisible session when it is on).
        /// </summary>
        public bool InputDimValOnCreate { get; set; } = true;

        /// <summary>
        /// Application-level user-preference toggle — the stand-in for
        /// swSketchOverdefiningDimsPromptToSetState. On by default; while on,
        /// an over-defining dimension prompts instead of landing driven.
        /// </summary>
        public bool OverdefiningDimsPromptToSetState { get; set; } = true;

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

        /// <summary>
        /// When true, RegisterSketchUndo throws a non-seam exception — models
        /// a SetApiUndoObject failure. The creation must still succeed: undo
        /// plumbing can never break a committed sketch.
        /// </summary>
        public bool FailUndoRegistration { get; set; }

        /// <summary>Every registration the command handed this host, in order.</summary>
        public List<SketchUndoRegistration> UndoRegistrations { get; } =
            new List<SketchUndoRegistration>();

        /// <summary>
        /// The custom-undo registration the native adapter performs through
        /// SetApiUndoObject. Modelled as one named undo step whose revert
        /// removes exactly the created sketch (by identity — an
        /// already-removed sketch is a safe no-op, a user feature is never
        /// touched) and whose redo re-adds it, like DoUndo toggling.
        /// </summary>
        public void RegisterSketchUndo(SketchUndoRegistration registration)
        {
            UndoRegistrations.Add(registration);
            if (FailUndoRegistration)
                throw new InvalidOperationException(
                    "Injected: the host could not register the undo step.");
            var doc = Document;
            if (doc == null) return;
            var target = doc.Sketches.Find(s => s.Name == registration.SketchName);
            var step = new FakeUndoStep(registration.UndoName);
            step.Reverts.Add(() =>
            {
                if (target != null)
                {
                    doc.Sketches.Remove(target);
                    if (ReferenceEquals(doc.ActiveSketch, target))
                        doc.ActiveSketch = null;
                }
            });
            step.DoRedo = () =>
            {
                if (target != null && !doc.Sketches.Contains(target))
                    doc.Sketches.Add(target);
            };
            doc.UndoSteps.Add(step);
        }

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

            return new SketchHostState(
                kind, selection, doc?.ActiveSketch != null,
                doc == null
                    ? null
                    : new HostLengthUnit(doc.MetresPerLengthUnit, doc.LengthUnitSymbol));
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
        private readonly List<FakeLine> _buffered = new List<FakeLine>();
        private readonly List<QrConstructionSquare> _stagedSquares =
            new List<QrConstructionSquare>();
        private QrDimensionSpec? _dimensionSpec;
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
        public int LineCount => _buffered.Count;

        /// <summary>
        /// Stages one contour edge of the QR pattern at its final size in
        /// sketch-space metres. Nothing is drawn yet — the session buffers
        /// the staged edges and realizes them at commit: a block-capable
        /// host draws them unit-proportioned, wraps them in a block
        /// definition and places the one instance whose Scale reads the
        /// staged span in document units.
        /// </summary>
        public void AddLine(double x1, double y1, double x2, double y2)
        {
            if (_aborted || _committed)
                throw new SketchInsertionException("The insertion session is already finished.");
            var next = _buffered.Count + 1;
            if (_host.ThrowUnhandledOnLineNumber == next)
                throw new InvalidOperationException(
                    $"Injected: the host exploded inserting line {next}.");
            if (_host.FailOnLineNumber == next)
                throw new SketchInsertionException(
                    $"CreateLine returned no segment for line {next}.");
            _buffered.Add(new FakeLine(x1, y1, x2, y2));
        }

        /// <summary>
        /// Stages the model's construction layout — construction squares plus
        /// the dimension plan — for commit-time emission. Like the buffered
        /// contour edges nothing lands yet; <see cref="KeepOpen"/> emits the
        /// staged layout AFTER the block instance exists, so the squares can
        /// constrain to its outer edges (a select-all inside the block step
        /// would swallow anything drawn earlier into the definition).
        /// </summary>
        public void AddConstructionLayout(
            IReadOnlyList<QrConstructionSquare> squares, QrDimensionSpec? dimensionSpec)
        {
            if (_aborted || _committed)
                throw new SketchInsertionException("The insertion session is already finished.");
            if (squares != null)
                _stagedSquares.AddRange(squares);
            _dimensionSpec = dimensionSpec;
        }

        public void KeepOpen()
        {
            if (_host.KeepOpenThrows)
                throw new InvalidOperationException("Injected: the host could not finish the sketch.");
            if (_buffered.Count > 0)
                RealizeAsBlockInstance();
            if (_dimensionSpec != null)
                EmitConstructionLayout();
            _committed = true;
            // Live-verified on SW 2023 SP5: the AddToDB recording is refused
            // (FinishRecordingUndoObject2 returns false), so a committed
            // creation produces NO step from the recording — the named undo
            // step arrives separately via RegisterSketchUndo.
            _doc.DiscardUndoRecording();
        }

        /// <summary>
        /// The buffered-edge → block-instance realization the native adapter
        /// performs inside the open sketch edit:
        ///   1. the staged span IS the requested size — the instance Scale is
        ///      that span expressed in document units;
        ///   2. the definition is drawn unit-proportioned (every edge divided
        ///      by the scale, so the def spans exactly one document unit);
        ///   3. select-all converts the drawn lines into the definition and
        ///      its first instance (scale 1 at the origin);
        ///   4. the instance's Scale is set to the document-unit size — the
        ///      PropertyManager field then reads as the size.
        /// </summary>
        private void RealizeAsBlockInstance()
        {
            double scaleDocUnits = BufferedSpan() / _doc.MetresPerLengthUnit;

            // Definition draw stage — rescaled edges land as ordinary sketch
            // lines first (the conversion step below swallows them).
            for (var i = 0; i < _buffered.Count; i++)
            {
                if (_host.FailBlockDraw && i == 0)
                    throw new SketchInsertionException(
                        "CreateLine returned no segment while drawing the block definition.");
                var e = _buffered[i];
                _sketch.Segments.Add(new FakeLine(
                    e.X1 / scaleDocUnits, e.Y1 / scaleDocUnits,
                    e.X2 / scaleDocUnits, e.Y2 / scaleDocUnits));
            }

            if (_host.FailBlockMake)
                throw new SketchInsertionException(
                    "MakeSketchBlockFromSelected failed to form a block definition.");

            // The select-all → MakeSketchBlockFromSelected step: the drawn
            // lines leave the sketch's raw segment list and become the
            // definition's content; the first instance lands at the origin.
            var def = new FakeBlockDefinition("Block" + _doc.NextBlockNumber++);
            def.Contours.AddRange(_sketch.Segments);
            _sketch.Segments.Clear();
            _doc.BlockDefinitions.Add(def);
            var instance = new FakeBlockInstance(def, 0.0, 0.0) { Name = def.Name + "-1" };
            def.Instances.Add(instance);
            _sketch.BlockInstances.Add(instance);

            if (_host.FailBlockInstance)
                throw new SketchInsertionException(
                    "The block instance's Scale could not be applied.");
            instance.Scale = scaleDocUnits;
        }

        /// <summary>
        /// The construction-layout emission the native adapter performs
        /// inside the open sketch edit AFTER the block instance exists —
        /// the model's staged squares + dimension spec become host-sketch
        /// world state:
        ///   1. draw the spec's driven square as construction geometry;
        ///   2. coincident-bind each of its edges to the block instance's
        ///      outer edges, so a Scale edit drags the square;
        ///   3. add the driven DisplayDimension — created only while the two
        ///      dimension-prompt toggles are suppressed (spike: the call
        ///      hangs an invisible session otherwise), restored after.
        /// The emission walks the spec's roles in order — today only the
        /// driven square; the equation-linked keep-out square is the next
        /// spec step.
        /// </summary>
        private void EmitConstructionLayout()
        {
            var spec = _dimensionSpec!;
            var staged = _stagedSquares.FirstOrDefault(s => s.Role == spec.DrivenSquare);
            if (staged == null)
                throw new SketchInsertionException(
                    "The dimension spec names a construction square that was never staged.");
            var instance = _sketch.BlockInstances.LastOrDefault();

            // 1. Draw — the square's edges land as construction geometry.
            if (_host.FailSquareDraw)
                throw new SketchInsertionException(
                    "CreateLine returned no segment while drawing the construction square.");
            var drawn = staged.Edges
                .Select(e => new FakeLine(e.Start.X, e.Start.Y, e.End.X, e.End.Y))
                .ToList();
            var square = new FakeConstructionSquare(staged.Role, drawn, instance);

            // 2. Coincident bindings — each edge gets two relations to block
            //    boundary points (a line pinned at two points tracks rigidly).
            if (_host.FailSquareConstraint)
                throw new SketchInsertionException(
                    "The construction square's edge could not be constrained coincident " +
                    "to the block's outer edges.");
            square.CoincidentBindings = drawn.Count * 2;
            _sketch.ConstructionSquares.Add(square);

            // 3. The driven dimension — prompt toggles forced off around the
            //    creation, restored exactly after (even on failure).
            var prevInput = _host.InputDimValOnCreate;
            var prevOverdef = _host.OverdefiningDimsPromptToSetState;
            try
            {
                _host.InputDimValOnCreate = false;
                _host.OverdefiningDimsPromptToSetState = false;
                if (_host.FailDimension)
                    throw new SketchInsertionException(
                        "IAddDimension2 returned no dimension for the construction square.");
                _sketch.Dimensions.Add(new FakeDisplayDimension(square, _doc)
                {
                    IsDriven = true,
                    PromptsSuppressedAtCreation =
                        !_host.InputDimValOnCreate && !_host.OverdefiningDimsPromptToSetState,
                });
            }
            finally
            {
                _host.InputDimValOnCreate = prevInput;
                _host.OverdefiningDimsPromptToSetState = prevOverdef;
            }
        }

        /// <summary>The staged outline span (max of the x/y extents), metres.</summary>
        private double BufferedSpan()
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var e in _buffered)
            {
                minX = Math.Min(minX, Math.Min(e.X1, e.X2));
                maxX = Math.Max(maxX, Math.Max(e.X1, e.X2));
                minY = Math.Min(minY, Math.Min(e.Y1, e.Y2));
                maxY = Math.Max(maxY, Math.Max(e.Y1, e.Y2));
            }
            return Math.Max(maxX - minX, maxY - minY);
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
