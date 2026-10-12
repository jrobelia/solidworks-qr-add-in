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
    /// The closed-loop edge enumeration <see cref="QrContour"/> and
    /// <see cref="QrConstructionSquare"/> share: vertex i connects to
    /// vertex i+1, and the last edge closes back to the first.
    /// </summary>
    internal static class SketchClosedLoop
    {
        /// <summary>One edge per vertex, wrapping the final edge to vertex 0.</summary>
        public static IEnumerable<SketchEdge> Edges(IReadOnlyList<SketchPoint2> vertices)
        {
            for (var i = 0; i < vertices.Count; i++)
                yield return new SketchEdge(vertices[i], vertices[(i + 1) % vertices.Count]);
        }
    }
}
