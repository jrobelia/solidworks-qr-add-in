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
        /// (select vs delete vs an exception). Equation handling is
        /// refusal-driven: the entries keyed to this sketch are LIVE
        /// references while it survives, so the ticket-#19 sweep that
        /// unblocks the delete runs only on a proven refusal — and a delete
        /// that STILL refuses restores them. Every successful delete ends
        /// in the orphan sweep, per the SketchEquationHygiene contract.
        /// </summary>
        public static string? DeleteSketchFeature(IModelDoc2 doc, IFeature sketchFeature)
        {
            var name = sketchFeature.Name;
            try
            {
                var ext = doc.Extension as IModelDocExtension
                    ?? throw new SketchHostException("The document exposed no extension interface.");

                // The refusal-driven ordering is pinned headless in
                // OwnedSketchDeletePlan: plain delete, then children, and
                // only a proven refusal sweeps the equations keyed to this
                // feature — they are LIVE references while it survives.
                // A delete that STILL refuses after the purge restores them.
                int childrenFlags =
                    (int)(swDeleteSelectionOptions_e.swDelete_Children |
                          swDeleteSelectionOptions_e.swDelete_Absorbed);
                bool firstRefusalLogged = false;
                var result = OwnedSketchDeletePlan.Run(
                    select: () => SelectSketch(doc, ext, name),
                    deletePlain: () => ext.DeleteSelection2(0),
                    deleteWithChildren: () =>
                    {
                        if (!firstRefusalLogged)
                        {
                            // A sketch consumed by later features (an
                            // extrude built from it) refuses the plain
                            // delete — the same subtree removal an
                            // interactive "delete children? yes" performs.
                            Diag.Log($"plain delete of '{name}' refused — " +
                                "retrying with children");
                            firstRefusalLogged = true;
                        }
                        return ext.DeleteSelection2(childrenFlags);
                    },
                    purgeReferences: () =>
                    {
                        var purged =
                            SketchEquationHygiene.PurgeEquationsReferencing(doc, name);
                        Diag.Log($"removed {purged.Count} equation(s) referencing " +
                            $"'{name}' — retrying delete");
                        return purged;
                    },
                    restore: texts =>
                        SketchEquationHygiene.RestoreEquations(doc, texts),
                    sweepOrphans: () =>
                        SketchEquationHygiene.PurgeOrphanedEquations(doc));

                switch (result.Outcome)
                {
                    case OwnedSketchDeleteOutcome.Deleted:
                        return null;
                    case OwnedSketchDeleteOutcome.SelectFailed:
                        return $"could not select sketch feature '{name}' for deletion";
                    case OwnedSketchDeleteOutcome.DeleteRefusedAfterPurge:
                        return $"DeleteSelection2 failed for '{name}' — its " +
                            $"{result.Purged} equation reference(s) were " +
                            $"removed first; {result.Restored} restored";
                    default:
                        return $"DeleteSelection2 failed for '{name}'";
                }
            }
            catch (Exception ex)
            {
                return $"deleting '{name}' failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Clears the selection and selects exactly this sketch feature —
        /// the delete step reads the current selection.
        /// </summary>
        private static bool SelectSketch(
            IModelDoc2 doc, IModelDocExtension ext, string name)
        {
            try
            {
                doc.ClearSelection2(true);
                return ext.SelectByID2(
                    name, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
            }
            catch { return false; }
        }
    }
}
