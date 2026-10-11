using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #10 / ticket #16 — the insertion session realizes the staged QR
    /// contours as ONE sketch block instance: the definition is drawn
    /// unit-proportioned (1 × 1 document unit) and the instance's Scale reads
    /// the requested size in document units — the PropertyManager scale field
    /// IS the size. Assertions stay on the fake host's world state — block
    /// present, instance scale, definition geometry — never call counts.
    /// </summary>
    public class QrBlockInsertionTests
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

        [Fact]
        public void A_create_produces_exactly_one_block_instance_in_the_new_sketch()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var instance = Assert.Single(sketch.BlockInstances);
            // The QR pattern is inside the block — the host sketch holds no
            // raw contour segments of its own.
            Assert.Empty(sketch.Segments);
            Assert.Same(instance.Definition, Assert.Single(host.Document.BlockDefinitions));
        }

        [Fact]
        public void The_instance_scale_reads_the_requested_size_in_document_units()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var instance = Assert.Single(host.Document!.Sketches.Single().BlockInstances);
            // mm document (0.001 m/unit): Scale IS the requested 25 mm size.
            Assert.Equal(SizeMm, instance.Scale, 9);
            Assert.Equal(0.0, instance.Angle, 12);
            Assert.Equal(0.0, instance.X, 12);
            Assert.Equal(0.0, instance.Y, 12);
        }

        [Fact]
        public void The_block_definition_is_unit_proportioned_and_carries_every_edge()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var def = Assert.Single(host.Document!.BlockDefinitions);
            Assert.Equal(Expected.Info.SegmentCount, def.Contours.Count);
            // One document unit across: ±0.5 mm on an mm document. The def
            // content is the staged geometry ÷ the instance scale — exactly
            // the generator's unit contour set drawn in document units.
            var xs = def.Contours.SelectMany(e => new[] { e.X1, e.X2 }).ToList();
            var ys = def.Contours.SelectMany(e => new[] { e.Y1, e.Y2 }).ToList();
            Assert.Equal(0.0005, xs.Max(), 12);
            Assert.Equal(-0.0005, xs.Min(), 12);
            Assert.Equal(0.0005, ys.Max(), 12);
            Assert.Equal(-0.0005, ys.Min(), 12);
            // …and it matches the model's unit set scaled to document units.
            var unit = Expected.Unit.Edges.ToList();
            for (var i = 0; i < unit.Count; i++)
            {
                Assert.Equal(unit[i].Start.X * 0.001, def.Contours[i].X1, 12);
                Assert.Equal(unit[i].Start.Y * 0.001, def.Contours[i].Y1, 12);
            }
        }

        [Fact]
        public void The_instance_renders_the_requested_geometry_at_the_requested_size()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var instance = host.Document!.Sketches.Single().BlockInstances.Single();
            // Effective geometry = def × scale — the same contour set the
            // sized emission produced, in sketch-space metres, ±S/2.
            var effective = instance.EffectiveEdges.ToList();
            var expected = Expected.Edges.ToList();
            Assert.Equal(expected.Count, effective.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Start.X, effective[i].X1, 12);
                Assert.Equal(expected[i].Start.Y, effective[i].Y1, 12);
                Assert.Equal(expected[i].End.X, effective[i].X2, 12);
                Assert.Equal(expected[i].End.Y, effective[i].Y2, 12);
            }
            double half = SizeMm / 2.0 / 1000.0;
            Assert.Equal(half, effective.Max(e => System.Math.Max(e.X1, e.X2)), 9);
            Assert.Equal(-half, effective.Min(e => System.Math.Min(e.X1, e.X2)), 9);
        }

        [Fact]
        public void Editing_the_instance_scale_rescales_every_cell_uniformly()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var instance = host.Document!.Sketches.Single().BlockInstances.Single();
            var before = instance.EffectiveEdges.ToList();

            // The engineer edits the PropertyManager Scale field — the QR's
            // resize handle. 25 → 50 doubles every definition edge.
            instance.Scale = 50.0;

            var after = instance.EffectiveEdges.ToList();
            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].X1 * 2.0, after[i].X1, 12);
                Assert.Equal(before[i].Y1 * 2.0, after[i].Y1, 12);
                Assert.Equal(before[i].X2 * 2.0, after[i].X2, 12);
                Assert.Equal(before[i].Y2 * 2.0, after[i].Y2, 12);
            }
            Assert.Equal(50.0 / 2.0 / 1000.0,
                after.Max(e => System.Math.Max(e.X1, e.X2)), 9);
        }

        [Fact]
        public void In_an_inch_document_the_scale_reads_the_size_in_inches()
        {
            var host = HostWithSelectedPlane();
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var instance = Assert.Single(host.Document.Sketches.Single().BlockInstances);
            // 25 mm = 0.984 in — the Scale field reads the doc-unit size.
            Assert.Equal(SizeMm / 25.4, instance.Scale, 9);
            // The definition still spans exactly one document unit — one inch.
            var xs = instance.Definition.Contours
                .SelectMany(e => new[] { e.X1, e.X2 }).ToList();
            Assert.Equal(0.0254 / 2.0, xs.Max(), 12);
            Assert.Equal(-0.0254 / 2.0, xs.Min(), 12);
            // And the effective geometry is the same 25 mm pattern.
            var effective = instance.EffectiveEdges.ToList();
            Assert.Equal(SizeMm / 2.0 / 1000.0,
                effective.Max(e => System.Math.Max(e.X1, e.X2)), 9);
        }

        // ---------- failure / cleanup at the block step ----------

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

        [Fact]
        public void A_block_draw_failure_removes_the_partial_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailBlockDraw = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.BlockDefinitions);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void A_block_make_failure_removes_the_partial_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailBlockMake = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("block", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.BlockDefinitions);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void A_block_instance_failure_removes_the_whole_sketch_block_included()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailBlockInstance = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            // The formed instance dies with the sketch feature it lives
            // inside — no block geometry remains reachable in the tree.
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.BlockInstances));
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void A_block_failure_with_a_failing_cleanup_reports_honestly()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailBlockMake = true;
            host.FailCleanup = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("Cleanup failed", result.Message);
            Assert.Contains("may remain", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was removed", result.Message);
            Assert.Equal(2, doc.Sketches.Count);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        // ---------- named undo over the block ----------

        [Fact]
        public void Named_undo_registers_the_same_step_which_removes_sketch_and_block()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(result.Succeeded, result.Message);
            var qrSketch = doc.Sketches.Single(s => !ReferenceEquals(s, userSketch));
            var def = doc.BlockDefinitions.Single();

            var reg = Assert.Single(host.UndoRegistrations);
            Assert.Equal(Core.Hosting.SketchHostContract.CreationUndoName, reg.UndoName);
            Assert.Equal(qrSketch.Name, reg.SketchName);
            // The registration still carries the requested geometry — the
            // staged edges a redo would re-insert through the same session.
            Assert.Equal(Expected.Info.SegmentCount, reg.Edges.Count);

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());

            // One Ctrl+Z removes the whole sketch feature — the block lives
            // inside it, so its instance leaves the feature tree with it.
            Assert.DoesNotContain(qrSketch, doc.Sketches);
            Assert.Contains(userSketch, doc.Sketches);
            Assert.Empty(doc.Sketches.SelectMany(s => s.BlockInstances));
        }

        [Fact]
        public void Redo_restores_the_sketch_with_its_block_intact()
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
            var instance = Assert.Single(qrSketch.BlockInstances);
            Assert.Equal(SizeMm, instance.Scale, 9);
            Assert.Equal(Expected.Info.SegmentCount,
                instance.EffectiveEdges.Count());
        }
    }
}
