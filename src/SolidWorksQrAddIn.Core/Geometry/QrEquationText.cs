using System;
using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// Pure parsing of equation-manager text — the ticket-#42 fix for the
    /// deleted-sketch defect. Deleting a QR sketch leaves its
    /// <c>"D{n}@{sketch}" = …</c> entries in the manager as orphans (the
    /// dimensions die with the feature; the equations do not), and
    /// SolidWorks refuses every later <c>IEquationMgr.Add</c> while any
    /// orphan exists — a second circle create fails there. The host sweeps
    /// these entries at create start and on the delete path; this helper is
    /// the headless-testable decision it makes.
    ///
    /// Text shape: the manager stores dimension references as quoted atoms
    /// <c>"name@feature"</c> — optionally <c>"name@feature@document"</c>
    /// when a document context rides along. Only the two-part local form is
    /// ever reported: a contexted reference may resolve in ANOTHER
    /// document, so the sweep cannot prove it dead and must keep it.
    /// </summary>
    public static class QrEquationText
    {
        /// <summary>
        /// Every feature name a local <c>"name@feature"</c> atom in the
        /// equation references — occurrences in quote order, duplicates
        /// kept. Quoted atoms without exactly one '@', and atoms whose
        /// feature part names a document (<c>.sldprt</c>/<c>.sldasm</c>/
        /// <c>.slddrw</c>), report nothing.
        /// </summary>
        public static IReadOnlyList<string> LocalFeatureReferences(string equationText)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(equationText))
                return names;
            int i = 0;
            while (i < equationText.Length)
            {
                int open = equationText.IndexOf('"', i);
                if (open < 0) break;
                int close = equationText.IndexOf('"', open + 1);
                if (close < 0) break;
                i = close + 1;
                string atom = equationText.Substring(open + 1, close - open - 1);
                int at = atom.IndexOf('@');
                if (at <= 0 || at != atom.LastIndexOf('@'))
                    continue;   // no feature part, or a doc-contexted reference
                string feature = atom.Substring(at + 1);
                if (feature.Length == 0 || LooksLikeDocumentName(feature))
                    continue;
                names.Add(feature);
            }
            return names;
        }

        /// <summary>
        /// Whether the equation dangles: it references at least one local
        /// feature that <paramref name="featureExists"/> rejects. Equations
        /// with no local feature references are never orphaned — the sweep
        /// only ever touches entries it can prove dead.
        /// </summary>
        public static bool IsOrphaned(string equationText, Func<string, bool> featureExists)
        {
            foreach (var name in LocalFeatureReferences(equationText))
                if (!featureExists(name))
                    return true;
            return false;
        }

        /// <summary>A feature part that is actually a document name — never a feature.</summary>
        private static bool LooksLikeDocumentName(string name) =>
            name.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);
    }
}
