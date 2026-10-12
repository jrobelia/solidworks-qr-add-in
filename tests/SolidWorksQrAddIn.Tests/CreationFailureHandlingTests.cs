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
    /// Ticket 6 — cancellation and failure handling. Exercises the complete
    /// Create path against document changes and closes, stale selections, an
    /// open foreign sketch-edit session, empty/invalid input and generation
    /// failure, exceptions at every seam boundary, mid-insertion failure at
    /// several points (first line, a later edge, cleanup, teardown), and the
    /// named-undo contract. Assertions are on resulting DOCUMENT STATE —
    /// sketches, edit mode, flags, undo steps — never just that a method ran.
    /// No SolidWorks involved: TestSketchHost is the behavioural adapter.
    /// </summary>
    public class CreationFailureHandlingTests
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
            // Prior work in the tree AND on the undo stack — a failing or
            // undone QR operation must leave it exactly as it was.
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

        private static System.Collections.Generic.Dictionary<string, bool> FlagSnapshot(FakeDocument doc) =>
            doc.SketchManagerFlags.ToDictionary(kv => kv.Key, kv => kv.Value);

        private static void AssertFlagsRestored(
            FakeDocument doc, System.Collections.Generic.Dictionary<string, bool> expected) =>
            Assert.Equal(expected, doc.SketchManagerFlags);

        // ---------- cancellation ----------

        [Fact]
        public void Closing_the_window_before_create_never_asks_the_host()
        {
            // Cancel == the window closes with no Create press: the session
            // never even reads host state, let alone mutates the part.
            var windows = new System.Collections.Generic.List<FakeQrWindow>();
            var session = new QrSketchSession(() =>
            {
                var w = new FakeQrWindow();
                windows.Add(w);
                return w;
            });
            var host = HostWithSelectedPlane();

            session.OpenOrFocusWindow();
            session.SetContent(Content);
            windows[0].SimulateUserClose();

            Assert.False(session.HasOpenWindow);
            Assert.Empty(host.Document!.Sketches);
            Assert.Null(host.Document.ActiveSketch);
            Assert.Equal(0, host.CaptureCount);   // the host was never consulted
            Assert.Equal(0, host.BeginCount);
        }

        // ---------- document switching / closing ----------

        [Fact]
        public void Create_after_the_document_was_closed_fails_without_mutation()
        {
            var session = new QrSketchSession(() => new FakeQrWindow());
            var docA = FakeDocument.FreshPart();
            docA.Selection.Add(FakeSelType.DatumPlane);
            var host = new TestSketchHost { Document = docA };
            session.SetContent(Content);

            host.Document = null; // the part the support was selected in is gone

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Contains("No active document", result.Message);
            Assert.Empty(docA.Sketches);       // the closed doc was never redirected into
            Assert.Null(docA.ActiveSketch);
        }

        [Fact]
        public void A_support_selected_in_the_current_document_is_used_normally_after_a_switch()
        {
            var session = new QrSketchSession(() => new FakeQrWindow());
            var docA = FakeDocument.FreshPart();
            docA.Selection.Add(FakeSelType.DatumPlane);
            var host = new TestSketchHost { Document = docA };
            session.SetContent(Content);

            // User switched to a second part and selected a planar FACE there.
            var docB = FakeDocument.FreshPart();
            docB.Selection.Add(FakeSelType.Face);
            host.Document = docB;

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Empty(docA.Sketches);
            Assert.Single(docB.Sketches);      // the current document, not the stale one
        }

        [Fact]
        public void A_document_switched_between_capture_and_begin_targets_the_current_one()
        {
            // The in-flight case: eligibility saw doc A's plane, then the
            // active document became doc B (fresh planar-face selection)
            // before insertion began. The host re-reads state at begin —
            // creation lands in B; A is never touched.
            var session = new QrSketchSession(() => new FakeQrWindow());
            var docA = FakeDocument.FreshPart();
            docA.Selection.Add(FakeSelType.DatumPlane);
            var host = new TestSketchHost { Document = docA };
            session.SetContent(Content);

            var docB = FakeDocument.FreshPart();
            docB.Selection.Add(FakeSelType.Face);
            host.MutateBeforeBegin = _ => host.Document = docB;

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Empty(docA.Sketches);
            Assert.Single(docB.Sketches);
        }

        [Fact]
        public void A_document_closed_between_capture_and_begin_is_refused()
        {
            var session = new QrSketchSession(() => new FakeQrWindow());
            var host = HostWithSelectedPlane();
            var docA = host.Document!;
            session.SetContent(Content);

            host.MutateBeforeBegin = _ => host.Document = null; // doc closed mid-flight

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(docA.Sketches);
        }

        [Fact]
        public void A_selection_changed_to_another_valid_support_between_capture_and_begin_uses_the_fresh_one()
        {
            // Stale-selection mirror case: eligibility saw the plane, then the
            // user selected a planar FACE before insertion began. The fresh
            // selection is what gets used — the request is evaluated against
            // the state at begin, not the earlier capture.
            var session = new QrSketchSession(() => new FakeQrWindow());
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            session.SetContent(Content);

            host.MutateBeforeBegin = d =>
            {
                d.Selection.Clear();
                d.Selection.Add(FakeSelType.Face);
            };

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Single(doc.Sketches);
        }

        [Fact]
        public void A_document_switched_to_one_without_a_valid_selection_is_refused_not_redirected()
        {
            // The dangerous stale-support case: eligibility saw doc A's plane,
            // then the active document became doc C with NOTHING usable
            // selected. The cached selection must not steer creation into A —
            // and it must not be applied to C either.
            var session = new QrSketchSession(() => new FakeQrWindow());
            var docA = FakeDocument.FreshPart();
            docA.Selection.Add(FakeSelType.DatumPlane);
            var host = new TestSketchHost { Document = docA };
            session.SetContent(Content);

            var docC = FakeDocument.FreshPart();               // open but unselected
            host.MutateBeforeBegin = _ => host.Document = docC;

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(docA.Sketches);
            Assert.Empty(docC.Sketches);
            Assert.Null(docC.ActiveSketch);
        }

        // ---------- existing sketch edit mode ----------

        [Fact]
        public void Create_while_another_sketch_is_open_adds_nothing_and_touches_nothing()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            doc.ActiveSketch = userSketch;    // the engineer is editing THEIR sketch
            var flags = FlagSnapshot(doc);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("sketch", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Same(userSketch, doc.ActiveSketch);          // never closed
            Assert.Single(userSketch.Segments);                 // never modified
            Assert.Single(doc.Sketches);                        // nothing added
            AssertFlagsRestored(doc, flags);
        }

        // ---------- invalid input / generation failure ----------

        [Fact]
        public void Empty_content_stops_before_any_host_mutation()
        {
            var host = HostWithSelectedPlane();
            var flags = FlagSnapshot(host.Document!);

            var result = new CreateQrSketchCommand().Execute(host, "", SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(0, host.BeginCount);                   // insertion never started
            Assert.Empty(host.Document!.Sketches);
            Assert.Null(host.Document.ActiveSketch);
            AssertFlagsRestored(host.Document, flags);
            Assert.Empty(host.Document.UndoSteps);              // no undo scope left open
            Assert.False(host.Document.IsRecordingUndo);
        }

        [Fact]
        public void An_impossible_request_stops_before_any_host_mutation()
        {
            var host = HostWithSelectedPlane();
            var flags = FlagSnapshot(host.Document!);

            // A size below the insertion guard produces a generation failure —
            // still before the host is touched.
            var result = new CreateQrSketchCommand().Execute(host, Content, 0.001, Growth);

            Assert.False(result.Succeeded);
            Assert.Equal(0, host.BeginCount);
            Assert.Empty(host.Document!.Sketches);
            Assert.Null(host.Document.ActiveSketch);
            AssertFlagsRestored(host.Document, flags);
        }

        // ---------- boundary exceptions ----------

        [Fact]
        public void A_state_capture_that_throws_returns_a_failure_not_an_exception()
        {
            var host = HostWithSelectedPlane();
            host.ThrowOnCapture = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.Empty(host.Document!.Sketches);
            Assert.Null(host.Document.ActiveSketch);
        }

        [Fact]
        public void A_begin_that_throws_unexpectedly_returns_a_failure()
        {
            var host = HostWithSelectedPlane();
            host.BeginThrowsUnhandled = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("start", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(host.Document!.Sketches);
            Assert.Null(host.Document.ActiveSketch);
        }

        [Fact]
        public void A_begin_failure_after_the_sketch_exists_leaves_no_trace()
        {
            // The host entered edit mode and created the sketch, then failed
            // before the session could start. The caller has no handle to
            // that sketch — the HOST must have cleaned up behind itself.
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            var flags = FlagSnapshot(doc);
            host.BeginThrowsAfterSketchCreated = "The sketch manager reported an internal error.";

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("internal error", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);           // only the user's sketch remains
            Assert.Null(doc.ActiveSketch);                      // no edit session leaked
            AssertFlagsRestored(doc, flags);
        }

        [Fact]
        public void A_begin_failure_whose_self_cleanup_failed_reports_the_partial_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.BeginThrowsAfterSketchCreated = "The sketch manager reported an internal error.";
            host.BeginCleanupFails = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("may remain", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, doc.Sketches.Count);
            Assert.Same(userSketch, doc.Sketches[0]);           // prior work untouched
            Assert.Null(doc.ActiveSketch);                      // edit mode still exited
        }

        // ---------- mid-insertion failure ----------

        [Fact]
        public void Failure_on_the_first_edge_removes_the_just_created_sketch()
        {
            // Sketch created, zero edges landed — still cleaned up.
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailOnLineNumber = 1;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void Failure_on_a_late_edge_removes_the_partial_sketch()
        {
            // Deep into the contour edges — same cleanup contract.
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailOnLineNumber = Expected.Info.SegmentCount - 1;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void An_unexpected_exception_mid_insertion_still_aborts_and_cleans_up()
        {
            // A non-seam exception (COM error, host crash) must take the same
            // abort path as a declared SketchInsertionException — a partial
            // sketch and open edit session must never leak.
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.ThrowUnhandledOnLineNumber = 5;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        // ---------- cleanup failure ----------

        [Fact]
        public void A_delete_failure_reports_honestly_and_leaves_the_partial_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailOnLineNumber = 3;
            host.FailCleanup = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("Cleanup failed", result.Message);
            Assert.Contains("may remain", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was removed", result.Message);
            // Document state: edit mode exited, the partial sketch remains in
            // the tree, and the user's sketch was never touched.
            Assert.Null(doc.ActiveSketch);
            Assert.Equal(2, doc.Sketches.Count);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Equal("Sketch1", doc.Sketches[1].Name);      // the partial sketch remains
            // The staged edges are buffered until commit — a mid-staging
            // failure leaves an empty sketch shell (no drawn segments, no
            // block) rather than half a QR.
            Assert.Empty(doc.Sketches[1].Segments);
            Assert.Empty(doc.Sketches[1].BlockInstances);
        }

        [Fact]
        public void An_edit_exit_failure_reports_honestly_with_the_sketch_still_open()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailOnLineNumber = 3;
            host.FailCleanupEditExit = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("Cleanup failed", result.Message);
            Assert.Contains("may remain", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, doc.Sketches.Count);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Same(doc.Sketches[1], doc.ActiveSketch);     // still in edit mode, honestly
        }

        [Fact]
        public void A_cleanup_that_throws_is_reported_as_a_cleanup_failure()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            PriorUserSketch(doc);
            host.FailOnLineNumber = 3;
            host.AbortThrowsUnhandled = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("Cleanup failed", result.Message);
            Assert.Contains("may remain", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, doc.Sketches.Count);                // partial sketch is still there
        }

        [Fact]
        public void Teardown_failure_after_a_failed_insertion_keeps_the_failure_result()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailOnLineNumber = 3;
            host.DisposeThrows = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);                     // a result, not an exception
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
        }

        [Fact]
        public void A_commit_that_throws_aborts_the_finished_insertion()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.KeepOpenThrows = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        // ---------- flags on every path ----------

        [Fact]
        public void Flags_are_restored_when_cleanup_itself_fails()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var flags = FlagSnapshot(doc);
            host.FailOnLineNumber = 2;
            host.FailCleanup = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            AssertFlagsRestored(doc, flags);
        }

        [Fact]
        public void Flags_are_restored_when_leaving_edit_mode_fails()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var flags = FlagSnapshot(doc);
            host.FailOnLineNumber = 2;
            host.FailCleanupEditExit = true;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            AssertFlagsRestored(doc, flags);
        }

        [Fact]
        public void Flags_are_never_touched_when_begin_is_refused()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var flags = FlagSnapshot(doc);
            host.BeginFailure = "The selection went stale.";

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            AssertFlagsRestored(doc, flags);
        }

        // ---------- the injection wrapper ----------

        [Fact]
        public void The_injection_wrapper_can_fail_right_after_sketch_creation()
        {
            var inner = HostWithSelectedPlane();
            var doc = inner.Document!;
            var userSketch = PriorUserSketch(doc);
            var host = new FailureInjectingHost(inner, failOnLine: 1);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("removed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void The_injection_wrapper_can_fail_during_a_later_edge_insertion()
        {
            var inner = HostWithSelectedPlane();
            var doc = inner.Document!;
            PriorUserSketch(doc);
            var host = new FailureInjectingHost(inner, failOnLine: Expected.Info.SegmentCount);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Single(doc.Sketches);
            Assert.Null(doc.ActiveSketch);
        }

        [Fact]
        public void The_injection_wrapper_can_fail_at_begin_without_creating_anything()
        {
            var inner = HostWithSelectedPlane();
            var doc = inner.Document!;
            var userSketch = PriorUserSketch(doc);
            var host = new FailureInjectingHost(inner, failBegin: "The support vanished mid-create.");

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("vanished", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, inner.BeginCount);                  // inner never started
            Assert.Single(doc.Sketches);
            Assert.Same(userSketch, doc.Sketches[0]);
        }

        [Fact]
        public void The_injection_wrapper_can_fail_cleanup_over_a_failing_inner()
        {
            var inner = HostWithSelectedPlane();
            var doc = inner.Document!;
            var userSketch = PriorUserSketch(doc);
            inner.FailCleanup = true;
            var host = new FailureInjectingHost(inner, failOnLine: 4, failCleanup: true);

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            Assert.Contains("may remain", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, doc.Sketches.Count);
            Assert.Same(userSketch, doc.Sketches[0]);
        }

        // ---------- named undo ----------

        [Fact]
        public void A_successful_create_registers_exactly_one_named_undo_step()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            PriorUserSketch(doc);
            var stepsBefore = doc.UndoSteps.Count;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(stepsBefore + 1, doc.UndoSteps.Count);
            Assert.Equal(SketchHostContract.CreationUndoName, doc.UndoSteps[stepsBefore].Name);
        }

        [Fact]
        public void Undoing_the_creation_removes_only_the_qr_sketch()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            Assert.True(new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth).Succeeded);
            var qrSketch = doc.Sketches.Single(s => s != userSketch);

            doc.ActiveSketch = null;                            // engineer exits the QR sketch
            Assert.True(doc.UndoLastStep());                    // one Ctrl+Z

            Assert.DoesNotContain(qrSketch, doc.Sketches);
            Assert.Contains(userSketch, doc.Sketches);          // prior work intact
            Assert.Single(doc.UndoSteps);                       // the user's step was not eaten
            Assert.Equal("User Sketch", doc.UndoSteps[0].Name);
        }

        [Fact]
        public void A_failed_create_leaves_no_undoable_residue_over_prior_work()
        {
            var host = HostWithSelectedPlane();
            var doc = host.Document!;
            var userSketch = PriorUserSketch(doc);
            host.FailOnLineNumber = 4;

            var result = new CreateQrSketchCommand().Execute(host, Content, SizeMm, Growth);

            Assert.False(result.Succeeded);
            // The aborted attempt closes its undo scope; reverting it must be
            // a no-op — prior work is still exactly one Ctrl+Z away.
            Assert.True(doc.UndoLastStep());
            Assert.Contains(userSketch, doc.Sketches);
            Assert.Single(doc.UndoSteps);
            Assert.Equal("User Sketch", doc.UndoSteps[0].Name);
        }

        // ---------- session boundary ----------

        [Fact]
        public void A_host_that_throws_behind_the_session_surfaces_a_failure_result()
        {
            // Exceptions crossing the window boundary become the status text —
            // they must never escape toward SolidWorks.
            var session = new QrSketchSession(() => new FakeQrWindow());
            session.SetContent(Content);
            var host = HostWithSelectedPlane();
            host.ThrowOnCapture = true;

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.Equal(result.Message, session.StatusText);
        }
    }
}
