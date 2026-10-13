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
    /// Ticket #32 — the cell-shape selector and rounded-square cells at the
    /// generator seam. Square stays the default (existing output unchanged);
    /// RoundedSquare emits one polygonized rounded rect per emitted cell
    /// through the same union/validate/convert path. The corner radius — a
    /// fraction of the grown cell's half-width — is capped by the recorded
    /// bounds of docs/INSERTION-GUARD.md: the diagonal-overlap cap ρ ≤ 2g
    /// (growth's finite overlap), the merge-groove band g&lt;ρ&lt;g+F
    /// (spec story 9's near-sliver band) and the primitive's own edge floor;
    /// ρ over 100% of the half-width inverts edges outright. All reject
    /// before any host mutation.
    /// </summary>
    public class QrCellShapeTests
    {
        private const string Payload = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;
        private const int Pixels = 240;

        // At 25 mm / 3% the per-side growth (0.0356 mm) is itself below the
        // 0.05 mm insertion floor, so no radius resolves — the cap tests
        // live there. Valid emission is exercised at 50 mm / 3% (groove
        // regime, ρ ∈ [510, 600] of 5 300-unit half-width — f ≈ 9.6–11.3%)
        // and at 60 mm / 10% (clean regime, ρ ∈ [177, 1000] — f ≈ 3–16.7%).
        private static bool[,] Cells(int n, params (int row, int col)[] dark)
        {
            var cells = new bool[n, n];
            foreach (var (row, col) in dark)
                cells[row, col] = true;
            return cells;
        }

        private static QrSketchRequest RoundedRequest(
            string content, double sizeMm, double growth, double radiusFraction) =>
            new QrSketchRequest(content, sizeMm, growth)
            {
                CellShape = QrCellShape.RoundedSquare,
                CellShapeParameter = radiusFraction,
            };

        private static QrSketchResult RoundedCells(
            bool[,] cells, double sizeMm, double growth, double radiusFraction,
            bool invert = false) =>
            QrSketchGenerator.GenerateFromCells(cells, sizeMm, growth, invert,
                QrCellShape.RoundedSquare, radiusFraction);

        // ---------------- defaults: Square is today's output ----------------

        [Fact]
        public void The_request_defaults_to_square_cells()
        {
            var request = new QrSketchRequest(Payload, SizeMm, Growth);

            Assert.Equal(QrCellShape.Square, request.CellShape);
        }

        [Fact]
        public void Explicit_square_cells_produce_byte_identical_output_to_the_default()
        {
            var expected = QrSketchGenerator.Generate(Payload, SizeMm, Growth).Sketch!;
            var actual = QrSketchGenerator.Generate(new QrSketchRequest(Payload, SizeMm, Growth)
            {
                CellShape = QrCellShape.Square,
            }).Sketch!;

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
        public void A_shape_parameter_on_square_cells_is_inert()
        {
            // The parameter only means something to non-square shapes — a
            // square request never consults it (the pane keeps the field
            // disabled for Square).
            var expected = QrSketchGenerator.Generate(Payload, SizeMm, Growth).Sketch!;
            var actual = QrSketchGenerator.Generate(new QrSketchRequest(Payload, SizeMm, Growth)
            {
                CellShape = QrCellShape.Square,
                CellShapeParameter = 0.9,
            }).Sketch!;

            var expectedEdges = expected.Edges.ToList();
            var actualEdges = actual.Edges.ToList();
            Assert.Equal(expectedEdges.Count, actualEdges.Count);
            for (var i = 0; i < expectedEdges.Count; i++)
            {
                Assert.Equal(expectedEdges[i].Start.X, actualEdges[i].Start.X);
                Assert.Equal(expectedEdges[i].End.Y, actualEdges[i].End.Y);
            }
        }

        [Fact]
        public void A_zero_radius_rounded_cell_is_the_square_output_verbatim()
        {
            var squared = QrSketchGenerator.GenerateFromCells(
                Cells(2, (0, 0), (1, 1)), SizeMm, Growth).Sketch!;
            var rounded = QrSketchGenerator.GenerateFromCells(
                Cells(2, (0, 0), (1, 1)), SizeMm, Growth,
                cellShape: QrCellShape.RoundedSquare, cellShapeParameter: 0.0).Sketch!;

            var a = squared.Edges.ToList();
            var b = rounded.Edges.ToList();
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Start.X, b[i].Start.X);
                Assert.Equal(a[i].End.Y, b[i].End.Y);
            }
        }

        // ---------------- rounded emission ----------------

        [Fact]
        public void A_rounded_cell_emits_one_polygonized_rounded_rect()
        {
            // A lone cell at 50 mm / 3% (ρ = 530, groove regime: two arc
            // facets per corner — one emitted contour, its own outline).
            var result = RoundedCells(Cells(3, (1, 1)), 50.0, Growth, 0.10);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            var contour = Assert.Single(sketch.Contours);
            // More than the 4 corners of the square primitive — the corners
            // are arc facets, so the vertex count is strictly higher.
            Assert.True(contour.Vertices.Count > 4,
                $"a rounded cell must have arc facets, got {contour.Vertices.Count} vertices");

            double half = (sketch.Info.PitchMm / 2.0 + sketch.Info.GrowthMmPerSide) / 1000.0;
            var xs = contour.Vertices.Select(v => v.X).ToList();
            var ys = contour.Vertices.Select(v => v.Y).ToList();
            // The rounded cell keeps the grown square's extents.
            Assert.Equal(half, xs.Max(), 9);
            Assert.Equal(-half, xs.Min(), 9);
            Assert.Equal(half, ys.Max(), 9);
            Assert.Equal(-half, ys.Min(), 9);
            // …but the sharp corners are gone: no vertex sits on a square corner.
            Assert.DoesNotContain(contour.Vertices,
                v => System.Math.Abs(v.X) == half && System.Math.Abs(v.Y) == half);
        }

        [Fact]
        public void A_radius_in_the_clean_regime_polygonizes_with_more_facets()
        {
            // At 60 mm / 10% the floor sits far enough under g that a radius
            // under it (ρ ≤ g — the clean regime, no merge grooves) resolves
            // a multi-facet arc: the cell carries visibly more vertices than
            // the two-facet chamfer pair the groove regime allows.
            var result = RoundedCells(Cells(3, (1, 1)), 60.0, 0.10, 0.155);

            Assert.True(result.Succeeded, result.Error?.Message);
            var contour = Assert.Single(result.Sketch!.Contours);
            Assert.True(contour.Vertices.Count > 12,
                $"ρ ≤ g should allow a finer polygonization than two facets per corner; " +
                $"got {contour.Vertices.Count} vertices");
        }

        [Fact]
        public void Flush_merges_in_the_clean_regime_leave_no_groove()
        {
            // ρ ≤ g: the corner arcs stay inside the growth band, so two
            // side-adjacent cells merge into one outline whose shared
            // boundary stays flat — the two vertices at max y prove no
            // V-groove dips where the cells meet.
            var result = RoundedCells(Cells(2, (0, 0), (0, 1)), 60.0, 0.10, 0.155);

            Assert.True(result.Succeeded, result.Error?.Message);
            var contour = Assert.Single(result.Sketch!.Contours);
            double top = contour.Vertices.Max(v => v.Y);
            Assert.Equal(2, contour.Vertices.Count(v => v.Y == top));
        }

        [Fact]
        public void Over_the_groove_floor_merged_cells_keep_a_sketchable_notch()
        {
            // ρ ∈ [g+F, 2g] at 50 mm / 3%: the V-groove between the two
            // merged cells resolves above the floor — one contour, a real
            // groove on the shared boundary (more than the flush outline's
            // vertices), every delivered edge insertable.
            var result = RoundedCells(Cells(2, (0, 0), (0, 1)), 50.0, Growth, 0.10);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            var contour = Assert.Single(sketch.Contours);
            double top = contour.Vertices.Max(v => v.Y);
            // The groove dips below the top edge between the cells: the
            // boundary visits the top line in two runs (≥4 max-y vertices).
            Assert.True(contour.Vertices.Count(v => v.Y == top) >= 4,
                $"the merge groove should split the top boundary; got {contour.Vertices.Count}");
            Assert.True(sketch.Info.MinEdgeMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min edge {sketch.Info.MinEdgeMm} mm");
        }

        [Fact]
        public void Diagonal_rounded_cells_still_merge_into_one_contour()
        {
            // The finite diagonal overlap growth buys is preserved under the
            // radius cap: two diagonal cells must union into one contour.
            var result = RoundedCells(Cells(2, (0, 0), (1, 1)), 50.0, Growth, 0.10);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Single(result.Sketch!.Regions);
            Assert.Single(result.Sketch.Contours);
        }

        [Fact]
        public void A_real_payload_emits_valid_rounded_contours()
        {
            var result = QrSketchGenerator.Generate(
                RoundedRequest(Payload, 50.0, Growth, 0.10));

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.True(sketch.Info.MinEdgeMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min edge {sketch.Info.MinEdgeMm} mm");
            // Ring→hole→island nesting survives the shape change.
            Assert.True(sketch.Regions.Count(r => r.Holes.Any(h => h.Islands.Count > 0)) >= 1,
                "expected at least one ring→hole→island nesting");
            // The outline is still exactly the requested size.
            double half = 50.0 / 2.0 / 1000.0;
            var xs = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.X);
            Assert.Equal(half, xs.Max(), 9);
            Assert.Equal(QrCellShape.RoundedSquare, sketch.Info.CellShape);
            Assert.Equal(0.10, sketch.Info.CellShapeParameter, 9);
        }

        [Fact]
        public void The_keep_out_and_qr_squares_are_identical_under_either_shape()
        {
            var square = QrSketchGenerator.Generate(Payload, 50.0, Growth).Sketch!;
            var rounded = QrSketchGenerator.Generate(
                RoundedRequest(Payload, 50.0, Growth, 0.10)).Sketch!;

            Assert.Equal(square.ConstructionSquares.Count, rounded.ConstructionSquares.Count);
            for (var i = 0; i < square.ConstructionSquares.Count; i++)
            {
                var a = square.ConstructionSquares[i];
                var b = rounded.ConstructionSquares[i];
                Assert.Equal(a.Role, b.Role);
                Assert.Equal(a.Vertices.Count, b.Vertices.Count);
                for (var j = 0; j < a.Vertices.Count; j++)
                {
                    Assert.Equal(a.Vertices[j].X, b.Vertices[j].X);
                    Assert.Equal(a.Vertices[j].Y, b.Vertices[j].Y);
                }
            }
        }

        // ---------------- the radius cap ----------------

        [Fact]
        public void The_diagonal_overlap_cap_rejects_past_2g_of_radius()
        {
            // ρ > 2g lets the facing corner caps of diagonal cells meet —
            // the finite overlap growth guarantees is lost (derivation in
            // docs/INSERTION-GUARD.md). At 25 mm / 3% that bound is 11.3%
            // of half-width — 15% is over it.
            var result = RoundedCells(Cells(2, (0, 0), (1, 1)), SizeMm, Growth, 0.15);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
            Assert.Contains("radius", result.Error.Message,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_radius_just_under_the_cap_is_accepted_and_one_over_it_rejected()
        {
            // At 60 mm / 10% the diagonal-overlap bound is ρ = 2000 units
            // (33.3% of half-width) and the groove band ends far below —
            // 32% clears, 34% rejects.
            var under = RoundedCells(Cells(2, (0, 0), (1, 1)), 60.0, 0.10, 0.32);
            var over = RoundedCells(Cells(2, (0, 0), (1, 1)), 60.0, 0.10, 0.34);

            Assert.True(under.Succeeded, under.Error?.Message);
            Assert.False(over.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, over.Error!.Kind);
        }

        [Fact]
        public void A_radius_inside_the_merge_groove_band_is_rejected()
        {
            // g < ρ < g+F — the groove where neighbouring cells merge resolves
            // below the floor (spec story 9's near-sliver band). For the
            // two-cell fixture at 25 mm / 3% the floor is 41 grid units, so
            // the band is ρ ∈ (300, 341) — f = 6% lands inside it.
            var result = RoundedCells(Cells(2, (0, 0), (0, 1)), SizeMm, Growth, 0.06);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
            Assert.Contains("groove", result.Error.Message,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_edge_inverting_radius_is_rejected_before_any_geometry()
        {
            // Above 100% of the half-width adjacent corner arcs would pass
            // each other and invert the edge — refused outright.
            var result = RoundedCells(Cells(2, (0, 0), (1, 1)), SizeMm, Growth, 1.5);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            Assert.Contains("radius", result.Error.Message,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(-0.1)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void A_non_finite_or_negative_radius_is_rejected(double bad)
        {
            var result = RoundedCells(Cells(2, (0, 0), (1, 1)), SizeMm, Growth, bad);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
        }

        [Fact]
        public void A_radius_too_small_to_resolve_is_rejected_not_substituted()
        {
            // A near-zero radius leaves even a single corner facet below the
            // floor — refused like growth that rounds to zero.
            var result = RoundedCells(Cells(2, (0, 0), (1, 1)), SizeMm, Growth, 0.001);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
        }

        [Fact]
        public void A_rounded_request_still_clears_the_other_insertion_guards()
        {
            // 5 mm at 3% is below the recorded floor whatever the shape —
            // the lattice guards are unchanged.
            var result = QrSketchGenerator.Generate(
                RoundedRequest(Payload, 5.0, Growth, 0.10));

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
        }

        // ---------------- invert composition ----------------

        [Fact]
        public void Rounded_cells_compose_with_invert()
        {
            // Under invert the emitted polarity flips: the light cells emit
            // rounded rects — one field bounded at the matrix edge with the
            // dark cell left as a pocket. The pocket itself stays a sharp
            // square: around each of its corners the flanking light cells'
            // grown straight edges cross exactly at the corner point and
            // dominate the diagonal cell's corner arc — rounding only
            // surfaces on the emitted cells' own outlines (islands).
            var result = RoundedCells(Cells(3, (1, 1)), 50.0, Growth, 0.10, invert: true);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.True(sketch.Info.Inverted);
            var field = Assert.Single(sketch.Regions);
            var keepOut = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.KeepOutSquare);
            Assert.Equal(keepOut.Vertices.Max(v => v.X),
                field.Boundary.Vertices.Max(v => v.X), 9);
            Assert.Equal(keepOut.Vertices.Min(v => v.X),
                field.Boundary.Vertices.Min(v => v.X), 9);
            Assert.Single(field.Holes);
        }

        [Fact]
        public void An_isolated_light_cell_emits_a_rounded_island_under_invert()
        {
            // The disjoint regime the spec calls "separate rounded pits":
            // a dark ring around one light cell — under invert the field
            // carries the ring as a pocket and the lone emitted light cell
            // sits inside it as an island whose own outline is the rounded
            // rect (arc facets, not a bare rectangle).
            var cells = new bool[3, 3];
            for (var r = 0; r < 3; r++)
                for (var c = 0; c < 3; c++)
                    cells[r, c] = r != 1 || c != 1;
            var result = RoundedCells(cells, 50.0, Growth, 0.10, invert: true);

            Assert.True(result.Succeeded, result.Error?.Message);
            var field = Assert.Single(result.Sketch!.Regions);
            var pocket = Assert.Single(field.Holes);
            var island = Assert.Single(pocket.Islands);
            Assert.True(island.Boundary.Vertices.Count > 4,
                "the emitted light cell's own outline is rounded");
        }

        // ---------------- preview parity ----------------

        [Fact]
        public void The_preview_renders_the_rounded_result_through_the_same_path()
        {
            var rounded = QrSketchGenerator.Generate(
                RoundedRequest(Payload, 50.0, Growth, 0.10)).Sketch!;
            var squared = QrSketchGenerator.Generate(Payload, 50.0, Growth).Sketch!;

            var roundedPic = QrPictureRenderer.Render(rounded, Pixels);
            var squaredPic = QrPictureRenderer.Render(squared, Pixels);

            // Same footprint, same render path — the signature differs
            // because the geometry does (rounded corners emit less material).
            Assert.Equal(squaredPic.MmPerPixel, roundedPic.MmPerPixel, 12);
            Assert.NotEqual(squaredPic.Signature(), roundedPic.Signature());
            Assert.True(roundedPic.DarkPixelCount < squaredPic.DarkPixelCount,
                "rounding removes corner material from the same cells");
        }

        // ---------------- command seam ----------------

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        [Fact]
        public void The_command_passes_the_cell_shape_through_to_insertion()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(
                host, RoundedRequest(Payload, 50.0, Growth, 0.10));

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(
                RoundedRequest(Payload, 50.0, Growth, 0.10)).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                sketch.ProfileSegments.Count());
        }

        // ---------------- the shape-name parse seam ----------------

        [Theory]
        [InlineData("square", QrCellShape.Square)]
        [InlineData(" Square ", QrCellShape.Square)]
        [InlineData("ROUNDED", QrCellShape.RoundedSquare)]
        [InlineData("rounded-square", QrCellShape.RoundedSquare)]
        public void Shape_names_parse_case_insensitively(string text, QrCellShape expected)
        {
            Assert.True(QrCellShapes.TryParse(text, out QrCellShape shape));
            Assert.Equal(expected, shape);
        }

        [Fact]
        public void An_unrecognized_shape_name_is_refused_not_coerced()
        {
            Assert.False(QrCellShapes.TryParse("blob", out _));
        }

        [Fact]
        public void Shape_codes_round_trip()
        {
            foreach (var shape in new[] { QrCellShape.Square, QrCellShape.RoundedSquare })
            {
                Assert.True(QrCellShapes.TryParse(QrCellShapes.CodeFor(shape), out var parsed));
                Assert.Equal(shape, parsed);
            }
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

        [Fact]
        public void The_session_defaults_to_square_cells()
        {
            var (session, _) = NewSession();

            Assert.Equal(QrCellShape.Square, session.CellShape);
            Assert.False(session.ShapeParameterApplies);
        }

        [Fact]
        public void SetCellShape_revalidates_the_whole_request()
        {
            var (session, _) = NewSession();
            session.SetSizeText("50");
            session.SetContent(Payload);
            var squareSignature = session.Picture!.Signature();

            var result = session.SetCellShape(QrCellShape.RoundedSquare);
            result = session.SetShapeParameterText("10");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.NotNull(session.Picture);
            Assert.NotEqual(squareSignature, session.Picture!.Signature());
        }

        [Fact]
        public void A_shape_change_syncs_the_open_window()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            var before = windows[0].SyncCount;

            session.SetCellShape(QrCellShape.RoundedSquare);

            Assert.True(windows[0].SyncCount > before);
        }

        [Fact]
        public void The_shape_parameter_is_inert_under_square()
        {
            // While Square is selected the parameter field is disabled — the
            // session never consults its text, however malformed.
            var (session, _) = NewSession();
            session.SetContent(Payload);

            var result = session.SetShapeParameterText("not-a-number");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("not-a-number", session.ShapeParameterText);
            Assert.True(session.HasValidRequest);
        }

        [Fact]
        public void A_bad_parameter_surfaces_once_the_shape_needs_it()
        {
            // The text is kept verbatim across the shape switch and is
            // re-validated the moment it applies — growth-field discipline.
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetShapeParameterText("not-a-number");

            var result = session.SetCellShape(QrCellShape.RoundedSquare);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);
            Assert.Equal("not-a-number", session.ShapeParameterText);
            Assert.Contains("not-a-number", session.StatusText);
        }

        [Fact]
        public void An_over_cap_parameter_rejects_before_create()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.RoundedSquare);
            session.SetShapeParameterText("150");

            Assert.False(session.HasValidRequest);
            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.CaptureCount);
        }

        [Fact]
        public void Create_inserts_the_rounded_geometry()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetSizeText("50");
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.RoundedSquare);
            session.SetShapeParameterText("10");

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(
                RoundedRequest(Payload, 50.0, Growth, 0.10)).Sketch!;
            Assert.Equal(expected.Info.SegmentCount, sketch.ProfileSegments.Count());
        }
    }
}
