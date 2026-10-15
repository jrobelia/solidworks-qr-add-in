using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// Which side of an axis-aligned construction square an edge lies on —
    /// numbered like <see cref="QrConstructionSquare.Edges"/>'s loop order:
    /// the square's vertices run CCW from bottom-left, so edge 0 is the
    /// bottom side, 1 the right, 2 the top, 3 the left.
    /// </summary>
    public enum QrSquareEdgeRole
    {
        Bottom = 0,
        Right = 1,
        Top = 2,
        Left = 3,
    }

    /// <summary>
    /// Observed-edge → side matching for the circle path's closed
    /// construction squares (spec #13, PR-#43 QA). The emitter draws each
    /// staged square as ONE ISketchManager.CreateCornerRectangle — the
    /// canonical rectangle primitive, four edges sharing corner sketch
    /// points, a genuinely closed profile (per-edge CreateLine leaves two
    /// stacked endpoints at every corner — SolidWorks never merges
    /// separately created endpoints). The rectangle call returns its four
    /// segments with no contractual order, so the dim wiring never relies
    /// on return order: each side is identified by the bound it lies on —
    /// the horizontal edge at the square's min-Y is the bottom edge, the
    /// vertical edge at min-X the left. Contains no SolidWorks objects.
    /// </summary>
    public static class QrSquareEdges
    {
        /// <summary>
        /// The observed-edge index per <see cref="QrSquareEdgeRole"/> for
        /// <paramref name="square"/>'s four drawn sides, or null when the
        /// observed set is not exactly the square's four sides — a missing
        /// side, two edges claiming one side, a diagonal, an off-bound or
        /// degenerate edge.
        /// </summary>
        public static int[]? OrderFor(
            QrConstructionSquare square,
            IReadOnlyList<SketchEdge> observed,
            double tolerance)
        {
            if (observed.Count != 4)
                return null;
            double minX = square.Vertices.Min(v => v.X);
            double minY = square.Vertices.Min(v => v.Y);
            double maxX = square.Vertices.Max(v => v.X);
            double maxY = square.Vertices.Max(v => v.Y);
            var order = new[] { -1, -1, -1, -1 };
            for (var i = 0; i < observed.Count; i++)
            {
                var role = Classify(
                    observed[i], minX, minY, maxX, maxY, tolerance);
                if (role == null || order[(int)role.Value] >= 0)
                    return null;
                order[(int)role.Value] = i;
            }
            return order;
        }

        /// <summary>
        /// The side an observed edge lies on within the axis-aligned
        /// bounds (minX, minY)–(maxX, maxY): a horizontal edge on the
        /// min-Y bound is Bottom, on max-Y Top; a vertical edge on the
        /// min-X bound is Left, on max-X Right. Null for a diagonal, a
        /// degenerate point edge, or an edge sitting on no bound.
        /// </summary>
        public static QrSquareEdgeRole? Classify(
            SketchEdge edge,
            double minX, double minY, double maxX, double maxY,
            double tolerance)
        {
            double dx = Math.Abs(edge.End.X - edge.Start.X);
            double dy = Math.Abs(edge.End.Y - edge.Start.Y);
            if (dy <= tolerance && dx > tolerance)
            {
                double y = (edge.Start.Y + edge.End.Y) / 2.0;
                if (Math.Abs(y - minY) <= tolerance) return QrSquareEdgeRole.Bottom;
                if (Math.Abs(y - maxY) <= tolerance) return QrSquareEdgeRole.Top;
            }
            else if (dx <= tolerance && dy > tolerance)
            {
                double x = (edge.Start.X + edge.End.X) / 2.0;
                if (Math.Abs(x - minX) <= tolerance) return QrSquareEdgeRole.Left;
                if (Math.Abs(x - maxX) <= tolerance) return QrSquareEdgeRole.Right;
            }
            return null;
        }
    }
}
