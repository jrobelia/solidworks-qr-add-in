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
    /// Ticket 4 — the engineer's size and outline-growth inputs. Exercises:
    ///
    ///   - the geometry seam across several sizes/growths on one payload
    ///     (grown bounds == requested size within the declared tolerance,
    ///     nesting preserved, finite overlap/gaps, uniform scaling);
    ///   - the session seam (raw field text → parsed request → picture, with
    ///     useful errors and no silent substitution on invalid input);
    ///   - current-request behaviour through the create path (Create uses the
    ///     CURRENT inputs, a stale valid request is never created, editing
    ///     inputs never alters an existing sketch).
    /// </summary>
    public class QrSizeGrowthTests
    {
        private const string Payload = "SW-QR-MVP";

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

        private static string SignatureOf(string content, double sizeMm, double growthFraction) =>
            QrPictureRenderer.Render(
                QrSketchGenerator.Generate(content, sizeMm, growthFraction).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();

        // ---------------- geometry seam: one payload, several requests ----------------

        [Theory]
        [InlineData(20.0, 0.03)]
        [InlineData(25.0, 0.03)]
        [InlineData(40.0, 0.05)]
        [InlineData(60.0, 0.10)]
        public void Requested_sizes_produce_the_exact_grown_outline(double sizeMm, double growth)
        {
            var result = QrSketchGenerator.Generate(Payload, sizeMm, growth);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            double half = sizeMm / 2.0 / 1000.0;
            double tolM = QrInsertionGuard.SizeToleranceMm / 1000.0;

            var xs = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            var ys = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.Y).ToList();
            Assert.True(System.Math.Abs(xs.Max() - half) <= tolM, $"maxX {xs.Max() * 1000} mm");
            Assert.True(System.Math.Abs(xs.Min() + half) <= tolM, $"minX {xs.Min() * 1000} mm");
            Assert.True(System.Math.Abs(ys.Max() - half) <= tolM, $"maxY {ys.Max() * 1000} mm");
            Assert.True(System.Math.Abs(ys.Min() + half) <= tolM, $"minY {ys.Min() * 1000} mm");
            Assert.Equal(sizeMm, sketch.Info.FinalSizeMm, 9);
        }

        [Fact]
        public void Different_sizes_scale_the_qr_uniformly_in_both_directions()
        {
            // 50 mm must be exactly the 25 mm geometry doubled — uniform
            // scaling, not a re-fit in one axis.
            var small = QrSketchGenerator.Generate(Payload, 25.0, 0.03).Sketch!;
            var large = QrSketchGenerator.Generate(Payload, 50.0, 0.03).Sketch!;

            Assert.Equal(small.Contours.Count, large.Contours.Count);
            for (var c = 0; c < small.Contours.Count; c++)
            {
                var a = small.Contours[c].Vertices;
                var b = large.Contours[c].Vertices;
                Assert.Equal(a.Count, b.Count);
                for (var i = 0; i < a.Count; i++)
                {
                    Assert.Equal(a[i].X * 2.0, b[i].X, 12);
                    Assert.Equal(a[i].Y * 2.0, b[i].Y, 12);
                }
            }
        }

        [Theory]
        [InlineData(0.03)]
        [InlineData(0.05)]
        [InlineData(0.10)]
        [InlineData(0.20)]
        public void Growth_changes_preserve_the_entered_outline_size_by_pitch(double growth)
        {
            // p = S/(N + 2r): more growth → smaller cells, same final width.
            var result = QrSketchGenerator.Generate(Payload, 25.0, growth);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.Equal(25.0, sketch.Info.FinalSizeMm, 9);
            double half = 25.0 / 2.0 / 1000.0;
            var xs = sketch.Contours.SelectMany(c => c.Vertices).Select(v => v.X).ToList();
            Assert.Equal(half, xs.Max(), 9);
            Assert.Equal(-half, xs.Min(), 9);
            // Growth really did change the cell pitch — the preserved size is
            // not a coincidence of an unchanged request.
            double expectedPitch = 25.0 / (sketch.Info.CellCount + 2 * sketch.Info.GrowthFractionPerSide);
            Assert.Equal(expectedPitch, sketch.Info.PitchMm, 9);
        }

        [Fact]
        public void More_growth_means_a_smaller_cell_pitch_at_the_same_size()
        {
            var thin = QrSketchGenerator.Generate(Payload, 25.0, 0.03).Sketch!;
            var thick = QrSketchGenerator.Generate(Payload, 25.0, 0.15).Sketch!;

            Assert.True(thick.Info.PitchMm < thin.Info.PitchMm,
                $"pitch {thick.Info.PitchMm} !< {thin.Info.PitchMm}");
            Assert.True(thick.Info.GrowthMmPerSide > thin.Info.GrowthMmPerSide);
        }

        [Theory]
        [InlineData(20.0, 0.03)]
        [InlineData(40.0, 0.10)]
        [InlineData(60.0, 0.25)]
        public void Nesting_and_finite_features_survive_across_requests(double sizeMm, double growth)
        {
            var result = QrSketchGenerator.Generate(Payload, sizeMm, growth);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            // Finder ring→hole→island nesting is preserved.
            Assert.True(sketch.Regions.Count(r => r.Holes.Any(h => h.Islands.Count > 0)) >= 1,
                "expected at least one ring→hole→island nesting");
            // Finite overlap and finite light gaps — above the insertion guard.
            Assert.True(sketch.Info.MinEdgeMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min edge {sketch.Info.MinEdgeMm} mm");
            Assert.True(sketch.Info.MinLightGapMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min light gap {sketch.Info.MinLightGapMm} mm");
            Assert.True(sketch.Info.GrowthMmPerSide > 0, "growth overlap must be finite");
        }

        // ---------------- session seam: the two new fields ----------------

        [Fact]
        public void A_fresh_session_starts_at_25mm_and_3_percent()
        {
            var (session, _) = NewSession();

            Assert.Equal("25", session.SizeText);
            Assert.Equal("3", session.GrowthText);
            Assert.Equal(25.0, session.RequestedSizeMm);
            Assert.Equal(0.03, session.RequestedGrowthFractionPerSide);
            Assert.Equal(QrSketchSession.InitialSizeMm, session.RequestedSizeMm);
            Assert.Equal(QrSketchSession.InitialGrowthFractionPerSide,
                session.RequestedGrowthFractionPerSide);
        }

        [Fact]
        public void A_valid_size_edit_updates_the_request_and_picture_together()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            var before = session.Picture;

            var result = session.SetSizeText("40");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(session.HasValidRequest);
            Assert.Equal(40.0, session.RequestedSizeMm);
            Assert.Equal(40.0, session.CurrentResult!.Sketch!.Info.FinalSizeMm, 9);
            Assert.NotNull(session.Picture);
            // Re-rendered from the new result — not the stale pre-edit raster.
            Assert.NotSame(before, session.Picture);
            Assert.Equal(SignatureOf(Payload, 40.0, 0.03), session.Picture!.Signature());
        }

        [Fact]
        public void A_valid_growth_edit_updates_the_request_and_picture_together()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            var sig3 = session.Picture!.Signature();

            var result = session.SetGrowthText("10");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(0.10, session.RequestedGrowthFractionPerSide);
            // Outline size is preserved — pitch recalculation, not a new size.
            Assert.Equal(25.0, session.CurrentResult!.Sketch!.Info.FinalSizeMm, 9);
            Assert.Equal(SignatureOf(Payload, 25.0, 0.10), session.Picture!.Signature());
            Assert.NotEqual(sig3, session.Picture!.Signature());
        }

        [Fact]
        public void Growth_text_is_a_percentage_per_side_not_a_fraction()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            session.SetGrowthText("8");

            Assert.Equal(0.08, session.RequestedGrowthFractionPerSide);
            Assert.Equal(0.08, session.CurrentResult!.Sketch!.Info.GrowthFractionPerSide, 6);
        }

        [Theory]
        [InlineData("40mm", 40.0)]
        [InlineData(" 40 ", 40.0)]
        [InlineData("40.5", 40.5)]
        public void Size_text_accepts_plain_engineering_notation(string text, double expectedMm)
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            var result = session.SetSizeText(text);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(expectedMm, session.RequestedSizeMm!.Value, 9);
        }

        [Theory]
        [InlineData("8%", 0.08)]
        [InlineData(" 5.5 ", 0.055)]
        public void Growth_text_accepts_plain_percent_notation(string text, double expectedFraction)
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            var result = session.SetGrowthText(text);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(expectedFraction, session.RequestedGrowthFractionPerSide!.Value, 9);
        }

        // ---------------- session seam: invalid input ----------------

        [Theory]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("2.5.3")]
        [InlineData("0")]
        [InlineData("-5")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("1e999")] // parses to +Infinity — non-finite
        public void Invalid_size_text_gives_a_useful_error_and_no_picture(string bad)
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            Assert.NotNull(session.Picture);

            var result = session.SetSizeText(bad);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidSize, result.Error!.Kind);
            Assert.False(string.IsNullOrWhiteSpace(result.Error.Message));
            Assert.Null(session.Picture);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.RequestedSizeMm);
            Assert.Equal(result.Error.Message, session.StatusText);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData("0")]
        [InlineData("-3")]
        [InlineData("50")]
        [InlineData("75")]
        [InlineData("NaN")]
        public void Invalid_growth_text_gives_a_useful_error_and_no_picture(string bad)
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            Assert.NotNull(session.Picture);

            var result = session.SetGrowthText(bad);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGrowth, result.Error!.Kind);
            Assert.False(string.IsNullOrWhiteSpace(result.Error.Message));
            Assert.Null(session.Picture);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.RequestedGrowthFractionPerSide);
        }

        [Fact]
        public void Rejected_size_text_is_kept_verbatim_never_silently_replaced()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            session.SetSizeText("abc");

            // The field keeps what the engineer typed — no revert to "25".
            Assert.Equal("abc", session.SizeText);
            Assert.False(session.HasValidRequest);
        }

        [Fact]
        public void Rejected_growth_text_is_kept_verbatim_never_silently_replaced()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            session.SetGrowthText("99");

            Assert.Equal("99", session.GrowthText);
            Assert.False(session.HasValidRequest);
        }

        [Fact]
        public void A_size_below_the_insertion_guard_is_an_error_not_a_substitution()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            // 10 mm at 3% leaves the smallest feature below the 0.05 mm guard.
            var result = session.SetSizeText("10");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
            Assert.Null(session.Picture);
            Assert.Equal("10", session.SizeText);
        }

        [Fact]
        public void Growth_beyond_the_guard_is_an_error_not_a_substitution()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            // 49.9% is inside (0,50) but closes one-cell light channels on
            // the grid → the insertion guard must reject it.
            var result = session.SetGrowthText("49.9");

            Assert.False(result.Succeeded);
            Assert.True(result.Error!.Kind == QrSketchErrorKind.GrowthClosesGaps ||
                        result.Error.Kind == QrSketchErrorKind.BelowInsertionLimit,
                $"kind={result.Error.Kind}");
            Assert.Equal("49.9", session.GrowthText);
        }

        [Fact]
        public void Valid_to_invalid_to_valid_transitions_track_the_current_input()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            var goodSig = session.Picture!.Signature();

            session.SetSizeText("abc");
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);

            var result = session.SetSizeText("25");
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(session.HasValidRequest);
            Assert.Equal(goodSig, session.Picture!.Signature());
        }

        [Fact]
        public void An_invalid_edit_invalidates_the_request_even_with_valid_content()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            session.SetGrowthText("xyz");

            Assert.False(session.HasValidRequest);
            Assert.NotNull(session.StatusText);
            Assert.Equal(QrSketchErrorKind.InvalidGrowth, session.CurrentResult!.Error!.Kind);
        }

        // ---------------- current-request behaviour through Create ----------------

        [Fact]
        public void Create_uses_the_current_size_and_growth()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            session.SetSizeText("40");
            session.SetGrowthText("8");

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var expected = QrSketchGenerator.Generate(Payload, 40.0, 0.08).Sketch!;
            var sketch = host.Document!.Sketches.Single();
            var segs = sketch.EffectiveSegments.ToList();
            Assert.Equal(expected.Info.SegmentCount, segs.Count);
            double half = 40.0 / 2.0 / 1000.0;
            Assert.Equal(half, segs.Max(s => System.Math.Max(s.X1, s.X2)), 9);
            Assert.Equal(-half, segs.Min(s => System.Math.Min(s.X1, s.X2)), 9);
        }

        [Fact]
        public void Create_never_falls_back_to_a_stale_valid_request()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload); // a valid request exists…
            session.SetSizeText("abc");  // …but the current input is invalid.

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.CaptureCount); // the host was never consulted
        }

        [Fact]
        public void Create_with_invalid_growth_never_touches_the_host()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            session.SetGrowthText("0");

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.CaptureCount);
        }

        [Fact]
        public void Editing_inputs_never_alters_an_existing_sketch()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            Assert.True(session.CreateSketch(host).Succeeded);
            var first = host.Document!.Sketches.Single();
            int firstSegCount = first.EffectiveSegments.Count();
            var firstEdges = first.EffectiveSegments
                .Select(s => (s.X1, s.Y1, s.X2, s.Y2)).ToList();
            host.Document.ActiveSketch = null; // engineer exits the sketch

            session.SetSizeText("40");
            session.SetGrowthText("12");
            session.SetContent("DIFFERENT-CONTENT");

            Assert.Equal(firstSegCount, first.EffectiveSegments.Count());
            Assert.Equal(firstEdges, first.EffectiveSegments
                .Select(s => (s.X1, s.Y1, s.X2, s.Y2)).ToList());
        }

        [Fact]
        public void A_later_create_uses_new_values_while_the_first_sketch_stays_unchanged()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            Assert.True(session.CreateSketch(host).Succeeded);
            host.Document!.ActiveSketch = null; // exit sketch 1

            session.SetSizeText("40");
            var second = session.CreateSketch(host);

            Assert.True(second.Succeeded, second.Message);
            Assert.Equal(2, host.Document.Sketches.Count);
            // Sketch 1 is still the 25 mm request…
            double quarter = 25.0 / 2.0 / 1000.0;
            var segs1 = host.Document.Sketches[0].EffectiveSegments.ToList();
            Assert.Equal(quarter, segs1.Max(s => System.Math.Max(s.X1, s.X2)), 9);
            // …and sketch 2 spans the new 40 mm.
            double half40 = 40.0 / 2.0 / 1000.0;
            var segs2 = host.Document.Sketches[1].EffectiveSegments.ToList();
            Assert.Equal(half40, segs2.Max(s => System.Math.Max(s.X1, s.X2)), 9);
            Assert.Equal(segs1.Count, segs2.Count); // same payload, same contours
        }

        [Fact]
        public void A_growth_change_between_creates_preserves_the_outline_size()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            Assert.True(session.CreateSketch(host).Succeeded);
            host.Document!.ActiveSketch = null;

            session.SetGrowthText("10");
            var second = session.CreateSketch(host);

            Assert.True(second.Succeeded, second.Message);
            double half = 25.0 / 2.0 / 1000.0;
            foreach (var sketch in host.Document!.Sketches)
            {
                var segs = sketch.EffectiveSegments.ToList();
                Assert.Equal(half, segs.Max(s => System.Math.Max(s.X1, s.X2)), 9);
                Assert.Equal(-half, segs.Min(s => System.Math.Min(s.X1, s.X2)), 9);
            }
            // But the geometry is different — 10% growth ≠ 3% growth.
            Assert.NotEqual(
                host.Document.Sketches[0].EffectiveSegments.Select(s => (s.X1, s.X2)).ToList(),
                host.Document.Sketches[1].EffectiveSegments.Select(s => (s.X1, s.X2)).ToList());
        }

        [Fact]
        public void Create_stays_blocked_while_a_sketch_is_open_even_with_new_inputs()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            Assert.True(session.CreateSketch(host).Succeeded); // sketch left open

            session.SetSizeText("40");
            var second = session.CreateSketch(host);

            Assert.False(second.Succeeded);
            Assert.Single(host.Document!.Sketches);
        }

        // ---------------- command seam: explicit requests ----------------

        [Fact]
        public void The_command_inserts_the_requested_size_and_growth()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Payload, 40.0, 0.05);

            Assert.True(result.Succeeded, result.Message);
            double half = 40.0 / 2.0 / 1000.0;
            var segs = host.Document!.Sketches.Single().EffectiveSegments.ToList();
            Assert.Equal(half, segs.Max(s => System.Math.Max(s.X1, s.X2)), 9);
            Assert.Equal(-half, segs.Min(s => System.Math.Min(s.X1, s.X2)), 9);
            Assert.Equal(half, segs.Max(s => System.Math.Max(s.Y1, s.Y2)), 9);
            Assert.Equal(-half, segs.Min(s => System.Math.Min(s.Y1, s.Y2)), 9);
        }

        [Fact]
        public void The_command_rejects_an_undeliverable_request_before_mutating()
        {
            var host = HostWithSelectedPlane();

            // 5 mm at 3% falls below the recorded insertion guard.
            var result = new CreateQrSketchCommand().Execute(host, Payload, 5.0, 0.03);

            Assert.False(result.Succeeded);
            Assert.Contains("geometry", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document!.Sketches);
            Assert.Null(host.Document.ActiveSketch);
        }
    }
}
