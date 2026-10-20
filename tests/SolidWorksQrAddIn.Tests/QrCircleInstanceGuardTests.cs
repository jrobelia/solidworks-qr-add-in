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
    /// Ticket #41 (spec #13) — the MaxCircleInstances insertion guard on the
    /// CIRCLE plan path: under real circles, sketch-edit responsiveness scales
    /// with delivered INSTANCE count (pattern dots = sketch regions), not
    /// polygon segments, so the segment ceiling of <see cref="QrSegmentGuardTests"/>
    /// does not apply — this bound is its circle-mode sibling. The guard runs
    /// on the plan's post-invert EMITTED count — the dots the host's pattern
    /// actually delivers, never N² — inside generation, before any host
    /// mutation. The budget sits ~1.1× the largest live-proven circle insert,
    /// the same methodology that sized MaxSegmentCount on the spec-13 branch
    /// (docs/INSERTION-GUARD.md records the evidence).
    /// </summary>
    public class QrCircleInstanceGuardTests
    {
        private const string Payload = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;
        private const double Dot = 0.90;

        // Over the ceiling for every plausible bound: a ~2 000-char payload
        // encodes at the maximum QR version (v40, N = 177) and emits ~15 000
        // dots — several times any edit-responsiveness bound the live ladder
        // could justify.
        private static readonly string OverBudgetPayload = new string('a', 2000);

        /// <summary>Row-major dark fill of the n×n data matrix.</summary>
        private static bool[,] DarkCells(int n, int dark)
        {
            var cells = new bool[n, n];
            for (var i = 0; i < dark; i++)
                cells[i / n, i % n] = true;
            return cells;
        }

        /// <summary>
        /// A data matrix whose circle plan emits exactly
        /// <paramref name="emitted"/> dots. Normal polarity: the pattern spans
        /// the N×N data square, so `emitted` row-major dark cells deliver
        /// exactly `emitted` instances. Invert: the pattern spans the whole
        /// (N+8)×(N+8) working matrix and every non-dark cell emits — margin
        /// included — so dark = (N+8)² − emitted; the chosen N keeps that
        /// fill legal (0 ≤ dark ≤ N²) for any emitted count ≳ 150.
        /// </summary>
        private static bool[,] EmittingExactly(int emitted, bool invert)
        {
            if (!invert)
            {
                int n = (int)System.Math.Ceiling(System.Math.Sqrt((double)emitted));
                return DarkCells(n, emitted);
            }
            int ni = (emitted - 64) / 16;
            int m = ni + 8;
            return DarkCells(ni, m * m - emitted);
        }

        private static QrSketchResult Generate(bool[,] cells, bool invert) =>
            QrSketchGenerator.GenerateFromCells(cells, cells.GetLength(0) * 2.0,
                Growth, invert: invert, cellShape: QrCellShape.Circle,
                cellShapeParameter: Dot);

        // ---------------- the guard fires on emitted dots ----------------

        [Fact]
        public void The_bound_itself_is_accepted_and_one_dot_over_rejects()
        {
            var atBound = Generate(EmittingExactly(QrInsertionGuard.MaxCircleInstances, false), false);
            Assert.True(atBound.Succeeded, atBound.Error?.Message);
            Assert.Equal(QrInsertionGuard.MaxCircleInstances,
                atBound.Sketch!.CirclePlan!.EmittedCount);
            Assert.Equal(QrInsertionGuard.MaxCircleInstances,
                atBound.Sketch.Info.InstanceCount);

            var over = Generate(EmittingExactly(QrInsertionGuard.MaxCircleInstances + 1, false), false);
            Assert.False(over.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManyInstances, over.Error!.Kind);
        }

        [Fact]
        public void Under_invert_the_same_instance_budget_applies()
        {
            var atBound = Generate(EmittingExactly(QrInsertionGuard.MaxCircleInstances, true), true);
            Assert.True(atBound.Succeeded, atBound.Error?.Message);
            Assert.Equal(QrInsertionGuard.MaxCircleInstances,
                atBound.Sketch!.CirclePlan!.EmittedCount);

            var over = Generate(EmittingExactly(QrInsertionGuard.MaxCircleInstances + 1, true), true);
            Assert.False(over.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManyInstances, over.Error!.Kind);
        }

        [Fact]
        public void The_bound_counts_emitted_dots_not_grid_cells()
        {
            // A grid much larger than the bound whose DARK cells alone stay
            // under it: the guard must read the emitted-dot count, never N².
            int n = (int)System.Math.Ceiling(
                System.Math.Sqrt((double)QrInsertionGuard.MaxCircleInstances)) + 8;
            var result = Generate(DarkCells(n, QrInsertionGuard.MaxCircleInstances), false);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(result.Sketch!.Info.CellCount * result.Sketch.Info.CellCount
                        > QrInsertionGuard.MaxCircleInstances,
                "fixture did not exceed the bound in raw cells");
        }

        [Fact]
        public void An_over_budget_encoded_payload_rejects()
        {
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(OverBudgetPayload, 400.0, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = Dot,
                });

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.TooManyInstances, result.Error!.Kind);
        }

        // ---------------- what stays accepted ----------------

        [Fact]
        public void The_version_one_circle_payload_stays_accepted()
        {
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = Dot,
                });

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(result.Sketch!.Info.InstanceCount <= QrInsertionGuard.MaxCircleInstances);
        }

        [Fact]
        public void The_spike_proven_density_stays_accepted()
        {
            // The PATTERN-SPIKE.md PAT8 rung — 'a'×100 at ECC M lands N = 41
            // (the spike's "v5 (41×41)" real mask, ≈2.3 s all-in live;
            // this payload emits 826 dots) — is the density the live
            // evidence proves responsive; the bound must keep it legal.
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(new string('a', 100), 100.0, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = Dot,
                });

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(41, result.Sketch!.Info.CellCount);
            Assert.True(result.Sketch.Info.InstanceCount <= QrInsertionGuard.MaxCircleInstances);
        }

        // ---------------- the message is actionable ----------------

        [Fact]
        public void The_rejection_message_names_the_count_the_budget_and_the_levers()
        {
            var result = Generate(EmittingExactly(QrInsertionGuard.MaxCircleInstances + 1, false), false);

            Assert.False(result.Succeeded);
            var message = result.Error!.Message;
            // The measured count, honestly one over the budget.
            var count = Regex.Match(message, @"(\d+) circle-dot instances");
            Assert.True(count.Success, $"message names no instance count: {message}");
            Assert.Equal(QrInsertionGuard.MaxCircleInstances + 1,
                int.Parse(count.Groups[1].Value));
            // The budget itself.
            Assert.Contains($"{QrInsertionGuard.MaxCircleInstances}", message);
            // The levers: smaller payload, lighter ECC, square cells.
            Assert.Contains("content", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("error-correction", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("square", message, System.StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- before any host mutation ----------------

        [Fact]
        public void A_rejected_circle_request_never_reaches_the_host()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);

            var result = new CreateQrSketchCommand().Execute(
                host,
                new QrSketchRequest(OverBudgetPayload, 400.0, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = Dot,
                });

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document.Sketches);
            Assert.Equal(0, host.BeginCount);
        }

        [Fact]
        public void The_rejection_surfaces_on_the_status_line()
        {
            // The session seam reports generator failures verbatim — the
            // guard's message lands on the pane's status line for free,
            // exactly like the dot-size rejections.
            var windows = new List<FakeQrWindow>();
            var session = new QrSketchSession(() =>
            {
                var w = new FakeQrWindow();
                windows.Add(w);
                return w;
            });
            session.SetCellShape(QrCellShape.Circle);
            session.SetSizeText("400");   // pitch stays well above the dot-% cap at v40
            var result = session.SetContent(OverBudgetPayload);

            Assert.False(result.Succeeded);
            Assert.False(session.HasValidRequest);
            Assert.Equal(QrSketchErrorKind.TooManyInstances, result.Error!.Kind);
            Assert.Equal(result.Error.Message, session.StatusText);
            Assert.Null(session.Picture);
        }
    }
}
