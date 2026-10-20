using System;
using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>Where a sketch-delete attempt ended.</summary>
    public enum OwnedSketchDeleteOutcome
    {
        /// <summary>The feature is gone; the orphan sweep ran.</summary>
        Deleted,

        /// <summary>The feature could not be selected for deletion.</summary>
        SelectFailed,

        /// <summary>
        /// Delete refused in both forms and there were no equations keyed
        /// to the feature to purge — whatever blocked it is not ours.
        /// </summary>
        DeleteRefused,

        /// <summary>
        /// Delete refused even after the referencing equations were swept
        /// — the removed texts were restored so the surviving sketch keeps
        /// its live references.
        /// </summary>
        DeleteRefusedAfterPurge,
    }

    /// <summary>What a delete attempt did — the outcome plus the counts.</summary>
    public sealed class OwnedSketchDeleteResult
    {
        internal OwnedSketchDeleteResult(
            OwnedSketchDeleteOutcome outcome, int purged, int restored)
        {
            Outcome = outcome;
            Purged = purged;
            Restored = restored;
        }

        public OwnedSketchDeleteOutcome Outcome { get; }

        /// <summary>Equation texts the purge removed (0 unless the purge ran).</summary>
        public int Purged { get; }

        /// <summary>Of the purged texts, how many were restored on a failed retry.</summary>
        public int Restored { get; }
    }

    /// <summary>
    /// The refusal-driven delete ordering the sketch cleanup follows
    /// (spec #13, ticket #42 review): the equations keyed to the sketch are
    /// LIVE references while it survives, so the ticket-#19 sweep that
    /// unblocks the delete runs only on a proven refusal — never first —
    /// and a delete that STILL refuses after the sweep hands the removed
    /// texts back so the surviving sketch keeps them. Every success ends
    /// in the orphan sweep (the deleted feature's leftovers break the next
    /// create's equation install).
    ///
    /// Pure sequencing — the host adapter (OwnedSketchCleanup) supplies the
    /// verbs; the headless tests pin the ordering.
    /// </summary>
    public static class OwnedSketchDeletePlan
    {
        /// <summary>
        /// Runs the delete protocol: select, plain delete; on refusal,
        /// select + delete-with-children; on refusal, purge the equations
        /// keyed to the feature and retry once — restoring them if even
        /// that fails. <paramref name="sweepOrphans"/> runs after every
        /// successful delete.
        /// </summary>
        /// <param name="select">Clear + select the feature; false = the
        /// selection failed.</param>
        /// <param name="deletePlain">DeleteSelection on the plain
        /// selection.</param>
        /// <param name="deleteWithChildren">DeleteSelection with the
        /// children/absorbed flags — the extrude-consumed-sketch retry.</param>
        /// <param name="purgeReferences">Remove the equations keyed to the
        /// feature; returns the removed texts verbatim (for restore).</param>
        /// <param name="restore">Re-adds purged equation texts; returns
        /// the count re-added.</param>
        /// <param name="sweepOrphans">The post-delete orphan sweep.</param>
        public static OwnedSketchDeleteResult Run(
            Func<bool> select,
            Func<bool> deletePlain,
            Func<bool> deleteWithChildren,
            Func<IReadOnlyList<string>> purgeReferences,
            Func<IReadOnlyList<string>, int> restore,
            Action sweepOrphans)
        {
            if (!select())
                return new OwnedSketchDeleteResult(
                    OwnedSketchDeleteOutcome.SelectFailed, 0, 0);

            if (deletePlain() ||
                (select() && deleteWithChildren()))
            {
                sweepOrphans();
                return new OwnedSketchDeleteResult(
                    OwnedSketchDeleteOutcome.Deleted, 0, 0);
            }

            // A proven refusal — the ticket-#19 blocker is the only purge
            // worth a sweep, and only now.
            var purged = purgeReferences() ?? (IReadOnlyList<string>)new string[0];
            if (purged.Count == 0)
                return new OwnedSketchDeleteResult(
                    OwnedSketchDeleteOutcome.DeleteRefused, 0, 0);
            if (select() && deleteWithChildren())
            {
                sweepOrphans();
                return new OwnedSketchDeleteResult(
                    OwnedSketchDeleteOutcome.Deleted, purged.Count, 0);
            }
            int restored = restore(purged);
            return new OwnedSketchDeleteResult(
                OwnedSketchDeleteOutcome.DeleteRefusedAfterPurge,
                purged.Count, restored);
        }
    }
}
