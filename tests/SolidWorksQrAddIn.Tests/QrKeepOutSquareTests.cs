using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #10 / ticket #18 — the host sketch gains the keep-out square: the
    /// working-matrix boundary ((N+8)·pitch per side, ADR 0002) drawn as
    /// construction geometry at the fixed quiet-zone offset, always present.
    /// It cannot coincident-bind to the block (it sits outside the block's
    /// extent), so it is centred on the QR square by two equation-linked
    /// offset dimensions and sized by an equation-linked side dimension —
    /// the total footprint tracks every edit to the block's Scale (and any
    /// move of the QR square with it). Assertions stay on the fake host's
    /// world state — construction squares, the recorded equations, the live
    /// dimension values — never call counts.
    /// </summary>
    public class QrKeepOutSquareTests
    {
        private const string Content = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        private static readonly QrSketch Expected =
            QrSketchGenerator.Generate(Content, SizeMm, Growth).Sketch!;

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        private static FakeSketch PriorUserSketch(FakeDocument doc)
        {
            var sketch = new FakeSketch("SketchUser");
            sketch.Segments.Add(new FakeLine(0, 0, 0.01, 0));
            doc.Sketches.Add(sketch);
            var captured = sketch;
            doc.PushUserUndoStep("User Sketch", () =>
            {
                doc.Sketches.Remove(captured);
                if (ReferenceEquals(doc.ActiveSketch, captured))
                    doc.ActiveSketch = null;
            });
            return sketch;
        }

        private static FakeConstructionSquare QrSquareOf(FakeSketch sketch) =>
            sketch.ConstructionSquares.Single(s => s.Role == QrSquareRole.QrSquare);

        private static FakeConstructionSquare KeepOutOf(FakeSketch sketch) =>
            sketch.ConstructionSquares.Single(s => s.Role == QrSquareRole.KeepOutSquare);

        private static double SideOf(FakeConstructionSquare square)
        {
            var edges = square.EffectiveEdges.ToList();
            return edges.Max(e => System.Math.Max(e.X1, e.X2)) -
                   edges.Min(e => System.Math.Min(e.X1, e.X2));
        }

        private static (double X, double Y) CentreOf(FakeConstructionSquare square)
        {
            var edges = square.EffectiveEdges.ToList();
            double cx = (edges.Max(e => System.Math.Max(e.X1, e.X2)) +
                         edges.Min(e => System.Math.Min(e.X1, e.X2))) / 2.0;
            double cy = (edges.Max(e => System.Math.Max(e.Y1, e.Y2)) +
                         edges.Min(e => System.Math.Min(e.Y1, e.Y2))) / 2.0;
            return (cx, cy);
        }

        // ---------- emission ----------

        [Fact]
        public void A_create_emits_the_keep_out_square_as_construction_geometry_at_the_matrix_boundary()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var keepOut = KeepOutOf(sketch);
            Assert.True(keepOut.IsConstruction);
            // The working-matrix boundary: the staged keep-out side is
            // (N+8)·pitch — the square lands centred on the origin at that
            // span, strictly outside the QR square.
            var staged = Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.KeepOutSquare);
            double expectedSide =
                staged.Vertices.Max(v => v.X) - staged.Vertices.Min(v => v.X);
            Assert.Equal(expectedSide, SideOf(keepOut), 9);
            var centre = CentreOf(keepOut);
            Assert.Equal(0.0, centre.X, 9);
            Assert.Equal(0.0, centre.Y, 9);
        }

        [Fact]
        public void The_keep_out_square_stays_out_of_profiles_it_is_construction_only()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var sketch = host.Document!.Sketches.Single();

            // Profile geometry is raw segments + block effective edges —
            // everything the QR contributes stays inside the grown extent;
            // the keep-out square is larger, so none of its edges can be
            // profile geometry.
            double qrHalf = SizeMm / 2.0 / 1000.0;
            foreach (var seg in sketch.EffectiveSegments)
            {
                Assert.True(System.Math.Abs(seg.X1) <= qrHalf + 1e-9);
                Assert.True(System.Math.Abs(seg.X2) <= qrHalf + 1e-9);
                Assert.True(System.Math.Abs(seg.Y1) <= qrHalf + 1e-9);
                Assert.True(System.Math.Abs(seg.Y2) <= qrHalf + 1e-9);
            }
            var keepOut = KeepOutOf(sketch);
            Assert.True(SideOf(keepOut) > 2.0 * qrHalf,
                "the keep-out square is strictly larger than the grown QR extent");
        }

        // ---------- equation-linked dimensions ----------

        [Fact]
        public void The_keep_out_side_dimension_is_equation_linked_to_the_qr_square_dimension()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var doc = host.Document!;
            var sketch = doc.Sketches.Single();
            var qrSquare = QrSquareOf(sketch);
            var keepOut = KeepOutOf(sketch);

            var qrDim = sketch.Dimensions.Single(d => d.IsDriven);
            var sideDim = sketch.Dimensions.Single(
                d => ReferenceEquals(d.Target, keepOut) && d.Measures == FakeDimMeasure.SideLength);

            // The equation link is recorded world state: the keep-out side
            // dim reads the QR-square dim — the text names both.
            Assert.False(sideDim.IsDriven);
            Assert.NotNull(sideDim.Equation);
            Assert.Same(qrDim, sideDim.EquationSource);
            Assert.Contains(qrDim.Name, sideDim.Equation!);
            Assert.Contains(sideDim.Name, sideDim.Equation!);
            Assert.Contains(sideDim.Equation!, doc.Equations);

            // Its value IS the keep-out side in document units — the
            // working-matrix boundary, not "QR size + 8·pitch".
            var staged = Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.KeepOutSquare);
            double expectedSideDocUnits =
                (staged.Vertices.Max(v => v.X) - staged.Vertices.Min(v => v.X)) /
                doc.MetresPerLengthUnit;
            Assert.Equal(expectedSideDocUnits, sideDim.ValueDocUnits, 9);
        }

        [Fact]
        public void The_keep_out_square_is_positioned_by_equation_linked_offset_dims()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var doc = host.Document!;
            var sketch = doc.Sketches.Single();
            var keepOut = KeepOutOf(sketch);

            // The locating dims — one per axis between the keep-out and the
            // QR square's corresponding edges — are kept equation-linked so
            // the square stays centred as the QR square grows (the ticket's
            // "offset dim placed, constraints left" — deleting it would free
            // the position and let the square drift off-centre).
            var offsets = sketch.Dimensions
                .Where(d => ReferenceEquals(d.Target, keepOut) &&
                            d.Measures == FakeDimMeasure.OffsetGap)
                .ToList();
            Assert.Equal(2, offsets.Count);
            var qrDim = sketch.Dimensions.Single(d => d.IsDriven);
            double expectedGap =
                (Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.KeepOutSquare)
                    .Vertices.Max(v => v.X) -
                 Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.QrSquare)
                    .Vertices.Max(v => v.X)) / doc.MetresPerLengthUnit;
            foreach (var gap in offsets)
            {
                Assert.Same(qrDim, gap.EquationSource);
                Assert.NotNull(gap.Equation);
                Assert.Contains(gap.Equation!, doc.Equations);
                Assert.Equal(expectedGap, gap.ValueDocUnits, 9);
            }
            // The offset dims implement the centring: the fake records the
            // square they centre the keep-out on.
            Assert.Same(QrSquareOf(sketch), keepOut.CentredOn);
        }

        [Fact]
        public void The_keep_out_dimension_tracks_a_scale_edit_and_keeps_the_fixed_cell_margin()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var doc = host.Document!;
            var sketch = doc.Sketches.Single();
            var instance = sketch.BlockInstances.Single();
            var keepOut = KeepOutOf(sketch);
            var sideDim = sketch.Dimensions.Single(
                d => ReferenceEquals(d.Target, keepOut) && d.Measures == FakeDimMeasure.SideLength);

            instance.Scale = 50.0;

            // The margin stays the fixed quiet-zone offset in CELLS — the
            // keep-out side is the QR-square dim × the cell-count ratio, so
            // at 2× scale it is exactly 2× larger (ADR 0002: the margin is
            // measured in pitches, and the pitch rescales with the block).
            var stagedKeep = Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.KeepOutSquare);
            var stagedQr = Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.QrSquare);
            double ratio =
                (stagedKeep.Vertices.Max(v => v.X) - stagedKeep.Vertices.Min(v => v.X)) /
                (stagedQr.Vertices.Max(v => v.X) - stagedQr.Vertices.Min(v => v.X));
            Assert.Equal(50.0 * ratio, sideDim.ValueDocUnits, 9);
            Assert.Equal(50.0 * ratio / 1000.0, SideOf(keepOut), 9);
            var centre = CentreOf(keepOut);
            Assert.Equal(0.0, centre.X, 9);
            Assert.Equal(0.0, centre.Y, 9);
        }

        [Fact]
        public void The_keep_out_square_stays_centred_on_the_qr_square_when_the_block_moves()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();
            var qrSquare = QrSquareOf(sketch);
            var keepOut = KeepOutOf(sketch);

            // The keep-out is anchored to the QR square, not the sketch
            // origin — an engineer moving the block drags the QR square (its
            // coincident bindings) and the keep-out follows it.
            instance.X = 0.02;
            instance.Y = -0.01;

            var qr = CentreOf(qrSquare);
            var keep = CentreOf(keepOut);
            Assert.Equal(qr.X, keep.X, 9);
            Assert.Equal(qr.Y, keep.Y, 9);
        }

        // ---------- failure / cleanup at the keep-out step ----------

        [Fact]
        public void A_keep_out_draw_failure_removes_the_whole_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailKeepOutDraw = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.BlockInstances));
            Assert.Empty(doc.Sketches.SelectMany(s => s.ConstructionSquares));
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void A_keep_out_constraint_failure_removes_the_whole_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailKeepOutConstraint = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.ConstructionSquares));
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void A_keep_out_dimension_failure_removes_the_sketch_and_restores_the_toggles()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailKeepOutDimension = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.Dimensions));
            Assert.True(host.InputDimValOnCreate);
            Assert.True(host.OverdefiningDimsPromptToSetState);
        }

        [Fact]
        public void An_equation_failure_removes_the_whole_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailEquation = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Equations);
            Assert.Empty(doc.Sketches.SelectMany(s => s.ConstructionSquares));
            Assert.Null(doc.ActiveSketch);
        }

        // ---------- named undo over both squares + dims ----------

        [Fact]
        public void Named_undo_removes_the_sketch_with_both_squares_and_all_dimensions()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var qrSketch = doc.Sketches.Single(s => !ReferenceEquals(s, userSketch));
            Assert.Equal(2, qrSketch.ConstructionSquares.Count);

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());

            // One Ctrl+Z removes the whole sketch feature — the block, both
            // construction squares, and every dimension live inside it.
            Assert.DoesNotContain(qrSketch, doc.Sketches);
            Assert.Contains(userSketch, doc.Sketches);
        }

        [Fact]
        public void Redo_restores_the_keep_out_square_and_its_equation_dimension()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var qrSketch = doc.Sketches.Single();

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());
            Assert.True(doc.RedoLastStep());

            Assert.Contains(qrSketch, doc.Sketches);
            var keepOut = KeepOutOf(qrSketch);
            Assert.True(keepOut.IsConstruction);
            var sideDim = qrSketch.Dimensions.Single(
                d => ReferenceEquals(d.Target, keepOut) && d.Measures == FakeDimMeasure.SideLength);
            Assert.NotNull(sideDim.Equation);
            var staged = Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.KeepOutSquare);
            Assert.Equal(
                (staged.Vertices.Max(v => v.X) - staged.Vertices.Min(v => v.X)) /
                doc.MetresPerLengthUnit,
                sideDim.ValueDocUnits, 9);
        }
    }
}
