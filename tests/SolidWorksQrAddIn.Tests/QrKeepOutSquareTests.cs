using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #10 / ticket #18, reworked by ADR 0003 — the keep-out square is
    /// the working-matrix boundary ((N+8)·pitch per side, ADR 0002) drawn as
    /// construction geometry INSIDE the block definition, staged in the same
    /// sketch-space metres as the contours. It needs no constraints, dims, or equations:
    /// folded into the definition it inherits the instance transform, so
    /// the four-cell quiet zone tracks every Scale edit and every drag for
    /// free. Assertions stay on the fake host's world state — the
    /// definition's construction segments and their effective geometry —
    /// never call counts.
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

        private static double Extent(FakeLine e) => new[]
            { System.Math.Abs(e.X1), System.Math.Abs(e.Y1),
              System.Math.Abs(e.X2), System.Math.Abs(e.Y2) }.Max();

        /// <summary>The keep-out edges inside a definition — the LARGER square.</summary>
        private static System.Collections.Generic.List<FakeLine> KeepOutEdges(FakeBlockDefinition def)
        {
            var max = def.ConstructionEdges.Max(Extent);
            return def.ConstructionEdges.Where(e => Extent(e) == max).ToList();
        }

        /// <summary>The model's keep-out square half-extent in sketch-space metres.</summary>
        private static double ModelKeepOutHalf() =>
            Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.KeepOutSquare)
                .Vertices.Max(v => System.Math.Max(System.Math.Abs(v.X), System.Math.Abs(v.Y)));

        // ---------- emission ----------

        [Fact]
        public void The_keep_out_square_lives_inside_the_block_definition_as_construction_geometry()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var def = sketch.BlockInstances.Single().Definition;

            var keepOut = KeepOutEdges(def);
            Assert.Equal(4, keepOut.Count);
            Assert.All(keepOut, e => Assert.True(e.Construction));

            // Def-space extent: the model's sized keep-out half ÷ the
            // instance scale — strictly outside the unit QR box (±0.5 mm).
            double expectedHalfM = ModelKeepOutHalf() / SizeMm;
            Assert.All(keepOut, e => Assert.Equal(expectedHalfM, Extent(e), 9));
            Assert.True(expectedHalfM > 0.0005);
        }

        [Fact]
        public void The_keep_out_square_renders_at_the_matrix_boundary_at_the_requested_size()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();

            // Rendered extent = the model's sized half-extent (the block
            // projects def×Scale; def = sized÷Scale) — the working matrix
            // boundary in metres.
            var cons = instance.EffectiveEdges.Where(e => e.Construction).ToList();
            double keepOutHalf = cons.Max(Extent);
            Assert.Equal(ModelKeepOutHalf(), keepOutHalf, 9);
            // …and strictly outside the grown QR extent (±S/2).
            Assert.True(keepOutHalf > SizeMm / 2.0 / 1000.0);
        }

        [Fact]
        public void The_keep_out_square_stays_out_of_profiles_it_is_construction_only()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();

            // An extrude reads ProfileSegments — contours only, no square
            // edges; the construction squares still render.
            Assert.DoesNotContain(sketch.ProfileSegments, e => e.Construction);
            var keepOut = sketch.EffectiveSegments
                .Where(e => e.Construction)
                .Where(e => Extent(e) == sketch.EffectiveSegments
                    .Where(x => x.Construction).Max(Extent))
                .ToList();
            Assert.Equal(4, keepOut.Count);
        }

        [Fact]
        public void The_keep_out_square_tracks_a_scale_edit_with_no_equations()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();

            // Same transform machinery as the contours: a Scale edit
            // rescales the rendered keep-out square — no equation state
            // exists anywhere to do it.
            instance.Scale = SizeMm / 2.0;
            var cons = instance.EffectiveEdges.Where(e => e.Construction).ToList();
            Assert.Equal(ModelKeepOutHalf() / 2.0, cons.Max(Extent), 9);

            instance.Scale = SizeMm * 2.0;
            cons = instance.EffectiveEdges.Where(e => e.Construction).ToList();
            Assert.Equal(ModelKeepOutHalf() * 2.0, cons.Max(Extent), 9);
        }

        [Fact]
        public void The_keep_out_square_stays_centred_on_the_qr_square_when_the_block_moves()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();
            instance.X = 0.017;
            instance.Y = -0.023;

            var cons = instance.EffectiveEdges.Where(e => e.Construction).ToList();
            double RelExtent(FakeLine e) => new[]
            {
                System.Math.Abs(e.X1 - instance.X), System.Math.Abs(e.Y1 - instance.Y),
                System.Math.Abs(e.X2 - instance.X), System.Math.Abs(e.Y2 - instance.Y),
            }.Max();
            // The keep-out edges are those outside the QR square's half
            // extent (0.0125 m); the keep-out sits beyond it.
            var keepOut = cons.Where(e => RelExtent(e) > 0.013).ToList();
            Assert.Equal(4, keepOut.Count);
            var xs = keepOut.SelectMany(e => new[] { e.X1, e.X2 }).ToList();
            var ys = keepOut.SelectMany(e => new[] { e.Y1, e.Y2 }).ToList();
            Assert.Equal(instance.X, (xs.Max() + xs.Min()) / 2.0, 9);
            Assert.Equal(instance.Y, (ys.Max() + ys.Min()) / 2.0, 9);
        }

        [Fact]
        public void A_keep_out_draw_failure_removes_the_whole_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            // The draw stage fails on the first construction-square edge —
            // whichever square emits first, the session aborts identically.
            host.FailSquareDraw = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.BlockInstances));
        }

        // ---------- named undo over the block + keep-out square ----------

        [Fact]
        public void Named_undo_removes_the_keep_out_square_with_the_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(result.Succeeded, result.Message);
            var qrSketch = doc.Sketches.Single(s => !ReferenceEquals(s, userSketch));

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());

            Assert.DoesNotContain(qrSketch, doc.Sketches);
            Assert.Contains(userSketch, doc.Sketches);
        }

        [Fact]
        public void Redo_restores_the_keep_out_square_inside_the_block()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var qrSketch = doc.Sketches.Single();
            var def = qrSketch.BlockInstances.Single().Definition;
            var before = KeepOutEdges(def).Count;

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());
            Assert.True(doc.RedoLastStep());

            Assert.Contains(qrSketch, doc.Sketches);
            var keepOut = KeepOutEdges(qrSketch.BlockInstances.Single().Definition);
            Assert.Equal(before, keepOut.Count);
            Assert.All(keepOut, e => Assert.True(e.Construction));
        }
    }
}
