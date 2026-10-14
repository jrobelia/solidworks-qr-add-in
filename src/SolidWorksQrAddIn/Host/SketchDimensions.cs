using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// The "D{n}@{sketch}" dimension discovery the circle-mode paths share
    /// (tickets #40/#42): sketch dimensions are NOT sub-features and no
    /// enumerator exists, so they resolve by name through
    /// <see cref="IModelDoc2.IParameter"/> over D1..D64 — spike-proven, and
    /// far past the ~11 dims the emitter creates. The circle emitter scans
    /// to learn the names its equations bind; the probes scan to report
    /// them. One scan, one bound, both callers.
    /// </summary>
    internal static class SketchDimensions
    {
        /// <summary>
        /// Every resolvable "D{n}@{sketchName}" dimension, in D-number
        /// (creation) order, as (full reference name, IDimension) pairs.
        /// </summary>
        public static List<(string Name, IDimension Dim)> OfSketch(
            IModelDoc2 doc, string sketchName)
        {
            var dims = new List<(string, IDimension)>();
            for (var i = 1; i <= 64; i++)
            {
                string name = $"D{i}@{sketchName}";
                IDimension? dim = null;
                try { dim = doc.IParameter(name) as IDimension; } catch { }
                if (dim != null)
                    dims.Add((name, dim));
            }
            return dims;
        }

        /// <summary>"D5@Sketch1" → "D5" — the bare D-number of a scanned name.</summary>
        public static string DimNumber(string referenceName)
        {
            int at = referenceName.IndexOf('@');
            return at > 0 ? referenceName.Substring(0, at) : referenceName;
        }
    }
}
