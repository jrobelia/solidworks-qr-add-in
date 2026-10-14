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
        /// (select vs delete vs an exception). The sketch's equation entries
        /// are swept first — SolidWorks refuses to delete a sketch whose
        /// dims still feed the equation manager (ticket-#19 finding), and
        /// the leftovers become the orphans that break the NEXT circle
        /// create's equation install (ticket #42, SketchEquationHygiene).
        /// </summary>
        public static string? DeleteSketchFeature(IModelDoc2 doc, IFeature sketchFeature)
        {
            var name = sketchFeature.Name;
            try
            {
                SketchEquationHygiene.PurgeEquationsReferencing(doc, name);
                doc.ClearSelection2(true);
                var ext = doc.Extension as IModelDocExtension
                    ?? throw new SketchHostException("The document exposed no extension interface.");
                bool selected = ext.SelectByID2(
                    name, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                if (!selected)
                    return $"could not select sketch feature '{name}' for deletion";
                if (!ext.DeleteSelection2(0))
                {
                    // A sketch consumed by later features (e.g. an extrude
                    // built from it) refuses the plain delete — the children
                    // cannot outlive their profile, so the undo path deletes
                    // them with it (the same subtree removal an interactive
                    // "delete children? yes" performs).
                    Diag.Log($"plain delete of '{name}' refused — retrying with children");
                    doc.ClearSelection2(true);
                    selected = ext.SelectByID2(
                        name, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                    if (!selected ||
                        !ext.DeleteSelection2(
                            (int)(swDeleteSelectionOptions_e.swDelete_Children |
                                  swDeleteSelectionOptions_e.swDelete_Absorbed)))
                        return $"DeleteSelection2 failed for '{name}'";
                }
                // The delete just orphaned whatever the pre-sweep missed —
                // drop those before the next create's equation install
                // refuses on them.
                SketchEquationHygiene.PurgeOrphanedEquations(doc);
                return null;
            }
            catch (Exception ex)
            {
                return $"deleting '{name}' failed: {ex.Message}";
            }
        }
    }
}
