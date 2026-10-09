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
    /// edit state, segment recording, flag capture/restore, redraw, abort
    /// cleanup — rather than just logging calls.
    /// </summary>
    public sealed class TestSketchHost : ISketchHost
    {
        public FakeDocument? Document { get; set; }

        /// <summary>When set, the 1-based AddLine call that throws SketchInsertionException.</summary>
        public int? FailOnLineNumber { get; set; }

        /// <summary>When true, abort cleanup reports failure and leaves the partial sketch.</summary>
        public bool FailCleanup { get; set; }

        /// <summary>When set, BeginSketchInsertion throws with this message (selection lost, etc.).</summary>
        public string? BeginFailure { get; set; }

        /// <summary>
        /// Invoked inside <see cref="BeginSketchInsertion"/> just before its
        /// re-validation — models a selection that changed between the
        /// command's eligibility capture and the insertion begin.
        /// </summary>
        public Action<FakeDocument>? MutateBeforeBegin { get; set; }

        /// <summary>Times CaptureState was invoked — proves the host was (not) consulted.</summary>
        public int CaptureCount { get; private set; }

        public SketchHostState CaptureState()
        {
            CaptureCount++;
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
            var doc0 = Document;
            if (doc0 != null)
                MutateBeforeBegin?.Invoke(doc0);
            var state = CaptureState();
            if (BeginFailure != null)
                throw new SketchHostException(BeginFailure);
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
            var sketch = new FakeSketch("Sketch" + doc.NextSketchNumber++);
            doc.Sketches.Add(sketch);
            doc.ActiveSketch = sketch;
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
            if (_host.FailOnLineNumber == _sketch.Segments.Count + 1)
                throw new SketchInsertionException(
                    $"CreateLine returned no segment for line {_sketch.Segments.Count + 1}.");
            _sketch.Segments.Add(new FakeLine(x1, y1, x2, y2));
        }

        public void KeepOpen()
        {
            _committed = true;
        }

        public CleanupOutcome Abort()
        {
            _aborted = true;
            try
            {
                if (_host.FailCleanup)
                    return CleanupOutcome.Failed(
                        $"Could not select sketch feature '{_sketch.Name}' for deletion.");
                // Exit edit mode, then delete only the sketch this session created.
                if (_doc.ActiveSketch == _sketch)
                    _doc.ActiveSketch = null;
                _doc.Sketches.Remove(_sketch);
                return CleanupOutcome.Cleaned;
            }
            finally
            {
                RestoreFlags();
            }
        }

        public void Dispose()
        {
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
