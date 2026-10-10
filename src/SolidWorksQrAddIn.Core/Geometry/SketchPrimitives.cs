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
}
