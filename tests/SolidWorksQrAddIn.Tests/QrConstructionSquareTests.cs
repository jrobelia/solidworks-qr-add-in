using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #10 / ticket #17, reworked by ADR 0003 — the QR construction
    /// square lives INSIDE the block definition: the grown QR-cell extent
    /// drawn as construction geometry in unit definition space, folded into
    /// the definition with the contours so it tracks the instance's Scale
    /// and moves with no constraints, dimensions, or equations. The
    /// keep-out square is <see cref="QrKeepOutSquareTests"/>' subject; this
    /// file covers the QR square, the shared fold mechanics, and the
    /// failure/undo paths. Assertions stay on the fake host's world state —
    /// definition contents, construction flags, effective geometry — never
    /// call counts.
    /// </summary>
    public class QrConstructionSquareTests
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

        /// <summary>The model's QR square — the smaller of the two staged squares.</summary>
        private static QrConstructionSquare ModelQrSquare() =>
            Expected.ConstructionSquares.Single(s => s.Role == QrSquareRole.QrSquare);

        [Fact]
        public void The_qr_square_lives_inside_the_block_definition_as_construction_geometry()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var def = sketch.BlockInstances.Single().Definition;

            // The definition carries BOTH folds: contours plus eight
            // construction edges (two squares). The QR square is the
            // smaller one — the unit box, ±0.5·unit = ±0.0005 m in a mm doc.
            Assert.Equal(8, def.ConstructionEdges.Count);
            var minExtent = def.ConstructionEdges.Min(Extent);
            var qrEdges = def.ConstructionEdges.Where(e => Extent(e) == minExtent).ToList();
            Assert.Equal(4, qrEdges.Count);
            Assert.All(def.ConstructionEdges, e => Assert.True(e.Construction));
            Assert.All(qrEdges, e => Assert.Equal(0.0005, Extent(e), 9));
            Assert.All(qrEdges, e => Assert.Equal(
                0.0005,
                System.Math.Max(System.Math.Abs(e.X2 - e.X1),
                                System.Math.Abs(e.Y2 - e.Y1)) / 2.0,
                9));
        }

        [Fact]
        public void The_square_renders_on_the_instance_at_the_requested_size()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();
            Assert.Equal(SizeMm, instance.Scale, 9);

            // At Scale=25 the unit-box QR square renders at ±12.5 mm — the
            // same span the grown profile reaches.
            var cons = instance.EffectiveEdges.Where(e => e.Construction).ToList();
            var qrHalf = cons.Min(Extent);
            Assert.Equal(0.0125, qrHalf, 9);
            var profileHalf = instance.ProfileEdges
                .SelectMany(e => new[] { e.X1, e.X2 }).Select(System.Math.Abs).Max();
            Assert.Equal(profileHalf, qrHalf, 9);
        }

        [Fact]
        public void A_scale_edit_rescales_the_square_with_the_block()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();
            instance.Scale = SizeMm / 2.0;

            var cons = instance.EffectiveEdges.Where(e => e.Construction).ToList();
            Assert.Equal(0.00625, cons.Min(Extent), 9);
            var profileHalf = instance.ProfileEdges
                .SelectMany(e => new[] { e.X1, e.X2 }).Select(System.Math.Abs).Max();
            Assert.Equal(profileHalf, cons.Min(Extent), 9);
        }

        [Fact]
        public void Dragging_the_instance_moves_the_square_with_it()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();
            instance.X = 0.030;
            instance.Y = -0.010;

            // Measure each edge's extent relative to the moved centre: the
            // QR square is the smaller square centred on (X, Y).
            double RelExtent(FakeLine e) => new[]
            {
                System.Math.Abs(e.X1 - instance.X), System.Math.Abs(e.Y1 - instance.Y),
                System.Math.Abs(e.X2 - instance.X), System.Math.Abs(e.Y2 - instance.Y),
            }.Max();

            var cons = instance.EffectiveEdges.Where(e => e.Construction).ToList();
            var qrEdges = cons.Where(e => RelExtent(e) == cons.Min(RelExtent)).ToList();
            Assert.Equal(4, qrEdges.Count);
            Assert.All(qrEdges, e => Assert.Equal(0.0125, RelExtent(e), 9));
            var xs = qrEdges.SelectMany(e => new[] { e.X1, e.X2 }).ToList();
            var ys = qrEdges.SelectMany(e => new[] { e.Y1, e.Y2 }).ToList();
            Assert.Equal(0.030, (xs.Max() + xs.Min()) / 2.0, 9);
            Assert.Equal(-0.010, (ys.Max() + ys.Min()) / 2.0, 9);
        }

        [Fact]
        public void The_squares_are_not_profile_geometry()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();

            // Rendered geometry carries the two construction squares on top
            // of the contours; profile-eligible geometry stays the contours.
            Assert.Equal(
                sketch.ProfileSegments.Count() + 8,
                sketch.EffectiveSegments.Count());
            Assert.DoesNotContain(sketch.ProfileSegments, e => e.Construction);
            Assert.Equal(8, sketch.EffectiveSegments.Count(e => e.Construction));
        }

        [Fact]
        public void A_square_draw_failure_removes_the_whole_sketch_block_included()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailSquareDraw = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.BlockInstances));
            Assert.Null(doc.ActiveSketch);
        }

        // ---------- named undo over the block + squares ----------

        [Fact]
        public void Named_undo_removes_the_sketch_with_its_squares()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(result.Succeeded, result.Message);
            var qrSketch = doc.Sketches.Single(s => !ReferenceEquals(s, userSketch));

            // The registration ferries the staged squares — a redo re-stages
            // them inside the block definition.
            var reg = Assert.Single(host.UndoRegistrations);
            Assert.Equal(2, reg.ConstructionSquares!.Count);

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());

            // One Ctrl+Z removes the whole sketch feature — the block with
            // both construction squares lives inside it.
            Assert.DoesNotContain(qrSketch, doc.Sketches);
            Assert.Contains(userSketch, doc.Sketches);
        }

        [Fact]
        public void Redo_restores_the_sketch_with_its_squares_inside_the_block()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var qrSketch = doc.Sketches.Single();

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());
            Assert.DoesNotContain(qrSketch, doc.Sketches);

            Assert.True(doc.RedoLastStep());
            Assert.Contains(qrSketch, doc.Sketches);
            var instance = qrSketch.BlockInstances.Single();
            Assert.Equal(SizeMm, instance.Scale, 9);
            var cons = instance.Definition.ConstructionEdges;
            Assert.Equal(8, cons.Count);
            Assert.All(cons, e => Assert.True(e.Construction));
            // The smaller construction square is the unit box.
            Assert.Equal(4, cons.Count(e => Extent(e) == cons.Min(Extent)));
            Assert.Equal(8, qrSketch.EffectiveSegments.Count(e => e.Construction));
        }

        [Fact]
        public void No_host_sketch_geometry_is_emitted_for_the_squares()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            var sketch = host.Document!.Sketches.Single();
            // Everything landed inside the block: the host sketch's raw
            // segment list is empty — no stray squares or dims at top level.
            Assert.Empty(sketch.Segments);
            Assert.Single(sketch.BlockInstances);
        }
    }
}
