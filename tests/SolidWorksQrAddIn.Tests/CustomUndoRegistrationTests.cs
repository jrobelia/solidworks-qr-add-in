using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket 6 follow-up — the custom undo object (SetApiUndoObject) that
    /// replaces the refused undo recording. Assertions run through the seam:
    /// the command hands the host a SketchUndoRegistration after a committed
    /// creation; the fake models the undo object's DoUndo toggle as a named
    /// step that removes exactly that sketch (and re-adds it on redo).
    /// </summary>
    public class CustomUndoRegistrationTests
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
        public void A_successful_create_hands_the_host_one_undo_registration()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var reg = Assert.Single(host.UndoRegistrations);
            Assert.Equal(SketchHostContract.CreationUndoName, reg.UndoName);
            Assert.Equal(result.CreatedSketchName, reg.SketchName);
            Assert.Equal(Expected.Info.SegmentCount, reg.Edges.Count);
        }

        [Fact]
        public void Undo_removes_exactly_the_created_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(result.Succeeded, result.Message);
            var qrSketch = doc.Sketches.Single(s => !ReferenceEquals(s, userSketch));

            doc.ActiveSketch = null;                 // engineer exits the sketch
            Assert.True(doc.UndoLastStep());         // Ctrl+Z on "Create QR Sketch"

            Assert.DoesNotContain(qrSketch, doc.Sketches);
            Assert.Contains(userSketch, doc.Sketches);   // prior work intact
        }

        [Fact]
        public void Undo_with_the_sketch_already_deleted_is_a_safe_no_op()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var qrSketch = doc.Sketches.Single(s => !ReferenceEquals(s, userSketch));

            doc.ActiveSketch = null;
            doc.Sketches.Remove(qrSketch);           // the user deleted it themselves
            Assert.True(doc.UndoLastStep());         // undo fires anyway — must not throw or delete anything else

            Assert.Contains(userSketch, doc.Sketches);
            Assert.Single(doc.Sketches);             // only the user's sketch remains
        }

        [Fact]
        public void Redo_restores_the_removed_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var qrSketch = doc.Sketches.Single();

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());
            Assert.DoesNotContain(qrSketch, doc.Sketches);

            Assert.True(doc.RedoLastStep());         // the same handler toggles back
            Assert.Contains(qrSketch, doc.Sketches);
            // Redo restores the sketch whole — block instance, contours, and
            // the construction squares inside its definition.
            Assert.Equal(Expected.Info.SegmentCount,
                qrSketch.ProfileSegments.Count());
            Assert.Equal(8, qrSketch.BlockInstances.Single()
                .Definition.ConstructionEdges.Count);
            // ... with the block instance back at its unscaled state — the
            // live round-1 failure was the rebuild dying inside
            // MakeSketchBlockFromSelected, leaving no instance at all.
            var instance = Assert.Single(qrSketch.BlockInstances);
            Assert.Equal(1.0, instance.Scale, 9);
        }

        [Fact]
        public void Each_create_registers_a_fresh_step_for_its_own_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;

            var first = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(first.Succeeded, first.Message);
            var qrSketch1 = doc.Sketches.Single(s => s.Name == first.CreatedSketchName);
            doc.ActiveSketch = null;                 // exit the first sketch before the next create

            var second = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(second.Succeeded, second.Message);
            var qrSketch2 = doc.Sketches.Single(s => s.Name == second.CreatedSketchName);
            doc.ActiveSketch = null;

            Assert.Equal(2, host.UndoRegistrations.Count);
            // Newest-first: each undo step removes only ITS sketch.
            Assert.True(doc.UndoLastStep());
            Assert.DoesNotContain(qrSketch2, doc.Sketches);
            Assert.Contains(qrSketch1, doc.Sketches);
            Assert.True(doc.UndoLastStep());
            Assert.DoesNotContain(qrSketch1, doc.Sketches);
        }

        [Fact]
        public void Undo_removes_the_created_sketch_so_the_next_create_succeeds()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);

            doc.ActiveSketch = null;
            Assert.True(doc.UndoLastStep());
            Assert.Contains(userSketch, doc.Sketches);
            Assert.Single(doc.Sketches);

            // A create following the undo lands cleanly — the removed sketch
            // left nothing behind to impede it.
            var second = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);
            Assert.True(second.Succeeded, second.Message);
            Assert.Equal(2, doc.Sketches.Count);
        }

        [Fact]
        public void A_failed_undo_registration_does_not_break_creation()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            host.FailUndoRegistration = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.Single(doc.Sketches);                       // the creation stands
            Assert.Empty(doc.UndoSteps);                       // just no undo step
            Assert.Single(host.UndoRegistrations);             // the attempt was handed over
        }

        [Fact]
        public void A_failed_create_registers_no_undo_step()
        {
            var host = HostWithSelectedPlane();
            host.FailOnLineNumber = 3;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Empty(host.UndoRegistrations);
        }
    }
}
