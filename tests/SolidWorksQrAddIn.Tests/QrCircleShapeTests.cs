using System;
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
    /// Exercises the Circle cell shape end to end at the public seams (spec
    /// #13, ticket #38): the session's shape/dot-size fields and inert-field
    /// discipline, the command's plan staging through ISketchInsertion (the
    /// fake realizes it as per-cell circles + plain construction squares —
    /// no block), and the preview's analytic disk rasterization. The
    /// generator-level plan contract lives in <see cref="QrCirclePlanTests"/>;
    /// this file proves the seams around it.
    /// </summary>
    public class QrCircleShapeTests
    {
        private const string Payload = "SW-QR-MVP";
        private const double SizeMm = 25.0;

        private static QrSketchSession NewSession() =>
            new QrSketchSession(() => new FakeQrWindow());

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        private static QrSketchRequest CircleRequest(double dotFraction = 0.90) =>
            new QrSketchRequest(Payload, SizeMm, 0.0)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = dotFraction,
            };

        /// <summary>
        /// A session with a valid Circle request staged — content set, shape
        /// picked — the state the pane sits in before Create.
        /// </summary>
        private static QrSketchSession CircleSession(double dotPercent = 90.0)
        {
            var session = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            if (dotPercent != 90.0)
                session.SetShapeParameterText(
                    dotPercent.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return session;
        }

        // ---------------- session state + inert fields ----------------

        [Fact]
        public void Square_is_the_default_shape_and_dot_size_defaults_high()
        {
            var session = NewSession();

            Assert.Equal(QrCellShape.Square, session.CellShape);
            Assert.True(session.GrowthApplies);
            Assert.False(session.ShapeParameterApplies);
            Assert.Equal("90", session.ShapeParameterText);
            // Inert under Square: the parsed parameter is never carried.
            Assert.Null(session.RequestedShapeParameter);
        }

        [Fact]
        public void Picking_circle_revalidates_and_flips_field_applicability()
        {
            var session = NewSession();
            session.SetContent(Payload);

            var result = session.SetCellShape(QrCellShape.Circle);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(QrCellShape.Circle, session.CellShape);
            Assert.False(session.GrowthApplies, "growth is inert under Circle");
            Assert.True(session.ShapeParameterApplies);
            // The default dot size parses and is reported as the in-force value.
            Assert.Equal(0.90, session.RequestedShapeParameter!.Value, 9);
            // Growth is never consulted under Circle — its parsed value is not carried.
            Assert.Null(session.RequestedGrowthFractionPerSide);
            Assert.NotNull(session.Picture);
        }

        [Fact]
        public void Circle_result_carries_a_circle_plan_and_no_contours()
        {
            var session = NewSession();
            session.SetContent(Payload);
            var result = session.SetCellShape(QrCellShape.Circle);

            var sketch = result.Sketch!;
            Assert.NotNull(sketch.CirclePlan);
            Assert.Empty(sketch.Contours);
            Assert.Empty(sketch.Edges);
            Assert.Equal(QrCellShape.Circle, sketch.Info.CellShape);
            Assert.Equal(0.90, sketch.Info.CellShapeParameter, 9);
            Assert.Equal(sketch.CirclePlan!.EmittedCount, sketch.Info.InstanceCount);
            Assert.Equal(0, sketch.Info.SegmentCount);
        }

        [Fact]
        public void Growth_text_is_kept_verbatim_and_inert_under_circle()
        {
            var session = NewSession();
            session.SetContent(Payload);
            session.SetGrowthText("7.5");

            session.SetCellShape(QrCellShape.Circle);

            // The entered text survives — the pane's disabled field still
            // shows it — while the generated request never consults it.
            Assert.Equal("7.5", session.GrowthText);
            Assert.Null(session.RequestedGrowthFractionPerSide);
            var info = session.CurrentResult!.Sketch!.Info;
            Assert.Equal(0.0, info.GrowthFractionPerSide);

            // ...and switching back to Square makes it apply again.
            session.SetCellShape(QrCellShape.Square);
            Assert.Equal(0.075, session.RequestedGrowthFractionPerSide!.Value, 9);
        }

        [Fact]
        public void Invalid_growth_text_cannot_fail_a_circle_request()
        {
            var session = NewSession();
            session.SetContent(Payload);
            var bad = session.SetGrowthText("bogus");
            Assert.False(bad.Succeeded);   // invalid under Square

            var circled = session.SetCellShape(QrCellShape.Circle);

            Assert.True(circled.Succeeded, circled.Error?.Message);
            Assert.Equal("bogus", session.GrowthText);   // kept verbatim
            // Switching back re-surfaces the field's own error — the text
            // was never silently repaired.
            var back = session.SetCellShape(QrCellShape.Square);
            Assert.False(back.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGrowth, back.Error!.Kind);
        }

        [Fact]
        public void Invalid_dot_size_fails_with_the_parameter_error_kind()
        {
            var session = CircleSession();

            var result = session.SetShapeParameterText("bogus");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            Assert.Equal("bogus", session.ShapeParameterText);   // verbatim, never coerced
            Assert.Null(session.Picture);                        // no stale preview
            Assert.False(session.HasValidRequest);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-20")]
        [InlineData("abc")]
        public void Dot_size_rejects_non_positive_and_non_numeric_text(string text)
        {
            var session = CircleSession();
            var result = session.SetShapeParameterText(text);
            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
        }

        [Fact]
        public void Dot_size_at_or_over_the_per_request_cap_fails_before_insertion()
        {
            var session = CircleSession();
            // 25 mm / 21 cells → pitch ≈ 1.19 mm → cap ≈ 95.8%; 99% must refuse.
            var result = session.SetShapeParameterText("99");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            Assert.Contains("insertion floor", result.Error!.Message);
        }

        [Fact]
        public void Bad_dot_size_text_is_inert_under_square_and_surfaces_under_circle()
        {
            var session = NewSession();
            session.SetContent(Payload);
            var ok = session.SetShapeParameterText("xyz");
            Assert.True(ok.Succeeded, ok.Error?.Message);   // inert under Square
            Assert.Equal("xyz", session.ShapeParameterText);

            var circled = session.SetCellShape(QrCellShape.Circle);
            Assert.False(circled.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, circled.Error!.Kind);
            Assert.Equal("xyz", session.ShapeParameterText);   // never silently repaired
        }

        [Fact]
        public void Circle_info_lines_report_shape_dot_size_and_instance_count()
        {
            var session = CircleSession();
            var info = session.CurrentResult!.Sketch!.Info;

            var shape = session.InfoLines.SingleOrDefault(l => l.Label == "Cell shape");
            Assert.NotNull(shape);
            Assert.Contains("Circle", shape!.Value);
            Assert.Contains("90", shape.Value);
            var dots = session.InfoLines.SingleOrDefault(l => l.Label == "Dots");
            Assert.NotNull(dots);
            Assert.Equal(info.InstanceCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture), dots!.Value);
            // Segment count is meaningless for circle entities — no such row.
            Assert.DoesNotContain(session.InfoLines,
                l => l.Label.IndexOf("segment", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Square_info_lines_report_the_square_shape_without_dot_facts()
        {
            var session = NewSession();
            session.SetContent(Payload);

            var shape = session.InfoLines.SingleOrDefault(l => l.Label == "Cell shape");
            Assert.NotNull(shape);
            Assert.Equal("Square", shape!.Value);
            Assert.Null(session.InfoLines.SingleOrDefault(l => l.Label == "Dots"));
        }

        // ---------------- command path ----------------

        [Fact]
        public void Circle_create_delivers_one_circle_per_emitted_cell_and_no_block()
        {
            var host = HostWithSelectedPlane();
            var result = new CreateQrSketchCommand().Execute(host, CircleRequest());

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var plan = sketch.CirclePlan;
            Assert.NotNull(plan);
            Assert.Empty(sketch.BlockInstances);          // no block in circle mode
            Assert.Empty(sketch.ProfileSegments);         // no contour segments
            Assert.Equal(plan!.EmittedCount, sketch.Circles.Count);
            // Every circle is a true dot: the plan's diameter at an emitted
            // cell's centre — the whole mask lands, nothing else.
            foreach (var circle in sketch.Circles)
            {
                Assert.Equal(plan.DotDiameterM, circle.Diameter, 12);
            }
            // The seed lands on the data corner — data cell (0,0) at matrix
            // (4,4) under normal polarity, the guaranteed-dark finder cell.
            var seed = plan.CellCentreM(plan.PatternOriginRow, plan.PatternOriginCol);
            Assert.Contains(sketch.Circles, c =>
                Math.Abs(c.CentreX - seed.X) < 1e-12 &&
                Math.Abs(c.CentreY - seed.Y) < 1e-12);
            // And every emitted cell is represented exactly once.
            var expected = new List<SketchPoint2>();
            for (var r = 0; r < plan.CellsPerSide; r++)
                for (var c = 0; c < plan.CellsPerSide; c++)
                    if (plan.Emits(r, c))
                        expected.Add(plan.CellCentreM(r, c));
            Assert.Equal(expected.Count, sketch.Circles.Count);
            foreach (var centre in expected)
                Assert.Contains(sketch.Circles, c =>
                    Math.Abs(c.CentreX - centre.X) < 1e-12 &&
                    Math.Abs(c.CentreY - centre.Y) < 1e-12);
        }

        [Fact]
        public void Circle_create_lays_the_construction_squares_beside_the_dots()
        {
            var host = HostWithSelectedPlane();
            var result = new CreateQrSketchCommand().Execute(host, CircleRequest());

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            // No block carries them — the two squares land as plain
            // construction segments in the sketch itself.
            Assert.Equal(8, sketch.Segments.Count);
            Assert.All(sketch.Segments, s => Assert.True(s.Construction));
        }

        [Fact]
        public void Circle_create_reports_the_dot_pattern_and_registers_undo_with_the_plan()
        {
            var host = HostWithSelectedPlane();
            var result = new CreateQrSketchCommand().Execute(host, CircleRequest());

            Assert.True(result.Succeeded, result.Message);
            Assert.Contains("circle-dot pattern", result.Message);
            var registration = host.UndoRegistrations.Last();
            Assert.NotNull(registration.CirclePlan);
            Assert.Equal(registration.SketchName, result.CreatedSketchName);
        }

        [Fact]
        public void Circle_create_through_the_session_uses_the_same_plan_path()
        {
            var host = HostWithSelectedPlane();
            var session = CircleSession();

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            Assert.NotNull(sketch.CirclePlan);
            Assert.Equal(sketch.CirclePlan!.EmittedCount, sketch.Circles.Count);
        }

        [Fact]
        public void Circle_realize_failure_aborts_and_reports_the_cleanup()
        {
            var host = HostWithSelectedPlane();
            host.FailCirclePattern = true;

            var result = new CreateQrSketchCommand().Execute(host, CircleRequest());

            Assert.False(result.Succeeded);
            Assert.Contains("Failed to create the sketch", result.Message);
            Assert.Contains("was removed", result.Message);
            Assert.Empty(host.Document!.Sketches);
            Assert.Null(host.Document.ActiveSketch);
        }

        [Fact]
        public void Rejected_circle_dot_size_never_touches_the_host()
        {
            var host = HostWithSelectedPlane();
            var result = new CreateQrSketchCommand().Execute(
                host, CircleRequest(dotFraction: 1.5));

            Assert.False(result.Succeeded);
            // Generation failed BEFORE BeginSketchInsertion — no sketch, no
            // undo step, no state mutation of any kind.
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.BeginCount);
        }

        // ---------------- Square byte-identity regression ----------------
        // Ticket #38 must leave the Square output untouched: identical
        // requests stay byte-identical to the historical pipeline, whatever
        // the new fields carry.

        /// <summary>A deterministic byte fingerprint of the whole sketch payload.</summary>
        private static string SketchFingerprint(QrSketch sketch)
        {
            var bytes = new List<byte>();
            void Add(double v) => bytes.AddRange(BitConverter.GetBytes(v));
            foreach (var edge in sketch.Edges)
            {
                Add(edge.Start.X); Add(edge.Start.Y);
                Add(edge.End.X); Add(edge.End.Y);
            }
            foreach (var square in sketch.ConstructionSquares)
                foreach (var e in square.Edges)
                {
                    Add(e.Start.X); Add(e.Start.Y); Add(e.End.X); Add(e.End.Y);
                }
            Add(sketch.Info.PitchMm);
            Add(sketch.Info.GrowthMmPerSide);
            Add(sketch.Info.MinEdgeMm);
            Add(sketch.Info.MinLightGapMm);
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(
                    sha.ComputeHash(bytes.ToArray())).Replace("-", "");
        }

        [Fact]
        public void A_square_request_with_defaults_is_identical_to_the_legacy_overload()
        {
            var legacy = QrSketchGenerator.Generate(Payload, SizeMm, 0.03).Sketch!;
            var explicitSquare = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, 0.03)
                {
                    CellShape = QrCellShape.Square,
                    CellShapeParameter = 0.90,   // inert under Square
                }).Sketch!;

            Assert.Equal(SketchFingerprint(legacy), SketchFingerprint(explicitSquare));
            Assert.Equal(legacy.Info.SegmentCount, explicitSquare.Info.SegmentCount);
            Assert.Equal(legacy.Info.CellCount, explicitSquare.Info.CellCount);
        }

        [Fact]
        public void The_square_payload_fingerprint_is_pinned()
        {
            // The recorded fingerprint of the unchanged pipeline for the
            // canonical 25 mm / 3% request — any drift in contour output,
            // ordering, or squares trips this test.
            var sketch = QrSketchGenerator.Generate(Payload, SizeMm, 0.03).Sketch!;
            Assert.Equal(
                "5A240390111FEF5346145C8B7C208B2137EE64632D91D49523F044E53E0CB22A",
                SketchFingerprint(sketch));
        }

        // ---------------- preview: true disks ----------------

        /// <summary>Millimetres, centred, y-up — the same space as the sketch.</summary>
        private static bool DarkAt(QrPicture pic, double xMm, double yMm)
        {
            double extentMm = pic.MmPerPixel * pic.WidthPixels;
            int px = (int)Math.Floor((xMm + extentMm / 2.0) / pic.MmPerPixel);
            int py = (int)Math.Floor((extentMm / 2.0 - yMm) / pic.MmPerPixel);
            return pic.IsDark(px, py);
        }

        private static bool[,] Cells(int n, params (int row, int col)[] dark)
        {
            var cells = new bool[n, n];
            foreach (var (row, col) in dark)
                cells[row, col] = true;
            return cells;
        }

        [Fact]
        public void Circle_preview_renders_a_true_disk_not_a_cell_square()
        {
            // One emitting cell — plus the data-corner seed (always dark on
            // a real QR; included here so the fixture is self-consistent).
            var cells = Cells(7, (0, 0), (3, 3));
            var sketch = QrSketchGenerator.GenerateFromCells(
                cells, 100.0, 0.05, invert: false,
                QrCellShape.Circle, 0.7).Sketch!;
            var pic = QrPictureRenderer.Render(sketch, 240);
            var plan = sketch.CirclePlan!;
            double pitchMm = plan.PitchM * 1000.0;
            double radiusMm = plan.DotDiameterM * 1000.0 / 2.0;

            // Centre of the isolated cell's dot: matrix (7,7) — data (3,3).
            var centre = plan.CellCentreM(4 + 3, 4 + 3);
            double cxMm = centre.X * 1000.0, cyMm = centre.Y * 1000.0;
            Assert.True(DarkAt(pic, cxMm, cyMm), "the dot centre must be dark");
            // Mid-radius points stay dark in every direction.
            Assert.True(DarkAt(pic, cxMm + radiusMm * 0.7, cyMm));
            Assert.True(DarkAt(pic, cxMm, cyMm + radiusMm * 0.7));
            // The cell CORNER — dark for a square cell — stays white: the
            // disk does not fill its cell.
            double corner = pitchMm / 2.0 * 0.96;
            Assert.False(DarkAt(pic, cxMm + corner, cyMm + corner),
                "a disk must not reach its cell corner — a grown square would");
            // Past the radius on the axis: white again — the dot is bounded.
            Assert.False(DarkAt(pic, cxMm + radiusMm + pitchMm * 0.06, cyMm));
        }

        [Fact]
        public void Circle_preview_dots_stay_disjoint_between_emitting_cells()
        {
            var cells = Cells(7, (0, 0), (3, 3), (3, 4));
            var sketch = QrSketchGenerator.GenerateFromCells(
                cells, 100.0, 0.05, invert: false,
                QrCellShape.Circle, 0.8).Sketch!;
            var pic = QrPictureRenderer.Render(sketch, 240);
            var plan = sketch.CirclePlan!;

            var a = plan.CellCentreM(4 + 3, 4 + 3);
            var b = plan.CellCentreM(4 + 3, 4 + 4);
            double midX = (a.X + b.X) * 500.0;   // metres→mm midpoint
            double midY = (a.Y + b.Y) * 500.0;
            Assert.False(DarkAt(pic, midX, midY),
                "the dot-to-dot gap must render light — dots never merge");
            Assert.True(DarkAt(pic, a.X * 1000.0, a.Y * 1000.0));
            Assert.True(DarkAt(pic, b.X * 1000.0, b.Y * 1000.0));
        }

        [Fact]
        public void Circle_preview_dark_area_matches_the_disk_area()
        {
            var sketch = QrSketchGenerator.Generate(CircleRequest()).Sketch!;
            var pic = QrPictureRenderer.Render(sketch, 240);
            var plan = sketch.CirclePlan!;

            double radiusM = plan.DotDiameterM / 2.0;
            double materialMm2 = plan.EmittedCount * Math.PI * radiusM * radiusM * 1e6;
            double renderedMm2 =
                pic.DarkPixelCount * pic.MmPerPixel * pic.MmPerPixel;

            Assert.True(
                Math.Abs(renderedMm2 - materialMm2) / materialMm2 < 0.05,
                $"rendered {renderedMm2:F2} mm² vs disk total {materialMm2:F2} mm²");
        }

        [Fact]
        public void Circle_preview_keeps_the_quiet_zone_margin_blank()
        {
            var sketch = QrSketchGenerator.Generate(CircleRequest()).Sketch!;
            var pic = QrPictureRenderer.Render(sketch, 240);

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
        public void Circle_preview_under_invert_dots_the_light_cells()
        {
            // Invert: the quiet zone emits — a dot lands at matrix cell
            // (0,0), the pattern seed, in the top-left margin corner; the
            // single dark data cell leaves a HOLE in the dot field.
            var cells = Cells(7, (3, 3));
            var sketch = QrSketchGenerator.GenerateFromCells(
                cells, 100.0, 0.05, invert: true,
                QrCellShape.Circle, 0.6).Sketch!;
            var pic = QrPictureRenderer.Render(sketch, 240);
            var plan = sketch.CirclePlan!;

            var seed = plan.CellCentreM(0, 0);
            Assert.True(DarkAt(pic, seed.X * 1000.0, seed.Y * 1000.0),
                "the quiet-zone corner emits under invert — a dot must land there");
            var hole = plan.CellCentreM(4 + 3, 4 + 3);
            Assert.False(DarkAt(pic, hole.X * 1000.0, hole.Y * 1000.0),
                "the one dark data cell emits nothing under invert — its centre stays light");
        }

        [Fact]
        public void Circle_preview_signature_matches_the_session_picture()
        {
            var session = CircleSession();
            var direct = QrPictureRenderer.Render(
                session.CurrentResult!.Sketch!, QrSketchSession.PreviewPixelsAcross);
            Assert.Equal(direct.Signature(), session.Picture!.Signature());
        }
    }
}
