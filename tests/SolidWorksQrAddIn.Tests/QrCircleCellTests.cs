using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #33 — circle cells, disjoint dots only (spec #13's deviation:
    /// the merged-blob regimes of story 7 are deliberately excluded). Each
    /// emitted cell contributes one K-sided polygon circle centred in its
    /// cell; the shape parameter is the dot size % — the circle diameter as
    /// a fraction of cell pitch. Dots are ALWAYS disjoint: the dot % is
    /// capped so the gap between neighbours (orthogonal and diagonal) clears
    /// the insertion floor in both grid units and mm; near-contact values
    /// reject before any host mutation. K follows cell size so the chord
    /// deviation stays under the min-feature floor while every delivered
    /// edge clears it (docs/INSERTION-GUARD.md). The growth field does not
    /// apply to circles — the layout is the growth-0 map, pitch = S/N.
    /// </summary>
    public class QrCircleCellTests
    {
        private const string Payload = "SW-QR-MVP";
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

        private static bool[,] AllDarkExcept(int n, params (int row, int col)[] light)
        {
            var cells = new bool[n, n];
            for (var r = 0; r < n; r++)
                for (var c = 0; c < n; c++)
                    cells[r, c] = true;
            foreach (var (row, col) in light)
                cells[row, col] = false;
            return cells;
        }

        private static QrSketchRequest CircleRequest(
            string content, double sizeMm, double growth, double dotFraction) =>
            new QrSketchRequest(content, sizeMm, growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = dotFraction,
            };

        private static QrSketchResult CircleCells(
            bool[,] cells, double sizeMm, double growth, double dotFraction,
            bool invert = false) =>
            QrSketchGenerator.GenerateFromCells(cells, sizeMm, growth, invert,
                QrCellShape.Circle, dotFraction);

        // ---------------- the shape-name parse seam ----------------

        [Theory]
        [InlineData("circle", QrCellShape.Circle)]
        [InlineData(" Circle ", QrCellShape.Circle)]
        [InlineData("CIRCLE", QrCellShape.Circle)]
        [InlineData("square", QrCellShape.Square)]
        [InlineData("rounded", QrCellShape.RoundedSquare)]
        public void Shape_names_parse_case_insensitively(string text, QrCellShape expected)
        {
            Assert.True(QrCellShapes.TryParse(text, out QrCellShape shape));
            Assert.Equal(expected, shape);
        }

        [Fact]
        public void Shape_codes_round_trip_for_all_shapes()
        {
            foreach (var shape in new[]
                     { QrCellShape.Square, QrCellShape.RoundedSquare, QrCellShape.Circle })
            {
                Assert.True(QrCellShapes.TryParse(QrCellShapes.CodeFor(shape), out var parsed));
                Assert.Equal(shape, parsed);
            }
        }

        // ---------------- dot emission ----------------

        [Fact]
        public void A_lone_circle_cell_emits_one_polygon_dot_centred_in_its_cell()
        {
            // 3×3 fixture at 25 mm — pitch 25/3 mm; the centre cell emits a
            // dot of diameter = dotFraction · pitch centred on the cell.
            var result = CircleCells(Cells(3, (1, 1)), SizeMm, Growth, 0.5);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            var contour = Assert.Single(sketch.Contours);
            Assert.Equal(QrCellShape.Circle, sketch.Info.CellShape);
            Assert.Equal(0.5, sketch.Info.CellShapeParameter, 9);

            double pitchM = sketch.Info.PitchMm / 1000.0;
            // Inscribed polygon: EVERY vertex lies on the nominal circle —
            // radius f·p/2 about the cell centre (the origin for the lone
            // centre cell). For odd K the extreme vertices need not sit on
            // the axes, so centre/extents are checked radially: within a
            // grid unit of the circle.
            double radiusM = 0.5 * pitchM / 2.0;
            double tolM = 2.0 * sketch.Info.PitchMm / QrInsertionGuard.GridUnitsPerCell / 1000.0;
            foreach (var v in contour.Vertices)
            {
                double dist = System.Math.Sqrt(v.X * v.X + v.Y * v.Y);
                Assert.True(System.Math.Abs(dist - radiusM) <= tolM,
                    $"vertex {dist * 1000.0} mm off-centre vs nominal {radiusM * 1000.0} mm");
            }
            // The dot stays strictly inside its own cell — disjoint by
            // construction: no vertex reaches the cell half-width.
            Assert.True(radiusM < pitchM / 2.0);
            Assert.True(contour.Vertices.Count >= 3,
                "a polygon circle needs at least three sides");
        }

        [Fact]
        public void Emitted_dots_are_always_disjoint_neighbouring_cells_never_merge()
        {
            // A solid 2×2 dark block — under Squares this unions into one
            // contour; under Circle each cell's dot stays its own region
            // even at a near-cap dot size.
            var result = CircleCells(AllDarkExcept(2), 50.0, Growth, 0.9);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.Equal(4, sketch.Regions.Count);
            Assert.Equal(4, sketch.Contours.Count);
            Assert.All(sketch.Contours, c => Assert.False(c.IsHole));
        }

        [Fact]
        public void The_gap_between_neighbouring_dots_clears_the_insertion_floor()
        {
            var result = CircleCells(AllDarkExcept(2), 50.0, Growth, 0.9);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            double pitchM = sketch.Info.PitchMm / 1000.0;
            double radiusM = 0.9 * pitchM / 2.0;

            // Orthogonal gap: the two left/right cells in a row — the right
            // dot's leftmost vertex minus the left dot's rightmost vertex.
            var rows = sketch.Regions
                .GroupBy(r => System.Math.Round(
                    r.Boundary.Vertices.Max(v => v.Y) +
                    r.Boundary.Vertices.Min(v => v.Y), 9))
                .ToList();
            Assert.Equal(2, rows.Count);
            var topRow = rows.OrderBy(g => g.Key).First().ToList();
            Assert.Equal(2, topRow.Count);
            var left = topRow.OrderBy(r => r.Boundary.Vertices.Min(v => v.X)).First();
            var right = topRow.OrderBy(r => r.Boundary.Vertices.Min(v => v.X)).Last();
            double orthGapM = right.Boundary.Vertices.Min(v => v.X)
                - left.Boundary.Vertices.Max(v => v.X);
            Assert.True(orthGapM >= 0.05 / 1000.0,
                $"orthogonal gap {orthGapM * 1000.0} mm must clear the 0.05 mm floor");

            // Diagonal gap: corner-to-corner between the top-left and
            // bottom-right dots — nominally (√2·p − d)·... measure the
            // vertex pair distance between the two diagonal regions.
            var bottomRow = rows.OrderBy(g => g.Key).Last().ToList();
            var bottomRight = bottomRow.OrderBy(r => r.Boundary.Vertices.Min(v => v.X)).Last();
            double best = double.MaxValue;
            foreach (var a in left.Boundary.Vertices)
                foreach (var b in bottomRight.Boundary.Vertices)
                {
                    double dx = a.X - b.X, dy = a.Y - b.Y;
                    best = System.Math.Min(best, System.Math.Sqrt(dx * dx + dy * dy));
                }
            Assert.True(best >= 0.05 / 1000.0,
                $"diagonal gap {best * 1000.0} mm must clear the 0.05 mm floor");
            Assert.True(radiusM > 0 && radiusM < pitchM / 2.0);
        }

        [Fact]
        public void Dot_size_is_measured_off_the_cell_pitch_not_the_grown_cell()
        {
            // Growth does not apply: pitch = S/N exactly, and two requests
            // that differ only in growth emit identical contours.
            var atThree = CircleCells(Cells(3, (1, 1)), SizeMm, 0.03, 0.5).Sketch!;
            var atForty = CircleCells(Cells(3, (1, 1)), SizeMm, 0.40, 0.5).Sketch!;

            var a = atThree.Edges.ToList();
            var b = atForty.Edges.ToList();
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Start.X, b[i].Start.X);
                Assert.Equal(a[i].Start.Y, b[i].Start.Y);
                Assert.Equal(a[i].End.X, b[i].End.X);
                Assert.Equal(a[i].End.Y, b[i].End.Y);
            }
            // And pitch is S/N — growth plays no part in the layout.
            Assert.Equal(SizeMm / 3.0, atThree.Info.PitchMm, 9);
            Assert.Equal(0.0, atThree.Info.GrowthMmPerSide, 12);
        }

        [Fact]
        public void A_zero_growth_circle_request_still_generates()
        {
            // Growth is not consulted at all under Circle — a value that
            // would fail the square guards outright is simply inert.
            var result = CircleCells(Cells(3, (1, 1)), SizeMm, 0.0, 0.5);

            Assert.True(result.Succeeded, result.Error?.Message);
        }

        // ---------------- the dot-size domain ----------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void A_non_positive_or_non_finite_dot_size_is_rejected(double bad)
        {
            var result = CircleCells(Cells(3, (1, 1)), SizeMm, Growth, bad);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
        }

        [Fact]
        public void A_dot_size_over_one_hundred_percent_is_refused_outright()
        {
            // Past 100% of pitch the circles overlap their neighbours —
            // dots must stay disjoint, so the domain stops at 100%.
            var result = CircleCells(Cells(3, (1, 1)), SizeMm, Growth, 1.5);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            Assert.Contains("disjoint", result.Error.Message,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_dot_size_leaving_a_sub_floor_gap_rejects_with_the_cap()
        {
            // 3×3 at 25 mm: pitch 8.33 mm, mmPerUnit 8.33e-4 — the floor is
            // 62 grid units, so the cap sits just under 99.4%. 100% and
            // 99.9% both leave a sub-floor gap; 90% is fine.
            var tangent = CircleCells(AllDarkExcept(3), SizeMm, Growth, 1.0);
            var near = CircleCells(AllDarkExcept(3), SizeMm, Growth, 0.999);
            var clear = CircleCells(AllDarkExcept(3), SizeMm, Growth, 0.9);

            Assert.False(tangent.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, tangent.Error!.Kind);
            Assert.Contains("%", tangent.Error.Message);
            Assert.False(near.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, near.Error!.Kind);
            Assert.True(clear.Succeeded, clear.Error?.Message);
        }

        [Fact]
        public void A_dot_size_too_small_to_polygonize_is_rejected()
        {
            // The dot's polygon edges must clear the floor too — a dot that
            // cannot place even a triangle's chords above it is refused
            // (never silently substituted for a square).
            var result = CircleCells(Cells(3, (1, 1)), SizeMm, Growth, 0.001);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
        }

        [Fact]
        public void The_cap_message_names_the_gap_and_the_bound()
        {
            var result = CircleCells(AllDarkExcept(2), SizeMm, Growth, 1.0);

            Assert.False(result.Succeeded);
            Assert.Contains("gap", result.Error!.Message,
                System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dot", result.Error.Message,
                System.StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- polygon fidelity: K follows cell size ----------------

        [Fact]
        public void Polygon_side_count_grows_with_cell_size_so_chords_stay_fine()
        {
            // Same dot fraction, growing mm-per-unit: the 0.05 mm floor in
            // grid units shrinks, so K rises to keep the chord deviation
            // under it — a larger cell buys a rounder dot.
            var small = CircleCells(Cells(3, (1, 1)), 25.0, Growth, 0.5).Sketch!;
            var large = CircleCells(Cells(3, (1, 1)), 100.0, Growth, 0.5).Sketch!;

            int kSmall = Assert.Single(small.Contours).Vertices.Count;
            int kLarge = Assert.Single(large.Contours).Vertices.Count;
            Assert.True(kLarge > kSmall,
                $"K should follow cell size: {kSmall} sides at 25 mm vs {kLarge} at 100 mm");
        }

        [Fact]
        public void Every_delivered_dot_edge_clears_the_floor()
        {
            var result = CircleCells(AllDarkExcept(3), SizeMm, Growth, 0.8);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(result.Sketch!.Info.MinEdgeMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min edge {result.Sketch.Info.MinEdgeMm} mm");
        }

        // ---------------- invert composition ----------------

        [Fact]
        public void Under_invert_every_light_cell_emits_a_separate_circular_pit()
        {
            // Disjoint dots invert to separate circular pits (spec #13):
            // the emitted polarity flips to light cells across the WHOLE
            // working matrix — a lone dark cell in a 3×3 fixture leaves the
            // other 8 data cells plus the 4-cell quiet-zone margin emitting,
            // 11×11 − 1 = 120 disjoint pits, one region each.
            var cells = AllDarkExcept(3);
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    cells[r, c] = r == 1 && c == 1;   // only the centre cell dark
            var result = CircleCells(cells, SizeMm, Growth, 0.3, invert: true);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.True(sketch.Info.Inverted);
            Assert.Equal(120, sketch.Regions.Count);
            Assert.All(sketch.Regions, r => Assert.Empty(r.Holes));
            // Every pit is its own region — a ring→hole→island nesting can
            // never form because nothing touches.
            Assert.All(sketch.Contours, c => Assert.False(c.IsHole));
        }

        [Fact]
        public void The_pit_sits_at_the_light_cells_centre_under_invert()
        {
            // One light cell in a dark 3×3: under invert the light cells
            // emit — the eight dark data cells stay material while the lone
            // light cell AND the whole quiet-zone margin become disjoint
            // pits. One of them sits exactly at the centre light cell (the
            // origin).
            var result = CircleCells(AllDarkExcept(3, (1, 1)), SizeMm, Growth, 0.5, invert: true);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            // 11×11 working matrix minus the 8 dark data cells.
            Assert.Equal(113, sketch.Regions.Count);
            var centrePit = sketch.Contours.SingleOrDefault(c =>
            {
                double cx = c.Vertices.Average(v => v.X);
                double cy = c.Vertices.Average(v => v.Y);
                return System.Math.Abs(cx) < 1e-5 && System.Math.Abs(cy) < 1e-5;
            });
            Assert.NotNull(centrePit);
        }

        // ---------------- layout: quiet zone and squares ----------------

        [Fact]
        public void The_qr_square_and_keep_out_square_keep_square_semantics()
        {
            // Growth does not apply: pitch = S/N. The QR square is the
            // ±S/2 data extent exactly; the keep-out square is the
            // working-matrix edge (N+8)·p — the same rules squares use.
            var sketch = CircleCells(Cells(3, (1, 1)), SizeMm, Growth, 0.5).Sketch!;

            var qr = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.QrSquare);
            var keepOut = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.KeepOutSquare);
            Assert.Equal(SizeMm / 2.0 / 1000.0, qr.Vertices.Max(v => v.X), 9);
            double pitchM = sketch.Info.PitchMm / 1000.0;
            Assert.Equal((3 + 2 * QrSketchInfo.QuietZoneCellsPerSide) * pitchM / 2.0,
                keepOut.Vertices.Max(v => v.X), 9);
        }

        [Fact]
        public void A_real_payload_emits_disjoint_dots()
        {
            var result = QrSketchGenerator.Generate(CircleRequest(Payload, SizeMm, Growth, 0.5));

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            // Every dark cell its own dot — regions are all bare dots.
            Assert.All(sketch.Regions, r => Assert.Empty(r.Holes));
            Assert.True(sketch.Info.MinEdgeMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min edge {sketch.Info.MinEdgeMm} mm");
            Assert.Equal(SizeMm, sketch.Info.FinalSizeMm, 9);
            Assert.Equal(QrCellShape.Circle, sketch.Info.CellShape);
        }

        // ---------------- preview parity ----------------

        [Fact]
        public void The_preview_renders_the_dots_through_the_same_path()
        {
            var circle = QrSketchGenerator.Generate(
                CircleRequest(Payload, 50.0, Growth, 0.5)).Sketch!;
            var squared = QrSketchGenerator.Generate(Payload, 50.0, Growth).Sketch!;

            var circlePic = QrPictureRenderer.Render(circle, Pixels);
            var squaredPic = QrPictureRenderer.Render(squared, Pixels);

            // Same render path — the signature differs because the geometry
            // does (dots mark less material on the same cells). mm-per-pixel
            // legitimately differs: pitch is S/N under circles.
            Assert.NotEqual(squaredPic.Signature(), circlePic.Signature());
            Assert.True(circlePic.DarkPixelCount < squaredPic.DarkPixelCount,
                "dots mark less material than grown squares on the same cells");
        }

        // ---------------- the session seam ----------------

        private static (QrSketchSession session, List<FakeQrWindow> windows) NewSession()
        {
            var windows = new List<FakeQrWindow>();
            var session = new QrSketchSession(() =>
            {
                var w = new FakeQrWindow();
                windows.Add(w);
                return w;
            });
            return (session, windows);
        }

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        [Fact]
        public void The_growth_field_does_not_apply_under_circle()
        {
            // Like the shape parameter under Square: the growth text is kept
            // verbatim but never parsed — a malformed value cannot block a
            // circle request.
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");

            var result = session.SetGrowthText("not-a-number");

            Assert.False(session.GrowthApplies);
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("not-a-number", session.GrowthText);
            Assert.Null(session.RequestedGrowthFractionPerSide);
            Assert.True(session.HasValidRequest);
        }

        [Fact]
        public void A_bad_growth_text_surfaces_again_back_on_square()
        {
            // The verbatim-kept text is re-validated the moment growth
            // applies again — growth-field discipline, never a silent
            // substitution.
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");
            session.SetGrowthText("not-a-number");

            var result = session.SetCellShape(QrCellShape.Square);

            Assert.True(session.GrowthApplies);
            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGrowth, result.Error!.Kind);
            Assert.Equal("not-a-number", session.GrowthText);
        }

        [Fact]
        public void The_shape_parameter_reads_as_dot_size_under_circle()
        {
            var (session, _) = NewSession();

            session.SetCellShape(QrCellShape.Circle);

            Assert.True(session.ShapeParameterApplies);
            Assert.Contains("dot", session.ShapeParameterLabel,
                System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("pitch", session.ShapeParameterLabel,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_dot_size_edit_revalidates_the_whole_request()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            var squareSignature = session.Picture!.Signature();

            session.SetCellShape(QrCellShape.Circle);
            var result = session.SetShapeParameterText("50");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.NotNull(session.Picture);
            Assert.NotEqual(squareSignature, session.Picture!.Signature());
            Assert.Equal(0.5, session.RequestedShapeParameter!.Value, 9);
        }

        [Fact]
        public void A_bad_dot_size_fails_the_request_with_a_field_error()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);

            var result = session.SetShapeParameterText("not-a-number");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            Assert.Equal("not-a-number", session.ShapeParameterText);
            Assert.Null(session.Picture);
            Assert.Contains("dot", session.StatusText,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_zero_dot_size_is_refused_at_the_field()
        {
            // A 0% dot emits nothing — the field bounds it before the
            // generator has to.
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);

            var result = session.SetShapeParameterText("0");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
        }

        [Fact]
        public void A_near_contact_dot_size_rejects_before_any_create()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("99.9");

            Assert.False(session.HasValidRequest);
            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.CaptureCount);
        }

        [Fact]
        public void The_info_line_names_the_dot_size()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");

            Assert.True(session.HasValidRequest);
            var line = session.InfoLines.Single(l => l.Label == "Cell shape");
            Assert.Contains("Circle", line.Value);
            Assert.Contains("50", line.Value);
        }

        [Fact]
        public void Create_inserts_the_circle_geometry()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(
                CircleRequest(Payload, SizeMm, Growth, 0.5)).Sketch!;
            Assert.Equal(expected.Info.SegmentCount, sketch.ProfileSegments.Count());
        }

        // ---------------- command seam ----------------

        [Fact]
        public void The_command_passes_the_circle_shape_through_to_insertion()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(
                host, CircleRequest(Payload, SizeMm, Growth, 0.5));

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(
                CircleRequest(Payload, SizeMm, Growth, 0.5)).Sketch!;
            Assert.Equal(expected.Info.SegmentCount, sketch.ProfileSegments.Count());
        }
    }
}
