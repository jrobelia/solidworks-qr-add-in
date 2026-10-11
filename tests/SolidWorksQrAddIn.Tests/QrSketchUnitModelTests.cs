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
    }
}
