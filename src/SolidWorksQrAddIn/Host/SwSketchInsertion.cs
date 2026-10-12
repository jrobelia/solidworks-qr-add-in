using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// Native insertion session bound to exactly one newly created sketch.
    /// KeepOpen leaves that sketch in edit mode; Abort exits edit mode and
    /// deletes only this session's sketch feature. Flags captured at Begin are
    /// restored on every exit path; a committed session redraws the view.
    ///
    /// When <paramref name="undoRecording"/> is set the host opened a
    /// StartRecordingUndoObject scope in BeginSketchInsertion; this session
    /// tries to close it once — named "Create QR Sketch" — on commit or on
    /// abort. Live-verified on SW 2023 SP5: the finish call is refused in
    /// every arrangement (AddToDB-mode insertion bypasses the undo system),
    /// so the named step is best-effort only — a naming aid, never a
    /// rollback mechanism.
    /// </summary>
    public sealed class SwSketchInsertion : ISketchInsertion
    {
        private readonly IModelDoc2 _doc;
        private readonly ISketchManager _mgr;
        private readonly IFeature _sketchFeature;
        private readonly IModelDocExtension? _ext;
        private readonly IMathUtility? _math;
        private readonly bool _undoRecording;
        private readonly SketchManagerFlagSnapshot _previousFlags;
        private readonly List<SketchEdge> _buffered = new List<SketchEdge>();
        private readonly List<QrConstructionSquare> _stagedSquares =
            new List<QrConstructionSquare>();
        private bool _committed;
        private bool _aborted;
        private bool _flagsRestored;
        private bool _undoFinished;
        private CleanupOutcome? _abortOutcome;

        /// <param name="math">
        /// The app's math utility — the block calls need MathPoint insertion
        /// points. Null only when the host could not obtain one; the commit
        /// then fails honestly instead of drawing half a block.
        /// </param>
        /// <param name="previousFlags">
        /// The sketch-manager flag values from before the session — restored
        /// on every exit path.
        /// </param>
        internal SwSketchInsertion(
            IModelDoc2 doc, ISketchManager mgr, IFeature sketchFeature,
            IModelDocExtension? ext, IMathUtility? math, bool undoRecording,
            SketchManagerFlagSnapshot previousFlags)
        {
            _doc = doc;
            _mgr = mgr;
            _sketchFeature = sketchFeature;
            _ext = ext;
            _math = math;
            _undoRecording = undoRecording;
            _previousFlags = previousFlags;

            // Bulk-insert mode for the session: verified live, the interactive
            // path sporadically refuses CreateLine on coincident endpoints of a
            // dense contour set (QR ~250 segments). AddToDB + suppressed display
            // inserts every validated edge; flags are restored on every exit
            // path and the committed session redraws. Verified: all 252 segments
            // of the fixed QR insert cleanly this way.
            try { _mgr.AddToDB = true; } catch { }
            try { _mgr.DisplayWhenAdded = false; } catch { }
            try { _mgr.AutoInference = false; } catch { }
        }

        public string SketchName => _sketchFeature.Name;
        public int LineCount => _buffered.Count;

        /// <summary>
        /// The sketch feature this session owns — the undo object needs it to
        /// delete (and re-track) exactly this sketch after a redo re-insert.
        /// </summary>
        internal IFeature Feature => _sketchFeature;

        /// <summary>
        /// Stages one contour edge at its final size in sketch-space metres.
        /// Nothing is drawn yet — the session buffers the staged edges and
        /// realizes them at <see cref="KeepOpen"/>: the QR lands as ONE
        /// sketch block instance whose definition is drawn unit-proportioned
        /// (1 × 1 document unit) and whose Scale reads the requested size in
        /// document units — the PropertyManager scale field IS the QR's
        /// resize handle (spec #10).
        /// </summary>
        public void AddLine(double x1, double y1, double x2, double y2)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            _buffered.Add(new SketchEdge(new SketchPoint2(x1, y1), new SketchPoint2(x2, y2)));
        }

        /// <summary>
        /// Stages the model's construction squares — the QR square and the
        /// keep-out square in sketch-space metres at the requested size —
        /// for commit-time emission INSIDE the block definition (ADR 0003).
        /// Nothing is drawn yet: <see cref="KeepOpen"/> normalizes them into
        /// the same definition as the contours, flagged construction, BEFORE
        /// the selection fold that forms the block — the squares then scale
        /// and move with the instance with no host-side constraints,
        /// dimensions, or equations.
        /// </summary>
        public void AddConstructionSquares(IReadOnlyList<QrConstructionSquare> squares)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            if (squares != null)
                _stagedSquares.AddRange(squares);
        }

        public void KeepOpen()
        {
            if (_buffered.Count > 0)
                RealizeAsBlockInstance();
            _committed = true;
            // The whole creation was meant to become one named undo step.
            // Measured live (SW 2023 SP5, rev 31.5.0): FinishRecordingUndoObject2
            // returns false in EVERY arrangement tried — recording opened
            // before or after InsertSketch, finished while the sketch is open
            // or after exiting edit mode. AddToDB-mode bulk insertion (which
            // the insertion needs to accept the contour set at all) bypasses
            // the undo system, so no named step ever exists and one EditUndo
            // cannot revert the creation — documented as a limitation in
            // docs/ACCEPTANCE.md. The attempt stays: a host that CAN record
            // still gets the named step, and failure only logs.
            TryFinishUndo();
        }

        /// <summary>
        /// The staged-geometry → block-instance realization (spec #10), run
        /// inside the open sketch edit:
        ///   1. the instance Scale IS the requested size — the span of the
        ///      staged contour edges and the staged QR square (the grown
        ///      outline itself), expressed in the document's own length
        ///      unit;
        ///   2. the definition is drawn unit-proportioned — every staged
        ///      contour edge ÷ the scale, so the QR content lands inside a
        ///      definition spanning exactly one document unit;
        ///   3. the staged construction squares are drawn through the same
        ///      ÷scale normalization and flagged construction — they ride
        ///      INTO the block definition (ADR 0003), so the QR footprint
        ///      and the four-cell quiet-zone boundary scale and move with
        ///      the instance, and construction geometry never feeds a
        ///      profile;
        ///   4. the by-object selection converts ONLY those drawn lines —
        ///      contours and squares alike — into the definition and its
        ///      first instance (scale 1 at the origin);
        ///   5. the instance's Scale is set to the document-unit size — the
        ///      PropertyManager field then reads as the QR size.
        /// Live-verified (consexp, docs/BLOCK-GATES.md round 4): construction
        /// segments inside a block definition render on the instance, track
        /// Scale edits and drags, keep the construction flag through explode,
        /// and never add a sketch region.
        /// Any failure surfaces as a <see cref="SketchInsertionException"/>
        /// so the command's single cleanup path owns the partial sketch.
        /// </summary>
        private void RealizeAsBlockInstance()
        {
            // 1. The instance Scale IS the requested size: the staged span
            //    (contour edges + the QR square, the grown outline itself)
            //    in the document's own length unit — a host fact read once,
            //    since the normalization below needs it too.
            var docUnit = DocLengthUnits.Of(_doc);
            double scaleDocUnits = StagedSpan() / docUnit.MetresPerUnit;
            if (scaleDocUnits <= 0.0)
                throw new SketchInsertionException(
                    "The staged QR outline spans zero — no block definition can be formed.");

            // 2. Draw the definition: staged edges ÷ scale = unit-proportioned
            //    (one document unit across). These are ordinary sketch lines
            //    until the block-make step swallows them into the block.
            var drawn = new List<ISketchSegment>(_buffered.Count + 8);
            for (var i = 0; i < _buffered.Count; i++)
            {
                var e = _buffered[i];
                drawn.Add(DrawDefEdge(
                    e.Start.X / scaleDocUnits, e.Start.Y / scaleDocUnits,
                    e.End.X / scaleDocUnits, e.End.Y / scaleDocUnits,
                    "block definition", $"line {i + 1} of {_buffered.Count}"));
            }

            // 3. The staged construction squares — same ÷scale normalization
            //    as the contours, then construction-flagged, so they land in
            //    the block's selection fold below and become definition
            //    content that tracks the instance's transforms.
            var squareIndex = 0;
            foreach (var square in _stagedSquares)
            {
                string label = square.Role == QrSquareRole.QrSquare ? "QR square" : "keep-out square";
                foreach (var e in square.Edges)
                {
                    squareIndex++;
                    var segment = DrawDefEdge(
                        e.Start.X / scaleDocUnits, e.Start.Y / scaleDocUnits,
                        e.End.X / scaleDocUnits, e.End.Y / scaleDocUnits,
                        label, $"edge {squareIndex}");
                    // A swallowed flag failure would land the square as SOLID
                    // definition geometry — profile content that feeds
                    // extrudes. Propagate honestly: the abort path owns it.
                    try { segment.ConstructionGeometry = true; }
                    catch (Exception ex)
                    {
                        throw new SketchInsertionException(
                            $"The construction-geometry flag could not be applied to the " +
                            $"{label} (edge {squareIndex}): {ex.Message}", ex);
                    }
                    drawn.Add(segment);
                }
            }

            if (_ext == null || _math == null)
                throw new SketchInsertionException(
                    "The document exposed no extension/math interface — the QR block could not be formed.");

            // 4. Select exactly what this session drew — BY OBJECT, not
            //    SelectAll. Measured live (undo-callback reinsert, round-1
            //    blockqr gate): inside the API undo object's redo the blanket
            //    select-all delivered an empty selection and
            //    MakeSketchBlockFromSelected formed no definition. Selecting
            //    each drawn segment via Select4 is exact in every context —
            //    and the fold takes the construction squares too, which is
            //    the point: they become definition geometry, not host lines.
            ISketchBlockDefinition? def;
            try
            {
                _doc.ClearSelection2(true);
                var selected = 0;
                foreach (var segment in drawn)
                {
                    try { if (segment.Select4(true, null)) selected++; }
                    catch { /* one refused segment is not fatal — counted below */ }
                }
                if (selected < drawn.Count)
                    throw new SketchInsertionException(
                        $"Only {selected} of {drawn.Count} block-definition segments could be " +
                        "selected — the block make would be partial or empty.");
                var origin = _math.CreatePoint(new[] { 0.0, 0.0, 0.0 }) as MathPoint
                    ?? throw new SketchInsertionException(
                        "The math utility returned no insertion point.");
                def = _mgr.MakeSketchBlockFromSelected(origin) as ISketchBlockDefinition;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"MakeSketchBlockFromSelected failed: {ex.Message}", ex);
            }
            if (def == null)
                throw new SketchInsertionException(
                    "MakeSketchBlockFromSelected formed no block definition.");

            // 5. The make leaves instance #1 in the sketch at scale 1 — set
            //    its Scale to the document-unit size so the PropertyManager
            //    field reads as the QR size (verified settable live, spike Q2).
            try
            {
                var sketch = _sketchFeature as ISketch
                    ?? throw new SketchInsertionException(
                        "The sketch feature did not expose its sketch interface.");
                if (sketch.GetSketchBlockInstanceCount() < 1)
                    throw new SketchInsertionException(
                        "The block definition formed but placed no instance in the sketch.");
                var instance = sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance
                    ?? throw new SketchInsertionException(
                        "The block instance could not be read back.");
                instance.Scale = scaleDocUnits;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The block instance's Scale could not be applied: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// One CreateLine + error wrap for the definition draw — shared by
        /// the contour loop and the square loop so both normalize the same
        /// way (÷ the instance scale) and fail the same way.
        /// </summary>
        private ISketchSegment DrawDefEdge(
            double x1, double y1, double x2, double y2, string what, string which)
        {
            try
            {
                var segment = _mgr.CreateLine(x1, y1, 0.0, x2, y2, 0.0) as ISketchSegment;
                if (segment == null)
                    throw new SketchInsertionException(
                        $"CreateLine returned no segment while drawing the {what} ({which}).");
                return segment;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"CreateLine failed while drawing the {what} ({which}): {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The staged outline span (max of the x/y extents), metres — the
        /// buffered contour edges PLUS the staged QR square, which IS the
        /// grown outline. With it staged the span is the requested size even
        /// when the dark cells never reach the outline — the block Scale
        /// stays authoritative for any matrix. The keep-out square is not
        /// extent evidence: it deliberately spans the wider working-matrix
        /// boundary.
        /// </summary>
        private double StagedSpan()
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var e in _buffered)
                Include(e);
            foreach (var square in _stagedSquares)
                if (square.Role == QrSquareRole.QrSquare)
                    foreach (var e in square.Edges)
                        Include(e);
            return Math.Max(maxX - minX, maxY - minY);

            void Include(SketchEdge e)
            {
                minX = Math.Min(minX, Math.Min(e.Start.X, e.End.X));
                maxX = Math.Max(maxX, Math.Max(e.Start.X, e.End.X));
                minY = Math.Min(minY, Math.Min(e.Start.Y, e.End.Y));
                maxY = Math.Max(maxY, Math.Max(e.Start.Y, e.End.Y));
            }
        }

        public CleanupOutcome Abort()
        {
            if (_committed)
                return CleanupOutcome.Cleaned;   // a committed sketch is never removed
            if (_abortOutcome != null)
                return _abortOutcome;            // report the REAL outcome, honestly
            _aborted = true;
            _abortOutcome = AbortOnce();
            return _abortOutcome;
        }

        private CleanupOutcome AbortOnce()
        {
            try
            {
                // Exit edit mode, then delete ONLY this session's sketch
                // feature — the same stages the begin-time abandon path runs.
                // Never a blind Undo — prior user work must survive.
                var exitError = OwnedSketchCleanup.ExitSketchEditMode(_mgr);
                if (exitError != null)
                    return CleanupOutcome.Failed(
                        $"could not exit sketch edit mode: {exitError}");

                var deleteError = OwnedSketchCleanup.DeleteSketchFeature(_doc, _sketchFeature);
                return deleteError == null
                    ? CleanupOutcome.Cleaned
                    : CleanupOutcome.Failed(deleteError);
            }
            finally
            {
                RestoreFlags();
                TryFinishUndo();
            }
        }

        public void Dispose()
        {
            if (_committed)
            {
                RestoreFlags();
                try { _doc.GraphicsRedraw2(); } catch { /* redraw is best-effort */ }
                return;
            }
            if (!_aborted)
            {
                try { Abort(); }
                catch (Exception ex) { Diag.Log($"dispose-time abort failed: {ex.Message}"); }
            }
        }

        private void RestoreFlags()
        {
            if (_flagsRestored) return;
            _flagsRestored = true;
            _previousFlags.Restore(_mgr);
        }

        /// <summary>
        /// Attempts to close the undo recording opened in BeginSketchInsertion,
        /// naming the step "Create QR Sketch". Returns true when the host
        /// accepted the finish. Best-effort: a host that refuses still leaves
        /// correct geometry.
        /// </summary>
        private bool TryFinishUndo()
        {
            if (_undoFinished || !_undoRecording || _ext == null) return false;
            try
            {
                if (_ext.FinishRecordingUndoObject2(SketchHostContract.CreationUndoName, false))
                {
                    _undoFinished = true;
                    return true;
                }
                Diag.Log("FinishRecordingUndoObject2 returned false");
            }
            catch (Exception ex)
            {
                Diag.Log($"FinishRecordingUndoObject2 failed: {ex.Message}");
            }
            return false;
        }
    }
}
