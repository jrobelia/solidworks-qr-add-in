using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #67, ticket #66 — the pending-edit discipline inside
    /// <see cref="QrSketchSession"/>. A debounced keystroke path stages field
    /// text verbatim (<see cref="QrSketchSession.StageContent"/> and friends)
    /// without revalidating; <see cref="QrSketchSession.CommitPendingEdits"/>
    /// drains the queue through the same setters the immediate path uses, in
    /// the order the verbatim-edit rule needs — preset fields before the
    /// content box. <see cref="QrSketchSession.CreateSketch"/> commits inside
    /// itself, so a create can never observe unflushed edits no matter who
    /// holds the trigger. Exercised at the session seam — the debounce timer
    /// that decides WHEN to commit is the pane's and never enters Core.
    /// </summary>
    public class QrPendingEditTests
    {
        private const string PayloadA = "SW-QR-MVP";
        private const string PayloadB = "HTTPS://QR-MVP.DEV/T3";

        private static (QrSketchSession session, List<FakeQrWindow> windows) NewSession(
            TestSketchHost? host = null, TestHostStateWatcher? watcher = null)
        {
            var windows = new List<FakeQrWindow>();
            var session = new QrSketchSession(
                () =>
                {
                    var w = new FakeQrWindow();
                    windows.Add(w);
                    return w;
                },
                host == null ? (System.Func<Core.Hosting.ISketchHost>?)null : () => host,
                watcher);
            return (session, windows);
        }

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        // ---------------- staging is not an edit ----------------

        [Fact]
        public void Staged_edits_are_pending_until_the_commit_lands()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            var picture = session.Picture!.Signature();

            session.StageContent("");
            session.StageSizeText("not a size");

            // Nothing revalidates on a stage — the shown text and the request
            // in force keep their last committed values.
            Assert.True(session.HasPendingEdits);
            Assert.Equal("", session.PendingContentText);
            Assert.Equal("not a size", session.PendingSizeText);
            Assert.Equal(PayloadA, session.Content);
            Assert.True(session.HasValidRequest, session.StatusText);
            Assert.Equal(picture, session.Picture!.Signature());
        }

        [Fact]
        public void Staging_never_syncs_the_open_window()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            session.SetContent(PayloadA);
            var before = windows[0].SyncCount;

            session.StageContent(PayloadB);
            session.StageGrowthText("8");

            // A keystroke burst is exactly what the debounce coalesces — no
            // regeneration, no session→view push per keystroke.
            Assert.Equal(before, windows[0].SyncCount);
        }

        [Fact]
        public void Commit_applies_staged_text_verbatim_rejected_text_included()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            session.StageSizeText("not a size");
            session.StageGrowthText("8");

            session.CommitPendingEdits();

            // The rejected text is kept verbatim — never clamped or
            // substituted — and the error echoes it.
            Assert.Equal("not a size", session.SizeText);
            Assert.Equal("8", session.GrowthText);
            Assert.False(session.HasValidRequest);
            Assert.Contains("not a size", session.StatusText);
            Assert.False(session.HasPendingEdits);
            Assert.Null(session.PendingSizeText);
            Assert.Null(session.PendingGrowthText);
        }

        [Fact]
        public void Committing_nothing_is_a_no_op()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            session.SetContent(PayloadA);
            var before = windows[0].SyncCount;

            session.CommitPendingEdits();

            Assert.False(session.HasPendingEdits);
            Assert.Equal(before, windows[0].SyncCount);
            Assert.True(session.HasValidRequest, session.StatusText);
        }

        [Fact]
        public void Pending_preset_fields_are_named_until_committed()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");

            session.StagePresetField("ssid", "MyNet");

            Assert.True(session.IsPresetFieldPending("ssid"));
            Assert.False(session.IsPresetFieldPending("password"));
            Assert.Equal(string.Empty, session.Content); // not composed yet

            session.CommitPendingEdits();

            Assert.False(session.IsPresetFieldPending("ssid"));
            // The staged value landed in the preset's remembered fields and
            // composed — still failing openly for the missing password.
            Assert.Equal("MyNet", session.PresetFields["ssid"]);
            Assert.False(session.HasValidRequest);
        }

        [Fact]
        public void A_staged_content_field_under_raw_stages_a_content_edit()
        {
            var (session, _) = NewSession();

            // Raw's one declared field IS the content box — the staged path
            // converges on the same write the immediate setter makes.
            session.StagePresetField("content", "  padded\ntext  ");

            Assert.Equal("  padded\ntext  ", session.PendingContentText);

            session.CommitPendingEdits();

            Assert.Equal("  padded\ntext  ", session.Content);
            Assert.Equal("raw", session.ContentPresetId);
        }

        // ---------------- the ordering discipline ----------------

        [Fact]
        public void Preset_field_edits_land_before_a_pending_content_edit()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");
            session.StagePresetField("ssid", "OtherNet");
            session.StageContent("typed by hand");

            session.CommitPendingEdits();

            // The content edit lands LAST: a manual box edit under a preset
            // is the Raw-reverting verbatim edit — the typed text wins over
            // the recomposition the staged field produced.
            Assert.Equal("raw", session.ContentPresetId);
            Assert.Equal("typed by hand", session.Content);
            Assert.True(session.HasValidRequest, session.StatusText);

            // …but the staged field still landed in the preset's memory —
            // re-picking the type recomposes from it.
            session.SetContentType("wifi");
            Assert.Equal("WIFI:T:WPA2;S:OtherNet;P:secret;;", session.Content);
        }

        [Fact]
        public void A_preset_switch_commits_pending_edits_under_the_old_preset()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            session.StagePresetField("ssid", "MyNet");
            session.StagePresetField("password", "secret");

            // Pending edits belong to the preset they were typed under —
            // the switch commits them under WiFi before SMS selects.
            session.SetContentType("sms");

            Assert.Equal("sms", session.ContentPresetId);
            Assert.False(session.HasPendingEdits);

            session.SetContentType("wifi");
            Assert.Equal("WIFI:T:WPA2;S:MyNet;P:secret;;", session.Content);
        }

        [Fact]
        public void A_discrete_edit_commits_pending_edits_first()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            session.StageGrowthText("8");

            // A committed pick applies over the freshest text values — a
            // pending keystroke edit can never overwrite a later edit.
            session.SetInvert(true);

            Assert.Equal("8", session.GrowthText);
            Assert.True(session.Invert);
            Assert.False(session.HasPendingEdits);
        }

        [Fact]
        public void An_explicit_edit_lands_after_earlier_staged_edits()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            session.StageSizeText("40");

            // The probe path: an immediate setter commits the queue first,
            // so its own edit is always the newest word on the field.
            session.SetSizeText("50");

            Assert.Equal("50", session.SizeText);
            Assert.Equal(50, session.RequestedSizeMm);
            Assert.False(session.HasPendingEdits);
        }

        // ---------------- create flushes inside ----------------

        [Fact]
        public void Create_flushes_pending_edits_before_creating()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.StageContent(PayloadB);

            var result = session.CreateSketch(host);

            // The created sketch encodes the text that was still only
            // PENDING when Create fired — the flush is inside the create.
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(PayloadB, session.Content);
            Assert.False(session.HasPendingEdits);
            var expected = QrSketchGenerator.Generate(PayloadB,
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document!.Sketches[0].ProfileSegments.Count());
        }

        [Fact]
        public void Create_with_a_pending_invalid_edit_fails_without_touching_the_host()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.StageContent("");   // pending: the request is really invalid

            var result = session.CreateSketch(host);

            // Never the stale still-valid state — the pending edit is the
            // request, and it refuses before the host is asked anything.
            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.CaptureCount);
        }

        // ---------------- a pending edit keeps its context ----------------

        [Fact]
        public void A_pending_size_edit_keeps_the_unit_it_was_typed_in()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            session.SetContent(PayloadA);
            var editingUnit = session.LengthUnit;   // mm

            session.StageSizeText("40", editingUnit);
            // The document switches to inches with the edit still pending —
            // the unit change commits it through the unit it was TYPED in.
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            watcher.RaiseChanged();

            Assert.Equal(40, session.RequestedSizeMm!.Value, 8);
            Assert.Equal(40 / 25.4,
                double.Parse(session.SizeText,
                    System.Globalization.CultureInfo.InvariantCulture), 10);
            Assert.False(session.HasPendingEdits);
        }
    }
}
