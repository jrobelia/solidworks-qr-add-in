using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #10 / ticket #16 — the pane's read-only cell (module) size
    /// readout in document units plus the static resize note. The readout
    /// lives on <see cref="QrSketchSession"/> so it is headlessly testable;
    /// the document's length unit is host state the session reads through
    /// the injected host provider — it can never invent a unit.
    /// </summary>
    public class QrPaneReadoutTests
    {
        private const string Content = "SW-QR-MVP";

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

        [Fact]
        public void A_valid_request_shows_the_module_pitch_in_document_units()
        {
            var host = HostWithSelectedPlane();
            var (session, _) = NewSession(host);

            session.SetContent(Content);

            // "SW-QR-MVP" is a version-1 QR (N=21); pitch = 25/(21 + 2·0.03)
            // — the module size the insertion guard enforces, in mm.
            Assert.Equal("1.187 mm", session.CellSizeReadout);
        }

        [Fact]
        public void The_readout_updates_live_as_size_and_growth_change()
        {
            var host = HostWithSelectedPlane();
            var (session, _) = NewSession(host);
            session.SetContent(Content);

            session.SetSizeText("50");
            Assert.Equal("2.374 mm", session.CellSizeReadout);   // 50/21.06

            session.SetGrowthText("8");
            Assert.Equal("2.363 mm", session.CellSizeReadout);   // 50/(21+0.16)
        }

        [Fact]
        public void The_readout_updates_live_as_content_changes_the_matrix()
        {
            var host = HostWithSelectedPlane();
            var (session, _) = NewSession(host);
            session.SetContent(Content);
            var first = session.CellSizeReadout;

            // 30 alphanumerics → version 2 (N=25) instead of version 1
            // (N=21): a larger matrix but still inside the insertion guard
            // at 25 mm — pitch shrinks, readout stays valid.
            session.SetContent(new string('X', 30));

            Assert.NotEqual(first, session.CellSizeReadout);
            Assert.EndsWith(" mm", session.CellSizeReadout);
        }

        [Fact]
        public void An_inch_document_reports_the_cell_size_in_inches()
        {
            var host = HostWithSelectedPlane();
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            var (session, _) = NewSession(host);

            session.SetContent(Content);

            Assert.Equal("0.04674 in", session.CellSizeReadout);
        }

        [Fact]
        public void Invalid_input_clears_the_readout()
        {
            var host = HostWithSelectedPlane();
            var (session, _) = NewSession(host);
            session.SetContent(Content);
            Assert.False(string.IsNullOrEmpty(session.CellSizeReadout));

            session.SetContent("");

            Assert.Equal(string.Empty, session.CellSizeReadout);
        }

        [Fact]
        public void A_host_that_cannot_report_its_unit_shows_millimetres()
        {
            var host = HostWithSelectedPlane();
            host.ThrowOnCapture = true;
            var (session, _) = NewSession(host);

            session.SetContent(Content);

            // The pitch IS this many millimetres — the label stays honest
            // about the unit the number is expressed in.
            Assert.Equal("1.187 mm", session.CellSizeReadout);
        }

        [Fact]
        public void The_readout_stays_millimetres_without_a_host_provider()
        {
            var (session, _) = NewSession();

            session.SetContent(Content);

            Assert.Equal("1.187 mm", session.CellSizeReadout);
        }

        [Fact]
        public void The_resize_note_names_the_block_scale_as_the_resize_control()
        {
            // The pane shows this verbatim — the session owns the string so
            // the mechanism is discoverable headlessly.
            Assert.Contains("Scale", QrSketchSession.ResizeNote);
            Assert.Contains("block", QrSketchSession.ResizeNote,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_quiet_zone_note_names_the_keep_out_square_and_the_margin()
        {
            Assert.Contains("quiet zone", QrSketchSession.QuietZoneNote,
                System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("keep-out", QrSketchSession.QuietZoneNote,
                System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never", QrSketchSession.QuietZoneNote);
        }

        // ---------------- info section ----------------
        // Label/value rows the pane renders as plain text (never boxes) —
        // the session formats every value, in the document's length unit,
        // so a unit edit or document switch is a re-read, not a re-derive.

        private static string InfoValue(QrSketchSession session, string label)
        {
            foreach (var line in session.InfoLines)
                if (line.Label == label)
                    return line.Value;
            throw new System.Exception($"no info line labelled '{label}'");
        }

        [Fact]
        public void The_info_section_describes_the_current_request()
        {
            var host = HostWithSelectedPlane();
            var (session, _) = NewSession(host);

            session.SetContent(Content);

            // "SW-QR-MVP" → version 1 (N=21, M=29): outline 25 mm, pitch
            // 25/21.06 = 1.187 mm, quiet zone 4 cells = 4.748 mm per side,
            // footprint 29·p = 34.43 mm.
            Assert.Equal("25 mm", InfoValue(session, "QR code size"));
            Assert.Equal("34.43 mm", InfoValue(session, "Overall size"));
            Assert.Equal("21 × 21 cells", InfoValue(session, "QR grid"));
            Assert.Equal("29 × 29 cells", InfoValue(session, "With quiet zone"));
            Assert.Equal("1.187 mm", InfoValue(session, "Cell size"));
            Assert.Equal("4 cells (4.748 mm) per side", InfoValue(session, "Quiet zone"));
            Assert.Equal("Version 1 · ECC M · 9 characters", InfoValue(session, "Encoding"));
        }

        [Fact]
        public void A_unit_change_event_reformats_the_info_without_a_field_edit()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            session.SetContent(Content);
            Assert.Equal("1.187 mm", session.CellSizeReadout);

            // The engineer switches the document to inches behind the pane's
            // back — the watcher event, not a field edit, re-reads the unit.
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            watcher.RaiseChanged();

            Assert.Equal("0.04674 in", session.CellSizeReadout);
            Assert.Equal("0.04674 in", InfoValue(session, "Cell size"));
            // The size is expressed only in the document's current unit;
            // the physical size remains the same after conversion.
            Assert.Equal("0.9843 in", InfoValue(session, "QR code size"));
        }

        [Fact]
        public void A_directly_opened_task_pane_receives_unit_changes_without_the_menu_command()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            var pane = new FakeQrWindow();
            pane.ShowModeless();
            session.AttachView(pane);
            session.SetContent(Content);
            int before = pane.SyncCount;

            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            watcher.RaiseChanged();

            Assert.Equal("0.04674 in", session.CellSizeReadout);
            Assert.True(pane.SyncCount > before, "The directly opened pane was not notified.");
            Assert.Equal(0, session.WindowsCreated);
        }

        [Fact]
        public void Inch_size_input_is_converted_to_millimetres_for_geometry()
        {
            var host = HostWithSelectedPlane();
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            var (session, _) = NewSession(host);

            session.SetContent(Content);
            session.SetSizeText("1");

            Assert.True(session.HasValidRequest, session.StatusText);
            Assert.Equal(25.4, session.RequestedSizeMm!.Value, 10);
            Assert.Equal(25.4, session.CurrentResult!.Sketch!.Info.FinalSizeMm, 10);
        }

        [Fact]
        public void Unit_changes_convert_the_input_without_changing_the_physical_request()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            session.SetContent(Content);
            session.SetSizeText("40");
            var request = session.CurrentResult;
            session.OpenOrFocusWindow();

            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            watcher.RaiseChanged();

            Assert.Equal(40 / 25.4,
                double.Parse(session.SizeText, System.Globalization.CultureInfo.InvariantCulture), 10);
            Assert.Equal(40, session.RequestedSizeMm);
            Assert.Same(request, session.CurrentResult);
            Assert.All(session.InfoLines, line => Assert.DoesNotContain(" mm", line.Value));
        }

        [Fact]
        public void Unit_changes_convert_the_default_even_before_content_is_entered()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";

            watcher.RaiseChanged();

            Assert.Equal(25 / 25.4,
                double.Parse(session.SizeText, System.Globalization.CultureInfo.InvariantCulture), 10);
            Assert.Empty(session.InfoLines);
        }

        [Fact]
        public void Size_label_and_input_follow_the_document_unit_without_valid_content()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            Assert.Equal("QR code size (mm):", session.SizeLabel);
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";

            watcher.RaiseChanged();

            Assert.Equal("QR code size (in):", session.SizeLabel);
            session.SetSizeText("1 in");
            Assert.Equal(25.4, session.RequestedSizeMm!.Value, 10);
        }

        [Fact]
        public void A_pending_size_edit_keeps_its_original_unit_when_committed_after_a_unit_change()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            session.SetContent(Content);
            var editingUnit = session.LengthUnit;
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            watcher.RaiseChanged();

            session.SetSizeText("40", editingUnit);

            Assert.Equal(40, session.RequestedSizeMm!.Value, 8);
            Assert.Equal(40 / 25.4,
                double.Parse(session.SizeText, System.Globalization.CultureInfo.InvariantCulture), 10);
        }

        [Fact]
        public void Invalid_size_text_is_not_replaced_by_a_unit_change()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            session.SetContent(Content);
            session.SetSizeText("not a size");
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";

            watcher.RaiseChanged();

            Assert.Equal("not a size", session.SizeText);
            Assert.Equal("QR code size (in):", session.SizeLabel);
            Assert.False(session.HasValidRequest);
            Assert.Empty(session.InfoLines);
            Assert.Contains("in", session.StatusText);
            Assert.DoesNotContain("millimetres", session.StatusText);
        }

        [Fact]
        public void A_size_suffix_that_becomes_valid_in_the_new_unit_revalidates_immediately()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            session.SetContent(Content);
            session.SetSizeText("1 in");
            Assert.False(session.HasValidRequest);
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";

            watcher.RaiseChanged();

            Assert.True(session.HasValidRequest, session.StatusText);
            Assert.Equal(25.4, session.RequestedSizeMm!.Value, 10);
            Assert.Equal("1 in", session.SizeText);
        }

        [Fact]
        public void A_repeated_unit_event_does_not_refresh_unchanged_info()
        {
            var host = HostWithSelectedPlane();
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            var pane = new FakeQrWindow();
            session.AttachView(pane);
            session.SetContent(Content);
            int before = pane.SyncCount;

            watcher.RaiseChanged();

            Assert.Equal(before, pane.SyncCount);
        }

        [Fact]
        public void An_unreadable_host_preserves_the_last_known_unit()
        {
            var host = HostWithSelectedPlane();
            host.Document!.MetresPerLengthUnit = 0.0254;
            host.Document.LengthUnitSymbol = "in";
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(host, watcher);
            session.SetContent(Content);
            host.ThrowOnCapture = true;

            watcher.RaiseChanged();

            Assert.Equal("QR code size (in):", session.SizeLabel);
            Assert.Equal("0.04674 in", session.CellSizeReadout);
        }

        [Fact]
        public void Release_unsubscribes_the_session_from_host_state_changes()
        {
            var watcher = new TestHostStateWatcher();
            var (session, _) = NewSession(HostWithSelectedPlane(), watcher);
            Assert.Equal(1, watcher.SubscriberCount);

            session.Release();

            Assert.Equal(0, watcher.SubscriberCount);
        }

        [Fact]
        public void Invalid_input_clears_the_info_section()
        {
            var host = HostWithSelectedPlane();
            var (session, _) = NewSession(host);
            session.SetContent(Content);
            Assert.NotEmpty(session.InfoLines);

            session.SetContent("");

            Assert.Empty(session.InfoLines);
        }
    }
}
