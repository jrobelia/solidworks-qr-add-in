using System;
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
    /// closes it once — named "Create QR Sketch" — on commit (leaving one
    /// undo step for the whole creation) or on abort (a step that reverts to
    /// no net change). The scope is a naming aid, never a rollback mechanism.
    /// </summary>
    public sealed class SwSketchInsertion : ISketchInsertion
    {
        private readonly IModelDoc2 _doc;
        private readonly ISketchManager _mgr;
        private readonly IFeature _sketchFeature;
        private readonly IModelDocExtension? _ext;
        private readonly bool _undoRecording;
        private readonly SketchManagerFlagSnapshot _previousFlags;
        private bool _committed;
        private bool _aborted;
        private bool _flagsRestored;
        private bool _undoFinished;
        private CleanupOutcome? _abortOutcome;
        private int _lineCount;

        /// <param name="previousFlags">
        /// The sketch-manager flag values from before the session — restored
        /// on every exit path.
        /// </param>
        internal SwSketchInsertion(
            IModelDoc2 doc, ISketchManager mgr, IFeature sketchFeature,
            IModelDocExtension? ext, bool undoRecording,
            SketchManagerFlagSnapshot previousFlags)
        {
            _doc = doc;
            _mgr = mgr;
            _sketchFeature = sketchFeature;
            _ext = ext;
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
        public int LineCount => _lineCount;

        public void AddLine(double x1, double y1, double x2, double y2)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            try
            {
                var segment = _mgr.CreateLine(x1, y1, 0.0, x2, y2, 0.0) as ISketchSegment;
                if (segment == null)
                    throw new SketchInsertionException(
                        $"CreateLine returned no segment for line {_lineCount + 1}.");
                _lineCount++;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"CreateLine failed for line {_lineCount + 1}: {ex.Message}", ex);
            }
        }

        public void KeepOpen()
        {
            _committed = true;
            // The whole creation becomes one named undo step — even though
            // the sketch stays open in edit mode. Behaviour verified live;
            // a host that can't record undos still leaves a correct sketch.
            FinishUndoOnce();
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
                FinishUndoOnce();
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
        /// Closes the undo recording opened in BeginSketchInsertion, exactly
        /// once, naming the step "Create QR Sketch". Best-effort: a host that
        /// refuses still leaves correct geometry.
        /// </summary>
        private void FinishUndoOnce()
        {
            if (_undoFinished || !_undoRecording || _ext == null) return;
            _undoFinished = true;
            try
            {
                if (!_ext.FinishRecordingUndoObject2(SketchHostContract.CreationUndoName, false))
                    Diag.Log("FinishRecordingUndoObject2 returned false");
            }
            catch (Exception ex)
            {
                Diag.Log($"FinishRecordingUndoObject2 failed: {ex.Message}");
            }
        }
    }
}
