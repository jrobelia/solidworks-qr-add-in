using System;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// The two stages every "remove the sketch WE created" path shares —
    /// the aborted insertion session (<see cref="SwSketchInsertion"/>) and
    /// the failed begin (<see cref="SolidWorksSketchHost"/>) use the same
    /// steps: exit sketch edit mode, then delete ONLY the tracked sketch
    /// feature. Never a blind Undo — prior user work must survive.
    ///
    /// Each stage returns its error detail, or null on success; the two
    /// call sites format it into their own reporting shape (a
    /// <see cref="CleanupOutcome"/> vs a "may remain" note on a thrown
    /// message) instead of sharing the strings.
    /// </summary>
    internal static class OwnedSketchCleanup
    {
        /// <summary>Exit sketch edit mode. Returns null, or the failure detail.</summary>
        public static string? ExitSketchEditMode(ISketchManager mgr)
        {
            try
            {
                mgr.InsertSketch(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Select and delete exactly this session's sketch feature — nothing
        /// else. Returns null on success, or a detail naming the failed stage
        /// (select vs delete vs an exception).
        /// </summary>
        public static string? DeleteSketchFeature(IModelDoc2 doc, IFeature sketchFeature)
        {
            var name = sketchFeature.Name;
            try
            {
                doc.ClearSelection2(true);
                var ext = doc.Extension as IModelDocExtension
                    ?? throw new SketchHostException("The document exposed no extension interface.");
                bool selected = ext.SelectByID2(
                    name, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                if (!selected)
                    return $"could not select sketch feature '{name}' for deletion";
                if (!ext.DeleteSelection2(0))
                    return $"DeleteSelection2 failed for '{name}'";
                return null;
            }
            catch (Exception ex)
            {
                return $"deleting '{name}' failed: {ex.Message}";
            }
        }
    }
}
