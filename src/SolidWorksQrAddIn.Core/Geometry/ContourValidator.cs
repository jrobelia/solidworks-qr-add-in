using System.Collections.Generic;
using Clipper2Lib;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// Validates union output contours on the integer grid BEFORE any physical
    /// conversion or host mutation. All predicates are exact integer tests —
    /// no epsilon. Rejects: degenerate (&lt;3 vertex) contours, zero-area loops,
    /// zero-length edges, self-intersections, self-touching (pinched) contours,
    /// and residual corner-only contacts between distinct contours.
    ///
    /// A correct grown-rect union can never produce these, so a violation means
    /// the input or topology behaved unexpectedly — reject rather than insert
    /// geometry SolidWorks would later choke on (zero-thickness features).
    /// </summary>
    internal static class ContourValidator
    {
        /// <summary>Returns null when the contour set is clean, else a violation description.</summary>
        public static string? FindViolation(IReadOnlyList<Path64> contours)
        {
            for (var i = 0; i < contours.Count; i++)
            {
                var violation = CheckSingle(contours[i], i);
                if (violation != null) return violation;
            }

            for (var i = 0; i < contours.Count; i++)
                for (var j = i + 1; j < contours.Count; j++)
                {
                    var violation = CheckPair(contours[i], i, contours[j], j);
                    if (violation != null) return violation;
                }

            return null;
        }

        private static string? CheckSingle(Path64 c, int index)
        {
            if (c.Count < 3)
                return $"contour {index} has {c.Count} vertices (degenerate)";

            // Integer-grid polygon area is exact to 0.5 — nonzero area is a
            // strict test here, no epsilon needed.
            if (System.Math.Abs(Clipper.Area(c)) < 0.5)
                return $"contour {index} has zero area";

            int n = c.Count;
            for (var i = 0; i < n; i++)
            {
                var a = c[i];
                var b = c[(i + 1) % n];
                if (a.X == b.X && a.Y == b.Y)
                    return $"contour {index} has a zero-length edge at vertex {i}";
            }

            // Self-contact: a vertex may only coincide with its neighbours.
            for (var i = 0; i < n; i++)
                for (var j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue; // closing pair is adjacent
                    if (c[i].X == c[j].X && c[i].Y == c[j].Y)
                        return $"contour {index} touches itself at vertex {i} = vertex {j} (corner-only contact)";
                }

            // Self-intersection / vertex-on-edge for non-adjacent edge pairs.
            for (var i = 0; i < n; i++)
                for (var j = i + 1; j < n; j++)
                {
                    if (j == i + 1) continue;                    // share endpoint v_{i+1}
                    if (i == 0 && j == n - 1) continue;          // share endpoint v_0
                    var v = CheckEdgePair(c, i, c, j, $"contour {index}");
                    if (v != null) return v;
                }

            return null;
        }

        private static string? CheckPair(Path64 a, int ai, Path64 b, int bi)
        {
            // Shared vertices first — the classic residual corner-only contact.
            for (var i = 0; i < a.Count; i++)
                for (var j = 0; j < b.Count; j++)
                    if (a[i].X == b[j].X && a[i].Y == b[j].Y)
                        return $"contours {ai} and {bi} touch at a shared vertex (corner-only contact)";

            // Then vertices strictly inside the other contour's edges.
            for (var i = 0; i < a.Count; i++)
            {
                for (var j = 0; j < b.Count; j++)
                {
                    if (PointOnSegmentInterior(a[i], b[j], b[(j + 1) % b.Count]) ||
                        PointOnSegmentInterior(b[j], a[i], a[(i + 1) % a.Count]))
                        return $"contours {ai} and {bi} touch (vertex on edge)";
                }
            }

            for (var i = 0; i < a.Count; i++)
                for (var j = 0; j < b.Count; j++)
                {
                    var v = CheckEdgePair(a, i, b, j, $"contours {ai}/{bi}");
                    if (v != null) return v;
                }

            return null;
        }

        /// <summary>Detects proper crossings and collinear overlaps between two edges.</summary>
        private static string? CheckEdgePair(Path64 a, int ai, Path64 b, int bi, string who)
        {
            var p = a[ai];
            var p2 = a[(ai + 1) % a.Count];
            var q = b[bi];
            var q2 = b[(bi + 1) % b.Count];

            long o1 = Orient(p, p2, q);
            long o2 = Orient(p, p2, q2);
            long o3 = Orient(q, q2, p);
            long o4 = Orient(q, q2, p2);

            if (o1 == 0 && o2 == 0 && o3 == 0 && o4 == 0)
            {
                // Collinear: reject any overlapping span (a duplicate / doubled
                // edge) — endpoints merely touching is already caught elsewhere.
                if (RangesOverlap(p, p2, q, q2))
                    return $"{who}: collinear edges overlap";
                return null;
            }

            if (((o1 > 0) != (o2 > 0)) && ((o3 > 0) != (o4 > 0)) &&
                o1 != 0 && o2 != 0 && o3 != 0 && o4 != 0)
                return $"{who}: edges cross";

            // Vertex lying on the other edge's interior (a T-touch — also a
            // corner-only contact), not caught by the strict crossing test.
            if (PointOnSegmentInterior(q, p, p2) || PointOnSegmentInterior(q2, p, p2) ||
                PointOnSegmentInterior(p, q, q2) || PointOnSegmentInterior(p2, q, q2))
                return $"{who}: an edge endpoint touches the other edge";

            return null;
        }

        /// <summary>Signed area×2 of triangle (a, b, p) — exact integer orientation.</summary>
        private static long Orient(Point64 a, Point64 b, Point64 p) =>
            (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

        /// <summary>Is p strictly inside the open segment a→b (exact integer test)?</summary>
        private static bool PointOnSegmentInterior(Point64 p, Point64 a, Point64 b)
        {
            if (Orient(a, b, p) != 0) return false;
            if (p.X == a.X && p.Y == a.Y) return false;
            if (p.X == b.X && p.Y == b.Y) return false;
            return p.X >= System.Math.Min(a.X, b.X) && p.X <= System.Math.Max(a.X, b.X) &&
                   p.Y >= System.Math.Min(a.Y, b.Y) && p.Y <= System.Math.Max(a.Y, b.Y);
        }

        /// <summary>Collinear edge overlap: project both segments on their common axis.</summary>
        private static bool RangesOverlap(Point64 p, Point64 p2, Point64 q, Point64 q2)
        {
            if (p.X != p2.X)
                return System.Math.Max(p.X, p2.X) > System.Math.Min(q.X, q2.X) &&
                       System.Math.Max(q.X, q2.X) > System.Math.Min(p.X, p2.X);
            if (p.Y != p2.Y)
                return System.Math.Max(p.Y, p2.Y) > System.Math.Min(q.Y, q2.Y) &&
                       System.Math.Max(q.Y, q2.Y) > System.Math.Min(p.Y, p2.Y);
            return false;
        }
    }
}
