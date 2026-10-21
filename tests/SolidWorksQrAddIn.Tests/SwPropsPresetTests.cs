using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #45 (spec #21) — the SW Properties content type at the session
    /// seam: the preset exposes a template field whose
    /// <c>$PRP:"name"</c> references resolve through the injected property
    /// provider into the literal payload (what the QR encodes), the resolved
    /// readout and insert names refresh on edits and host changes, create
    /// re-resolves against the live part, and a missing/empty/unreadable
    /// reference fails with an INVALID verdict naming it — before any host
    /// mutation. <c>$PRP</c> text under every other preset stays literal.
    /// </summary>
    public class SwPropsPresetTests
    {
        private static (QrSketchSession session, FakePartPropertySource props,
            TestHostStateWatcher watcher) NewSession(
            TestSketchHost? host = null, FakePartPropertySource? props = null,
            bool withWatcher = true)
        {
            var partProps = props ?? new FakePartPropertySource();
            var watcher = withWatcher ? new TestHostStateWatcher() : null;
            var session = new QrSketchSession(
                () => new FakeQrWindow(),
                host == null ? (System.Func<ISketchHost>?)null : () => host,
                watcher,
                () => partProps);
            return (session, partProps, watcher!);
        }

        /// <summary>A session without a property provider — the degraded path.</summary>
        private static QrSketchSession SessionWithoutProperties() =>
            new QrSketchSession(() => new FakeQrWindow());

        private static FakePartPropertySource PartSource()
        {
            var source = new FakePartPropertySource();
            source.DocumentProperties["PartNo"] = "12345-B";
            source.DocumentProperties["Vendor"] = string.Empty;   // present but blank
            source.ConfigurationProperties["Revision"] = "C";
            return source;
        }

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        // ---------------- the content type ----------------

        [Fact]
        public void Swprops_is_an_offered_content_type_exposing_a_template_field()
        {
            var (session, _, _) = NewSession();

            session.SetContentType("swprops");

            Assert.Equal("swprops", session.ContentPresetId);
            Assert.Equal("SW Properties", session.CurrentPreset.DisplayName);
            var field = Assert.Single(session.CurrentPreset.Fields);
            Assert.Equal("template", field.Key);
            Assert.Equal(PayloadFieldKind.Template, field.Kind);
        }

        // ---------------- template → resolved content ----------------

        [Fact]
        public void A_template_edit_resolves_references_into_the_payload_content()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("swprops");

            var result = session.SetPresetField(
                "template", "PART-$PRP:\"PartNo\"-$PRP:\"Revision\"");

            // The content box carries the RESOLVED literal — the template
            // itself is the field's stored text, never what gets encoded.
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("PART-12345-B-C", session.Content);
            Assert.Equal("PART-$PRP:\"PartNo\"-$PRP:\"Revision\"",
                session.PresetFields["template"]);
            Assert.True(session.HasValidRequest, session.StatusText);
            Assert.NotNull(session.Picture);
        }

        [Fact]
        public void The_resolved_preview_echoes_the_literal_the_qr_encodes()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "SN $PRP:\"PartNo\"");

            Assert.Equal("SN 12345-B", session.ResolvedTemplatePreview);
        }

        [Fact]
        public void Every_template_edit_re_resolves_the_readout()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("swprops");

            session.SetPresetField("template", "SN $PRP:\"PartNo\"");
            Assert.Equal("SN 12345-B", session.ResolvedTemplatePreview);

            session.SetPresetField("template", "rev $PRP:\"Revision\"");
            Assert.Equal("rev C", session.ResolvedTemplatePreview);
            Assert.Equal("rev C", session.Content);
        }

        [Fact]
        public void The_resolved_preview_is_null_under_every_other_preset()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContent("plain");

            Assert.Null(session.ResolvedTemplatePreview);
        }

        [Fact]
        public void The_insert_names_list_enumerates_the_parts_real_names()
        {
            var (session, _, _) = NewSession(props: PartSource());

            // Configuration names first, then document-only names — the
            // source's merged order, deduplicated.
            Assert.Equal(
                new[] { "Revision", "PartNo", "Vendor" },
                session.SwPropertyNames.ToArray());
        }

        [Fact]
        public void Selecting_swprops_with_an_empty_template_fails_openly()
        {
            var (session, _, _) = NewSession(props: PartSource());

            var result = session.SetContentType("swprops");

            // An empty resolved payload is the generator's EmptyContent
            // verdict — nothing crashes, nothing pretends to be valid.
            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.EmptyContent, result.Error!.Kind);
            Assert.False(session.HasValidRequest);
            Assert.Equal(string.Empty, session.Content);
        }

        // ---------------- failures name the reference, mutate nothing ----------------

        [Fact]
        public void A_missing_reference_fails_the_request_naming_it()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("swprops");

            var result = session.SetPresetField("template", "x $PRP:\"NoSuch\" y");

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidPayload, result.Error!.Kind);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);
            Assert.Empty(session.InfoLines);
            Assert.Contains("$PRP:\"NoSuch\"", session.StatusText);
            // The template field itself is flagged — the box to fix.
            Assert.Equal("template", session.PresetFieldError!.FieldKey);
            Assert.Contains("$PRP:\"NoSuch\"", session.ResolvedTemplatePreview);
            Assert.StartsWith("ERR:", session.ResolvedTemplatePreview);
            // The box only ever holds text the current type produced.
            Assert.Equal(string.Empty, session.Content);
        }

        [Fact]
        public void An_empty_reference_fails_distinctly_from_a_missing_one()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("swprops");

            var result = session.SetPresetField("template", "x $PRP:\"Vendor\"");

            Assert.False(result.Succeeded);
            Assert.Contains("$PRP:\"Vendor\"", session.StatusText);
            Assert.Contains("empty", session.StatusText);
        }

        [Fact]
        public void A_template_edit_never_reverts_the_preset_to_raw()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("swprops");

            session.SetPresetField("template", "x $PRP:\"PartNo\"");

            // The template is a preset FIELD — editing it composes, it is
            // not the verbatim content-box edit that reverts to Raw.
            Assert.Equal("swprops", session.ContentPresetId);
        }

        [Fact]
        public void The_template_is_remembered_across_content_type_switches()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "SN $PRP:\"PartNo\"");

            session.SetContentType("url");
            Assert.Equal("url", session.ContentPresetId);

            session.SetContentType("swprops");
            Assert.Equal("SN $PRP:\"PartNo\"", session.PresetFields["template"]);
            Assert.Equal("SN 12345-B", session.Content);
        }

        // ---------------- $PRP stays literal under other presets ----------------

        [Fact]
        public void Dollar_PRP_text_in_the_raw_box_encodes_literally()
        {
            var (session, _, _) = NewSession(props: PartSource());

            session.SetContent("$PRP:\"PartNo\" is text here");

            Assert.Equal("$PRP:\"PartNo\" is text here", session.Content);
            Assert.True(session.HasValidRequest, session.StatusText);
        }

        [Fact]
        public void Dollar_PRP_text_in_another_presets_field_composes_literally()
        {
            var (session, _, _) = NewSession(props: PartSource());
            session.SetContentType("wifi");

            session.SetPresetField("ssid", "$PRP:\"PartNo\"");
            session.SetPresetField("password", "secret");

            // Only the Template-kind field resolves — every other field's
            // text is verbatim input to its composer: the token text lands
            // (escaped per the WIFI payload convention), never the value.
            Assert.Contains("$PRP", session.Content);
            Assert.DoesNotContain("12345-B", session.Content);
        }

        // ---------------- host-change re-resolution ----------------

        [Fact]
        public void A_host_state_change_re_resolves_against_changed_properties()
        {
            var host = HostWithSelectedPlane();
            var (session, props, watcher) = NewSession(host, PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "rev $PRP:\"Revision\"");
            Assert.Equal("rev C", session.Content);

            // A configuration/property edit behind the pane's back — the
            // watcher event, not a field edit, refreshes the resolution.
            props.ConfigurationProperties["Revision"] = "D";
            watcher.RaiseChanged();

            Assert.Equal("rev D", session.Content);
            Assert.Equal("rev D", session.ResolvedTemplatePreview);
            Assert.True(session.HasValidRequest, session.StatusText);
        }

        [Fact]
        public void A_host_state_change_refreshes_the_insert_names()
        {
            var host = HostWithSelectedPlane();
            var (session, props, watcher) = NewSession(host, PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "text");

            props.DocumentProperties["DrawnBy"] = "JRB";
            watcher.RaiseChanged();

            Assert.Contains("DrawnBy", session.SwPropertyNames);
        }

        [Fact]
        public void A_host_state_change_can_fail_a_now_unresolvable_template()
        {
            var host = HostWithSelectedPlane();
            var (session, props, watcher) = NewSession(host, PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "rev $PRP:\"Revision\"");
            Assert.True(session.HasValidRequest, session.StatusText);

            // The referenced property vanished behind the pane's back —
            // the request goes INVALID naming it, never encoding stale text.
            props.ConfigurationProperties.Remove("Revision");
            watcher.RaiseChanged();

            Assert.False(session.HasValidRequest);
            Assert.Contains("$PRP:\"Revision\"", session.StatusText);
        }

        [Fact]
        public void A_host_state_change_under_raw_leaves_dollar_PRP_text_alone()
        {
            var host = HostWithSelectedPlane();
            var (session, props, watcher) = NewSession(host, PartSource());
            session.SetContent("$PRP:\"PartNo\" literal");
            var before = session.Content;

            props.ConfigurationProperties["Revision"] = "D";
            watcher.RaiseChanged();

            Assert.Equal(before, session.Content);
            Assert.Equal("raw", session.ContentPresetId);
        }

        // ---------------- create-time re-resolution ----------------

        [Fact]
        public void Create_re_resolves_against_the_live_store_before_generating()
        {
            var host = HostWithSelectedPlane();
            var (session, props, _) = NewSession(host, PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "PART-$PRP:\"PartNo\"-$PRP:\"Revision\"");
            Assert.Equal("PART-12345-B-C", session.Content);

            // A property edit after the last field edit — create must encode
            // the CURRENT value, not the preview's resolution.
            props.ConfigurationProperties["Revision"] = "D";
            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal("PART-12345-B-D", session.Content);
            var expected = QrSketchGenerator.Generate(
                "PART-12345-B-D",
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document!.Sketches.Single().ProfileSegments.Count());
        }

        [Fact]
        public void Create_with_an_unresolvable_reference_fails_without_touching_the_host()
        {
            var host = HostWithSelectedPlane();
            var (session, props, _) = NewSession(host, PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "PART-$PRP:\"PartNo\"");
            Assert.True(session.HasValidRequest, session.StatusText);

            // The referenced property is deleted behind the pane's back —
            // create re-resolves, fails naming it, and never touches the host.
            props.DocumentProperties.Remove("PartNo");
            var captures = host.CaptureCount;
            var result = session.CreateSketch(host);

            Assert.False(result.Succeeded);
            Assert.Contains("$PRP:\"PartNo\"", result.Message);
            Assert.Equal(captures, host.CaptureCount);   // the host was never even re-read
            Assert.Equal(0, host.BeginCount);
            Assert.Empty(host.Document!.Sketches);
        }

        [Fact]
        public void Create_encodes_the_resolved_literal_from_the_ticket_example()
        {
            var host = HostWithSelectedPlane();
            var (session, _, _) = NewSession(host, PartSource());
            session.SetContentType("swprops");
            session.SetPresetField("template", "PART-$PRP:\"PartNo\"-$PRP:\"Revision\"");

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var expected = QrSketchGenerator.Generate(
                "PART-12345-B-C",
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document!.Sketches.Single().ProfileSegments.Count());
        }

        // ---------------- degraded paths ----------------

        [Fact]
        public void A_template_with_no_references_composes_verbatim_without_a_source()
        {
            var session = SessionWithoutProperties();
            session.SetContentType("swprops");

            var result = session.SetPresetField("template", "plain literal");

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal("plain literal", session.Content);
            Assert.True(session.HasValidRequest, session.StatusText);
            Assert.Empty(session.SwPropertyNames);
        }

        [Fact]
        public void A_reference_without_a_property_source_fails_naming_it()
        {
            var session = SessionWithoutProperties();
            session.SetContentType("swprops");

            var result = session.SetPresetField("template", "x $PRP:\"PartNo\"");

            Assert.False(result.Succeeded);
            Assert.Contains("$PRP:\"PartNo\"", session.StatusText);
            Assert.False(session.HasValidRequest);
        }

        [Fact]
        public void A_reference_with_no_part_document_fails_naming_it()
        {
            var source = PartSource();
            source.Document = HostDocumentKind.None;
            var (session, _, _) = NewSession(props: source);
            session.SetContentType("swprops");

            var result = session.SetPresetField("template", "x $PRP:\"PartNo\"");

            Assert.False(result.Succeeded);
            Assert.Contains("$PRP:\"PartNo\"", session.StatusText);
            Assert.Empty(session.SwPropertyNames);
        }
    }
}
