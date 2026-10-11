using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #10 / ticket #17 — the host sketch gains a construction QR square
    /// coincident to the block instance's outer edges (it tracks the
    /// PropertyManager Scale edit) carrying a driven DisplayDimension — the
    /// live size readout. Emitted from the generator's construction squares +
    /// dimension spec through the existing insertion session — no new
    /// caller-facing contract. Assertions stay on the fake host's world
    /// state — construction segments, the bound instance, the dimension —
    /// never call counts.
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

        [Fact]
        public void A_create_emits_the_qr_square_as_construction_geometry_in_the_host_sketch()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var square = Assert.Single(sketch.ConstructionSquares);
            Assert.Equal(QrSquareRole.QrSquare, square.Role);
            Assert.True(square.IsConstruction);
            // Four edges at the grown QR extent — ±S/2 sketch-space metres.
            var edges = square.EffectiveEdges.ToList();
            Assert.Equal(4, edges.Count);
            double half = SizeMm / 2.0 / 1000.0;
            Assert.Equal(half, edges.Max(e => System.Math.Max(e.X1, e.X2)), 12);
            Assert.Equal(-half, edges.Min(e => System.Math.Min(e.X1, e.X2)), 12);
            Assert.Equal(half, edges.Max(e => System.Math.Max(e.Y1, e.Y2)), 12);
            Assert.Equal(-half, edges.Min(e => System.Math.Min(e.Y1, e.Y2)), 12);
        }

        [Fact]
        public void The_keep_out_square_is_not_emitted_yet_only_the_driven_square_lands()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            // The model carries both squares (generator-tested); the spec
            // names the QR square as the driven one, and only it lands —
            // the equation-linked keep-out square is the next ticket.
            Assert.Equal(2, Expected.ConstructionSquares.Count);
            var sketch = host.Document!.Sketches.Single();
            Assert.DoesNotContain(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.KeepOutSquare);
        }

        [Fact]
        public void The_square_is_bound_to_the_block_instance_and_tracks_a_scale_edit()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var sketch = host.Document!.Sketches.Single();
            var instance = sketch.BlockInstances.Single();
            var square = sketch.ConstructionSquares.Single();

            // The coincident bindings hold the square's edges on the block's
            // outer edges — the bound target IS the created instance.
            Assert.Same(instance, square.BoundInstance);
            Assert.True(square.CoincidentBindings >= 4,
                "each square edge needs at least one coincident binding to the block");

            // The engineer edits the PropertyManager Scale field — the square
            // follows the block's outer edges to the new extent.
            instance.Scale = 50.0;

            var edges = square.EffectiveEdges.ToList();
            double newHalf = 50.0 / 2.0 / 1000.0;
            Assert.Equal(newHalf, edges.Max(e => System.Math.Max(e.X1, e.X2)), 9);
            Assert.Equal(-newHalf, edges.Min(e => System.Math.Min(e.X1, e.X2)), 9);
            Assert.Equal(newHalf, edges.Max(e => System.Math.Max(e.Y1, e.Y2)), 9);
            Assert.Equal(-newHalf, edges.Min(e => System.Math.Min(e.Y1, e.Y2)), 9);
        }

        [Fact]
        public void The_driven_dimension_reads_the_qr_size_and_updates_on_a_scale_edit()
        {
            var host = HostWithSelectedPlane();
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var sketch = host.Document!.Sketches.Single();
            var square = sketch.ConstructionSquares.Single();
            var instance = sketch.BlockInstances.Single();

            var dim = Assert.Single(sketch.Dimensions);
            Assert.True(dim.IsDriven);                  // a readout, never a driver
            Assert.Same(square, dim.Target);            // it measures the QR square
            Assert.Equal(SizeMm, dim.ValueDocUnits, 9); // live readout in doc units

            // A Scale edit drags the square; the driven dim reads the new size.
            instance.Scale = 50.0;
            Assert.Equal(50.0, dim.ValueDocUnits, 9);
        }

        [Fact]
        public void The_dimension_lands_with_the_prompt_toggles_guarded_and_restored()
        {
            var host = HostWithSelectedPlane();
            // The user's preferences start ON — the dangerous state the
            // spike documented (IAddDimension2 hangs an invisible session).
            Assert.True(host.InputDimValOnCreate);
            Assert.True(host.OverdefiningDimsPromptToSetState);

            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var dim = host.Document!.Sketches.Single().Dimensions.Single();

            // The dim was created while both prompts were suppressed …
            Assert.True(dim.PromptsSuppressedAtCreation);
            // … and the user preferences are back exactly as found.
            Assert.True(host.InputDimValOnCreate);
            Assert.True(host.OverdefiningDimsPromptToSetState);
        }

        // ---------- failure / cleanup at the square + dim step ----------

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
            // The failed square step is inside the same insertion session —
            // abort removes the sketch and the block lives inside it.
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.BlockInstances));
            Assert.Empty(doc.Sketches.SelectMany(s => s.ConstructionSquares));
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void A_square_constraint_failure_removes_the_whole_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailSquareConstraint = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.BlockInstances));
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void A_dimension_failure_removes_the_sketch_and_still_restores_the_toggles()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailDimension = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Empty(doc.Sketches.SelectMany(s => s.Dimensions));
            // A failed dim must never strand the user's prompt preferences.
            Assert.True(host.InputDimValOnCreate);
            Assert.True(host.OverdefiningDimsPromptToSetState);
        }

        // ---------- named undo over the square + dim ----------

        [Fact]
        public void Named_undo_removes_the_sketch_with_its_square_and_dimension()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(result.Succeeded, result.Message);
            var qrSketch = doc.Sketches.Single(s => !ReferenceEquals(s, userSketch));

            // The registration ferries the layout too — a redo re-stages the
            // squares + dimension spec through the same session contract.
            var reg = Assert.Single(host.UndoRegistrations);
            Assert.Equal(2, reg.ConstructionSquares!.Count);
            Assert.NotNull(reg.DimensionSpec);

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());

            // One Ctrl+Z removes the whole sketch feature — the block, the
            // construction square and the dimension all live inside it.
            Assert.DoesNotContain(qrSketch, doc.Sketches);
            Assert.Contains(userSketch, doc.Sketches);
        }

        [Fact]
        public void Redo_restores_the_sketch_with_its_square_and_dimension()
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
            var square = Assert.Single(qrSketch.ConstructionSquares);
            Assert.Equal(QrSquareRole.QrSquare, square.Role);
            var dim = Assert.Single(qrSketch.Dimensions);
            Assert.True(dim.IsDriven);
            Assert.Equal(SizeMm, dim.ValueDocUnits, 9);
        }
    }
}
