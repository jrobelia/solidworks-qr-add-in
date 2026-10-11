using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// Keeps registered undo handlers alive: the add-in must hold a managed
    /// reference for as long as SolidWorks can call back into the CCW, and
    /// must be able to release every document reference on unload. A
    /// handler removes itself when SolidWorks fires UndoReSet (undo-list
    /// reset, document close); DisconnectFromSW calls
    /// <see cref="ReleaseAll"/> as the belt-and-braces path.
    /// </summary>
    internal static class QrSketchUndoRegistry
    {
        private static readonly object Gate = new object();
        private static readonly List<QrSketchUndoObject> Live = new List<QrSketchUndoObject>();

        public static void Keep(QrSketchUndoObject handler)
        {
            lock (Gate) Live.Add(handler);
        }

        public static void Drop(QrSketchUndoObject handler)
        {
            lock (Gate) Live.Remove(handler);
        }

        /// <summary>Unload path: make every live handler a safe no-op.</summary>
        public static void ReleaseAll()
        {
            QrSketchUndoObject[] snapshot;
            lock (Gate) snapshot = Live.ToArray();
            foreach (var handler in snapshot)
            {
                try { handler.UndoReSet(); }
                catch { /* unloading must never throw back into SolidWorks */ }
            }
        }
    }

    /// <summary>
    /// The custom undo object handed to
    /// <see cref="IModelDocExtension.SetApiUndoObject"/> after a successful
    /// QR creation — AddToDB insertion bypasses native undo recording, so
    /// this object IS the "Create QR Sketch" entry on the document's undo
    /// list.
    ///
    /// Semantics (verified against the SW API docs + shipped VBA example):
    ///   - <see cref="DoUndo"/> fires when the user undoes (and, per the
    ///     single-callback design of this API, on redo — the handler is a
    ///     toggle: undo deletes exactly the created sketch, redo re-inserts
    ///     the captured geometry on the captured support).
    ///   - <see cref="UndoReSet"/> fires when the undo list resets (document
    ///     close, ClearUndoList): drop every COM reference and become a
    ///     permanent no-op.
    ///
    /// Safety invariants: undo deletes a feature only when ITS OWN feature
    /// ID (unique per document, never reused) is still in the tree — a user
    /// sketch that reuses the name survives, an already-deleted sketch is a
    /// no-op; nothing ever throws back across the COM boundary.
    /// </summary>
    /// <remarks>
    /// Implements BOTH published undo interfaces: SetApiUndoObject's PHandler
    /// marshals as an interface pointer whose cast targets the SwUndoAPIHandler
    /// coclass interface — a class exposing only ISwUndoAPIHandler fails the
    /// marshal with InvalidCastException (observed live on SW 2023 SP5).
    /// </remarks>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class QrSketchUndoObject : ISwUndoAPIHandler, SwUndoAPIHandler
    {
        private IModelDoc2? _doc;
        private IFeature? _sketch;        // the live sketch feature; null while undone
        private int _sketchFeatureId;
        private string _undoName = "";
        private object? _support;         // IFeature (datum plane) or IEntity (face)
        private double[]? _edges;         // flat x1,y1,x2,y2, sketch-space metres
        private IMathUtility? _math;      // block insertion points for the redo
        private bool _undone;
        private bool _released;

        /// <summary>Native-only initialisation; never called across COM.</summary>
        internal void Initialize(
            IModelDoc2 doc, IFeature sketch, object? support,
            string undoName, IReadOnlyList<SketchEdge> edges, IMathUtility? math)
        {
            _doc = doc;
            _sketch = sketch;
            _undoName = undoName;
            _math = math;
            try { _sketchFeatureId = sketch.GetID(); }
            catch { _sketchFeatureId = 0; }
            _support = support;
            var flat = new double[edges.Count * 4];
            for (var i = 0; i < edges.Count; i++)
            {
                flat[i * 4 + 0] = edges[i].Start.X;
                flat[i * 4 + 1] = edges[i].Start.Y;
                flat[i * 4 + 2] = edges[i].End.X;
                flat[i * 4 + 3] = edges[i].End.Y;
            }
            _edges = flat;
        }

        /// <summary>SolidWorks undo/redo callback — must never throw back.</summary>
        public void DoUndo()
        {
            Diag.Log($"QR undo: DoUndo called (undone={_undone} released={_released})");
            try
            {
                if (_released || _doc == null) return;
                if (!_undone)
                    DeleteCreatedSketch();
                else
                    RecreateSketch();
            }
            catch (Exception ex)
            {
                Diag.Log($"QR undo step failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Undo-list reset — release everything, become a no-op.</summary>
        public void UndoReSet()
        {
            try
            {
                _released = true;
                _doc = null;
                _sketch = null;
                _support = null;
                _edges = null;
                _math = null;
                QrSketchUndoRegistry.Drop(this);
            }
            catch { /* a reset callback must never throw back into SolidWorks */ }
        }

        /// <summary>
        /// The undo half of the toggle: exit any open sketch edit session,
        /// then delete exactly the feature this step owns — verified by its
        /// never-reused feature ID, not by name. A sketch the user already
        /// deleted, or a user feature that merely reuses our name, is a
        /// safe no-op.
        /// </summary>
        private void DeleteCreatedSketch()
        {
            var doc = _doc!;
            try
            {
                var mgr = doc.SketchManager as ISketchManager;
                if (mgr?.ActiveSketch != null)
                    mgr.InsertSketch(false);
            }
            catch { /* exiting edit mode is best-effort; the delete verifies next */ }

            var live = _sketchFeatureId != 0 ? FindFeatureById(doc, _sketchFeatureId) : null;
            if (live == null)
            {
                // Already gone (user deleted it) or untraceable — never
                // delete anything else in its place.
                Diag.Log($"QR undo: feature id {_sketchFeatureId} not in tree — no-op");
                _sketch = null;
                _undone = true;
                return;
            }

            var error = OwnedSketchCleanup.DeleteSketchFeature(doc, live);
            Diag.Log(error != null
                ? $"QR undo delete failed: {error}"
                : "QR undo: created sketch deleted");
            _sketch = null;
            _undone = true;
            // Verified live: SolidWorks never invokes the handler on
            // EditRedo — an API undo item is undo-only. Re-registering the
            // SAME handler puts a fresh step on the undo list, so the next
            // undo press calls DoUndo again and the toggle reinserts the
            // sketch — the only restore path this API offers. The reinsert
            // branch deliberately does NOT re-register: a permanent toggle
            // parked on top of the undo list would make prior user work
            // unreachable by Ctrl+Z.
            if (error == null)
                ReRegister();
        }

        /// <summary>Re-adds this handler to the document's undo list. Never throws.</summary>
        private void ReRegister()
        {
            try
            {
                var ext = _doc?.Extension as IModelDocExtension;
                if (ext != null && ext.SetApiUndoObject(this, _undoName))
                    QrSketchUndoRegistry.Keep(this);
                else
                    Diag.Log("QR undo: re-register returned false");
            }
            catch (Exception ex)
            {
                Diag.Log($"QR undo re-register failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The redo half of the toggle: reselect the captured support and
        /// re-run the SAME insertion pipeline (AddToDB, flag snapshot/restore)
        /// the original creation used. A support that can no longer be
        /// selected, or an open sketch edit session, makes redo a no-op —
        /// never a partial reinsertion into someone else's state.
        /// </summary>
        private void RecreateSketch()
        {
            Diag.Log("QR redo: reinsert requested");
            var doc = _doc!;
            var support = _support;
            var edges = _edges;
            if (support == null || edges == null)
            {
                Diag.Log("QR redo: no support/geometry captured — nothing to reinsert");
                return;
            }
            var mgr = doc.SketchManager as ISketchManager;
            if (mgr == null || mgr.ActiveSketch != null)
            {
                Diag.Log("QR redo: a sketch is already open — reinsert skipped");
                return;
            }

            bool selected;
            try
            {
                selected = support is IEntity entity ? entity.Select4(false, null)
                    : support is IFeature feature ? feature.Select2(false, 0)
                    : false;
            }
            catch { selected = false; }
            if (!selected)
            {
                Diag.Log("QR redo: the original support could not be reselected");
                return;
            }

            SwSketchInsertion insertion;
            try
            {
                insertion = SolidWorksSketchHost.BeginOnSelectedSupport(
                    doc, recordUndo: false, _math, capturedSupport: out _);
            }
            catch (Exception ex)
            {
                Diag.Log($"QR redo could not begin a sketch: {ex.Message}");
                return;
            }

            try
            {
                for (var i = 0; i + 3 < edges.Length; i += 4)
                    insertion.AddLine(edges[i], edges[i + 1], edges[i + 2], edges[i + 3]);
                insertion.KeepOpen();
                _sketch = insertion.Feature;
                try { _sketchFeatureId = _sketch.GetID(); }
                catch { _sketchFeatureId = 0; }
                _undone = false;
            }
            finally
            {
                try { insertion.Dispose(); }
                catch { /* a failed teardown leaves the outcome as the sketch tree shows it */ }
            }
        }

        /// <summary>Finds a feature by its document-unique, never-reused ID.</summary>
        private static IFeature? FindFeatureById(IModelDoc2 doc, int featureId)
        {
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                try
                {
                    if (f.GetID() == featureId)
                        return f;
                }
                catch { /* a feature that cannot report its ID cannot be ours */ }
            }
            return null;
        }
    }
}
