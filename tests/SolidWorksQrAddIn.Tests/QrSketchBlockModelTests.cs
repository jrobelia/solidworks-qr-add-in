using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// The block-instance-era half of the generator result (spec #10, ADR
    /// 0002/0003): the working matrix carries the fixed 4-cell light quiet
    /// zone, and the result emits the sized contour set plus the QR square
    /// and keep-out square as construction squares in the same sketch-space
    /// metres — no dimensions (ADR 0003 deleted the dimension spec), no
    /// separate unit model (the host re-normalizes the sized set into the
    /// block definition; the sized contours are the single transport and
    /// the preview source). Assertions stay on the returned model — never
    /// pipeline internals.
    /// </summary>
    public class QrSketchBlockModelTests
    {
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        private static bool[,] Cells(int n, params (int row, int col)[] dark)
        {
            var cells = new bool[n, n];
            foreach (var (row, col) in dark)
                cells[row, col] = true;
            return cells;
        }

        // ---------- working matrix: N×N data + fixed 4 light cells/side ----------

        [Fact]
        public void Working_matrix_adds_four_light_cells_per_side_around_the_data()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // Version 1 → 21×21 data cells; the working matrix is (N+8)×(N+8) —
            // the ISO 18004 quiet zone, always present, never a setting (ADR 0002).
            Assert.Equal(21, sketch.Info.CellCount);
            Assert.Equal(4, QrSketchInfo.QuietZoneCellsPerSide);
            Assert.Equal(29, sketch.Info.MatrixCellCount);
        }

        [Fact]
        public void Fixture_matrices_are_padded_with_the_same_quiet_zone()
        {
            var sketch = QrSketchGenerator.GenerateFromCells(
                Cells(3, (0, 0), (2, 2)), SizeMm, Growth).Sketch!;

            Assert.Equal(3, sketch.Info.CellCount);
            Assert.Equal(11, sketch.Info.MatrixCellCount);
        }

        [Fact]
        public void Quiet_zone_cells_stay_light_and_emit_no_geometry()
        {
            // An all-dark data block grows to exactly the requested size — if the
            // padding cells ever emitted, the outline would spill past it into
            // the margin.
            var n = 4;
            var cells = new bool[n, n];
            for (var r = 0; r < n; r++)
                for (var c = 0; c < n; c++)
                    cells[r, c] = true;

            var sketch = QrSketchGenerator.GenerateFromCells(cells, SizeMm, Growth).Sketch!;

            double half = SizeMm / 2.0 / 1000.0; // metres
            var xs = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            var ys = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.Y).ToList();
            Assert.Equal(half, xs.Max(), 9);
            Assert.Equal(-half, xs.Min(), 9);
            Assert.Equal(half, ys.Max(), 9);
            Assert.Equal(-half, ys.Min(), 9);
        }

        [Fact]
        public void Data_cells_sit_centred_inside_the_quiet_zone()
        {
            // One dark cell at the data top-left corner. Its grown edges land on
            // the grown data extent (−S/2 at the left, +S/2 at the top since
            // matrix row 0 maps to +y) — the margin lies outside it untouched.
            var sketch = QrSketchGenerator.GenerateFromCells(
                Cells(4, (0, 0)), SizeMm, Growth).Sketch!;

            double half = SizeMm / 2.0 / 1000.0;
            var contour = Assert.Single(sketch.Contours);
            Assert.Equal(-half, contour.Vertices.Min(v => v.X), 9);
            Assert.Equal(half, contour.Vertices.Max(v => v.Y), 9);
            // …and it does NOT reach the far edges — the data area is centred,
            // not stretched across the matrix.
            Assert.True(contour.Vertices.Max(v => v.X) < half);
            Assert.True(contour.Vertices.Min(v => v.Y) > -half);
        }

        // ---------- construction squares (no dimensions — ADR 0003) ----------

        [Fact]
        public void Qr_square_is_the_grown_outline_in_sketch_metres()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            var qrSquare = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.QrSquare);

            // The square is the grown QR-cell extent at the requested size:
            // ±S/2 in sketch-space metres, staged exactly like the contours —
            // its span is the insertion's authoritative size measurement.
            Assert.Equal(4, qrSquare.Vertices.Count);
            Assert.Equal(0.0125, qrSquare.Vertices.Max(v => v.X), 9);
            Assert.Equal(-0.0125, qrSquare.Vertices.Min(v => v.X), 9);
            Assert.Equal(0.0125, qrSquare.Vertices.Max(v => v.Y), 9);
            Assert.Equal(-0.0125, qrSquare.Vertices.Min(v => v.Y), 9);
            Assert.Equal(4, qrSquare.Edges.Count());
        }

        [Fact]
        public void Keep_out_square_is_the_working_matrix_boundary_in_metres()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            var keepOut = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.KeepOutSquare);

            // ADR 0002: the working-matrix boundary — (N+8) cell pitches per
            // side at the requested size, in the same sketch-space metres as
            // the contours. The host's def normalization shrinks it to the
            // unit-matrix boundary inside the block.
            double expectedSide = sketch.Info.MatrixCellCount * sketch.Info.PitchMm / 1000.0;
            double side = keepOut.Vertices.Max(v => v.X) - keepOut.Vertices.Min(v => v.X);
            Assert.Equal(expectedSide, side, 9);
            Assert.Equal(expectedSide,
                keepOut.Vertices.Max(v => v.Y) - keepOut.Vertices.Min(v => v.Y), 9);
            Assert.Equal(expectedSide / 2.0, keepOut.Vertices.Max(v => v.X), 9);
            Assert.Equal(-expectedSide / 2.0, keepOut.Vertices.Min(v => v.Y), 9);
        }

        [Fact]
        public void Keep_out_square_strictly_contains_the_qr_square()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            var qrSquare = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.QrSquare);
            var keepOut = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.KeepOutSquare);

            // r′ < 0.5 ⇒ N + 2r′ < N + 8 always: the margin band is real.
            Assert.True(keepOut.Vertices.Min(v => v.X) < qrSquare.Vertices.Min(v => v.X));
            Assert.True(keepOut.Vertices.Max(v => v.X) > qrSquare.Vertices.Max(v => v.X));
            Assert.True(keepOut.Vertices.Min(v => v.Y) < qrSquare.Vertices.Min(v => v.Y));
            Assert.True(keepOut.Vertices.Max(v => v.Y) > qrSquare.Vertices.Max(v => v.Y));
        }

        [Fact]
        public void Squares_also_emit_for_fixture_matrices()
        {
            var sketch = QrSketchGenerator.GenerateFromCells(
                Cells(3, (0, 0), (1, 1)), SizeMm, Growth).Sketch!;

            Assert.Equal(2, sketch.ConstructionSquares.Count);
            Assert.Equal(new[] { QrSquareRole.QrSquare, QrSquareRole.KeepOutSquare },
                sketch.ConstructionSquares.Select(s => s.Role).OrderBy(r => r).ToArray());
        }

        [Fact]
        public void Squares_are_closed_loops_no_dimensions_are_emitted()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // ADR 0003 superseded the dimension plan: the squares ride inside
            // the block definition and inherit the instance transform, so the
            // model carries squares only — the size surfaces are the block PM
            // Scale field and the pane readout, never sketch dimensions.
            Assert.Equal(2, sketch.ConstructionSquares.Count);
            foreach (var square in sketch.ConstructionSquares)
            {
                // A square is one closed loop: four vertices, four edges, the
                // last edge closing back to the first vertex.
                Assert.Equal(4, square.Vertices.Count);
                var edges = square.Edges.ToList();
                Assert.Equal(4, edges.Count);
                Assert.Equal(square.Vertices[0].X, edges[3].End.X);
                Assert.Equal(square.Vertices[0].Y, edges[3].End.Y);
            }
        }

        // ---------- the sized contour set: structure + edge enumeration ----------

        [Fact]
        public void Sized_contours_reach_the_grown_outline_on_every_side()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // For a real QR the finder cells reach all four grown edges: the
            // contour set itself spans the requested outline — the same
            // extents the QR square marks.
            var xs = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            var ys = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.Y).ToList();
            Assert.Equal(0.0125, xs.Max(), 9);
            Assert.Equal(-0.0125, xs.Min(), 9);
            Assert.Equal(0.0125, ys.Max(), 9);
            Assert.Equal(-0.0125, ys.Min(), 9);
        }

        [Fact]
        public void Sized_contours_preserve_ring_hole_island_nesting()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // The structure the sketch must reproduce: every contour in the
            // flat list is a region outline, a hole loop, or an island.
            Assert.Equal(sketch.Regions.Sum(r =>
                    1 + r.Holes.Count + r.Holes.Sum(h => h.Islands.Count)),
                sketch.Contours.Count);
            Assert.Contains(sketch.Contours, c => c.IsHole);
            int nestedRings = sketch.Regions.Count(r =>
                r.Holes.Any(h => h.Islands.Count > 0));
            Assert.True(nestedRings >= 1, "the flat set must keep ring→hole→island nesting");
        }

        [Fact]
        public void Sized_contours_are_centred_and_keep_axis_aligned_shared_endpoints()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            foreach (var contour in sketch.Contours)
            {
                var edges = contour.Edges.ToList();
                Assert.Equal(contour.Vertices.Count, edges.Count);
                for (var i = 0; i < edges.Count; i++)
                {
                    // Shared endpoints are bit-identical — the affine map
                    // preserves the closed loops the sketch needs.
                    var end = edges[i].End;
                    var nextStart = edges[(i + 1) % edges.Count].Start;
                    Assert.Equal(nextStart.X, end.X);
                    Assert.Equal(nextStart.Y, end.Y);
                }
                foreach (var e in edges)
                    Assert.True(e.Start.X == e.End.X || e.Start.Y == e.End.Y,
                        "contour edges must be axis-aligned");
            }
        }

        [Fact]
        public void Edges_enumerate_every_contour_edge_in_insertion_order()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            int expected = sketch.Contours.Sum(c => c.Vertices.Count);
            Assert.Equal(expected, sketch.Edges.Count());
            Assert.Equal(sketch.Info.SegmentCount, sketch.Edges.Count());
        }
    }
}
