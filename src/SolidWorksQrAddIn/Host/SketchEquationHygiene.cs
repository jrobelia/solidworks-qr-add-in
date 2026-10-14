using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// Equation-manager hygiene around the circle-dot sketch (ticket #42).
    /// Deleting a sketch leaves every <c>"D{n}@{sketch}" = …</c> equation
    /// behind as an orphan — the dimensions die with the feature, the
    /// manager keeps the text — and SolidWorks refuses every later
    /// <c>IEquationMgr.Add</c> (returns −1) while any orphan exists. That is
    /// the deleted-sketch defect the circle ladder measured ("a second
    /// circle create in a document whose earlier QR sketch was deleted
    /// trips an equation-manager refusal", docs/INSERTION-GUARD.md).
    ///
    /// Two sweeps cover the defect's two doors:
    ///   - <see cref="PurgeEquationsReferencing"/> backs the sketch delete's
    ///     retry: SolidWorks refuses to delete a sketch whose dims still
    ///     feed the equation manager in a headless session (the ticket-#19
    ///     sweep's live finding, fd0d69e) — but the entries it removes are
    ///     LIVE references while the feature lives, so it runs only on a
    ///     proven refusal, and a delete that still fails hands the texts
    ///     to <see cref="RestoreEquations"/> so the surviving sketch keeps
    ///     its references.
    ///   - <see cref="PurgeOrphanedEquations"/> runs at circle-create start
    ///     and after our deletes: any entry whose local feature references
    ///     resolve to nothing in the tree is dead by definition — whoever
    ///     orphaned it (a user Delete key, an old build, an abort) — and
    ///     keeping it would only break the next Add.
    ///
    /// Both are best-effort and never throw: equation hygiene must not turn
    /// a delete or a create into a new failure. Two-part
    /// <c>"name@feature"</c> atoms are the only references judged — a
    /// "name@feature@doc" atom carries a document context and is left for
    /// the user (<see cref="QrEquationText"/> has the parsing rules).
    /// </summary>
    internal static class SketchEquationHygiene
    {
        /// <summary>
        /// Removes every equation whose local feature references all
        /// dangle — i.e. any <c>"…@{feature}"</c> reference to a feature
        /// absent from the document's tree. Returns the count removed.
        /// </summary>
        public static int PurgeOrphanedEquations(IModelDoc2 doc)
        {
            var live = LiveFeatureNames(doc);
            return SweepWhere(doc, "orphaned",
                text => QrEquationText.IsOrphaned(text, live.Contains)).Count;
        }

        /// <summary>
        /// Removes every equation that references
        /// <paramref name="featureName"/> — the delete path's ticket-#19
        /// retry: entries keyed to a feature block its deletion, so a
        /// refused delete sweeps them and retries. Returns the removed
        /// texts (verbatim, newest index first) so a delete that STILL
        /// refuses can hand them to <see cref="RestoreEquations"/> — a
        /// surviving sketch must keep its live references.
        /// </summary>
        public static List<string> PurgeEquationsReferencing(
            IModelDoc2 doc, string featureName)
        {
            if (string.IsNullOrEmpty(featureName)) return new List<string>();
            return SweepWhere(doc, $"referencing '{featureName}'",
                text => References(text, featureName));
        }

        /// <summary>
        /// Re-adds equation texts a purge removed — the rollback half of
        /// the delete path's purge-and-retry: when the delete refused even
        /// after its references were swept, the surviving sketch must not
        /// be left stripped. Entries go back oldest-first so the table
        /// order survives. Best-effort; returns the count re-added.
        /// </summary>
        public static int RestoreEquations(
            IModelDoc2 doc, IReadOnlyList<string> texts)
        {
            int restored = 0;
            if (texts == null) return 0;
            try
            {
                var eqm = doc.GetEquationMgr() as IEquationMgr;
                if (eqm == null) return 0;
                // The purge collected in reverse index order — iterate
                // backwards to re-add in the original table order.
                for (int i = texts.Count - 1; i >= 0; i--)
                {
                    try { if (eqm.Add(-1, texts[i]) >= 0) restored++; }
                    catch { /* one stubborn entry is not worth the rest */ }
                }
                if (restored < texts.Count)
                    Diag.Log($"equation restore incomplete: {restored}/{texts.Count}");
            }
            catch (Exception ex)
            {
                Diag.Log($"equation restore failed: {ex.GetType().Name}: {ex.Message}");
            }
            return restored;
        }

        /// <summary>
        /// The reverse-scan/delete skeleton both purges share
        /// (<see cref="IEquationMgr.Delete"/> re-indexes the survivors, so
        /// the sweep walks the table backwards). Removes every entry
        /// <paramref name="isDead"/> accepts; returns the removed texts
        /// verbatim. Never throws.
        /// </summary>
        private static List<string> SweepWhere(
            IModelDoc2 doc, string tag, Func<string, bool> isDead)
        {
            var removed = new List<string>();
            try
            {
                var eqm = doc.GetEquationMgr() as IEquationMgr;
                if (eqm == null) return removed;
                for (int i = eqm.GetCount() - 1; i >= 0; i--)
                {
                    string? text = null;
                    try { text = eqm.Equation[i]; } catch { }
                    if (text == null || !isDead(text)) continue;
                    try
                    {
                        eqm.Delete(i);
                        removed.Add(text);
                        Diag.Log($"purged {tag} equation #{i}: {text.Trim()}");
                    }
                    catch { /* one stubborn index is not worth the rest */ }
                }
            }
            catch (Exception ex)
            {
                Diag.Log($"equation sweep ({tag}) failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
            return removed;
        }

        /// <summary>Does the equation name <paramref name="featureName"/> in any local atom?</summary>
        private static bool References(string text, string featureName)
        {
            foreach (var r in QrEquationText.LocalFeatureReferences(text))
                if (string.Equals(r, featureName, StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>Every feature name in the document's tree — reference truth.</summary>
        private static HashSet<string> LiveFeatureNames(IModelDoc2 doc)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                string? n = null;
                try { n = f.Name; } catch { }
                if (!string.IsNullOrEmpty(n))
                    names.Add(n!);
            }
            return names;
        }
    }
}
