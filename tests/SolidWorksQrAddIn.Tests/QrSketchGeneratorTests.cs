using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Exercises the geometry-generation module at its public seam:
    /// deterministic cell-matrix fixtures through <see cref="QrSketchGenerator.GenerateFromCells"/>
    /// and real encoded payloads through <see cref="QrSketchGenerator.Generate"/>.
    /// Assertions inspect returned geometry and errors — never Clipper internals
    /// or vertex ordering.
    /// </summary>
    public class QrSketchGeneratorTests
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

        [Fact]
        public void Adjacent_dark_cells_merge_into_one_closed_region()
        {
            // Two horizontally adjacent cells — grown rects must union into a
            // single contour with no internal duplicate edge.
            var cells = Cells(2, (0, 0), (0, 1));

            var result = QrSketchGenerator.GenerateFromCells(cells, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            var region = Assert.Single(sketch.Regions);
            Assert.Empty(region.Holes);
            var contour = Assert.Single(sketch.Contours);
            Assert.False(contour.IsHole);
            // One merged outline of a 2×1 grown block: still a rectangle.
            Assert.Equal(4, contour.Vertices.Count);
        }

        [Fact]
        public void Diagonal_dark_cells_overlap_finitely_into_one_contour()
        {
            // Diagonal neighbours touch at one corner only. Growth must turn
            // that point contact into a finite overlap: the union merges them
            // into ONE contour (a residual corner contact would either leave
            // two contours or be rejected by validation).
            var cells = Cells(2, (0, 0), (1, 1));

            var result = QrSketchGenerator.GenerateFromCells(cells, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Single(result.Sketch!.Regions);
            Assert.Single(result.Sketch.Contours);
        }

        [Fact]
        public void Finder_ring_keeps_its_hole_and_nested_filled_centre()
        {
            // QR finder pattern: 7×7 dark ring, light ring, 3×3 dark centre.
            var n = 7;
            var cells = new bool[n, n];
            for (var r = 0; r < n; r++)
                for (var c = 0; c < n; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 3), System.Math.Abs(c - 3));
                    cells[r, c] = ring == 3 || ring <= 1;
                }

            var result = QrSketchGenerator.GenerateFromCells(cells, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;

            var region = Assert.Single(sketch.Regions);
            Assert.False(region.Boundary.IsHole);
            var hole = Assert.Single(region.Holes);
            Assert.True(hole.Boundary.IsHole);
            var island = Assert.Single(hole.Islands);
            Assert.False(island.Boundary.IsHole);
            Assert.Empty(island.Holes);

            // Flattened for insertion: outer ring + hole + centre = 3 contours.
            Assert.Equal(3, sketch.Contours.Count);
            Assert.Equal(3, sketch.Info.ContourCount);
        }

        [Fact]
        public void Island_inside_the_hole_is_spatially_inside_the_hole()
        {
            var n = 7;
            var cells = new bool[n, n];
            for (var r = 0; r < n; r++)
                for (var c = 0; c < n; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 3), System.Math.Abs(c - 3));
                    cells[r, c] = ring == 3 || ring <= 1;
                }

            var sketch = QrSketchGenerator.GenerateFromCells(cells, SizeMm, Growth).Sketch!;
            var hole = sketch.Regions[0].Holes[0];
            var island = hole.Islands[0];

            AssertBBoxInside(island.Boundary, hole.Boundary);
            AssertBBoxInside(hole.Boundary, sketch.Regions[0].Boundary);
        }

        // ---------- real encoded payload ----------

        [Fact]
        public void Fixed_payload_encodes_at_version_1_with_21_data_cells()
        {
            var result = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth);

            Assert.True(result.Succeeded, result.Error?.Message);
            // 9 alphanumeric chars at ECC M → QR version 1 → 21×21 data cells.
            Assert.Equal(1, result.Sketch!.Info.Version);
            Assert.Equal(21, result.Sketch.Info.CellCount);
        }

        [Fact]
        public void Fixed_payload_outline_is_exactly_the_requested_size_and_centred()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            var xs = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            var ys = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.Y).ToList();
            double half = SizeMm / 2.0 / 1000.0; // metres
            double tol = QrInsertionGuard.SizeToleranceMm / 1000.0;

            Assert.Equal(half, xs.Max(), 9);
            Assert.Equal(-half, xs.Min(), 9);
            Assert.Equal(half, ys.Max(), 9);
            Assert.Equal(-half, ys.Min(), 9);
            Assert.Equal(SizeMm, sketch.Info.FinalSizeMm, 9);
            Assert.True(System.Math.Abs(xs.Max() - half) <= tol);
            Assert.True(System.Math.Abs(xs.Min() + half) <= tol);
        }

        [Fact]
        public void Fixed_payload_contains_finder_style_hole_island_nesting()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            // A QR has three finder patterns; each grown ring must enclose a
            // hole that still holds the filled centre island. Whatever the data
            // cells merge around them, at least one nested ring must survive.
            Assert.True(sketch.Regions.Count > 1, "a QR must produce multiple disjoint regions");
            int nestedRings = sketch.Regions.Count(r =>
                r.Holes.Any(h => h.Islands.Count > 0));
            Assert.True(nestedRings >= 1, "expected at least one ring→hole→island nesting");
            Assert.True(sketch.Contours.Count >= 4);
            Assert.Equal(sketch.Contours.Count, sketch.Info.ContourCount);
        }

        [Fact]
        public void Fixed_payload_clears_the_recorded_insertion_guard()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            Assert.True(sketch.Info.MinEdgeMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min edge {sketch.Info.MinEdgeMm} mm");
            Assert.True(sketch.Info.MinLightGapMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min light gap {sketch.Info.MinLightGapMm} mm");
            // pitch p = S/(N + 2r′): 25/(21 + 0.06) ≈ 1.1871 mm; growth ≈ 0.0356 mm.
            Assert.Equal(25.0 / (21 + 0.06), sketch.Info.PitchMm, 4);
            Assert.Equal(sketch.Info.PitchMm * sketch.Info.GrowthFractionPerSide,
                sketch.Info.GrowthMmPerSide, 9);
        }

        [Fact]
        public void Every_contour_is_closed_with_nonzero_area_and_finite_coordinates()
        {
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            foreach (var contour in sketch.Contours)
            {
                Assert.True(contour.Vertices.Count >= 3);
                foreach (var v in contour.Vertices)
                {
                    Assert.False(double.IsNaN(v.X) || double.IsInfinity(v.X));
                    Assert.False(double.IsNaN(v.Y) || double.IsInfinity(v.Y));
                }
                // Nonzero signed area (shoelace) — independent check of returned geometry.
                double area2 = 0;
                for (var i = 0; i < contour.Vertices.Count; i++)
                {
                    var a = contour.Vertices[i];
                    var b = contour.Vertices[(i + 1) % contour.Vertices.Count];
                    area2 += a.X * b.Y - b.X * a.Y;
                }
                Assert.True(System.Math.Abs(area2) > 1e-10, "contour encloses zero area");
            }
        }

        [Fact]
        public void Shared_endpoints_are_bit_identical_between_adjacent_edges()
        {
            // The affine grid→physical map must preserve shared endpoints
            // exactly — SolidWorks contour closure depends on it. Exact ==,
            // no epsilon: the same grid coordinate maps to the same double.
            var sketch = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            foreach (var contour in sketch.Contours)
            {
                var edges = contour.Edges.ToList();
                Assert.Equal(contour.Vertices.Count, edges.Count);
                for (var i = 0; i < edges.Count; i++)
                {
                    var end = edges[i].End;
                    var nextStart = edges[(i + 1) % edges.Count].Start;
                    Assert.Equal(nextStart.X, end.X);
                    Assert.Equal(nextStart.Y, end.Y);
                }
                // Every endpoint is axis-aligned with its neighbour (rect union
                // output): one of X/Y must match exactly along each edge.
                foreach (var e in edges)
                    Assert.True(e.Start.X == e.End.X || e.Start.Y == e.End.Y,
                        "contour edges must be axis-aligned");
            }
        }

        // ---------- input validation / actionable errors ----------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Empty_content_is_rejected_with_an_actionable_error(string? content)
        {
            var result = QrSketchGenerator.Generate(content!, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.EmptyContent, result.Error!.Kind);
            Assert.Contains("content", result.Error.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Invalid_size_is_rejected(double badSize)
        {
            var result = QrSketchGenerator.Generate("SW-QR-MVP", badSize, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidSize, result.Error!.Kind);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.03)]
        [InlineData(0.5)]
        [InlineData(0.9)]
        [InlineData(double.NaN)]
        public void Growth_outside_open_0_to_50_percent_is_rejected(double badGrowth)
        {
            var result = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, badGrowth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGrowth, result.Error!.Kind);
        }

        [Fact]
        public void Growth_that_rounds_to_zero_on_the_grid_is_rejected_not_substituted()
        {
            // 0.004% of a cell < 1 grid unit → the overlap would vanish.
            var result = QrSketchGenerator.GenerateFromCells(
                Cells(2, (0, 0), (1, 1)), SizeMm, 0.00004);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.GrowthRoundsToZero, result.Error!.Kind);
            Assert.Contains("increase", result.Error.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Growth_close_to_50_percent_that_would_close_light_gaps_is_rejected()
        {
            // 49.9996% rounds to ≥ half a cell on the grid → light channels vanish.
            var result = QrSketchGenerator.GenerateFromCells(
                Cells(3, (0, 0), (0, 2), (2, 0), (2, 2)), SizeMm, 0.49996);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.GrowthClosesGaps, result.Error!.Kind);
        }

        [Fact]
        public void Size_too_small_to_resolve_grown_features_is_rejected()
        {
            // N=21, r=3%: smallest feature 2d = 2r·S/(N+2r) < 0.05 mm when
            // S ≲ 17.5 mm — a 5 mm request must be refused, not substituted.
            var result = QrSketchGenerator.Generate("SW-QR-MVP", 5.0, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
            Assert.Contains("guard", result.Error.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Tiny_growth_below_the_grid_resolution_floor_is_rejected()
        {
            // g = 2 units → min feature 4 units < 10-unit floor.
            var result = QrSketchGenerator.GenerateFromCells(
                Cells(2, (0, 0)), SizeMm, 0.0002);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
        }

        [Fact]
        public void Payload_beyond_qr_capacity_is_rejected_with_an_actionable_error()
        {
            var result = QrSketchGenerator.Generate(new string('A', 4000), SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.PayloadTooLong, result.Error!.Kind);
            Assert.Contains("Shorten", result.Error.Message);
        }

        [Fact]
        public void A_longer_but_valid_payload_still_produces_the_requested_size()
        {
            // ~200 alphanumeric chars → much denser QR (larger N), still valid.
            var content = new string('K', 200);
            var result = QrSketchGenerator.Generate(content, 40.0, 0.10);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(40.0, result.Sketch!.Info.FinalSizeMm, 9);
        }

        [Fact]
        public void Matrix_with_no_dark_cells_is_rejected()
        {
            var result = QrSketchGenerator.GenerateFromCells(new bool[3, 3], SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGeometry, result.Error!.Kind);
        }

        // ---------- error-correction level ----------

        [Fact]
        public void The_request_defaults_to_error_correction_M()
        {
            // M is the historical behaviour — the request must default to it
            // so the selector-free entry points change nothing.
            var request = new QrSketchRequest("SW-QR-MVP", SizeMm, Growth);

            Assert.Equal(QRCodeGenerator.ECCLevel.M, request.EccLevel);
        }

        [Fact]
        public void The_default_request_produces_the_same_geometry_as_before()
        {
            // The convenience entry point is the request at its defaults —
            // the encoded matrix must be byte-identical, not just similar.
            var expected = QrSketchGenerator.Generate("SW-QR-MVP", SizeMm, Growth).Sketch!;

            var actual = QrSketchGenerator
                .Generate(new QrSketchRequest("SW-QR-MVP", SizeMm, Growth)).Sketch!;

            Assert.Equal(expected.Info.Version, actual.Info.Version);
            Assert.Equal(expected.Info.SegmentCount, actual.Info.SegmentCount);
            var expectedEdges = expected.Edges.ToList();
            var actualEdges = actual.Edges.ToList();
            Assert.Equal(expectedEdges.Count, actualEdges.Count);
            for (var i = 0; i < expectedEdges.Count; i++)
            {
                Assert.Equal(expectedEdges[i].Start.X, actualEdges[i].Start.X);
                Assert.Equal(expectedEdges[i].Start.Y, actualEdges[i].Start.Y);
                Assert.Equal(expectedEdges[i].End.X, actualEdges[i].End.X);
                Assert.Equal(expectedEdges[i].End.Y, actualEdges[i].End.Y);
            }
        }

        [Fact]
        public void A_denser_ecc_level_changes_the_encoded_matrix()
        {
            // "SW-QR-MVP12" is 11 alphanumeric characters: ECC L still fits
            // QR version 1 (N=21) while H needs version 2 (N=25) — the level
            // on the request is what chooses the matrix, nothing else.
            var light = QrSketchGenerator.Generate(new QrSketchRequest(
                "SW-QR-MVP12", SizeMm, Growth) { EccLevel = QRCodeGenerator.ECCLevel.L });
            var dense = QrSketchGenerator.Generate(new QrSketchRequest(
                "SW-QR-MVP12", SizeMm, Growth) { EccLevel = QRCodeGenerator.ECCLevel.H });

            Assert.True(light.Succeeded, light.Error?.Message);
            Assert.True(dense.Succeeded, dense.Error?.Message);
            Assert.Equal(1, light.Sketch!.Info.Version);
            Assert.Equal(21, light.Sketch.Info.CellCount);
            Assert.Equal(2, dense.Sketch!.Info.Version);
            Assert.Equal(25, dense.Sketch.Info.CellCount);
        }

        [Fact]
        public void A_payload_too_long_for_one_level_can_still_encode_at_a_lighter_one()
        {
            // 4000 alphanumeric characters exceed the version-40 capacity at
            // M but not at L — the level on the request must actually reach
            // the encoder. (At 25 mm the dense L matrix then trips the
            // insertion guard instead; what matters here is that it is not a
            // CAPACITY refusal.)
            var request = new QrSketchRequest(new string('A', 4000), SizeMm, Growth);

            request.EccLevel = QRCodeGenerator.ECCLevel.M;
            var atM = QrSketchGenerator.Generate(request);
            Assert.False(atM.Succeeded);
            Assert.Equal(QrSketchErrorKind.PayloadTooLong, atM.Error!.Kind);

            request.EccLevel = QRCodeGenerator.ECCLevel.L;
            var atL = QrSketchGenerator.Generate(request);
            Assert.True(atL.Error?.Kind != QrSketchErrorKind.PayloadTooLong,
                $"the encoder should accept the payload at L, got {atL.Error?.Kind}");
        }

        [Theory]
        [InlineData(QRCodeGenerator.ECCLevel.M)]
        [InlineData(QRCodeGenerator.ECCLevel.H)]
        public void The_capacity_error_names_the_level_in_force(QRCodeGenerator.ECCLevel level)
        {
            var request = new QrSketchRequest(new string('A', 4000), SizeMm, Growth)
            {
                EccLevel = level,
            };

            var result = QrSketchGenerator.Generate(request);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.PayloadTooLong, result.Error!.Kind);
            Assert.Contains($"error correction {level}", result.Error.Message);
        }

        [Theory]
        [InlineData(QRCodeGenerator.ECCLevel.L)]
        [InlineData(QRCodeGenerator.ECCLevel.M)]
        [InlineData(QRCodeGenerator.ECCLevel.Q)]
        [InlineData(QRCodeGenerator.ECCLevel.H)]
        public void The_insertion_guard_applies_at_every_level(QRCodeGenerator.ECCLevel level)
        {
            // A 5 mm request is below the recorded insertion guard whatever
            // the level — denser matrices are not exempt.
            var request = new QrSketchRequest("SW-QR-MVP", 5.0, Growth) { EccLevel = level };

            var result = QrSketchGenerator.Generate(request);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
        }

        private static (double minX, double maxX, double minY, double maxY) BBox(QrContour c) =>
            (c.Vertices.Min(v => v.X), c.Vertices.Max(v => v.X),
             c.Vertices.Min(v => v.Y), c.Vertices.Max(v => v.Y));

        private static void AssertBBoxInside(QrContour inner, QrContour outer)
        {
            var i = BBox(inner);
            var o = BBox(outer);
            Assert.True(i.minX > o.minX && i.maxX < o.maxX &&
                        i.minY > o.minY && i.maxY < o.maxY,
                "inner contour bounding box must lie strictly inside the outer one");
        }
    }
}
