using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

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
        private readonly List<(double X1, double Y1, double X2, double Y2)> _buffered =
            new List<(double, double, double, double)>();
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
        /// (1 × 1 document unit) and whose Scale reads the staged span in
        /// document units — the PropertyManager scale field IS the QR's
        /// resize handle (spec #10).
        /// </summary>
        public void AddLine(double x1, double y1, double x2, double y2)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            _buffered.Add((x1, y1, x2, y2));
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
        /// The buffered-edge → block-instance realization (spec #10), run
        /// inside the open sketch edit:
        ///   1. the staged span IS the requested size — the instance Scale
        ///      is that span expressed in the document's own length unit;
        ///   2. the definition is drawn unit-proportioned — every staged edge
        ///      ÷ the scale, so the def spans exactly one document unit;
        ///   3. ClearSelection + SelectAll converts ONLY those drawn lines
        ///      into the definition and its first instance (scale 1 at the
        ///      origin) — construction squares the command emits afterwards
        ///      must therefore be drawn AFTER this step, never before it;
        ///   4. the instance's Scale is set to the document-unit size — the
        ///      PropertyManager field then reads as the QR size.
        /// Any failure surfaces as a <see cref="SketchInsertionException"/>
        /// so the command's single cleanup path owns the partial sketch.
        /// </summary>
        private void RealizeAsBlockInstance()
        {
            double scaleDocUnits = BufferedSpan() / DocLengthUnits.Of(_doc).MetresPerUnit;
            if (scaleDocUnits <= 0.0)
                throw new SketchInsertionException(
                    "The staged QR contours span zero — no block definition can be formed.");

            // 1. Draw the definition: staged edges ÷ scale = unit-proportioned
            //    (one document unit across). These are ordinary sketch lines
            //    until the select-all step swallows them into the block.
            for (var i = 0; i < _buffered.Count; i++)
            {
                var e = _buffered[i];
                try
                {
                    var segment = _mgr.CreateLine(
                        e.X1 / scaleDocUnits, e.Y1 / scaleDocUnits, 0.0,
                        e.X2 / scaleDocUnits, e.Y2 / scaleDocUnits, 0.0) as ISketchSegment;
                    if (segment == null)
                        throw new SketchInsertionException(
                            $"CreateLine returned no segment while drawing the block definition " +
                            $"(line {i + 1} of {_buffered.Count}).");
                }
                catch (SketchInsertionException) { throw; }
                catch (Exception ex)
                {
                    throw new SketchInsertionException(
                        $"CreateLine failed while drawing the block definition " +
                        $"(line {i + 1} of {_buffered.Count}): {ex.Message}", ex);
                }
            }

            if (_ext == null || _math == null)
                throw new SketchInsertionException(
                    "The document exposed no extension/math interface — the QR block could not be formed.");

            // 2. Select exactly what this session drew — the sketch was born
            //    empty, so SelectAll can only reach the definition lines —
            //    then fold the selection into the block definition + instance.
            ISketchBlockDefinition? def;
            try
            {
                _doc.ClearSelection2(true);
                _ext.SelectAll();
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

            // 3. The make leaves instance #1 in the sketch at scale 1 — set
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
