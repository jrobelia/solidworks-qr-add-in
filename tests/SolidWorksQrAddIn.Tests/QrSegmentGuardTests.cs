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
    /// Ticket #35 — the MaxSegmentCount insertion guard: polygonized circles
    /// and rounded corners inflate contour vertex counts ~4–8× over squares
    /// at the same density (the version-1 square record is 252 segments), so
    /// a dense shaped QR could hand SolidWorks an uneditable sketch. The
    /// guard runs on the GENERATED contours — after union and flattening,
    /// before the quadratic contour validator and long before
    /// <see cref="QrSketchResult.Ok"/> — so a rejection can never reach a
    /// host mutation and never pays the O(n²) validation sweep on a request
    /// it refuses. The provisional budget sits ~2× over the largest
    /// live-proven insert — the dense square payload of docs/ACCEPTANCE.md
    /// §F (3 896 segments, ≈6.1 s native) — pending ticket #36's live
    /// sketch-edit responsiveness measurement (reasoning and the measured
    /// per-shape counts in docs/INSERTION-GUARD.md).
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

        // ---------------- the guard fires on every shape ----------------

        [Fact]
        public void A_dense_circle_payload_rejects_over_the_segment_budget()
        {
            // The same v14 matrix that squares emit at ~4 000 segments dots
            // into ~21 000 — every cell its own K-gon contour, nothing the
            // union can merge away (measured: 21 640 at 150 mm / f = 50%).
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(DensePayload, 150.0, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = 0.50,
                });

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        [Fact]
        public void A_dense_rounded_square_payload_rejects_over_the_segment_budget()
        {
            // Corner facets survive on every merged-region boundary vertex:
            // the same 250-byte payload squares emit at ~3 100 segments
            // rounds into ~8 400 (measured: 8 396 at 150 mm / 10% growth).
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(new string('a', 250), 150.0, 0.10)
                {
                    CellShape = QrCellShape.RoundedSquare,
                    CellShapeParameter = 0.30,
                });

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        [Fact]
        public void A_dense_square_payload_rejects_too()
            // The ceiling is shape-agnostic: enough plain squares bust it —
            // a ~1 200-byte payload at v23 measures ~9 900 segments.
        {
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(new string('a', 1200), 200.0, Growth));

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        [Fact]
        public void Disjoint_square_cells_still_sum_past_the_budget()
        {
            // Squares never union away when they cannot touch: 2 116 spaced
            // cells × 4 segments each = 8 464 — over the budget without any
            // shape facets at all.
            var result = QrSketchGenerator.GenerateFromCells(
                SpacedDark(91), 200.0, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        [Fact]
        public void Disjoint_rounded_cells_bill_their_arc_facets()
        {
            // 256 spaced cells at the clean-regime polygonization (8 facets
            // per corner → 36 vertices/cell) = 9 216 segments.
            var result = QrSketchGenerator.GenerateFromCells(
                SpacedDark(31), 200.0, 0.10, invert: false,
                QrCellShape.RoundedSquare, cellShapeParameter: 0.15);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        [Fact]
        public void Under_invert_the_guard_still_applies()
        {
            // Invert emits the LIGHT cells of the whole working matrix —
            // a sparse fixture leaves ~840 separate circular pits (measured
            // 12 600 segments at 100 mm / f = 90%).
            var cells = new bool[21, 21];
            cells[10, 10] = true;
            var result = QrSketchGenerator.GenerateFromCells(
                cells, 100.0, Growth, invert: true,
                QrCellShape.Circle, cellShapeParameter: 0.90);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
        }

        // ---------------- what stays accepted ----------------

        [Fact]
        public void The_version_one_square_payload_stays_accepted()
        {
            // The recorded 252-segment record sits ~32× under the budget.
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(result.Sketch!.Info.SegmentCount <= QrInsertionGuard.MaxSegmentCount);
        }

        [Fact]
        public void A_shaped_small_payload_stays_accepted()
        {
            // A v1 payload under any shape lands far below the budget —
            // circle dots measure 1 824 segments here, rounded cells ~950.
            var circle = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = 0.90,
                });
            var rounded = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, 60.0, 0.10)
                {
                    CellShape = QrCellShape.RoundedSquare,
                    CellShapeParameter = 0.15,
                });

            Assert.True(circle.Succeeded, circle.Error?.Message);
            Assert.True(rounded.Succeeded, rounded.Error?.Message);
        }

        // ---------------- the message is actionable ----------------

        [Fact]
        public void The_rejection_message_names_the_count_the_budget_and_the_levers()
        {
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(DensePayload, 150.0, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = 0.50,
                });

            Assert.False(result.Succeeded);
            var message = result.Error!.Message;
            // The measured count, honestly greater than the budget.
            var count = Regex.Match(message, @"(\d+) sketch segments");
            Assert.True(count.Success, $"message names no segment count: {message}");
            Assert.True(int.Parse(count.Groups[1].Value) > QrInsertionGuard.MaxSegmentCount,
                $"reported count should exceed the budget: {message}");
            // The budget itself.
            Assert.Contains($"{QrInsertionGuard.MaxSegmentCount}-segment", message);
            // The levers: smaller payload, lighter ECC, squarer shape.
            Assert.Contains("content", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("error-correction", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("shape", message, System.StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- before any host mutation ----------------

        [Fact]
        public void A_rejected_request_never_reaches_the_host()
        {
            // The command seam: the same dense circle request must leave the
            // document sketch-less — the guard fires inside generation,
            // before the insertion path is ever begun.
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);

            var result = new CreateQrSketchCommand().Execute(
                host,
                new QrSketchRequest(DensePayload, 150.0, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = 0.50,
                });

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
            session.SetContent(DensePayload);
            session.SetSizeText("150");
            session.SetCellShape(QrCellShape.Circle);
            var result = session.SetShapeParameterText("50");

            Assert.False(result.Succeeded);
            Assert.False(session.HasValidRequest);
            Assert.Equal(QrSketchErrorKind.TooManySegments, result.Error!.Kind);
            Assert.Equal(result.Error.Message, session.StatusText);
            Assert.Null(session.Picture);
        }
    }
}
