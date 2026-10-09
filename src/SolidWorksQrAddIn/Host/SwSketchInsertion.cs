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
    /// </summary>
    public sealed class SwSketchInsertion : ISketchInsertion
    {
        private readonly IModelDoc2 _doc;
        private readonly ISketchManager _mgr;
        private readonly IFeature _sketchFeature;
        private readonly bool _prevAddToDB;
        private readonly bool _prevDisplayWhenAdded;
        private readonly bool _prevAutoInference;
        private readonly bool _prevInferenceMode;
        private bool _committed;
        private bool _aborted;
        private bool _flagsRestored;
        private int _lineCount;

        public SwSketchInsertion(
            IModelDoc2 doc, ISketchManager mgr, IFeature sketchFeature,
            bool prevAddToDB, bool prevDisplayWhenAdded, bool prevAutoInference, bool prevInferenceMode)
        {
            _doc = doc;
            _mgr = mgr;
            _sketchFeature = sketchFeature;
            _prevAddToDB = prevAddToDB;
            _prevDisplayWhenAdded = prevDisplayWhenAdded;
            _prevAutoInference = prevAutoInference;
            _prevInferenceMode = prevInferenceMode;
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
        }

        public CleanupOutcome Abort()
        {
            if (_aborted)
                return CleanupOutcome.Cleaned;
            _aborted = true;
            try
            {
                // Exit edit mode, then delete ONLY this session's sketch feature.
                // Never a blind Undo — prior user work must survive.
                try { _mgr.InsertSketch(false); }
                catch (Exception ex)
                {
                    return CleanupOutcome.Failed(
                        $"could not exit sketch edit mode: {ex.Message}");
                }

                try
                {
                    _doc.ClearSelection2(true);
                    var ext = _doc.Extension as IModelDocExtension
                        ?? throw new SketchHostException("The document exposed no extension interface.");
                    bool selected = ext.SelectByID2(
                        _sketchFeature.Name, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                    if (!selected)
                        return CleanupOutcome.Failed(
                            $"could not select sketch feature '{_sketchFeature.Name}' for deletion");
                    bool deleted = ext.DeleteSelection2(0);
                    if (!deleted)
                        return CleanupOutcome.Failed(
                            $"DeleteSelection2 failed for '{_sketchFeature.Name}'");
                    return CleanupOutcome.Cleaned;
                }
                catch (Exception ex)
                {
                    return CleanupOutcome.Failed($"deleting '{_sketchFeature.Name}' failed: {ex.Message}");
                }
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
                RestoreFlags();
                try { _doc.GraphicsRedraw2(); } catch { /* redraw is best-effort */ }
                return;
            }
            if (!_aborted)
                Abort();
        }

        private void RestoreFlags()
        {
            if (_flagsRestored) return;
            _flagsRestored = true;
            try { _mgr.AddToDB = _prevAddToDB; } catch { }
            try { _mgr.DisplayWhenAdded = _prevDisplayWhenAdded; } catch { }
            try { _mgr.AutoInference = _prevAutoInference; } catch { }
            try { _mgr.InferenceMode = _prevInferenceMode; } catch { }
        }
    }
}
