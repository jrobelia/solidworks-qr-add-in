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

        /// <summary>
        /// The equation-reference form of a dimension's full name —
        /// <c>"D{n}@{sketch}@{document}"</c> or the already-two-part
        /// <c>"D{n}@{sketch}"</c> both reduce to <c>"D{n}@{sketch}"</c>
        /// (PAT4's reference shape). The sketch's own name anchors the
        /// cut, so a document tail carrying '@' cannot shift it. Returns
        /// null for any other shape — a bare <c>"D{n}"</c>, a reference
        /// from another sketch, a name without the feature part — because
        /// silently truncating would mint a dead equation reference; the
        /// caller owns the refusal.
        /// </summary>
        public static string? LocalReferenceFor(string? fullName, string sketchName)
        {
            if (string.IsNullOrEmpty(sketchName)) return null;
            string name = fullName ?? "";
            int at = name.IndexOf('@');
            if (at <= 0)
                return null;   // no feature part (or an empty dim name)
            string tail = name.Substring(at + 1);
            if (tail == sketchName)
                return name;   // already the two-part reference
            if (tail.StartsWith(sketchName + "@", StringComparison.Ordinal))
                return name.Substring(0, at + 1 + sketchName.Length);
            return null;
        }

        /// <summary>A feature part that is actually a document name — never a feature.</summary>
        private static bool LooksLikeDocumentName(string name) =>
            name.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase);
    }
}
