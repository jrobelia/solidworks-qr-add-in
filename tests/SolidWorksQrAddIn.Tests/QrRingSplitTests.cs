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
    /// Tickets #52/#54 — the opt-in ring split. #52 built the request→result
    /// seam: with the flag on under the Square cell shape, the emitted set
    /// loses every island-bearing void — un-emitted cells flooded from the
    /// working-matrix border mark the exterior-connected void, and a minimal
    /// module-aligned cell path dropped through each enclosing band joins the
    /// hole's void to that flood — iterated to any nesting depth, under either
    /// polarity. The option is refused below ECC M, Circle output ignores it,
    /// and with it off the output is byte-identical to the historical
    /// pipeline. #54 carries the option across the session/pane seam: a fixed
    /// toggle beside invert, off by default and orthogonal to content presets;
    /// the below-M refusal surfaces as an INVALID verdict naming the ECC
    /// field; under Circle the control is inert and hidden; the preview shows
    /// the opened geometry through the existing parity contract; and the info
    /// line reports the opened-site and dropped-cell counts.
    /// </summary>
    public class QrRingSplitTests
    {
        private const string PayloadA = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        private static bool[,] FinderCells()
        {
            // The 7×7 finder pattern: dark ring, light ring, dark centre —
            // one island-bearing void (the light ring) in one band.
            var cells = new bool[7, 7];
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 7; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 3), System.Math.Abs(c - 3));
                    cells[r, c] = ring == 3 || ring <= 1;
                }
            return cells;
        }

        private static bool[,] NestedBandsCells()
        {
            // Stacked bands: dark border ring / light void / dark ring-island /
            // light void / dark block-island — two island-bearing voids at
            // different depths, so a channel legitimately spans several cells.
            var cells = new bool[11, 11];
            for (var r = 0; r < 11; r++)
                for (var c = 0; c < 11; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 5), System.Math.Abs(c - 5));
                    cells[r, c] = ring == 5 || ring == 3 || ring <= 1;
                }
            return cells;
        }

        /// <summary>Every hole that still contains a filled island, at any depth.</summary>
        private static int IslandBearingHoles(IEnumerable<QrRegion> regions)
        {
            var count = 0;
            foreach (var region in regions)
                foreach (var hole in region.Holes)
                {
                    if (hole.Islands.Count > 0)
                        count++;
                    count += IslandBearingHoles(hole.Islands);
                }
            return count;
        }

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        // ---------------- the option itself ----------------

        [Fact]
        public void The_flag_is_off_by_default_and_output_is_byte_identical()
        {
            var request = new QrSketchRequest(PayloadA, SizeMm, Growth);

            Assert.False(request.RingSplit);

            var viaRequest = QrSketchGenerator.Generate(request).Sketch!;
            var plain = QrSketchGenerator.Generate(PayloadA, SizeMm, Growth).Sketch!;

            Assert.Equal(0, viaRequest.Info.OpenedSiteCount);
            Assert.Equal(0, viaRequest.Info.DroppedCellCount);
            Assert.Equal(plain.Info.SegmentCount, viaRequest.Info.SegmentCount);
            var expected = plain.Edges.ToList();
            var actual = viaRequest.Edges.ToList();
            Assert.Equal(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Start.X, actual[i].Start.X);
                Assert.Equal(expected[i].Start.Y, actual[i].Start.Y);
                Assert.Equal(expected[i].End.X, actual[i].End.X);
                Assert.Equal(expected[i].End.Y, actual[i].End.Y);
            }
        }

        // ---------------- sites opened as cell drops ----------------

        [Fact]
        public void A_finder_ring_void_opens_with_one_dropped_cell()
        {
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(1, sketch.Info.OpenedSiteCount);
            Assert.Equal(1, sketch.Info.DroppedCellCount);

            // The one-cell band becomes a C — the hole joins the exterior —
            // and the former centre island surfaces as a top-level contour.
            Assert.Equal(2, sketch.Regions.Count);
            Assert.All(sketch.Regions, r => Assert.Empty(r.Holes));
            Assert.Equal(2, sketch.Contours.Count);
            Assert.Equal(2, sketch.Info.ContourCount);
        }

        [Theory]
        [InlineData(25.0, 0.03)]
        [InlineData(40.0, 0.10)]
        [InlineData(80.0, 0.45)]
        public void The_opening_works_at_every_size_and_growth(double sizeMm, double growth)
        {
            var split = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), sizeMm, growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            Assert.Equal(0, IslandBearingHoles(split.Sketch!.Regions));
        }

        [Fact]
        public void Stacked_bands_open_at_every_depth()
        {
            var plain = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), SizeMm, Growth).Sketch!;
            // Two island-bearing voids: the outer light ring bears the dark
            // ring-island; that island's own hole bears the centre block.
            Assert.Equal(2, IslandBearingHoles(plain.Regions));

            var split = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), SizeMm, Growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(2, sketch.Info.OpenedSiteCount);
            // Each site needs one cell per band it crosses: the outer void one
            // cell; the inner void crosses its island's band into the now-open
            // outer void — one cell more.
            Assert.Equal(2, sketch.Info.DroppedCellCount);
            // Former islands promote: band C + ring-island C + centre block.
            Assert.Equal(3, sketch.Regions.Count);
            Assert.All(sketch.Regions, r => Assert.Empty(r.Holes));
        }

        [Fact]
        public void A_real_payload_opens_every_island_bearing_site()
        {
            var plain = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth)).Sketch!;
            int expectedSites = IslandBearingHoles(plain.Regions);
            // The v1 payload's island-bearing voids are its three finder
            // rings — opened here by ordinary cell drops like any other
            // site (the thin finder-channel upgrade is ticket #53).
            Assert.True(expectedSites >= 3,
                $"expected the three finder rings to be island-bearing, found {expectedSites}");

            var split = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { RingSplit = true });

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(expectedSites, sketch.Info.OpenedSiteCount);
            Assert.True(sketch.Info.DroppedCellCount >= expectedSites,
                $"every site drops at least one cell: {sketch.Info.DroppedCellCount} " +
                $"dropped for {expectedSites} sites");
            // Promoted islands surface as top-level regions.
            Assert.True(sketch.Regions.Count > plain.Regions.Count,
                "former islands surface as top-level contours");

            // Payload, footprint and construction squares are untouched.
            Assert.Equal(plain.Info.Version, sketch.Info.Version);
            Assert.Equal(plain.Info.FinalSizeMm, sketch.Info.FinalSizeMm, 9);
            Assert.Equal(plain.ConstructionSquares.Count, sketch.ConstructionSquares.Count);
            for (var i = 0; i < plain.ConstructionSquares.Count; i++)
            {
                var a = plain.ConstructionSquares[i];
                var b = sketch.ConstructionSquares[i];
                Assert.Equal(a.Role, b.Role);
                Assert.Equal(a.Vertices.Count, b.Vertices.Count);
                for (var v = 0; v < a.Vertices.Count; v++)
                {
                    Assert.Equal(a.Vertices[v].X, b.Vertices[v].X);
                    Assert.Equal(a.Vertices[v].Y, b.Vertices[v].Y);
                }
            }
        }

        [Fact]
        public void A_dense_payload_with_data_area_sites_opens_everything()
        {
            var content = new string('K', 200);
            var plain = QrSketchGenerator.Generate(
                new QrSketchRequest(content, 40.0, 0.10)).Sketch!;
            int expectedSites = IslandBearingHoles(plain.Regions);
            Assert.True(expectedSites > 4,
                $"a dense payload should nest beyond the finders, found {expectedSites}");

            var split = QrSketchGenerator.Generate(
                new QrSketchRequest(content, 40.0, 0.10) { RingSplit = true });

            Assert.True(split.Succeeded, split.Error?.Message);
            Assert.Equal(0, IslandBearingHoles(split.Sketch!.Regions));
            Assert.Equal(expectedSites, split.Sketch!.Info.OpenedSiteCount);
        }

        [Fact]
        public void A_matrix_without_nesting_opens_nothing()
        {
            var cells = new bool[3, 3];
            cells[1, 1] = true;

            var split = QrSketchGenerator.GenerateFromCells(
                cells, SizeMm, Growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            Assert.Equal(0, split.Sketch!.Info.OpenedSiteCount);
            Assert.Equal(0, split.Sketch!.Info.DroppedCellCount);
        }

        // ---------------- polarity ----------------

        [Fact]
        public void Inverted_output_opens_the_island_bearing_pockets()
        {
            // Under invert the LIGHT cells emit: the fixture's light rings
            // become emitted bands and its dark rings the voids — two of
            // which bear emitted islands. The exterior flood finds no border
            // cell, so channels vent through the field to the matrix edge.
            var split = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), SizeMm, Growth, invert: true, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.True(sketch.Info.Inverted);
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(2, sketch.Info.OpenedSiteCount);
            Assert.True(sketch.Info.DroppedCellCount >= 2);
        }

        [Fact]
        public void Inverted_real_payload_opens_every_island_bearing_pocket()
        {
            var plain = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true }).Sketch!;
            int expectedSites = IslandBearingHoles(plain.Regions);
            Assert.True(expectedSites >= 3,
                $"inverted v1 should keep island-bearing pockets, found {expectedSites}");

            var split = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true, RingSplit = true });

            Assert.True(split.Succeeded, split.Error?.Message);
            Assert.Equal(0, IslandBearingHoles(split.Sketch!.Regions));
            Assert.Equal(expectedSites, split.Sketch!.Info.OpenedSiteCount);
        }

        // ---------------- the ECC gate ----------------

        [Fact]
        public void The_option_is_refused_below_ecc_M_naming_the_field()
        {
            var result = QrSketchGenerator.Generate(new QrSketchRequest(
                PayloadA, SizeMm, Growth)
            {
                RingSplit = true,
                EccLevel = QRCodeGenerator.ECCLevel.L,
            });

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidEccLevel, result.Error!.Kind);
            Assert.Contains("EccLevel", result.Error.Message);
            Assert.Contains("M", result.Error.Message);
        }

        [Theory]
        [InlineData(QRCodeGenerator.ECCLevel.M)]
        [InlineData(QRCodeGenerator.ECCLevel.Q)]
        [InlineData(QRCodeGenerator.ECCLevel.H)]
        public void The_option_is_accepted_at_ecc_M_and_above(QRCodeGenerator.ECCLevel level)
        {
            var result = QrSketchGenerator.Generate(new QrSketchRequest(
                PayloadA, SizeMm, Growth)
            {
                RingSplit = true,
                EccLevel = level,
            });

            Assert.True(result.Succeeded, result.Error?.Message);
        }

        [Fact]
        public void A_refused_request_never_mutates_the_host()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host, new QrSketchRequest(
                PayloadA, SizeMm, Growth)
            {
                RingSplit = true,
                EccLevel = QRCodeGenerator.ECCLevel.L,
            });

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Empty(host.Document.BlockDefinitions);
        }

        // ---------------- all-or-nothing ----------------

        [Fact]
        public void A_site_that_cannot_be_opened_is_a_named_all_or_nothing_refusal()
        {
            // Every real matrix is openable — the refusal is exercised through
            // the internal pass bound: with no pass allowed the pass reports
            // the InvalidGeometry-style failure instead of delivering a
            // partially opened mask.
            var emit = (bool[,])NestedBandsCells().Clone();

            var failure = QrRingSplitter.OpenIslandBearingVoids(
                emit, maxPasses: 0, out int openedSites, out int droppedCells);

            Assert.NotNull(failure);
            Assert.False(failure!.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGeometry, failure.Error!.Kind);
            Assert.Contains("island-bearing", failure.Error.Message);
            Assert.Equal(0, openedSites);
            Assert.Equal(0, droppedCells);
        }

        // ---------------- circle: the flag is inert ----------------

        [Fact]
        public void Circle_shape_ignores_the_flag()
        {
            var plain = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth,
                cellShape: QrCellShape.Circle, cellShapeParameter: 0.7).Sketch!;
            var flagged = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth,
                cellShape: QrCellShape.Circle, cellShapeParameter: 0.7,
                ringSplit: true);

            Assert.True(flagged.Succeeded, flagged.Error?.Message);
            var sketch = flagged.Sketch!;
            Assert.Equal(QrCellShape.Circle, sketch.Info.CellShape);
            Assert.Equal(0, sketch.Info.OpenedSiteCount);
            Assert.Equal(0, sketch.Info.DroppedCellCount);
            Assert.Equal(plain.CirclePlan!.EmittedCount, sketch.CirclePlan!.EmittedCount);
            for (var r = 0; r < sketch.CirclePlan.CellsPerSide; r++)
                for (var c = 0; c < sketch.CirclePlan.CellsPerSide; c++)
                    Assert.Equal(plain.CirclePlan.Emits(r, c), sketch.CirclePlan.Emits(r, c));
        }

        [Fact]
        public void Circle_shape_ignores_the_ecc_gate_too()
        {
            // The flag is inert under Circle — dots spend no correction
            // budget, so the ECC refusal does not apply either.
            var result = QrSketchGenerator.Generate(new QrSketchRequest(
                PayloadA, SizeMm, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.7,
                RingSplit = true,
                EccLevel = QRCodeGenerator.ECCLevel.L,
            });

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(QrCellShape.Circle, result.Sketch!.Info.CellShape);
        }

        // ---------------- session & pane seam (ticket #54) ----------------
        // The option is a fixed pane toggle beside invert — not a per-preset
        // field: its state rides the session into QrSketchRequest.RingSplit
        // under every content type, the below-M refusal surfaces as an
        // INVALID verdict naming the ECC field, and the info line reports
        // what the treatment cost.

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

        private static string InfoValue(QrSketchSession session, string label)
        {
            foreach (var line in session.InfoLines)
                if (line.Label == label)
                    return line.Value;
            throw new System.Exception($"no info line labelled '{label}'");
        }

        /// <summary>The session's initial-fields request with the option set.</summary>
        private static QrSketchRequest ExpectedRequest(bool ringSplit) =>
            new QrSketchRequest(PayloadA,
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide)
            { RingSplit = ringSplit };

        [Fact]
        public void The_pane_option_defaults_off()
        {
            var (session, _) = NewSession();

            Assert.False(session.RingSplit);
            Assert.True(session.RingSplitApplies);   // square is the default shape
        }

        [Theory]
        [InlineData(QrCellShape.Square, true)]
        [InlineData(QrCellShape.Circle, false)]
        public void Ring_split_applies_to_the_contour_producing_shape_only(
            QrCellShape shape, bool applies)
        {
            Assert.Equal(applies, QrCellShapes.RingSplitApplies(shape));
        }

        [Fact]
        public void SetRingSplit_regenerates_the_request_picture_and_view()
        {
            var (session, windows) = NewSession();
            session.SetContent(PayloadA);
            session.OpenOrFocusWindow();
            var unsplitSignature = session.Picture!.Signature();
            var syncs = windows[0].SyncCount;

            var result = session.SetRingSplit(true);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(session.RingSplit);
            var info = session.CurrentResult!.Sketch!.Info;
            Assert.True(info.OpenedSiteCount >= 3,
                $"the v1 payload's three finder rings open: {info.OpenedSiteCount}");
            Assert.True(info.DroppedCellCount >= info.OpenedSiteCount);
            // Preview parity: the picture IS a direct render of the split
            // module result — channels and dropped cells, no second renderer.
            var expected = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(ExpectedRequest(ringSplit: true)).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();
            Assert.Equal(expected, session.Picture!.Signature());
            Assert.NotEqual(unsplitSignature, session.Picture!.Signature());
            // The open view re-pulls: session→view sync like any field edit.
            Assert.True(windows[0].SyncCount > syncs);
        }

        [Fact]
        public void Toggling_back_off_restores_the_unsplit_output()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            var unsplit = session.Picture!.Signature();

            session.SetRingSplit(true);
            session.SetRingSplit(false);

            Assert.False(session.RingSplit);
            Assert.Equal(unsplit, session.Picture!.Signature());
            Assert.Equal(0, session.CurrentResult!.Sketch!.Info.OpenedSiteCount);
        }

        [Fact]
        public void RingSplit_without_valid_content_stays_invalid_but_the_setting_sticks()
        {
            var (session, _) = NewSession();

            var result = session.SetRingSplit(true);

            Assert.False(result.Succeeded);
            Assert.Null(session.Picture);
            Assert.True(session.RingSplit);

            // Content typed afterwards previews the opened geometry.
            session.SetContent(PayloadA);
            Assert.True(session.HasValidRequest);
            var expected = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(ExpectedRequest(ringSplit: true)).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();
            Assert.Equal(expected, session.Picture!.Signature());
        }

        [Fact]
        public void An_untouched_option_produces_the_historical_output()
        {
            // Default off: the pending request carries RingSplit=false and the
            // preview is exactly today's render — zero risk on the default path.
            var (session, _) = NewSession();
            session.SetContent(PayloadA);

            var expected = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(ExpectedRequest(ringSplit: false)).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();
            Assert.Equal(expected, session.Picture!.Signature());
            Assert.Equal(0, session.CurrentResult!.Sketch!.Info.OpenedSiteCount);
            Assert.Equal(0, session.CurrentResult!.Sketch!.Info.DroppedCellCount);
        }

        [Fact]
        public void The_below_M_refusal_names_the_ecc_field_and_never_touches_the_host()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.SetRingSplit(true);

            var refused = session.SetEccLevel(QRCodeGenerator.ECCLevel.L);

            Assert.False(refused.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidEccLevel, refused.Error!.Kind);
            Assert.Contains("EccLevel", refused.Error.Message);
            Assert.False(session.HasValidRequest);
            Assert.Null(session.Picture);
            Assert.Equal(refused.Error.Message, session.StatusText);

            // Create is refused by the same verdict — the host is never
            // touched, so nothing partial or stale can reach the part.
            var create = session.CreateSketch(host);
            Assert.False(create.Succeeded);
            Assert.Empty(host.Document!.Sketches);
            Assert.Empty(host.Document.BlockDefinitions);

            // The fix the message names — raising the level — clears it.
            var raised = session.SetEccLevel(QRCodeGenerator.ECCLevel.M);
            Assert.True(raised.Succeeded, raised.Error?.Message);
            Assert.True(session.CurrentResult!.Sketch!.Info.OpenedSiteCount >= 3);
        }

        [Fact]
        public void Under_circle_the_toggle_is_inert_and_neither_gate_applies()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            session.SetCellShape(QrCellShape.Circle);

            Assert.False(session.RingSplitApplies);   // the pane hides the control

            var result = session.SetRingSplit(true);

            // The flag stores verbatim — the inert-field discipline the
            // growth and dot-size texts already follow — but drives nothing:
            // dots emit no contours and no cells drop, and the ECC gate does
            // not apply either, even at L.
            Assert.True(session.RingSplit);
            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Equal(0, result.Sketch!.Info.OpenedSiteCount);
            Assert.Equal(0, result.Sketch!.Info.DroppedCellCount);
            var underL = session.SetEccLevel(QRCodeGenerator.ECCLevel.L);
            Assert.True(underL.Succeeded, underL.Error?.Message);

            // Back on Square the stored flag applies — and the below-M
            // refusal surfaces, exactly like a malformed inert text surfacing
            // on the shape that needs it.
            var back = session.SetCellShape(QrCellShape.Square);
            Assert.False(back.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidEccLevel, back.Error!.Kind);
            Assert.True(session.RingSplit);
            Assert.True(session.RingSplitApplies);
        }

        [Fact]
        public void The_info_line_reports_opened_sites_and_dropped_cells()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);

            // Option off carries no row — the treatment's cost report appears
            // only while it is in force, like the circle mode's Dots row.
            Assert.DoesNotContain(session.InfoLines, l => l.Label == "Ring split");

            session.SetRingSplit(true);

            var info = session.CurrentResult!.Sketch!.Info;
            var row = InfoValue(session, "Ring split");
            Assert.Contains(info.OpenedSiteCount + " void", row);
            Assert.Contains("opened", row);
            Assert.Contains(info.DroppedCellCount + " cell", row);
            Assert.Contains("dropped", row);
        }

        [Fact]
        public void Create_under_ring_split_inserts_the_opened_geometry()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.SetRingSplit(true);

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(
                ExpectedRequest(ringSplit: true)).Sketch!;
            Assert.Equal(expected.Info.SegmentCount, sketch.ProfileSegments.Count());
        }

        [Fact]
        public void The_toggle_applies_to_future_creates_only()
        {
            // A sketch keeps the geometry it was created with — a later
            // toggle never mutates committed work.
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.CreateSketch(host);
            host.Document!.ActiveSketch = null;
            var unsplitSegs = host.Document.Sketches[0].ProfileSegments.Count();

            session.SetRingSplit(true);
            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var expected = QrSketchGenerator.Generate(
                ExpectedRequest(ringSplit: true)).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document.Sketches[1].ProfileSegments.Count());
            Assert.Equal(unsplitSegs, host.Document.Sketches[0].ProfileSegments.Count());
            Assert.NotEqual(unsplitSegs, host.Document.Sketches[1].ProfileSegments.Count());
        }

        [Fact]
        public void The_option_rides_every_content_preset_orthogonally()
        {
            // A fixed pane field, not a per-preset input: picking a content
            // type and composing its fields leaves the toggle in force, and
            // the composed payload gets the same opened geometry.
            var (session, _) = NewSession();
            session.SetRingSplit(true);
            session.SetContentType("url");
            session.SetPresetField("url", "https://example.com/qr");

            Assert.True(session.RingSplit);
            Assert.True(session.HasValidRequest, session.StatusText);
            Assert.Contains("example.com/qr", session.Content);
            Assert.True(session.CurrentResult!.Sketch!.Info.OpenedSiteCount >= 3,
                "the composed payload's finder rings open like raw content's");
        }
    }
}
