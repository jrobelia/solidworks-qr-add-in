using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// A measured thin channel through a nested band (spec #51, tickets
    /// #53/#55): the corridor the ring split subtracts leaves two parallel
    /// wall edges sharing the same extent — its end tips land in void, so
    /// they leave no caps. The mouths are the midpoints of the paired wall
    /// ends — where the channel pierces the band's two boundaries — so
    /// their positions pin the channel's locus.
    /// </summary>
    public sealed class QrChannelGap
    {
        public QrChannelGap(double gapM, SketchPoint2 mouthA, SketchPoint2 mouthB)
        {
            GapM = gapM;
            MouthA = mouthA;
            MouthB = mouthB;
        }

        /// <summary>
        /// Perpendicular separation of the facing wall pair, metres — the
        /// channel's physical width, which must match
        /// <see cref="QrSketchInfo.FinderChannelWidthMm"/> for finder sites
        /// and quiet-zone egress corridors alike.
        /// </summary>
        public double GapM { get; }

        /// <summary>Midpoint of the paired wall ends on the lower-coordinate side.</summary>
        public SketchPoint2 MouthA { get; }

        /// <summary>Midpoint of the paired wall ends on the upper-coordinate side.</summary>
        public SketchPoint2 MouthB { get; }
    }

    /// <summary>
    /// The facing wall-pair scan the live ring-split gate measures on both
    /// sides of the host boundary (ticket #55): module contour edges carry
    /// the expectation; the block definition's and the exploded sketch's
    /// enumerable segments carry the delivered geometry — all of them
    /// axis-aligned <see cref="SketchEdge"/> lists, so one pure routine
    /// serves every side. A pair counts when the edges are collinear-class
    /// (both horizontal or both vertical), share the same extent along the
    /// axis within <paramref name="toleranceM"/>, and sit
    /// ≤ <paramref name="maxGapM"/> apart. QR grown edges face their
    /// neighbours across the light gap, so a cap of half the module's
    /// reported minimum light gap keeps the thin channels — the finder
    /// slots and the quiet-zone egress corridors — as the only possible
    /// hits: the module-aligned cell drops are a full light gap wide and
    /// never register.
    /// </summary>
    public static class QrChannelGaps
    {
        /// <summary>
        /// Every facing wall-pair in <paramref name="edges"/>. Returns the
        /// pairs in scan order — the harness compares the set against the
        /// module's expectation, not the order.
        /// </summary>
        public static List<QrChannelGap> Find(
            IEnumerable<SketchEdge> edges, double maxGapM, double toleranceM = 1e-9)
        {
            var list = edges.ToList();
            var found = new List<QrChannelGap>();
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                double ax = Math.Abs(a.End.X - a.Start.X);
                double ay = Math.Abs(a.End.Y - a.Start.Y);
                for (var j = i + 1; j < list.Count; j++)
                {
                    var b = list[j];
                    double bx = Math.Abs(b.End.X - b.Start.X);
                    double by = Math.Abs(b.End.Y - b.Start.Y);
                    if (ay < toleranceM && by < toleranceM &&
                        SameExtent(a.Start.X, a.End.X, b.Start.X, b.End.X, toleranceM))
                    {
                        // Horizontal walls — the channel runs left-right;
                        // mouths centre on each x end.
                        double gap = Math.Abs(a.Start.Y - b.Start.Y);
                        if (gap > toleranceM && gap <= maxGapM)
                        {
                            double midY = (a.Start.Y + b.Start.Y) / 2.0;
                            found.Add(new QrChannelGap(gap,
                                new SketchPoint2(Math.Min(a.Start.X, a.End.X), midY),
                                new SketchPoint2(Math.Max(a.Start.X, a.End.X), midY)));
                        }
                    }
                    else if (ax < toleranceM && bx < toleranceM &&
                             SameExtent(a.Start.Y, a.End.Y, b.Start.Y, b.End.Y, toleranceM))
                    {
                        // Vertical walls — the channel runs top-bottom;
                        // mouths centre on each y end.
                        double gap = Math.Abs(a.Start.X - b.Start.X);
                        if (gap > toleranceM && gap <= maxGapM)
                        {
                            double midX = (a.Start.X + b.Start.X) / 2.0;
                            found.Add(new QrChannelGap(gap,
                                new SketchPoint2(midX, Math.Min(a.Start.Y, a.End.Y)),
                                new SketchPoint2(midX, Math.Max(a.Start.Y, a.End.Y))));
                        }
                    }
                }
            }
            return found;

            static bool SameExtent(
                double a0, double a1, double b0, double b1, double tol) =>
                Math.Abs(Math.Min(a0, a1) - Math.Min(b0, b1)) < tol &&
                Math.Abs(Math.Max(a0, a1) - Math.Max(b0, b1)) < tol;
        }
    }
}
