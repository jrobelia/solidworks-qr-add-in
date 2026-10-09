using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// The test square is the first end-to-end proof shape: one closed loop of
    /// native line segments, centred on the sketch origin.
    /// </summary>
    public class TestSquareSketchTests
    {
        private const double Tol = 1e-9;

        [Fact]
        public void Produces_exactly_four_edges()
        {
            Assert.Equal(4, TestSquareSketch.Edges.Count);
        }

        [Fact]
        public void Edges_form_a_single_closed_loop()
        {
            var edges = TestSquareSketch.Edges;
            for (var i = 0; i < edges.Count; i++)
            {
                var end = edges[i].End;
                var nextStart = edges[(i + 1) % edges.Count].Start;
                Assert.Equal(nextStart.X, end.X, 12);
                Assert.Equal(nextStart.Y, end.Y, 12);
            }
        }

        [Fact]
        public void Every_edge_is_a_horizontal_or_vertical_side_of_equal_length()
        {
            foreach (var e in TestSquareSketch.Edges)
            {
                var dx = System.Math.Abs(e.End.X - e.Start.X);
                var dy = System.Math.Abs(e.End.Y - e.Start.Y);
                var horizontal = dy < Tol && System.Math.Abs(dx - TestSquareSketch.SideLengthMeters) < Tol;
                var vertical = dx < Tol && System.Math.Abs(dy - TestSquareSketch.SideLengthMeters) < Tol;
                Assert.True(horizontal || vertical,
                    $"edge ({e.Start.X},{e.Start.Y})->({e.End.X},{e.End.Y}) is not an axis-aligned side of the square");
            }
        }

        [Fact]
        public void Square_is_centred_on_the_sketch_origin()
        {
            var edges = TestSquareSketch.Edges;
            var minX = edges.Min(e => System.Math.Min(e.Start.X, e.End.X));
            var maxX = edges.Max(e => System.Math.Max(e.Start.X, e.End.X));
            var minY = edges.Min(e => System.Math.Min(e.Start.Y, e.End.Y));
            var maxY = edges.Max(e => System.Math.Max(e.Start.Y, e.End.Y));
            Assert.Equal(0.0, (minX + maxX) / 2.0, 12);
            Assert.Equal(0.0, (minY + maxY) / 2.0, 12);
            // Symmetric: extents are exactly half the side length.
            Assert.Equal(TestSquareSketch.SideLengthMeters / 2.0, maxX, 12);
            Assert.Equal(-TestSquareSketch.SideLengthMeters / 2.0, minX, 12);
        }

        [Fact]
        public void Side_length_is_the_declared_test_value()
        {
            // 25 mm — the spec's default size — keeps the test sketch consistent
            // with the eventual QR-size default. Metres, as CreateLine consumes.
            Assert.Equal(0.025, TestSquareSketch.SideLengthMeters, 12);
        }
    }
}
