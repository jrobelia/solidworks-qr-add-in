using System;
using System.Runtime.InteropServices;
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

            ISketch active;
            try
            {
                mgr.InsertSketch(true); // enters edit mode on the selected plane
                active = mgr.ActiveSketch as ISketch;
                if (active == null)
                {
                    try { mgr.InsertSketch(false); } catch { /* leave as found */ }
                    throw new SketchHostException(
                        "SolidWorks entered sketch edit mode without an active sketch.");
                }
            }
            catch (SketchHostException) { throw; }
            catch (Exception ex)
            {
                throw new SketchHostException(
                    "SolidWorks could not start a sketch on the selected plane.", ex);
            }

            // ISketch is also the sketch's IFeature — this is how we track and
            // delete ONLY the sketch this session created.
            var sketchFeature = active as IFeature
                ?? throw new SketchHostException("The active sketch did not expose a feature.");
            return new SwSketchInsertion(
                doc, mgr, sketchFeature,
                prevAddToDB, prevDisplayWhenAdded, prevAutoInference, prevInferenceMode);
        }
    }
}
