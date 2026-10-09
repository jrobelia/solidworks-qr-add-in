using System.Collections.Generic;
using Clipper2Lib;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Direct unit tests of the grid-space contour validator. A grown-rect
    /// union can never emit the degenerate inputs below, so they are crafted
    /// by hand to prove each rejection rule actually fires. Integer coords.
    /// </summary>
    public class ContourValidatorTests
    {
        private static Path64 Path(params (long x, long y)[] pts)
        {
            var p = new Path64(pts.Length);
            foreach (var (x, y) in pts)
                p.Add(new Point64(x, y));
            return p;
        }

        private static Path64 Rect(long x, long y, long w, long h) =>
            Path((x, y), (x + w, y), (x + w, y + h), (x, y + h));

        [Fact]
        public void Clean_nested_contours_pass()
        {
            var contours = new List<Path64>
            {
                Rect(0, 0, 100, 100),          // outer
                Rect(20, 20, 60, 60),          // hole
                Rect(40, 40, 20, 20),          // island inside the hole
            };
            Assert.Null(ContourValidator.FindViolation(contours));
        }

        [Fact]
        public void Zero_area_contour_is_rejected()
        {
            var contours = new List<Path64> { Path((0, 0), (10, 0), (20, 0)) }; // collinear
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("zero area", v);
        }

        [Fact]
        public void Zero_length_edge_is_rejected()
        {
            var contours = new List<Path64> { Path((0, 0), (10, 0), (10, 0), (10, 10), (0, 10)) };
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("zero-length", v);
        }

        [Fact]
        public void Two_vertex_contour_is_rejected()
        {
            var contours = new List<Path64> { Path((0, 0), (10, 10)) };
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("degenerate", v);
        }

        [Fact]
        public void Self_intersecting_bowtie_is_rejected()
        {
            // Asymmetric crossing (a symmetric bowtie's lobes cancel to zero
            // area and get caught by the area rule instead — also a rejection).
            var contours = new List<Path64> { Path((0, 0), (10, 0), (10, 10), (2, -3)) };
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("cross", v);
        }

        [Fact]
        public void Symmetric_bowtie_is_rejected_as_zero_area()
        {
            var contours = new List<Path64> { Path((0, 0), (10, 10), (10, 0), (0, 10)) };
            Assert.NotNull(ContourValidator.FindViolation(contours));
        }

        [Fact]
        public void Pinched_contour_touching_itself_at_one_vertex_is_rejected()
        {
            // Figure-8 pinched at (10,10): the classic residual corner contact.
            var contours = new List<Path64>
            {
                Path((0, 0), (10, 0), (10, 10), (0, 10), (0, 0),
                     (20, 10), (20, 20), (10, 20)),
            };
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("itself", v);
        }

        [Fact]
        public void Two_contours_sharing_one_vertex_are_rejected()
        {
            // Diagonal squares that kept a point contact — the failure mode
            // growth exists to eliminate.
            var contours = new List<Path64>
            {
                Rect(0, 0, 10, 10),
                Rect(10, 10, 10, 10),
            };
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("corner-only", v);
        }

        [Fact]
        public void Contour_vertex_lying_on_another_contours_edge_is_rejected()
        {
            var contours = new List<Path64>
            {
                Rect(0, 0, 20, 20),
                Path((10, 0), (30, 5), (30, -5)), // vertex (10,0) sits on the rect's bottom edge
            };
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("vertex on edge", v);
        }

        [Fact]
        public void Properly_crossing_contours_are_rejected()
        {
            var contours = new List<Path64>
            {
                Rect(0, 0, 20, 20),
                Rect(5, -5, 10, 30), // interior crossing, no vertex-on-edge touch
            };
            var v = ContourValidator.FindViolation(contours);
            Assert.NotNull(v);
            Assert.Contains("cross", v);
        }
    }
}
