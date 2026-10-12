using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #27 — the content-type picker behind the pane, exercised at the
    /// session seam: the dropdown selection
    /// (<see cref="QrSketchSession.SetContentType"/>), per-preset field edits
    /// (<see cref="QrSketchSession.SetPresetField"/>) composing the literal
    /// payload into <see cref="QrSketchSession.Content"/>, compose-error
    /// rejection via the status line, and the verbatim-edit rule — a manual
    /// content edit after composing is treated exactly like a Raw/typed edit.
    /// No WPF involved: the window is a FakeQrWindow, the host TestSketchHost.
    /// </summary>
    public class QrContentPresetTests
    {
        private const string PayloadA = "SW-QR-MVP";

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

        // ---------------- the dropdown ----------------

        [Fact]
        public void A_fresh_session_selects_the_raw_preset()
        {
            var (session, _) = NewSession();

            Assert.Equal(PayloadComposer.DefaultPresetId, session.ContentPresetId);
            Assert.Equal("raw", session.ContentPresetId);
            Assert.Equal("raw", session.CurrentPreset.Id);
        }

        [Fact]
        public void Switching_the_content_type_exposes_that_presets_fields()
        {
            var (session, _) = NewSession();

            session.SetContentType("wifi");

            Assert.Equal("wifi", session.ContentPresetId);
            Assert.Equal("WiFi", session.CurrentPreset.DisplayName);
            Assert.Equal(
                new[] { "ssid", "security", "password", "hidden" },
                session.CurrentPreset.Fields.Select(f => f.Key).ToArray());
        }

        // ---------------- compose → content verbatim ----------------

        [Fact]
        public void Preset_field_edits_compose_the_literal_payload_into_content()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");

            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");

            // The content box holds the literal WIFI: payload — the exact text
            // the encoder consumes, whitespace included.
            Assert.Equal("WIFI:T:WPA2;S:MyNet;P:secret;;", session.Content);
            Assert.True(session.HasValidRequest, session.StatusText);
        }

        [Fact]
        public void Composed_content_renders_the_matching_picture_and_info()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");

            // The preview/info describe the literal composed payload — what
            // you see in the content box is what gets encoded.
            var composed = "WIFI:T:WPA2;S:MyNet;P:secret;;";
            Assert.Equal(ExpectedSignature(composed), session.Picture!.Signature());
            var encoding = session.InfoLines.Single(l => l.Label == "Encoding");
            Assert.Contains(composed.Length + " characters", encoding.Value);
        }

        [Fact]
        public void Choice_and_boolean_preset_fields_compose_their_values()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            session.SetPresetField("ssid", "HomeNet");
            session.SetPresetField("security", "wep");
            session.SetPresetField("password", "abcde");
            session.SetPresetField("hidden", "true");

            // WEP passphrases are emitted quoted — the scanner's convention
            // (QRCoder's WiFi generator owns that detail, not the session).
            Assert.Equal("WIFI:T:WEP;S:HomeNet;P:\"abcde\";H:true;", session.Content);
        }

        // ---------------- invalid fields ----------------

        [Fact]
        public void An_invalid_preset_field_fails_the_request_with_a_status_and_no_picture()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");
            Assert.True(session.HasValidRequest, session.StatusText);

            // Deleting the secured network's password fails composition —
            // the status line names the bad field and no picture shows.
            var result = session.SetPresetField("password", "");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidPayload, result.Error!.Kind);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);
            Assert.Empty(session.InfoLines);
            Assert.Contains("Password", session.StatusText);
        }

        [Fact]
        public void Switching_to_a_preset_with_missing_required_fields_fails_openly()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            Assert.True(session.HasValidRequest, session.StatusText);

            var result = session.SetContentType("phone");

            // The phone preset needs a number — the request is invalid until
            // the fields compose, and the status names the missing field.
            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidPayload, result.Error!.Kind);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);
            Assert.Contains("Phone number", session.StatusText);
        }

        [Fact]
        public void Switching_types_clears_the_payload_until_the_new_fields_compose()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            Assert.Equal(PayloadA, session.Content);

            // Picking a different type drops the previous payload — the box
            // only ever shows text the CURRENT type produced, never another
            // type's leftovers beside the new field inputs.
            var result = session.SetContentType("phone");

            Assert.False(result.Succeeded);
            Assert.Equal(string.Empty, session.Content);
            Assert.False(session.HasValidRequest);

            // The fields composing again re-fills the box verbatim.
            session.SetPresetField("number", "+15550100");
            Assert.Equal("tel:+15550100", session.Content);
        }

        [Fact]
        public void A_compose_error_blocks_create_without_touching_the_host()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContentType("phone"); // missing required number

            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Equal(0, host.CaptureCount);
        }

        [Fact]
        public void A_compose_error_persists_until_the_field_is_fixed()
        {
            var (session, _) = NewSession();
            session.SetContentType("phone");
            Assert.False(session.HasValidRequest);

            // Another field's edit must not resurrect a request whose preset
            // fields still fail — the compose error is the honest state.
            session.SetSizeText("40");
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);

            session.SetPresetField("number", "+15550100");
            Assert.True(session.HasValidRequest, session.StatusText);
            Assert.Equal("tel:+15550100", session.Content);
            Assert.NotNull(session.Picture);
        }

        // ---------------- the verbatim-edit rule ----------------

        [Fact]
        public void A_manual_content_edit_after_composing_reverts_to_raw()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");

            // Hand-editing the composed payload is a Raw/typed edit: the
            // content box is authoritative free text again — the type flips
            // back to Raw and the typed text is the literal payload now.
            session.SetContent("WIFI:T:WPA;S:hand-edited;;");

            Assert.Equal("raw", session.ContentPresetId);
            Assert.Equal("WIFI:T:WPA;S:hand-edited;;", session.Content);
            Assert.True(session.HasValidRequest, session.StatusText);
        }

        [Fact]
        public void Entered_preset_fields_are_remembered_across_type_switches()
        {
            var (session, _) = NewSession();
            session.SetContentType("wifi");
            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");
            session.SetPresetField("hidden", "true");

            // Round-trip through another type: the WiFi fields come back and
            // recompose — the box shows their payload again.
            session.SetContentType("sms");
            Assert.Equal("sms", session.ContentPresetId);
            Assert.False(session.HasValidRequest);   // sms needs a number

            session.SetContentType("wifi");
            Assert.Equal("MyNet", session.PresetFields["ssid"]);
            Assert.Equal("true", session.PresetFields["hidden"]);
            Assert.Equal("WIFI:T:WPA2;S:MyNet;P:secret;H:true;", session.Content);
        }

        [Fact]
        public void Switching_back_to_raw_keeps_the_composed_text_as_literal_content()
        {
            var (session, _) = NewSession();
            session.SetContentType("phone");
            session.SetPresetField("number", "+15550100");
            Assert.Equal("tel:+15550100", session.Content);

            // Raw's only field IS the content box: picking Raw keeps the box's
            // current payload verbatim — now plain free text again.
            var result = session.SetContentType("raw");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("raw", session.ContentPresetId);
            Assert.Equal("tel:+15550100", session.Content);
            Assert.True(session.HasValidRequest, session.StatusText);
        }

        // ---------------- rejections ----------------

        [Fact]
        public void An_unknown_content_type_is_refused_and_keeps_the_current_selection()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);

            var result = session.SetContentType("bogus");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidPayload, result.Error!.Kind);
            Assert.Equal("raw", session.ContentPresetId);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);
            Assert.Contains("bogus", session.StatusText);
        }

        [Fact]
        public void A_field_the_current_preset_does_not_declare_is_refused()
        {
            var (session, _) = NewSession();
            session.SetContentType("phone");

            var result = session.SetPresetField("ssid", "MyNet");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidPayload, result.Error!.Kind);
            Assert.Equal("ssid", session.PresetFieldError!.FieldKey);
            Assert.Contains("ssid", session.StatusText);
        }

        [Fact]
        public void SetPresetField_under_raw_writes_content_like_a_typed_edit()
        {
            var (session, _) = NewSession();

            // Raw's one declared field is "content" — writing it through the
            // preset-field setter is the same verbatim write the box makes.
            var result = session.SetPresetField("content", "  padded\nline  ");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("  padded\nline  ", session.Content);
            Assert.Equal("raw", session.ContentPresetId);
        }

        // ---------------- create + sync ----------------

        [Fact]
        public void Create_inserts_the_composed_payloads_geometry()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContentType("wifi");
            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var expected = QrSketchGenerator.Generate(
                "WIFI:T:WPA2;S:MyNet;P:secret;;",
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document!.Sketches.Single().ProfileSegments.Count());
        }

        [Fact]
        public void Preset_edits_sync_the_open_window()
        {
            var (session, windows) = NewSession();
            session.OpenOrFocusWindow();
            var before = windows[0].SyncCount;

            session.SetContentType("wifi");
            session.SetPresetField("ssid", "MyNet");
            session.SetPresetField("password", "secret");

            Assert.True(windows[0].SyncCount >= before + 3);
        }
    }
}
