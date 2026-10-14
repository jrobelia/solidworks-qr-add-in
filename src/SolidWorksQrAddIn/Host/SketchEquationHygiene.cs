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
    ///   - <see cref="PurgeEquationsReferencing"/> runs BEFORE a sketch
    ///     delete: SolidWorks refuses to delete a sketch whose dims still
    ///     feed the equation manager in a headless session (the ticket-#19
    ///     sweep's live finding, fd0d69e) — entries keyed to the feature
    ///     go first, the feature second.
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
            try
            {
                var eqm = doc.GetEquationMgr() as IEquationMgr;
                if (eqm == null) return 0;
                var live = LiveFeatureNames(doc);
                int removed = 0;
                for (int i = eqm.GetCount() - 1; i >= 0; i--)
                {
                    string? text = null;
                    try { text = eqm.Equation[i]; } catch { }
                    if (text == null ||
                        !QrEquationText.IsOrphaned(text, live.Contains))
                        continue;
                    try
                    {
                        eqm.Delete(i);
                        removed++;
                        Diag.Log($"purged orphaned equation #{i}: {text.Trim()}");
                    }
                    catch { /* one stubborn index is not worth the rest */ }
                }
                return removed;
            }
            catch (Exception ex)
            {
                Diag.Log($"orphan-equation sweep failed: {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Removes every equation that references <paramref name="featureName"/>
        /// — run before deleting that feature (the delete refuses while its
        /// dims still feed equations), and again after, when the same
        /// entries have become orphans. Returns the count removed.
        /// </summary>
        public static int PurgeEquationsReferencing(IModelDoc2 doc, string featureName)
        {
            if (string.IsNullOrEmpty(featureName)) return 0;
            try
            {
                var eqm = doc.GetEquationMgr() as IEquationMgr;
                if (eqm == null) return 0;
                int removed = 0;
                // Backwards: IEquationMgr.Delete re-indexes the survivors.
                for (int i = eqm.GetCount() - 1; i >= 0; i--)
                {
                    string? text = null;
                    try { text = eqm.Equation[i]; } catch { }
                    if (text == null) continue;
                    var refs = QrEquationText.LocalFeatureReferences(text);
                    bool mentions = false;
                    foreach (var r in refs)
                        if (string.Equals(r, featureName, StringComparison.Ordinal))
                        { mentions = true; break; }
                    if (!mentions) continue;
                    try
                    {
                        eqm.Delete(i);
                        removed++;
                        Diag.Log($"removed equation #{i} referencing '{featureName}': {text.Trim()}");
                    }
                    catch { }
                }
                return removed;
            }
            catch (Exception ex)
            {
                Diag.Log($"equation sweep for '{featureName}' failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return 0;
            }
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
