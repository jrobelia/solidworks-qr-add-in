using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>A 2D point in sketch-space metres.</summary>
    public readonly struct SketchPoint2
    {
        public SketchPoint2(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    /// <summary>A straight sketch edge between two sketch-space points.</summary>
    public readonly struct SketchEdge
    {
        public SketchEdge(SketchPoint2 start, SketchPoint2 end)
        {
            Start = start;
            End = end;
        }

        public SketchPoint2 Start { get; }
        public SketchPoint2 End { get; }
    }

    /// <summary>
    /// The closed-square test shape from ticket 1: four native line segments
    /// forming one closed loop centred at the sketch origin. This is the
    /// first end-to-end proof of the host path, not the shipped QR geometry.
    /// </summary>
    public static class TestSquareSketch
    {
        /// <summary>25 mm — the same default size the QR tool will propose. In metres.</summary>
        public const double SideLengthMeters = 0.025;

        /// <summary>Four edges chained into a closed loop, centred on (0,0).</summary>
        public static IReadOnlyList<SketchEdge> Edges { get; } = BuildSquare();

        private static IReadOnlyList<SketchEdge> BuildSquare()
        {
            var h = SideLengthMeters / 2.0;
            var bl = new SketchPoint2(-h, -h);
            var br = new SketchPoint2(h, -h);
            var tr = new SketchPoint2(h, h);
            var tl = new SketchPoint2(-h, h);
            return new[]
            {
                new SketchEdge(bl, br),
                new SketchEdge(br, tr),
                new SketchEdge(tr, tl),
                new SketchEdge(tl, bl),
            };
        }
    }
}
