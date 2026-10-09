using System;
using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Preview;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Exercises the preview picture at its rendering seam: a QrPicture raster
    /// produced from the SAME QrSketch contour result that drives sketch
    /// insertion. Assertions inspect pixels (filled regions, holes, margin) —
    /// never renderer internals — and distinguish the grown-union picture from
    /// a raw encoder bitmap.
    /// </summary>
    public class QrPictureRendererTests
    {
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;
        private const int Pixels = 240;

        private static bool[,] Cells(int n, params (int row, int col)[] dark)
        {
            var cells = new bool[n, n];
            foreach (var (row, col) in dark)
                cells[row, col] = true;
            return cells;
        }

        private static QrSketch SketchOf(bool[,] cells) =>
            QrSketchGenerator.GenerateFromCells(cells, SizeMm, Growth).Sketch!;

        /// <summary>Millimetres, centred, y-up — the same space as the sketch.</summary>
        private static bool DarkAt(QrPicture pic, double xMm, double yMm)
        {
            double extentMm = pic.MmPerPixel * pic.WidthPixels;
            int px = (int)Math.Floor((xMm + extentMm / 2.0) / pic.MmPerPixel);
            int py = (int)Math.Floor((extentMm / 2.0 - yMm) / pic.MmPerPixel);
            return pic.IsDark(px, py);
        }

        [Fact]
        public void Picture_is_square_and_spans_the_grown_extent_plus_eight_cells()
        {
            var sketch = SketchOf(Cells(2, (0, 0)));

            var pic = QrPictureRenderer.Render(sketch, Pixels);

            Assert.Equal(Pixels, pic.WidthPixels);
            Assert.Equal(Pixels, pic.HeightPixels);
            // Footprint = final grown outline S + 4 cells of margin per side.
            double expectedExtent = sketch.Info.FinalSizeMm + 8.0 * sketch.Info.PitchMm;
            Assert.Equal(expectedExtent, pic.MmPerPixel * pic.WidthPixels, 9);
            Assert.Equal(4.0 * sketch.Info.PitchMm, pic.MarginMm, 9);
        }

        [Fact]
        public void The_four_cell_margin_is_blank_white_on_all_sides()
        {
            var sketch = SketchOf(Cells(2, (0, 0), (0, 1), (1, 0), (1, 1)));
            var pic = QrPictureRenderer.Render(sketch, Pixels);

            // Every pixel inside the margin band must be white — which also
            // proves no dotted box or border is drawn at the margin edge.
            int marginPx = (int)Math.Floor(pic.MarginMm / pic.MmPerPixel);
            Assert.True(marginPx > 4, "fixture margin should span many pixels");
            for (var y = 0; y < pic.HeightPixels; y++)
                for (var x = 0; x < pic.WidthPixels; x++)
                {
                    bool inMargin = x < marginPx || x >= pic.WidthPixels - marginPx ||
                                    y < marginPx || y >= pic.HeightPixels - marginPx;
                    if (inMargin)
                        Assert.False(pic.IsDark(x, y),
                            $"pixel ({x},{y}) lies in the margin band and must be white");
                }
        }

        [Fact]
        public void Dark_regions_render_the_grown_cell_not_the_raw_cell()
        {
            // Single cell: growth must widen the dark square past the raw cell
            // edges — the picture is of the grown contours, not an encoder bitmap.
            var sketch = SketchOf(Cells(2, (0, 0)));
            var pic = QrPictureRenderer.Render(sketch, Pixels);
            double p = sketch.Info.PitchMm;          // raw cell pitch, mm
            double d = sketch.Info.GrowthMmPerSide;  // grown expansion, mm

            // Raw cell (0,0): x in [-p, 0], y in [0, p] (centred, N=2).
            // Grown: x in [-p-d, +d], y in [-d, p+d].
            Assert.True(DarkAt(pic, -p - d / 2.0, p / 2.0),
                "a point inside the grown outline but outside the raw cell must be dark");
            Assert.True(DarkAt(pic, d / 2.0, -d / 2.0),
                "growth must also extend below/right of the raw cell");
            // Just beyond the grown edge sits the white margin.
            Assert.False(DarkAt(pic, -p - d - p, p / 2.0),
                "outside the grown extent must be white margin");
            Assert.False(DarkAt(pic, 3 * d, p / 2.0),
                "growth is bounded — the picture must not smear past the grown edge");
        }

        [Fact]
        public void Diagonally_grown_cells_fill_their_overlap_region()
        {
            // Two diagonal cells touch at one corner in the raw matrix; grown
            // squares overlap finitely. The overlap point is WHITE in an
            // unmodified encoder bitmap and must be DARK here.
            var sketch = SketchOf(Cells(2, (0, 0), (1, 1)));
            var pic = QrPictureRenderer.Render(sketch, Pixels);
            double d = sketch.Info.GrowthMmPerSide;

            // The corner contact point is the origin; ±d/2 along the diagonal
            // is inside both grown cells but outside both raw cells.
            Assert.True(DarkAt(pic, d / 2.0, d / 2.0),
                "the grown overlap between diagonal cells must render dark");
            Assert.True(DarkAt(pic, -d / 2.0, -d / 2.0));
            // Off the overlap: the remaining inter-cell gaps stay white.
            Assert.False(DarkAt(pic, 1.5 * d, 1.5 * d),
                "past the grown overlap the diagonal gap must stay white");
        }

        [Fact]
        public void Finder_ring_renders_black_ring_white_hole_black_island()
        {
            // 7×7 finder pattern: dark ring, light ring (the hole), dark 3×3
            // centre (the island). Nesting must survive into pixels.
            var n = 7;
            var cells = new bool[n, n];
            for (var r = 0; r < n; r++)
                for (var c = 0; c < n; c++)
                {
                    int ring = Math.Max(Math.Abs(r - 3), Math.Abs(c - 3));
                    cells[r, c] = ring == 3 || ring <= 1;
                }
            var sketch = SketchOf(cells);
            var pic = QrPictureRenderer.Render(sketch, Pixels);
            double p = sketch.Info.PitchMm;

            Assert.True(DarkAt(pic, 0.0, 0.0), "island centre must be dark");
            // Hole ring (light band) two cells left of centre.
            Assert.False(DarkAt(pic, -2.0 * p, 0.0), "the enclosed hole must render white");
            // Outer ring body three cells left of centre.
            Assert.True(DarkAt(pic, -3.0 * p, 0.0), "the ring body must render dark");
        }

        [Fact]
        public void Dark_pixel_area_matches_the_filled_geometry_area()
        {
            // The raster must cover the grown material area: regions minus
            // holes plus islands — an independent cross-check against shoelace
            // areas of the same contours handed to sketch insertion.
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;
            var pic = QrPictureRenderer.Render(sketch, Pixels);

            double materialMm2 = sketch.Regions.Sum(r => FilledAreaMm2(r.Boundary)
                - r.Holes.Sum(h => FilledAreaMm2(h.Boundary)
                    - h.Islands.Sum(i => FilledAreaMm2(i.Boundary))));
            double renderedMm2 = pic.DarkPixelCount * pic.MmPerPixel * pic.MmPerPixel;

            Assert.True(Math.Abs(renderedMm2 - materialMm2) / materialMm2 < 0.05,
                $"rendered {renderedMm2:F2} mm² vs geometric {materialMm2:F2} mm²");
        }

        [Fact]
        public void Matrix_row_zero_renders_at_the_top_of_the_picture()
        {
            // A single dark cell at row 0 / col 0 must appear top-left —
            // matching the sketch's upright (row 0 = +y) orientation.
            var topLeft = QrPictureRenderer.Render(
                SketchOf(Cells(4, (0, 0))), Pixels);
            var bottomLeft = QrPictureRenderer.Render(
                SketchOf(Cells(4, (3, 0))), Pixels);

            var (cx1, cy1) = DarkCentroid(topLeft);
            Assert.True(cx1 < topLeft.WidthPixels / 2.0, "col 0 must render left");
            Assert.True(cy1 < topLeft.HeightPixels / 2.0, "row 0 must render top");

            var (cx2, cy2) = DarkCentroid(bottomLeft);
            Assert.True(cx2 < bottomLeft.WidthPixels / 2.0);
            Assert.True(cy2 > bottomLeft.HeightPixels / 2.0, "last row must render bottom");
        }

        [Fact]
        public void Signature_is_deterministic_and_sensitive_to_the_geometry()
        {
            var a1 = QrPictureRenderer.Render(
                QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!, Pixels);
            var a2 = QrPictureRenderer.Render(
                QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!, Pixels);
            var b = QrPictureRenderer.Render(
                QrSketchGenerator.Generate("https://example.test/qr", SizeMm, Growth).Sketch!, Pixels);

            Assert.Equal(a1.Signature(), a2.Signature());
            Assert.NotEqual(a1.Signature(), b.Signature());
        }

        // ---------- helpers ----------

        private static double FilledAreaMm2(QrContour c)
        {
            double area2 = 0;
            for (var i = 0; i < c.Vertices.Count; i++)
            {
                var a = c.Vertices[i];
                var b = c.Vertices[(i + 1) % c.Vertices.Count];
                area2 += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(area2) / 2.0 * 1e6; // shoelace gives 2×area; m² → mm²
        }

        private static (double cx, double cy) DarkCentroid(QrPicture pic)
        {
            double sx = 0, sy = 0; int n = 0;
            for (var y = 0; y < pic.HeightPixels; y++)
                for (var x = 0; x < pic.WidthPixels; x++)
                    if (pic.IsDark(x, y)) { sx += x; sy += y; n++; }
            return (sx / n, sy / n);
        }
    }
}
