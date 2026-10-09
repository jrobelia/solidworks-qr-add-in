using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket 5 — sketch-support selection: a single reference plane (built-in
    /// or user-created, any orientation) or a single planar face supports the
    /// QR sketch; a non-planar face, an unsupported entity, no selection, and
    /// multiple selections are refused with a useful error and never mutate
    /// the part. Deterministic coordinate coverage: whatever the support, the
    /// insertion request is the module's contour edges passed through
    /// verbatim in sketch-space metres — the seam performs no transformation.
    /// Exercised through the ISketchHost test adapter; no SolidWorks involved.
    /// </summary>
    public class SketchSupportTests
    {
        private const string Content = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        private static readonly QrSketch Expected =
            QrSketchGenerator.Generate(Content, SizeMm, Growth).Sketch!;

        private static TestSketchHost HostWith(params int[] selectionCodes)
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.AddRange(selectionCodes);
            return host;
        }

        /// <summary>Nothing created and nothing left in edit mode.</summary>
        private static void AssertNothingMutated(FakeDocument doc)
        {
            Assert.Empty(doc.Sketches);
            Assert.Null(doc.ActiveSketch);
        }

        // ---------- supported selections ----------

        [Theory]
        [InlineData(FakeSelType.DatumPlane)]
        [InlineData(FakeSelType.Face)]
        public void One_plane_or_planar_face_supports_one_new_sketch(int selType)
        {
            var host = HostWith(selType);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.Single(host.Document!.Sketches);
        }

        [Theory]
        [InlineData(FakeSelType.DatumPlane)]
        [InlineData(FakeSelType.Face)]
        public void The_new_sketch_stays_open_for_editing_on_any_support(int selType)
        {
            var host = HostWith(selType);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.Same(host.Document!.Sketches.Single(), host.Document.ActiveSketch);
        }

        [Theory]
        [InlineData(FakeSelType.DatumPlane)]
        [InlineData(FakeSelType.Face)]
        public void Contour_edges_reach_the_host_untransformed_on_any_support(int selType)
        {
            // The seam contract: AddLine receives the module's sketch-space
            // metres unchanged — centring and orientation come from the module
            // result itself, never from an adapter-side transform. Asserted
            // exactly (bit-identical), not recomputed the way the code does.
            var host = HostWith(selType);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var segs = host.Document!.Sketches.Single().Segments;
            var expected = Expected.Edges.ToList();
            Assert.Equal(expected.Count, segs.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Start.X, segs[i].X1);
                Assert.Equal(expected[i].Start.Y, segs[i].Y1);
                Assert.Equal(expected[i].End.X, segs[i].X2);
                Assert.Equal(expected[i].End.Y, segs[i].Y2);
            }
        }

        [Fact]
        public void A_plane_and_a_face_receive_the_identical_insertion_request()
        {
            // Same QR, same contours — the support kind changes only where the
            // native sketch lands, never what the module asks to insert.
            var onPlane = HostWith(FakeSelType.DatumPlane);
            var onFace = HostWith(FakeSelType.Face);

            Assert.True(new CreateQrSketchCommand().Execute(onPlane, Content, SizeMm, Growth).Succeeded);
            Assert.True(new CreateQrSketchCommand().Execute(onFace, Content, SizeMm, Growth).Succeeded);

            var fromPlane = onPlane.Document!.Sketches.Single().Segments;
            var fromFace = onFace.Document!.Sketches.Single().Segments;
            Assert.Equal(fromPlane.Count, fromFace.Count);
            for (var i = 0; i < fromPlane.Count; i++)
            {
                Assert.Equal(fromPlane[i].X1, fromFace[i].X1);
                Assert.Equal(fromPlane[i].Y1, fromFace[i].Y1);
                Assert.Equal(fromPlane[i].X2, fromFace[i].X2);
                Assert.Equal(fromPlane[i].Y2, fromFace[i].Y2);
            }
        }

        [Theory]
        [InlineData(FakeSelType.DatumPlane)]
        [InlineData(FakeSelType.Face)]
        public void The_outline_stays_centred_on_the_new_sketch_origin(int selType)
        {
            // Spec: the nominal QR grid is centred at the new sketch origin —
            // the sketch-space extents are exactly ±size/2, no hidden offset.
            var host = HostWith(selType);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            var segs = host.Document!.Sketches.Single().Segments;
            double half = SizeMm / 2.0 / 1000.0;
            Assert.Equal(half, segs.Max(s => System.Math.Max(s.X1, s.X2)), 12);
            Assert.Equal(-half, segs.Min(s => System.Math.Min(s.X1, s.X2)), 12);
            Assert.Equal(half, segs.Max(s => System.Math.Max(s.Y1, s.Y2)), 12);
            Assert.Equal(-half, segs.Min(s => System.Math.Min(s.Y1, s.Y2)), 12);
        }

        // ---------- unsupported selections ----------

        [Fact]
        public void A_non_planar_face_is_refused_with_a_useful_error()
        {
            var host = HostWith(FakeSelType.Face);
            host.Document!.SelectedFaceIsPlanar = false;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("not planar", result.Message, System.StringComparison.OrdinalIgnoreCase);
            AssertNothingMutated(host.Document!);
        }

        [Theory]
        [InlineData(FakeSelType.Edge)]
        [InlineData(FakeSelType.Sketch)]
        public void An_unsupported_single_entity_is_refused_with_a_useful_error(int selType)
        {
            var host = HostWith(selType);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("cannot support a sketch", result.Message, System.StringComparison.OrdinalIgnoreCase);
            AssertNothingMutated(host.Document!);
        }

        [Fact]
        public void Nothing_selected_is_refused_with_a_useful_error()
        {
            var host = HostWith();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("Select", result.Message);
            Assert.Contains("planar face", result.Message, System.StringComparison.OrdinalIgnoreCase);
            AssertNothingMutated(host.Document!);
        }

        [Theory]
        [InlineData(FakeSelType.DatumPlane, FakeSelType.DatumPlane)]
        [InlineData(FakeSelType.DatumPlane, FakeSelType.Face)]
        [InlineData(FakeSelType.Face, FakeSelType.Face)]
        [InlineData(FakeSelType.Edge, FakeSelType.DatumPlane)]
        public void Multiple_selections_are_refused_as_ambiguous(int first, int second)
        {
            var host = HostWith(first, second);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("exactly one", result.Message, System.StringComparison.OrdinalIgnoreCase);
            AssertNothingMutated(host.Document!);
        }

        // ---------- revalidation at create ----------

        [Fact]
        public void Begin_revalidates_the_selection_it_does_not_trust_the_earlier_capture()
        {
            // The selection can change between the eligibility capture and the
            // insertion begin — the host must re-read it, never reuse a stale
            // or now-invalid support.
            var host = HostWith(FakeSelType.DatumPlane);
            host.MutateBeforeBegin = doc =>
            {
                doc.Selection.Clear();
                doc.Selection.Add(FakeSelType.Edge);
            };

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("support", result.Message, System.StringComparison.OrdinalIgnoreCase);
            AssertNothingMutated(host.Document!);
        }

        [Fact]
        public void A_selection_lost_between_capture_and_begin_is_refused()
        {
            var host = HostWith(FakeSelType.DatumPlane);
            host.MutateBeforeBegin = doc => doc.Selection.Clear();

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            AssertNothingMutated(host.Document!);
        }

        // ---------- through the session seam ----------

        [Fact]
        public void Session_create_succeeds_on_a_planar_face()
        {
            var session = new QrSketchSession(() => new FakeQrWindow());
            var host = HostWith(FakeSelType.Face);
            session.SetContent(Content);

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Same(host.Document!.Sketches.Single(), host.Document.ActiveSketch);
        }

        [Fact]
        public void Session_create_on_a_planar_face_still_blocks_while_editing()
        {
            var session = new QrSketchSession(() => new FakeQrWindow());
            var host = HostWith(FakeSelType.Face);
            session.SetContent(Content);
            Assert.True(session.CreateSketch(host).Succeeded);

            var second = session.CreateSketch(host);

            Assert.False(second.Succeeded);
            Assert.Contains("sketch", second.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(host.Document!.Sketches);
        }

        [Fact]
        public void The_reported_support_error_is_the_current_selection_not_a_cached_one()
        {
            // Stale-support guard: the host is re-asked at Create; an error
            // must describe the selection as it is then.
            var session = new QrSketchSession(() => new FakeQrWindow());
            var host = HostWith(FakeSelType.Face);
            host.Document!.SelectedFaceIsPlanar = false;
            session.SetContent(Content);

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Contains("not planar", result.Message, System.StringComparison.OrdinalIgnoreCase);
            AssertNothingMutated(host.Document!);
        }

        [Fact]
        public void Switching_the_selection_to_a_face_between_creates_is_picked_up_fresh()
        {
            var session = new QrSketchSession(() => new FakeQrWindow());
            var host = HostWith(FakeSelType.DatumPlane);
            session.SetContent(Content);
            Assert.True(session.CreateSketch(host).Succeeded);
            host.Document!.ActiveSketch = null; // engineer exits the sketch

            host.Document.Selection.Clear();
            host.Document.Selection.Add(FakeSelType.Face);
            var second = session.CreateSketch(host);

            Assert.True(second.Succeeded, second.Message);
            Assert.Equal(2, host.Document.Sketches.Count);
        }
    }
}
