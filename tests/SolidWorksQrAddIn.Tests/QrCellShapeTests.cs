using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #13, ticket #38 — the two-option cell-shape surface. Square is
    /// the default and stays byte-identical to the historical contour
    /// output; Circle emits a circle plan (one disk per emitted cell),
    /// never contours. Exercises the parser seam
    /// (<see cref="QrCellShapes"/>) and the session seam
    /// (<see cref="QrSketchSession.SetCellShape"/> /
    /// <see cref="QrSketchSession.SetShapeParameterText"/>) including the
    /// inert-field discipline: growth text is preserved verbatim but never
    /// consulted under Circle, exactly like the shape-parameter text under
    /// Square.
    /// </summary>
    public class QrCellShapeTests
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

        // ---------------- the two-option code surface ----------------

        [Theory]
        [InlineData("square", QrCellShape.Square)]
        [InlineData("Square", QrCellShape.Square)]
        [InlineData(" SQUARE ", QrCellShape.Square)]
        [InlineData("circle", QrCellShape.Circle)]
        [InlineData("CIRCLE", QrCellShape.Circle)]
        public void Shape_codes_parse_case_insensitively(string text, QrCellShape expected)
        {
            Assert.True(QrCellShapes.TryParse(text, out var shape));
            Assert.Equal(expected, shape);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("rounded")]
        [InlineData("dots")]
        [InlineData("sq")]
        public void Unknown_shape_text_is_refused(string? text)
        {
            Assert.False(QrCellShapes.TryParse(text, out _));
        }

        [Theory]
        [InlineData(QrCellShape.Square, "square")]
        [InlineData(QrCellShape.Circle, "circle")]
        public void Shape_codes_round_trip(QrCellShape shape, string code)
        {
            Assert.Equal(code, QrCellShapes.CodeFor(shape));
            Assert.True(QrCellShapes.TryParse(code, out var parsed));
            Assert.Equal(shape, parsed);
        }

        [Fact]
        public void An_enum_value_with_no_code_is_refused_not_spelled_square()
        {
            // Symmetric with TryParse's strictness: an out-of-range enum
            // has no canonical code — refusing beats silently spelling it
            // "square" (which would write the wrong shape downstream).
            var bogus = (QrCellShape)99;
            Assert.Throws<ArgumentOutOfRangeException>(() => QrCellShapes.CodeFor(bogus));
        }

        // ---------------- field applicability ----------------

        [Fact]
        public void Growth_applies_to_square_only_and_the_parameter_to_circle_only()
        {
            Assert.True(QrCellShapes.GrowthApplies(QrCellShape.Square));
            Assert.False(QrCellShapes.GrowthApplies(QrCellShape.Circle));
            Assert.False(QrCellShapes.ShapeParameterApplies(QrCellShape.Square));
            Assert.True(QrCellShapes.ShapeParameterApplies(QrCellShape.Circle));
        }

        [Fact]
        public void The_shape_parameter_label_names_the_dot_size()
        {
            Assert.Equal("Dot size (% of cell pitch):",
                QrCellShapes.ShapeParameterLabel(QrCellShape.Circle));
        }

        // ---------------- session seam: selection + inert fields ----------------

        [Fact]
        public void Square_is_the_default_shape()
        {
            var (session, _) = NewSession();
            Assert.Equal(QrCellShape.Square, session.CellShape);
            Assert.True(session.GrowthApplies);
            Assert.False(session.ShapeParameterApplies);
        }

        [Fact]
        public void The_dot_size_field_starts_with_a_high_default_under_the_cap()
        {
            var (session, _) = NewSession();
            // "High default below the cap" (spec #13): the v1 default request
            // computes cap ≈ 95.8% — the field must start valid AND high.
            Assert.NotNull(session.ShapeParameterText);
            Assert.True(double.Parse(session.ShapeParameterText) > 50.0,
                $"dot default should be high: {session.ShapeParameterText}");
            session.SetContent(Payload);
            Assert.True(session.SetCellShape(QrCellShape.Circle).Succeeded,
                session.StatusText);
        }

        [Fact]
        public void Selecting_circle_disables_growth_but_keeps_its_text_verbatim()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetGrowthText("junk-growth");

            var underCircle = session.SetCellShape(QrCellShape.Circle);

            // Under Circle the growth field is inert: garbage text cannot
            // break the request — it is stored verbatim, never parsed.
            Assert.True(underCircle.Succeeded, underCircle.Error?.Message);
            Assert.Equal("junk-growth", session.GrowthText);
            Assert.False(session.GrowthApplies);
            Assert.Null(session.RequestedGrowthFractionPerSide);

            // Back on Square the same text re-validates and surfaces its
            // own error — no silent substitution either direction.
            var back = session.SetCellShape(QrCellShape.Square);
            Assert.False(back.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGrowth, back.Error!.Kind);
            Assert.Equal("junk-growth", session.GrowthText);
        }

        [Fact]
        public void Under_square_the_dot_size_text_is_inert_and_kept_verbatim()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);

            var underSquare = session.SetShapeParameterText("not-a-number");
            Assert.True(underSquare.Succeeded, underSquare.Error?.Message);
            Assert.Equal("not-a-number", session.ShapeParameterText);
            Assert.Null(session.RequestedShapeParameter);

            // The malformed text surfaces the moment Circle needs it.
            var circle = session.SetCellShape(QrCellShape.Circle);
            Assert.False(circle.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, circle.Error!.Kind);
            Assert.Equal("not-a-number", session.ShapeParameterText);
        }

        [Fact]
        public void Circle_requests_regenerate_the_picture_as_dots()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            var squarePicture = session.Picture!;
            int squareDark = squarePicture.DarkPixelCount;

            Assert.True(session.SetCellShape(QrCellShape.Circle).Succeeded);
            var circlePicture = session.Picture!;
            Assert.Equal(squarePicture.WidthPixels, circlePicture.WidthPixels);
            Assert.NotEqual(squarePicture.Signature(), circlePicture.Signature());
            Assert.True(circlePicture.DarkPixelCount > 0);
            Assert.True(circlePicture.DarkPixelCount < squareDark,
                "90%-pitch disks cover less area than grown squares");
        }

        [Fact]
        public void Info_lines_report_the_shape_and_dot_count_under_circle()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);

            var shape = Assert.Single(session.InfoLines, l => l.Label == "Cell shape");
            Assert.Contains("Circle", shape.Value);
            Assert.Contains("90", shape.Value);
            var dots = Assert.Single(session.InfoLines, l => l.Label == "Dots");
            Assert.Equal(
                session.CurrentResult!.Sketch!.CirclePlan!.EmittedCount.ToString(),
                dots.Value);
        }

        [Fact]
        public void Info_lines_report_square_as_the_shape()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            var shape = Assert.Single(session.InfoLines, l => l.Label == "Cell shape");
            Assert.Equal("Square", shape.Value);
            Assert.DoesNotContain(session.InfoLines, l => l.Label == "Dots");
        }
    }
}
