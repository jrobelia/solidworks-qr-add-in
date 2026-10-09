using System;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// Native adapter: ISketchHost over the live SolidWorks session.
    /// All SolidWorks types stay inside this assembly — commands see only
    /// the seam's plain state and geometry types.
    /// </summary>
    public sealed class SolidWorksSketchHost : ISketchHost
    {
        private readonly ISldWorks _sw;

        public SolidWorksSketchHost(ISldWorks sw)
        {
            _sw = sw ?? throw new ArgumentNullException(nameof(sw));
        }

        public SketchHostState CaptureState()
        {
            var doc = _sw.IActiveDoc2 as IModelDoc2;
            if (doc == null)
                return new SketchHostState(HostDocumentKind.None, SupportSelection.None, false);

            var kind = doc.GetType() switch
            {
                (int)swDocumentTypes_e.swDocPART => HostDocumentKind.Part,
                (int)swDocumentTypes_e.swDocASSEMBLY => HostDocumentKind.Assembly,
                (int)swDocumentTypes_e.swDocDRAWING => HostDocumentKind.Drawing,
                _ => HostDocumentKind.Unknown,
            };

            var mgr = doc.SketchManager as ISketchManager;
            var editing = mgr?.ActiveSketch != null;

            return new SketchHostState(kind, ReadSelection(doc), editing);
        }

        /// <summary>
        /// Classifies the current selection in the terms the command needs.
        /// A supported sketch support is exactly one reference plane
        /// (swSelDATUMPLANES — built-in or user-created, any orientation) or
        /// exactly one planar face (swSelFACES whose surface IsPlane()). A
        /// face that fails the planarity check, an unsupported entity kind,
        /// and any multi-selection each get their own classification so the
        /// command's error can name the actual problem.
        /// </summary>
        private static SupportSelection ReadSelection(IModelDoc2 doc)
        {
            var selMgr = doc.ISelectionManager as ISelectionMgr;
            if (selMgr == null)
                return SupportSelection.None;
            int count = selMgr.GetSelectedObjectCount2(-1);
            if (count == 0)
                return SupportSelection.None;
            if (count > 1)
                return SupportSelection.Ambiguous;
            switch ((swSelectType_e)selMgr.GetSelectedObjectType3(1, -1))
            {
                case swSelectType_e.swSelDATUMPLANES:
                    return SupportSelection.ReferencePlane;
                case swSelectType_e.swSelFACES:
                    return IsSelectedFacePlanar(selMgr)
                        ? SupportSelection.PlanarFace
                        : SupportSelection.NonPlanarFace;
                default:
                    return SupportSelection.Other;
            }
        }

        /// <summary>
        /// Planarity of the single selected face, via its surface. Any failure
        /// to inspect the face counts as non-planar — an unsupported selection
        /// must produce an error, never a sketch.
        /// </summary>
        private static bool IsSelectedFacePlanar(ISelectionMgr selMgr)
        {
            try
            {
                var face = selMgr.GetSelectedObject6(1, -1) as IFace2;
                var surface = face?.GetSurface() as ISurface;
                return surface?.IsPlane() == true;
            }
            catch
            {
                return false;
            }
        }

        public ISketchInsertion BeginSketchInsertion()
        {
            var doc = _sw.IActiveDoc2 as IModelDoc2
                ?? throw new SketchHostException("No active document.");
            var mgr = doc.SketchManager as ISketchManager
                ?? throw new SketchHostException("SolidWorks did not expose a sketch manager.");

            // The selection could have changed since CaptureState — re-verify so
            // a stale selection can never steer insertion onto a wrong support.
            var selection = ReadSelection(doc);
            if (selection != SupportSelection.ReferencePlane &&
                selection != SupportSelection.PlanarFace)
                throw new SketchHostException(
                    "The selection changed; exactly one sketch support " +
                    "(a reference plane or a planar face) must be selected.");

            // Capture the sketch-manager flags the session may touch so they can
            // be restored exactly, on success and on failure.
            bool prevAddToDB = mgr.AddToDB;
            bool prevDisplayWhenAdded = mgr.DisplayWhenAdded;
            bool prevAutoInference = mgr.AutoInference;
            bool prevInferenceMode = mgr.InferenceMode;

            // Group the whole creation into ONE named undo step when the host
            // supports it — undoing it then reverts exactly this operation,
            // never an earlier user action. A recording scope is NOT a
            // rollback mechanism: failure cleanup still tracks and deletes the
            // new sketch itself (spec: never a blind Undo).
            var ext = doc.Extension as IModelDocExtension;
            bool recording = false;
            try
            {
                if (ext != null)
                {
                    ext.StartRecordingUndoObject();
                    recording = true;
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"StartRecordingUndoObject failed: {ex.Message}");
            }

            IFeature? sketchFeature = null;
            try
            {
                mgr.InsertSketch(true); // enters edit mode on the selected support
                var active = mgr.ActiveSketch as ISketch;
                if (active == null)
                    throw new SketchHostException(
                        "SolidWorks entered sketch edit mode without an active sketch.");

                // ISketch is also the sketch's IFeature — this is how we track
                // and delete ONLY the sketch this session created.
                sketchFeature = active as IFeature;
                if (sketchFeature == null)
                    throw new SketchHostException("The active sketch did not expose a feature.");

                try
                {
                    return new SwSketchInsertion(
                        doc, mgr, sketchFeature, ext, recording,
                        prevAddToDB, prevDisplayWhenAdded, prevAutoInference, prevInferenceMode);
                }
                catch (Exception ex)
                {
                    throw new SketchHostException(
                        $"SolidWorks could not start the sketch session: {ex.Message}", ex);
                }
            }
            catch (Exception ex)
            {
                // Anything that failed at/after entering edit mode left the
                // caller with no session handle — the half-entered sketch is
                // ours to clean up here. The recording scope is closed either
                // way so nothing later is swallowed into it.
                string leftover = AbandonNewSketch(doc, mgr, sketchFeature);
                FinishUndoQuietly(ext, recording);
                var why = ex is SketchHostException
                    ? ex.Message
                    : $"SolidWorks could not start a sketch on the selected support: {ex.Message}";
                throw new SketchHostException(why + leftover);
            }
        }

        /// <summary>
        /// Begin-time cleanup: exit the half-entered edit session and delete
        /// only the sketch feature this begin created. Returns "" when the
        /// document is clean, or a "may remain" note appended to the thrown
        /// message — honest reporting, never a claimed rollback.
        /// </summary>
        private static string AbandonNewSketch(IModelDoc2 doc, ISketchManager mgr, IFeature? created)
        {
            var note = "";
            try { mgr.InsertSketch(false); }
            catch (Exception ex)
            {
                note = $" Could not exit sketch edit mode: {ex.Message}.";
            }
            if (created == null)
                return note;
            try
            {
                doc.ClearSelection2(true);
                var ext = doc.Extension as IModelDocExtension;
                bool selected = ext != null && ext.SelectByID2(
                    created.Name, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                bool deleted = selected && ext!.DeleteSelection2(0);
                if (!deleted)
                    note += $" The partial sketch '{created.Name}' could not be removed " +
                            "and may remain in the feature tree.";
            }
            catch (Exception ex)
            {
                note += $" The partial sketch '{created.Name}' could not be removed " +
                        $"({ex.Message}) and may remain in the feature tree.";
            }
            return note;
        }

        /// <summary>Closes the undo scope on the failed-begin path; never throws.</summary>
        private static void FinishUndoQuietly(IModelDocExtension? ext, bool recording)
        {
            if (!recording || ext == null) return;
            try { ext.FinishRecordingUndoObject2(SketchHostContract.CreationUndoName, false); }
            catch (Exception ex) { Diag.Log($"FinishRecordingUndoObject2 (abandoned begin) failed: {ex.Message}"); }
        }
    }
}
