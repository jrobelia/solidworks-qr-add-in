using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Exercises the Create QR Sketch command through the test adapter:
    /// eligibility decisions, the QR insertion request itself, and failure /
    /// cleanup behaviour. No SolidWorks assemblies are involved.
    /// </summary>
    public class CreateQrSketchCommandTests
    {
        /// <summary>The payload exercised by this suite — a real encoded QR.</summary>
        private const string Content = "SW-QR-MVP";

        /// <summary>The request this suite uses unless a test sets its own values.</summary>
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        private static readonly QrSketch Expected = QrSketchGenerator
            .Generate(Content,
                SizeMm,
                Growth)
            .Sketch!;

        // ---------- happy path ----------

        [Fact]
        public void Creates_exactly_one_new_sketch_when_a_reference_plane_is_selected()
        {
            var host = HostWithSelectedPlane();
            var before = host.Document!.Sketches.Count;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(before + 1, host.Document.Sketches.Count);
        }

        [Fact]
        public void Inserts_every_generated_contour_edge_as_a_native_line()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            // The QR lands inside the block instance — the effective (drawn)
            // geometry the sketch shows is the contour set, in order.
            var effective = sketch.ProfileSegments.ToList();
            Assert.Equal(Expected.Info.SegmentCount, effective.Count);
            var expected = Expected.Edges.ToList();
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Start.X, effective[i].X1, 12);
                Assert.Equal(expected[i].Start.Y, effective[i].Y1, 12);
                Assert.Equal(expected[i].End.X, effective[i].X2, 12);
                Assert.Equal(expected[i].End.Y, effective[i].Y2, 12);
            }
        }

        [Fact]
        public void Inserted_sketch_spans_the_requested_25mm_centred_on_the_origin()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var segs = host.Document!.Sketches.Single().ProfileSegments.ToList();
            double half = SizeMm / 2.0 / 1000.0;
            Assert.Equal(half, segs.Max(s => System.Math.Max(s.X1, s.X2)), 9);
            Assert.Equal(-half, segs.Min(s => System.Math.Min(s.X1, s.X2)), 9);
            Assert.Equal(half, segs.Max(s => System.Math.Max(s.Y1, s.Y2)), 9);
            Assert.Equal(-half, segs.Min(s => System.Math.Min(s.Y1, s.Y2)), 9);
        }

        [Fact]
        public void Leaves_the_new_sketch_open_for_editing()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            Assert.Same(sketch, host.Document.ActiveSketch);
        }

        [Fact]
        public void Reports_the_created_sketch_name()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(host.Document!.Sketches.Single().Name, result.CreatedSketchName);
        }

        [Fact]
        public void Redraws_after_successful_insertion()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.True(host.Document!.RedrawCount >= 1);
        }

        [Fact]
        public void Restores_sketch_manager_flags_after_insertion()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            doc.SketchManagerFlags["AddToDB"] = false;
            doc.SketchManagerFlags["DisplayWhenAdded"] = true;
            var expected = doc.SketchManagerFlags.ToDictionary(kv => kv.Key, kv => kv.Value);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(expected, doc.SketchManagerFlags);
        }

        // ---------- eligibility rejections ----------

        [Fact]
        public void Rejects_when_no_document_is_open()
        {
            var host = new TestSketchHost(); // no document

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("document", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Assembly")]
        [InlineData("Drawing")]
        public void Rejects_when_active_document_is_not_a_part(string docKind)
        {
            var host = new TestSketchHost { Document = new FakeDocument(docKind) };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("part", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document.Sketches);
        }

        [Fact]
        public void Rejects_when_nothing_is_selected()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("select", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document!.Sketches);
        }

        [Fact]
        public void Rejects_when_the_selection_is_not_a_sketch_support()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.Edge);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("support a sketch", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document.Sketches);
        }

        [Fact]
        public void Rejects_when_a_sketch_is_already_open_and_leaves_it_untouched()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var openSketch = new FakeSketch("SketchUser");
            openSketch.Segments.Add(new FakeLine(0, 0, 0.01, 0));
            doc.ActiveSketch = openSketch;
            doc.Sketches.Add(openSketch);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("sketch", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Same(openSketch, doc.ActiveSketch);
            Assert.Single(openSketch.Segments);
            Assert.Single(doc.Sketches);
        }

        // ---------- insertion failures ----------

        [Fact]
        public void Reports_failure_when_the_host_refuses_to_begin_the_sketch()
        {
            var host = HostWithSelectedPlane();
            host.BeginFailure = "The selected plane reference is stale.";

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("stale", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document!.Sketches);
        }

        [Fact]
        public void Reports_insertion_failure_and_cleans_up_only_the_partial_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = new FakeSketch("SketchUser");
            doc.Sketches.Add(userSketch); // pre-existing, must survive
            host.FailOnLineNumber = 3;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("line", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void Reports_cleanup_failure_accurately_instead_of_claiming_rollback()
        {
            var host = HostWithSelectedPlane();
            host.FailOnLineNumber = 2;
            host.FailCleanup = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("cleanup", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Injected_line_failure_through_the_seam_aborts_and_cleans_up()
        {
            var host = new SolidWorksQrAddIn.Core.Hosting.FailureInjectingHost(
                HostWithSelectedPlane(), failOnLine: 3);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("line", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Restores_sketch_manager_flags_even_when_insertion_fails()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var expected = doc.SketchManagerFlags.ToDictionary(kv => kv.Key, kv => kv.Value);
            host.FailOnLineNumber = 2;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(expected, doc.SketchManagerFlags);
        }
    }
}
