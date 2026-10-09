using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Exercises the Create Test Sketch command through the test adapter:
    /// eligibility decisions, the insertion request itself, and failure /
    /// cleanup behaviour. No SolidWorks assemblies are involved.
    /// </summary>
    public class CreateTestSketchCommandTests
    {
        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        // ---------- happy path ----------

        [Fact]
        public void Creates_exactly_one_new_sketch_when_a_baseline_plane_is_selected()
        {
            var host = HostWithSelectedPlane();
            var before = host.Document!.Sketches.Count;

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(before + 1, host.Document.Sketches.Count);
        }

        [Fact]
        public void Created_sketch_contains_the_closed_square_as_four_line_segments()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            Assert.Equal(4, sketch.Segments.Count);
            // The fake records exactly what the host was asked to insert, so the
            // recorded segments must be the command's planned square edges.
            for (var i = 0; i < 4; i++)
            {
                var planned = TestSquareSketch.Edges[i];
                var actual = sketch.Segments[i];
                Assert.Equal(planned.Start.X, actual.X1, 12);
                Assert.Equal(planned.Start.Y, actual.Y1, 12);
                Assert.Equal(planned.End.X, actual.X2, 12);
                Assert.Equal(planned.End.Y, actual.Y2, 12);
            }
        }

        [Fact]
        public void Leaves_the_new_sketch_open_for_editing()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            Assert.Same(sketch, host.Document.ActiveSketch);
        }

        [Fact]
        public void Reports_the_created_sketch_name()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(host.Document!.Sketches.Single().Name, result.CreatedSketchName);
        }

        [Fact]
        public void Redraws_after_successful_insertion()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateTestSketchCommand().Execute(host);

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

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(expected, doc.SketchManagerFlags);
        }

        // ---------- eligibility rejections ----------

        [Fact]
        public void Rejects_when_no_document_is_open()
        {
            var host = new TestSketchHost(); // no document

            var result = new CreateTestSketchCommand().Execute(host);

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

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("part", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document.Sketches);
        }

        [Fact]
        public void Rejects_when_nothing_is_selected()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("select", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document!.Sketches);
        }

        [Fact]
        public void Rejects_when_the_selection_is_not_a_baseline_plane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.Face);

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("plane", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document.Sketches);
        }

        [Fact]
        public void Rejects_when_more_than_one_item_is_selected()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            host.Document.Selection.Add(FakeSelType.Edge);

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("plane", result.Message, System.StringComparison.OrdinalIgnoreCase);
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

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("sketch", result.Message, System.StringComparison.OrdinalIgnoreCase);
            // The user's sketch must remain exactly as it was: still open, same segments.
            Assert.Same(openSketch, doc.ActiveSketch);
            Assert.Single(openSketch.Segments);
            Assert.Single(doc.Sketches);
        }

        [Fact]
        public void Rejection_does_not_consume_a_sketch_name_or_alter_feature_tree()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            doc.ActiveSketch = new FakeSketch("SketchUser");
            doc.Sketches.Add(doc.ActiveSketch);
            var featuresBefore = doc.PreExistingFeatures.ToList();
            var nextNumber = doc.NextSketchNumber;

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Equal(featuresBefore, doc.PreExistingFeatures);
            Assert.Equal(nextNumber, doc.NextSketchNumber);
        }

        // ---------- insertion failures ----------

        [Fact]
        public void Reports_failure_when_the_host_refuses_to_begin_the_sketch()
        {
            var host = HostWithSelectedPlane();
            host.BeginFailure = "The selected plane reference is stale.";

            var result = new CreateTestSketchCommand().Execute(host);

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

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("line", result.Message, System.StringComparison.OrdinalIgnoreCase);
            // Only the partial sketch is removed; prior user geometry is intact.
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void Reports_successful_cleanup_in_the_failure_message()
        {
            var host = HostWithSelectedPlane();
            host.FailOnLineNumber = 2;

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Reports_cleanup_failure_accurately_instead_of_claiming_rollback()
        {
            var host = HostWithSelectedPlane();
            host.FailOnLineNumber = 2;
            host.FailCleanup = true;

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Contains("cleanup", result.Message, System.StringComparison.OrdinalIgnoreCase);
            // Honest reporting: message must NOT claim the partial sketch was removed.
            Assert.DoesNotContain("was removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Injected_line_failure_through_the_seam_aborts_and_cleans_up()
        {
            // Same wrapper the live probe uses: exercises the command's real
            // abort path over any ISketchHost implementation.
            var host = new SolidWorksQrAddIn.Core.Hosting.FailureInjectingHost(
                HostWithSelectedPlane(), failOnLine: 3);

            var result = new CreateTestSketchCommand().Execute(host);

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

            var result = new CreateTestSketchCommand().Execute(host);

            Assert.False(result.Succeeded);
            Assert.Equal(expected, doc.SketchManagerFlags);
        }
    }
}
