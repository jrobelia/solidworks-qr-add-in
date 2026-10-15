using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #13, PR-#43 QA fix — the circle path's construction squares are
    /// drawn as closed rectangles (<c>CreateCornerRectangle</c>), whose
    /// returned segments carry no contractual order. <see cref="QrSquareEdges"/>
    /// re-orders them into the canonical loop order (bottom, right, top,
    /// left — <see cref="QrConstructionSquare.Edges"/>) by which bound each
    /// edge lies on, so the master-dim wiring keeps its bottom/left roles.
    /// The headless half of the fix — the coincident-corners proof itself
    /// is a live gate (the fake host cannot model merged sketch points).
    /// </summary>
    public class QrSquareEdgesTests
    {
        private const double Half = 0.0125;        // the 25 mm QR square
        private const double Tol = Half * 2 * 0.25; // quarter-side, like the host

        /// <summary>CCW-from-bottom-left vertices — <see cref="QrSketchGenerator"/>'s convention.</summary>
        private static QrConstructionSquare Square() =>
            new QrConstructionSquare(QrSquareRole.QrSquare, new[]
            {
                new SketchPoint2(-Half, -Half), new SketchPoint2(+Half, -Half),
                new SketchPoint2(+Half, +Half), new SketchPoint2(-Half, +Half),
            });

        private static SketchEdge Bottom => new SketchEdge(
            new SketchPoint2(-Half, -Half), new SketchPoint2(+Half, -Half));
        private static SketchEdge Right => new SketchEdge(
            new SketchPoint2(+Half, -Half), new SketchPoint2(+Half, +Half));
        private static SketchEdge Top => new SketchEdge(
            new SketchPoint2(+Half, +Half), new SketchPoint2(-Half, +Half));
        private static SketchEdge Left => new SketchEdge(
            new SketchPoint2(-Half, +Half), new SketchPoint2(-Half, -Half));

        [Fact]
        public void Canonical_order_is_the_identity_permutation()
        {
            var order = QrSquareEdges.OrderFor(
                Square(), new List<SketchEdge> { Bottom, Right, Top, Left }, Tol);

            Assert.NotNull(order);
            Assert.Equal(new[] { 0, 1, 2, 3 }, order);
        }

        [Fact]
        public void A_shuffled_return_order_still_maps_each_side()
        {
            // As CreateCornerRectangle might return them: start anywhere.
            var observed = new List<SketchEdge> { Top, Left, Bottom, Right };

            var order = QrSquareEdges.OrderFor(Square(), observed, Tol);

            Assert.NotNull(order);
            // order[role] = observed index: bottom→2, right→3, top→0, left→1.
            Assert.Equal(new[] { 2, 3, 0, 1 }, order);
        }

        [Fact]
        public void Reversed_direction_edges_classify_the_same()
        {
            // A segment drawn end→start reports the same side — matching is
            // by the bound the edge lies on, not endpoint order.
            var observed = new List<SketchEdge>
            {
                new SketchEdge(Top.End, Top.Start),
                new SketchEdge(Bottom.End, Bottom.Start),
                new SketchEdge(Left.End, Left.Start),
                new SketchEdge(Right.End, Right.Start),
            };

            var order = QrSquareEdges.OrderFor(Square(), observed, Tol);

            Assert.NotNull(order);
            Assert.Equal(new[] { 1, 3, 0, 2 }, order);
        }

        [Fact]
        public void A_diagonal_edge_is_not_a_side()
        {
            var diagonal = new SketchEdge(
                new SketchPoint2(-Half, -Half), new SketchPoint2(+Half, +Half));

            var order = QrSquareEdges.OrderFor(
                Square(), new List<SketchEdge> { Bottom, Right, Top, diagonal }, Tol);

            Assert.Null(order);
        }

        [Fact]
        public void Two_edges_on_one_side_fail_the_match()
        {
            // A duplicated bottom edge leaves the left side unclaimed.
            var order = QrSquareEdges.OrderFor(
                Square(), new List<SketchEdge> { Bottom, Bottom, Right, Top }, Tol);

            Assert.Null(order);
        }

        [Fact]
        public void An_edge_off_every_bound_fails_the_match()
        {
            var inset = new SketchEdge(   // horizontal but on no bound
                new SketchPoint2(-Half, 0), new SketchPoint2(+Half, 0));

            var order = QrSquareEdges.OrderFor(
                Square(), new List<SketchEdge> { Bottom, Right, Top, inset }, Tol);

            Assert.Null(order);
        }

        [Fact]
        public void A_degenerate_point_edge_is_not_a_side()
        {
            var point = new SketchEdge(
                new SketchPoint2(-Half, -Half), new SketchPoint2(-Half, -Half));

            var order = QrSquareEdges.OrderFor(
                Square(), new List<SketchEdge> { Bottom, Right, Top, point }, Tol);

            Assert.Null(order);
        }

        [Fact]
        public void Anything_but_four_edges_fails_the_match()
        {
            Assert.Null(QrSquareEdges.OrderFor(
                Square(), new List<SketchEdge> { Bottom, Right, Top }, Tol));
            Assert.Null(QrSquareEdges.OrderFor(
                Square(), new List<SketchEdge> { Bottom, Right, Top, Left, Bottom }, Tol));
        }

        [Fact]
        public void Every_square_edge_classifies_to_its_named_role()
        {
            var square = Square();
            var edges = square.Edges.ToList();

            Assert.Equal(QrSquareEdgeRole.Bottom,
                QrSquareEdges.Classify(edges[0], -Half, -Half, +Half, +Half, Tol));
            Assert.Equal(QrSquareEdgeRole.Right,
                QrSquareEdges.Classify(edges[1], -Half, -Half, +Half, +Half, Tol));
            Assert.Equal(QrSquareEdgeRole.Top,
                QrSquareEdges.Classify(edges[2], -Half, -Half, +Half, +Half, Tol));
            Assert.Equal(QrSquareEdgeRole.Left,
                QrSquareEdges.Classify(edges[3], -Half, -Half, +Half, +Half, Tol));
        }

        [Fact]
        public void The_match_works_on_the_larger_keep_out_square_too()
        {
            const double keepHalf = 0.0145;   // 29 mm keep-out at pitch 1 mm
            var keep = new QrConstructionSquare(QrSquareRole.KeepOutSquare, new[]
            {
                new SketchPoint2(-keepHalf, -keepHalf), new SketchPoint2(+keepHalf, -keepHalf),
                new SketchPoint2(+keepHalf, +keepHalf), new SketchPoint2(-keepHalf, +keepHalf),
            });
            var observed = keep.Edges.Reverse().ToList();   // worst-case order

            var order = QrSquareEdges.OrderFor(keep, observed, keepHalf * 2 * 0.25);

            Assert.NotNull(order);
            Assert.Equal(new[] { 3, 2, 1, 0 }, order);
        }
    }
}
