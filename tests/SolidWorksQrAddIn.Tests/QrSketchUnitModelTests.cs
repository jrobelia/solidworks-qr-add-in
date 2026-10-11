using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// The block-instance-era half of the generator result (spec #10, ADR 0001/0002):
    /// the working matrix carries the fixed 4-cell light quiet zone, the result
    /// emits the QR square and keep-out square as construction geometry plus a
    /// dimension spec, and a unit-proportioned contour set rides alongside the
    /// sized one. Assertions stay on the returned model — never pipeline internals.
    /// </summary>
    public class QrSketchUnitModelTests
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

        // ---------- construction squares + dimension spec ----------

        [Fact]
        public void Qr_square_bounds_the_grown_qr_extent_at_the_requested_size()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            var qrSquare = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.QrSquare);
            Assert.True(qrSquare.IsConstruction);

            // The grown QR-cell extent: S per side, centred on the origin.
            double half = SizeMm / 2.0 / 1000.0;
            Assert.Equal(4, qrSquare.Vertices.Count);
            Assert.Equal(half, qrSquare.Vertices.Max(v => v.X), 9);
            Assert.Equal(-half, qrSquare.Vertices.Min(v => v.X), 9);
            Assert.Equal(half, qrSquare.Vertices.Max(v => v.Y), 9);
            Assert.Equal(-half, qrSquare.Vertices.Min(v => v.Y), 9);
            Assert.Equal(4, qrSquare.Edges.Count());
        }

        [Fact]
        public void Keep_out_square_is_the_working_matrix_boundary()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            var keepOut = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.KeepOutSquare);
            Assert.True(keepOut.IsConstruction);

            // ADR 0002: the working-matrix boundary — (N+8) cell pitches per
            // side, centred on the origin. NOT "4 cells outside the grown QR
            // square" (that would be S + 8p = (N + 2r′ + 8)·p — a larger square).
            double expectedSide = 29 * sketch.Info.PitchMm / 1000.0; // metres
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
        public void Dimension_spec_marks_the_qr_square_driven_and_the_keep_out_equation_linked()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // The QR square carries the driven dim (the live size readout);
            // the keep-out side is equation-linked to it (ADR 0001).
            Assert.Equal(QrSquareRole.QrSquare, sketch.DimensionSpec.DrivenSquare);
            Assert.Equal(QrSquareRole.KeepOutSquare, sketch.DimensionSpec.EquationLinkedSquare);
        }

        [Fact]
        public void Dimension_spec_relates_the_square_sides_in_cell_units()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // keep-out side = QR-square side + (8 − 2r′) cell pitches:
            // (N+8) cells vs (N + 2·0.03) cells → 7.94 pitches.
            var spec = sketch.DimensionSpec;
            Assert.Equal(8.0 - 2.0 * sketch.Info.GrowthFractionPerSide,
                spec.KeepOutSideOffsetCells, 9);
            Assert.Equal(7.94, spec.KeepOutSideOffsetCells, 9);

            // The relation must reconstruct the emitted keep-out side exactly —
            // an independent check that spec and squares tell the same story.
            double qrSideMm = SizeMm;
            double keepOutSideMm = 29 * sketch.Info.PitchMm;
            Assert.Equal(keepOutSideMm,
                qrSideMm + spec.KeepOutSideOffsetCells * sketch.Info.PitchMm, 9);
        }

        // ---------- unit-proportioned contours alongside the sized set ----------

        [Fact]
        public void Unit_contours_span_exactly_one_unit_at_the_grown_outline()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // The block-definition space: the grown QR outline reads 1×1 —
            // finder cells reach all four grown edges, so the extreme
            // coordinates are exactly ±0.5 (the host converts to document
            // units later).
            var xs = sketch.Unit.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            var ys = sketch.Unit.Contours.SelectMany(c => c.Vertices).Select(v => v.Y).ToList();
            Assert.Equal(0.5, xs.Max());
            Assert.Equal(-0.5, xs.Min());
            Assert.Equal(0.5, ys.Max());
            Assert.Equal(-0.5, ys.Min());
        }

        [Fact]
        public void Unit_contours_preserve_the_sized_contour_structure()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // Same model, second coordinate space: identical contour list,
            // hole flags and vertex counts — nothing re-shaped.
            Assert.Equal(sketch.Contours.Count, sketch.Unit.Contours.Count);
            Assert.Equal(sketch.Regions.Count, sketch.Unit.Regions.Count);
            for (var i = 0; i < sketch.Contours.Count; i++)
            {
                Assert.Equal(sketch.Contours[i].IsHole, sketch.Unit.Contours[i].IsHole);
                Assert.Equal(sketch.Contours[i].Vertices.Count,
                    sketch.Unit.Contours[i].Vertices.Count);
            }

            // Nesting preserved: at least one ring→hole→island survives.
            int nestedRings = sketch.Unit.Regions.Count(r =>
                r.Holes.Any(h => h.Islands.Count > 0));
            Assert.True(nestedRings >= 1, "unit regions must keep hole/island nesting");
        }

        [Fact]
        public void Unit_contours_are_centred_and_keep_axis_aligned_shared_endpoints()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            foreach (var contour in sketch.Unit.Contours)
            {
                var edges = contour.Edges.ToList();
                Assert.Equal(contour.Vertices.Count, edges.Count);
                for (var i = 0; i < edges.Count; i++)
                {
                    // Same affine guarantee as the sized set: shared endpoints
                    // are bit-identical and every edge is axis-aligned.
                    var end = edges[i].End;
                    var nextStart = edges[(i + 1) % edges.Count].Start;
                    Assert.Equal(nextStart.X, end.X);
                    Assert.Equal(nextStart.Y, end.Y);
                }
                foreach (var e in edges)
                    Assert.True(e.Start.X == e.End.X || e.Start.Y == e.End.Y,
                        "unit contour edges must be axis-aligned");
            }
        }

        [Fact]
        public void Sized_and_unit_contours_emit_side_by_side()
        {
            // Expand-contract: today's sized metre-space contours are still
            // there, unchanged — the unit set rides alongside, never replaces.
            var sketch = QrSketchGenerator.GenerateFromCells(
                Cells(3, (0, 0), (1, 1), (2, 2)), SizeMm, Growth).Sketch!;

            double half = SizeMm / 2.0 / 1000.0;
            var sizedXs = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            var unitXs = sketch.Unit.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            Assert.True(sizedXs.Max() <= half, "sized set stays inside the grown extent");
            Assert.True(unitXs.Max() <= 0.5, "unit set stays inside the unit outline");

            // The two sets tell the same shape at two scales: unit = sized / S.
            double sizeM = SizeMm / 1000.0;
            var sizedVertex = sketch.Contours[0].Vertices[0];
            var unitVertex = sketch.Unit.Contours[0].Vertices[0];
            Assert.Equal(unitVertex.X, sizedVertex.X / sizeM, 12);
            Assert.Equal(unitVertex.Y, sizedVertex.Y / sizeM, 12);
        }

        [Fact]
        public void Unit_edges_enumerate_every_contour_edge()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            int expected = sketch.Unit.Contours.Sum(c => c.Vertices.Count);
            Assert.Equal(expected, sketch.Unit.Edges.Count());
            Assert.Equal(sketch.Info.SegmentCount, sketch.Unit.Edges.Count());
        }
    }
}
