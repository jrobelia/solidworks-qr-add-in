using System.Collections.Generic;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Exercises the interaction state behind the modeless QR window at its
    /// test seam: content → preview picture transitions, exact-payload
    /// handling, current-request sketch creation through the ISketchHost
    /// seam, and the single-window lifecycle (focus-or-recreate, close,
    /// release on disconnect). No WPF or SolidWorks involved — the window is
    /// a FakeQrWindow and the host is TestSketchHost.
    /// </summary>
    public class QrSketchSessionTests
    {
        private const string PayloadA = "SW-QR-MVP";
        private const string PayloadB = "HTTPS://QR-MVP.DEV/T3";

        private static (QrSketchSession session, List<FakeQrWindow> windows) NewSession()
        {
            var windows = new List<FakeQrWindow>();
            var session = new QrSketchSession(() =>
            {
                var w = new FakeQrWindow();
                windows.Add(w);
                return w;
            });
            return (session, windows);
        }

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        private static string ExpectedSignature(string content) =>
            QrPictureRenderer.Render(
                QrSketchGenerator.Generate(content,
                    QrSketchSession.InitialSizeMm, QrSketchSession.InitialGrowthFractionPerSide).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();

        // ---------------- content → picture ----------------

        [Fact]
        public void Fresh_session_has_no_picture_and_prompts_for_content()
        {
            var (session, _) = NewSession();

            Assert.Null(session.Picture);
            Assert.False(session.HasValidRequest);
            Assert.False(string.IsNullOrWhiteSpace(session.StatusText));
        }

        [Fact]
        public void Valid_content_produces_a_picture_of_the_module_result()
        {
            var (session, _) = NewSession();

            var result = session.SetContent(PayloadA);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(session.HasValidRequest);
            Assert.NotNull(session.Picture);
            // The picture IS the grown contour result the sketch would use —
            // identical to rendering that same module output directly.
            Assert.Equal(ExpectedSignature(PayloadA), session.Picture!.Signature());
        }

        [Fact]
        public void Empty_content_shows_no_picture_and_an_actionable_status()
        {
            var (session, _) = NewSession();

            var result = session.SetContent("");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.EmptyContent, result.Error!.Kind);
            Assert.Null(session.Picture);
            Assert.False(session.HasValidRequest);
            Assert.Contains("content", session.StatusText, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Going_invalid_clears_the_previous_picture()
        {
            // A stale picture must never present itself as a valid preview of
            // invalid current input.
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            Assert.NotNull(session.Picture);

            session.SetContent("");

            Assert.Null(session.Picture);
            Assert.False(session.HasValidRequest);
        }

        [Fact]
        public void New_valid_content_replaces_the_picture()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            var first = session.Picture!.Signature();

            session.SetContent(PayloadB);

            Assert.True(session.HasValidRequest);
            Assert.NotNull(session.Picture);
            Assert.Equal(ExpectedSignature(PayloadB), session.Picture!.Signature());
            Assert.NotEqual(first, session.Picture!.Signature());
        }

        [Fact]
        public void Content_is_encoded_exactly_whitespace_and_line_breaks_included()
        {
            var (session, _) = NewSession();
            var padded = "  padded\ncontent  ";

            var result = session.SetContent(padded);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(padded, session.Content);
            // Same byte-exact payload → identical module result → identical picture.
            Assert.Equal(ExpectedSignature(padded), session.Picture!.Signature());
        }

        [Fact]
        public void Content_is_not_trimmed()
        {
            var (session, _) = NewSession();

            session.SetContent("A");
            var tight = session.Picture!.Signature();
            session.SetContent("A ");
            var padded = session.Picture!.Signature();

            Assert.NotEqual(tight, padded);
        }

        [Fact]
        public void Whitespace_only_content_is_encoded_not_treated_as_empty()
        {
            var (session, _) = NewSession();

            var result = session.SetContent("   ");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.NotNull(session.Picture);
        }

        [Fact]
        public void Content_beyond_qr_capacity_reports_the_error_without_a_picture()
        {
            var (session, _) = NewSession();

            var result = session.SetContent(new string('A', 4000));

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.PayloadTooLong, result.Error!.Kind);
            Assert.Null(session.Picture);
            Assert.Contains("Shorten", session.StatusText);
        }

        [Fact]
        public void A_valid_request_shows_a_plain_ready_status_with_no_readouts()
        {
            var (session, _) = NewSession();

            session.SetContent(PayloadA);

            // The status is a one-liner — QR facts (version, cell counts,
            // sizes) live in the info section (InfoLines), not status text.
            Assert.True(session.HasValidRequest);
            Assert.Equal("Ready — click Create Sketch to insert.", session.StatusText);
            Assert.DoesNotContain("version", session.StatusText,
                System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cell", session.StatusText,
                System.StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mm", session.StatusText,
                System.StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- error-correction level ----------------

        [Fact]
        public void The_ecc_level_defaults_to_M()
        {
            var (session, _) = NewSession();

            Assert.Equal(QRCodeGenerator.ECCLevel.M, session.EccLevel);
        }

        [Fact]
        public void SetEccLevel_revalidates_the_whole_request()
        {
            var (session, _) = NewSession();
            session.SetContent("SW-QR-MVP12"); // version 1 (N=21) at M

            var result = session.SetEccLevel(QRCodeGenerator.ECCLevel.H);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(2, session.CurrentResult!.Sketch!.Info.Version);
            Assert.Equal(25, session.CurrentResult.Sketch.Info.CellCount);
            Assert.NotNull(session.Picture);
        }

        [Fact]
        public void An_ecc_change_syncs_the_open_window()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            var before = windows[0].SyncCount;

            session.SetEccLevel(QRCodeGenerator.ECCLevel.H);

            Assert.True(windows[0].SyncCount > before);
        }

        [Fact]
        public void A_level_change_can_make_a_too_long_payload_fit()
        {
            var (session, _) = NewSession();
            var refused = session.SetContent(new string('A', 4000));
            Assert.Equal(QrSketchErrorKind.PayloadTooLong, refused.Error!.Kind);
            Assert.Contains("error correction M", session.StatusText);

            var result = session.SetEccLevel(QRCodeGenerator.ECCLevel.L);

            Assert.True(result.Error?.Kind != QrSketchErrorKind.PayloadTooLong,
                $"the payload should stop being a capacity refusal at L, got {result.Error?.Kind}");
        }

        [Fact]
        public void SetEccLevel_keeps_an_invalid_request_invalid()
        {
            var (session, _) = NewSession();
            session.SetContent("");

            var result = session.SetEccLevel(QRCodeGenerator.ECCLevel.H);

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.EmptyContent, result.Error!.Kind);
            Assert.Null(session.Picture);
            Assert.False(session.HasValidRequest);
        }

        // ---------------- create sketch ----------------

        [Fact]
        public void Create_with_invalid_content_fails_without_touching_the_host()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent("");

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.CaptureCount); // the host was never even asked
        }

        [Fact]
        public void Create_inserts_the_current_contents_geometry()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(PayloadA,
                QrSketchSession.InitialSizeMm, QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            var effective = sketch.ProfileSegments.ToList();
            Assert.Equal(expected.Info.SegmentCount, effective.Count);
            var expectedEdges = expected.Edges.ToList();
            for (var i = 0; i < expectedEdges.Count; i++)
            {
                Assert.Equal(expectedEdges[i].Start.X, effective[i].X1, 12);
                Assert.Equal(expectedEdges[i].End.Y, effective[i].Y2, 12);
            }
        }

        [Fact]
        public void Create_uses_the_content_current_at_create_time()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.CreateSketch(host);
            host.Document!.ActiveSketch = null; // engineer exits the QR sketch

            session.SetContent(PayloadB);
            var second = session.CreateSketch(host);

            Assert.True(second.Succeeded, second.Message);
            Assert.Equal(2, host.Document.Sketches.Count);
            var expectedB = QrSketchGenerator.Generate(PayloadB,
                QrSketchSession.InitialSizeMm, QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            Assert.Equal(expectedB.Info.SegmentCount,
                host.Document.Sketches[1].ProfileSegments.Count());
            // The first sketch keeps its own payload's geometry — unchanged.
            var expectedA = QrSketchGenerator.Generate(PayloadA,
                QrSketchSession.InitialSizeMm, QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            Assert.Equal(expectedA.Info.SegmentCount,
                host.Document.Sketches[0].ProfileSegments.Count());
        }

        [Fact]
        public void Create_validates_the_active_document_at_create_time()
        {
            var (session, _) = NewSession();
            var host = new TestSketchHost(); // no document when content is typed
            session.SetContent(PayloadA);

            var doc = FakeDocument.FreshPart();
            doc.Selection.Add(FakeSelType.DatumPlane);
            host.Document = doc; // document appears by the time Create is pressed

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Single(doc.Sketches);
        }

        [Fact]
        public void Create_targets_the_current_document_not_a_stale_one()
        {
            var (session, _) = NewSession();
            var docA = FakeDocument.FreshPart();
            docA.Selection.Add(FakeSelType.DatumPlane);
            var host = new TestSketchHost { Document = docA };
            session.SetContent(PayloadA);

            // The user switched documents before pressing Create: the request
            // must target the CURRENT document — never a cached support.
            var docB = FakeDocument.FreshPart();
            docB.Selection.Add(FakeSelType.DatumPlane);
            host.Document = docB;

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Empty(docA.Sketches);
            Assert.Single(docB.Sketches);
        }

        [Fact]
        public void Create_fails_when_the_selection_is_gone_at_create_time()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            host.Document!.Selection.Clear(); // support deselected before Create

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document.Sketches);
        }

        [Fact]
        public void Create_is_blocked_while_a_sketch_is_being_edited()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.CreateSketch(host); // leaves the new sketch open

            var second = session.CreateSketch(host);

            Assert.False(second.Succeeded);
            Assert.Contains("sketch", second.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Single(host.Document!.Sketches); // nothing added to the open sketch
        }

        [Fact]
        public void Create_inserts_the_geometry_at_the_session_ecc_level()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent("SW-QR-MVP12"); // version 1 (N=21) at M, version 2 (N=25) at H
            session.SetEccLevel(QRCodeGenerator.ECCLevel.H);

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var expected = QrSketchGenerator.Generate(new QrSketchRequest(
                "SW-QR-MVP12",
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide)
            {
                EccLevel = QRCodeGenerator.ECCLevel.H,
            }).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document!.Sketches.Single().ProfileSegments.Count());
        }

        [Fact]
        public void The_ecc_level_applies_to_future_creates_only()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent("SW-QR-MVP12");
            session.CreateSketch(host);
            host.Document!.ActiveSketch = null; // engineer exits the QR sketch

            session.SetEccLevel(QRCodeGenerator.ECCLevel.H);
            var second = session.CreateSketch(host);

            Assert.True(second.Succeeded, second.Message);
            Assert.Equal(2, host.Document.Sketches.Count);
            var atM = QrSketchGenerator.Generate("SW-QR-MVP12",
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            var atH = QrSketchGenerator.Generate(new QrSketchRequest("SW-QR-MVP12",
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide)
            {
                EccLevel = QRCodeGenerator.ECCLevel.H,
            }).Sketch!;
            // The first sketch keeps the level it was created with —
            // settings reach future creates, never existing sketches.
            Assert.Equal(atM.Info.SegmentCount,
                host.Document.Sketches[0].ProfileSegments.Count());
            Assert.Equal(atH.Info.SegmentCount,
                host.Document.Sketches[1].ProfileSegments.Count());
        }

        [Fact]
        public void Create_leaves_the_new_sketch_open_and_the_picture_available()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            var picture = session.Picture!.Signature();

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Same(host.Document!.Sketches.Single(), host.Document.ActiveSketch);
            // Picture remains as the reference for the created geometry.
            Assert.Equal(picture, session.Picture!.Signature());
        }

        // ---------------- window lifecycle ----------------

        [Fact]
        public void The_command_opens_one_modeless_window()
        {
            var (session, windows) = NewSession();

            session.OpenOrFocusWindow();

            Assert.Single(windows);
            Assert.True(windows[0].IsOpen);
            Assert.Equal(1, windows[0].ShowCount);
            Assert.True(session.HasOpenWindow);
        }

        [Fact]
        public void Invoking_the_command_again_focuses_the_existing_window()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();

            session.OpenOrFocusWindow();

            Assert.Single(windows);                 // no duplicate
            Assert.Equal(1, windows[0].ShowCount);  // never re-shown
            Assert.Equal(1, windows[0].FocusCount); // focused instead
            Assert.Equal(1, session.WindowsCreated);
        }

        [Fact]
        public void A_closed_window_is_recreated_on_the_next_command()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            windows[0].SimulateUserClose();
            Assert.False(session.HasOpenWindow);

            session.OpenOrFocusWindow();

            Assert.Equal(2, session.WindowsCreated);
            Assert.Equal(2, windows.Count);
            Assert.True(windows[1].IsOpen);
        }

        [Fact]
        public void Release_closes_the_window_and_forgets_it()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();

            session.Release();

            Assert.False(windows[0].IsOpen);
            Assert.Equal(1, windows[0].CloseCount);
            Assert.False(session.HasOpenWindow);
        }

        [Fact]
        public void Release_after_the_window_closed_is_safe()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            windows[0].SimulateUserClose();

            session.Release(); // must not throw or double-close

            Assert.Equal(1, windows[0].CloseCount);
        }

        [Fact]
        public void Release_without_a_window_is_safe()
        {
            var (session, _) = NewSession();

            session.Release();

            Assert.False(session.HasOpenWindow);
        }

        [Fact]
        public void A_window_recreated_after_release_gets_the_session_state()
        {
            var (session, windows) = NewSession();
            session.SetContent(PayloadA);
            session.OpenOrFocusWindow();
            session.Release();

            session.OpenOrFocusWindow();

            Assert.Equal(2, session.WindowsCreated);
            Assert.Equal(PayloadA, session.Content);
            Assert.NotNull(session.Picture);
        }

        // ---------------- session → window sync ----------------
        // Session state can change without the window driving it — probe
        // edits arrive with no keystroke. The session must tell the open
        // window to re-pull field texts/picture/status so what is shown can
        // never diverge from the session.

        [Fact]
        public void An_edit_made_outside_the_window_syncs_the_shown_view()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            var before = windows[0].SyncCount;

            session.SetContent(PayloadA);

            Assert.True(windows[0].SyncCount > before);
        }

        [Fact]
        public void Every_field_edit_syncs_the_open_window()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            var before = windows[0].SyncCount;

            session.SetContent(PayloadA);
            session.SetSizeText("40");
            session.SetGrowthText("8");

            Assert.True(windows[0].SyncCount >= before + 3);
        }

        [Fact]
        public void A_create_syncs_its_outcome_status_to_the_open_window()
        {
            var (session, windows) = NewSession();
            var host = HostWithSelectedPlane();
            session.OpenOrFocusWindow();
            session.SetContent(PayloadA);
            var before = windows[0].SyncCount;

            session.CreateSketch(host);

            Assert.True(windows[0].SyncCount > before);
        }

        [Fact]
        public void A_failed_create_syncs_its_outcome_status_to_the_open_window()
        {
            var (session, windows) = NewSession();
            var host = HostWithSelectedPlane();
            session.OpenOrFocusWindow();
            session.SetContent(PayloadA);
            host.Document!.Selection.Clear();
            var before = windows[0].SyncCount;

            session.CreateSketch(host);

            Assert.True(windows[0].SyncCount > before);
        }

        [Fact]
        public void A_closed_window_is_no_longer_synced()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            windows[0].SimulateUserClose();
            var count = windows[0].SyncCount;

            session.SetContent(PayloadA);

            // The dead view is left alone — the next window pulls the state
            // fresh when it is created.
            Assert.Equal(count, windows[0].SyncCount);
        }

        [Fact]
        public void Closing_the_window_before_create_leaves_the_part_unchanged()
        {
            var (session, windows) = NewSession();
            var host = HostWithSelectedPlane();
            session.OpenOrFocusWindow();
            session.SetContent(PayloadA);

            windows[0].SimulateUserClose();

            Assert.False(session.HasOpenWindow);
            Assert.Empty(host.Document!.Sketches); // cancellation touches nothing
        }
    }
}
