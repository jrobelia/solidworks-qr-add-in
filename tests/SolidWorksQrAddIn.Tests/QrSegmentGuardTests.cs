using System.Collections.Generic;
using System.Text.RegularExpressions;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #38 (port of spec-13 ticket #35) — the MaxSegmentCount
    /// insertion guard on the SQUARE contour path: a dense enough QR can
    /// hand SolidWorks an uneditable sketch long before the feature floors
    /// trip. The guard runs on the GENERATED contours — after union and
    /// flattening, before the quadratic contour validator — so a rejection
    /// can never reach a host mutation and never pays the O(n²) validation
    /// sweep on a request it refuses. The budget sits just above the
    /// largest live-proven insert — the dense square payload of
    /// docs/ACCEPTANCE.md §F (3 896 segments, ≈6.1 s native) — recorded in
    /// docs/INSERTION-GUARD.md. Only Square reaches this pipeline: Circle
    /// emits a circle plan of native pattern instances, not contours, so
    /// its density bound is the instance count (a different knob).
    /// </summary>
    public class QrSegmentGuardTests
    {
        private const string Payload = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        // A dense real payload — the 358-byte class of the ACCEPTANCE.md §F
        // dense record (QR version 14, N = 73 at ECC M). Byte-mode content
        // (lowercase) packs like the recorded row.
        private static readonly string DensePayload = new string('a', 358);

        // Over the ceiling: a ~1 200-byte payload reaches v23 and measures
        // ~9 900 square segments — past the 4 400 budget by ~2×.
        private static readonly string OverBudgetPayload = new string('a', 1200);

        private static bool[,] SpacedDark(int n)
        {
            // Dark cells two pitches apart in both directions — grown cells
            // can never touch (the gap is a whole light cell minus growth),
            // so every emitted primitive survives as its own contour. The
            // cheapest way to multiply segments per cell.
            var cells = new bool[n, n];
            for (var r = 0; r < n; r += 2)
                for (var c = 0; c < n; c += 2)
                    cells[r, c] = true;
            return cells;
        }

        // ---------------- the guard fires on square contours ----------------

        [Fact]
        public void A_dense_square_payload_rejects_over_the_segment_budget()
        {
            // A ~1 200-byte payload at v23 measures ~9 900 segments.
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(OverBudgetPayload, 200.0, Growth));

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        [Fact]
        public void Disjoint_square_cells_still_sum_past_the_budget()
        {
            // Squares never union away when they cannot touch: 2 116 spaced
            // cells × 4 segments each = 8 464 — over the budget.
            var result = QrSketchGenerator.GenerateFromCells(
                SpacedDark(91), 200.0, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        [Fact]
        public void Under_invert_the_guard_still_applies()
        {
            // Invert emits the LIGHT cells of the whole working matrix —
            // disjoint light cells inside a mostly-dark field still sum
            // past the budget.
            var cells = new bool[91, 91];
            for (var r = 0; r < 91; r++)
                for (var c = 0; c < 91; c++)
                    cells[r, c] = !(r % 2 == 0 && c % 2 == 0);
            var result = QrSketchGenerator.GenerateFromCells(
                cells, 200.0, Growth, invert: true);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        // ---------------- what stays accepted ----------------

        [Fact]
        public void The_version_one_square_payload_stays_accepted()
        {
            // The recorded 252-segment record sits ~17× under the budget.
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(result.Sketch!.Info.SegmentCount <= QrInsertionGuard.MaxSegmentCount);
        }

        [Fact]
        public void The_acceptance_recorded_dense_payload_stays_accepted()
        {
            // The docs/ACCEPTANCE.md §F record — 358 bytes, ~3 900 segments —
            // is the workload the budget was sized to keep legal.
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(DensePayload, 150.0, Growth));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(result.Sketch!.Info.SegmentCount <= QrInsertionGuard.MaxSegmentCount);
        }

        // ---------------- the message is actionable ----------------

        [Fact]
        public void The_rejection_message_names_the_count_the_budget_and_the_levers()
        {
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(OverBudgetPayload, 200.0, Growth));

            Assert.False(result.Succeeded);
            var message = result.Error!.Message;
            // The measured count, honestly greater than the budget.
            var count = Regex.Match(message, @"(\d+) sketch segments");
            Assert.True(count.Success, $"message names no segment count: {message}");
            Assert.True(int.Parse(count.Groups[1].Value) > QrInsertionGuard.MaxSegmentCount,
                $"reported count should exceed the budget: {message}");
            // The budget itself.
            Assert.Contains($"{QrInsertionGuard.MaxSegmentCount}", message);
            // The levers: smaller payload, lighter ECC, circle dots.
            Assert.Contains("content", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("error-correction", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("circle", message, System.StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- before any host mutation ----------------

        [Fact]
        public void A_rejected_request_never_reaches_the_host()
        {
            // The command seam: the same dense square request must leave the
            // document sketch-less — the guard fires inside generation,
            // before the insertion path is ever begun.
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);

            var result = new CreateQrSketchCommand().Execute(
                host,
                new QrSketchRequest(OverBudgetPayload, 200.0, Growth));

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document.Sketches);
        }

        [Fact]
        public void The_rejection_surfaces_on_the_status_line()
        {
            // The session seam reports generator failures verbatim — the
            // guard's message lands on the pane's status line for free,
            // exactly like the feature-floor rejections.
            var windows = new List<FakeQrWindow>();
            var session = new QrSketchSession(() =>
            {
                var w = new FakeQrWindow();
                windows.Add(w);
                return w;
            });
            session.SetContent(OverBudgetPayload);
            var result = session.SetSizeText("200");

            Assert.False(result.Succeeded);
            Assert.False(session.HasValidRequest);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
            Assert.Equal(result.Error.Message, session.StatusText);
            Assert.Null(session.Picture);
        }
    }
}
