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
    /// Tickets #52/#53/#54 — the opt-in ring split. #52 built the
    /// request→result seam: with the flag on under the Square cell shape,
    /// the emitted set loses every island-bearing hole — un-emitted cells
    /// flooded from the working-matrix border mark the exterior-connected
    /// void, and a minimal module-aligned cell path dropped through each
    /// enclosing band joins the hole's void to that flood — iterated to
    /// any nesting depth, under either polarity. Under invert the path's
    /// emitted quiet-zone leg keeps its cells and vents by the same thin
    /// channel (user feedback — the margin drops read as a module-wide
    /// removed line). #53 stops the three
    /// finder rings paying whole modules: each opens by a thin
    /// axis-aligned channel cut through the band at a ring corner, hugging
    /// the void's inner edge (off the detection runs' centre-lines; the
    /// inner-edge line reads as continuing into the gap), while every
    /// other site keeps the cell drop. The option is refused below ECC M, Circle output
    /// ignores it, and with it off the output is byte-identical to the
    /// historical pipeline. #54 carries the option across the
    /// session/pane seam: a fixed toggle beside invert, off by default
    /// and orthogonal to content presets; the below-M refusal surfaces as
    /// an INVALID verdict naming the ECC field; under Circle the control
    /// is inert and hidden; the preview shows the opened geometry through
    /// the existing parity contract; and the info line reports the
    /// opened-site and dropped-cell counts.
    /// </summary>
    public class QrRingSplitTests
    {
        private const string PayloadA = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        private static bool[,] FinderCells()
        {
            // The 7×7 finder pattern: dark ring, light ring, dark centre —
            // one island-bearing hole (the light ring) in one band.
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
            // light void / dark block-island — two island-bearing holes at
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
            Assert.Equal(0.0, viaRequest.Info.FinderChannelWidthMm);
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

        // ---------------- the finder-ring thin channel (ticket #53) ----------------

        /// <summary>
        /// The two channel mouths of each cut: the corridor the Difference
        /// removes through the band leaves two parallel wall edges exactly
        /// the channel width apart (its end tips land in void free core, so
        /// they leave no caps). A mouth is the midpoint of a pair of facing
        /// wall-ends — where the channel pierces the band's outer and inner
        /// boundaries — so its position pins the locus. The wall-pair scan
        /// is production's <see cref="QrChannelGaps.Find"/> — the same
        /// routine the live gate measures channels with.
        /// </summary>
        private static List<SketchPoint2> ChannelMouths(QrSketch sketch)
        {
            double gapM = sketch.Info.FinderChannelWidthMm / 1000.0;
            var mouths = new List<SketchPoint2>();
            foreach (var region in sketch.Regions)
                foreach (var gap in QrChannelGaps.Find(
                             region.Boundary.Edges, maxGapM: gapM * 2.0))
                    if (System.Math.Abs(gap.GapM - gapM) < 1e-9)
                    {
                        mouths.Add(gap.MouthA);
                        mouths.Add(gap.MouthB);
                    }
            return mouths;
        }

        /// <summary>
        /// The derived channel width in mm: the larger of the integer-grid
        /// floor (min feature units) and the recorded insertion floor
        /// (0.05 mm) expressed on the request's grid.
        /// </summary>
        private static double ExpectedChannelWidthMm(
            double sizeMm, double growth, int dataCells)
        {
            long u = QrInsertionGuard.GridUnitsPerCell;
            long g = (long)System.Math.Round(growth * u);
            double mmPerUnit = sizeMm / (dataCells * (double)u + 2 * g);
            long w = System.Math.Max(QrInsertionGuard.MinGridFeatureUnits,
                (long)System.Math.Ceiling(
                    QrInsertionGuard.MinInsertableFeatureMm / mmPerUnit));
            return w * mmPerUnit;
        }

        [Fact]
        public void A_finder_ring_void_opens_with_a_thin_channel_not_a_dropped_cell()
        {
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(1, sketch.Info.OpenedSiteCount);
            // The finder ring pays no module — the channel is sub-cell.
            Assert.Equal(0, sketch.Info.DroppedCellCount);

            // The band becomes a C — the hole joins the exterior — and the
            // former centre island surfaces as a top-level contour.
            Assert.Equal(2, sketch.Regions.Count);
            Assert.All(sketch.Regions, r => Assert.Empty(r.Holes));
            Assert.Equal(2, sketch.Contours.Count);
            Assert.Equal(2, sketch.Info.ContourCount);
        }

        [Fact]
        public void The_channel_width_is_derived_from_the_grid_and_insertion_floor()
        {
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;

            double expectedMm = ExpectedChannelWidthMm(25.0, 0.03, 7);
            Assert.True(expectedMm >= QrInsertionGuard.MinInsertableFeatureMm);
            Assert.Equal(expectedMm, sketch.Info.FinderChannelWidthMm, 9);

            // The delivered geometry agrees: the corridor's two walls sit
            // exactly the channel width apart, so each mouth pins the locus.
            Assert.Equal(2, ChannelMouths(sketch).Count);
        }

        [Theory]
        [InlineData(25.0, 0.03)]
        [InlineData(40.0, 0.10)]
        [InlineData(80.0, 0.45)]
        public void The_channel_width_tracks_the_grid_at_every_size_and_growth(
            double sizeMm, double growth)
        {
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), sizeMm, growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(0, sketch.Info.DroppedCellCount);
            Assert.Equal(ExpectedChannelWidthMm(sizeMm, growth, 7),
                sketch.Info.FinderChannelWidthMm, 9);
        }

        /// <summary>
        /// The channel width in integer grid units, derived exactly like
        /// production's <see cref="QrRingSplitter.FinderChannelWidthUnits"/>.
        /// </summary>
        private static long ExpectedChannelWidthUnits(
            double sizeMm, double growth, int dataCells)
        {
            long u = QrInsertionGuard.GridUnitsPerCell;
            double mmPerUnit = sizeMm /
                (dataCells * (double)u + 2 * System.Math.Round(growth * u));
            return QrRingSplitter.FinderChannelWidthUnits(mmPerUnit);
        }

        [Fact]
        public void The_channel_sits_at_the_ring_corner_on_the_voids_inner_edge()
        {
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;

            // 7 data cells in a 15-cell working matrix: the finder centre sits
            // at the sketch origin — detection runs sample x=0 and y=0. The
            // channel must sit at the ring corner, hugging the void's grown
            // left inner edge: a vertical slit on the top arm whose walls sit
            // inside the void off the inner-edge line x = 5u+g — the near
            // wall at 5u+g+w+10 (a wall ON the grown edge would be a
            // coincident-edge violation; a centred straddle would leave a
            // sub-floor fin of band material at high growth; and the corner
            // remnant edge must itself clear the floor, so the inset is one
            // channel width plus the grid-floor margin). Mouths sit where the
            // walls meet the band's grown outer and inner edges (row 4's
            // grown edges 4u−g and 5u+g; +y is matrix row 0, so the mouths
            // sit at 3.5u+g and 2.5u−g) at the shared midline
            // x = −2.5u+g+1.5w+10.
            double u = QrInsertionGuard.GridUnitsPerCell;
            double gUnits = System.Math.Round(Growth * u);
            double mPerUnit = (SizeMm / 1000.0) / (7 * u + 2 * gUnits);
            long wUnits = ExpectedChannelWidthUnits(SizeMm, Growth, 7);
            var mouths = ChannelMouths(sketch);
            Assert.Equal(2, mouths.Count);
            Assert.All(mouths, mouth =>
            {
                Assert.Equal(
                    (-2.5 * u + gUnits + 1.5 * wUnits +
                     QrInsertionGuard.MinGridFeatureUnits) * mPerUnit,
                    mouth.X, 9);
                Assert.True(mouth.Y > 0.0, "the mouth is on the top arm");
            });
            Assert.Contains(mouths, mouth =>
                System.Math.Abs(mouth.Y - (3.5 * u + gUnits) * mPerUnit) < 1e-9);
            Assert.Contains(mouths, mouth =>
                System.Math.Abs(mouth.Y - (2.5 * u - gUnits) * mPerUnit) < 1e-9);

            // The inner edge truly continues into the gap: the void's grown
            // left edge (x = 5u+g → sketch −2.5u+g) still runs up to the
            // ring corner beside the mouth, where the w-long jog lands on
            // the mouth's near wall.
            double edgeX = (-2.5 * u + gUnits) * mPerUnit;
            double cornerY = (2.5 * u - gUnits) * mPerUnit;
            var band = Assert.Single(sketch.Regions,
                r => r.Boundary.Edges.Any(e =>
                    System.Math.Abs(e.Start.X - edgeX) < 1e-9));
            Assert.Contains(band.Boundary.Edges, e =>
                System.Math.Abs(e.Start.X - edgeX) < 1e-9 &&
                System.Math.Abs(e.End.X - e.Start.X) < 1e-9 &&
                (System.Math.Abs(e.End.Y - cornerY) < 1e-9 ||
                 System.Math.Abs(e.Start.Y - cornerY) < 1e-9));
        }

        [Fact]
        public void A_blocked_locus_falls_back_to_the_next_corner_locus()
        {
            // The locus-clearance sweep runs at the working-matrix level:
            // the two corner-adjacent loci's outer tips sit in the quiet
            // zone — emitting those cells (the internal seam takes the whole
            // matrix) blocks the top-left and left-top cuts, and the emitted
            // cells simply fuse to the band. The next clearing candidate is
            // the top arm's right end — the mirror inner-edge locus on the
            // void's right edge, given the same edge-aligned treatment as
            // every candidate.
            var emit = new bool[15, 15];
            var finder = FinderCells();
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 7; c++)
                    emit[r + 4, c + 4] = finder[r, c];
            emit[3, 5] = true;   // blocks the top-left cut's outer tip
            emit[5, 3] = true;   // blocks the left-top cut's outer tip

            var failure = QrRingSplitter.OpenIslandBearingHoles(
                emit, growthUnits: 300, channelWidthUnits: 142,
                out int openedSites, out int droppedCells,
                out Clipper2Lib.Paths64 channels);

            Assert.Null(failure);
            Assert.Equal(1, openedSites);
            Assert.Equal(0, droppedCells);
            Clipper2Lib.Path64 channel = Assert.Single(channels);
            // The fallback cut: a vertical channel hugging the void's grown
            // right inner edge (10u−g — its walls at 10u−g−w−10−w and
            // 10u−g−w−10), spanning the outer-tip and inner-tip cell centres
            // (rows 3.5·U and 5.5·U).
            Assert.Equal(100000 - 300 - 152 - 142, channel.Min(p => p.X));
            Assert.Equal(100000 - 300 - 152, channel.Max(p => p.X));
            Assert.Equal(35000, channel.Min(p => p.Y));
            Assert.Equal(55000, channel.Max(p => p.Y));
            Assert.Equal(4, channel.Distinct().Count());
        }

        [Fact]
        public void A_finder_void_with_no_clear_locus_is_a_named_all_or_nothing_refusal()
        {
            // Every candidate's outer tip emitted (again cells outside the
            // fixture's data area — the internal seam): the void stays an
            // enclosed island-bearing finder ring, but no corner locus can
            // reach a clear void, so the pass must refuse with a named
            // error rather than partially open the QR.
            var emit = new bool[15, 15];
            var finder = FinderCells();
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 7; c++)
                    emit[r + 4, c + 4] = finder[r, c];
            foreach (var (r, c) in new[]
                     { (3, 5), (3, 9), (5, 3), (9, 3),
                       (11, 5), (11, 9), (5, 11), (9, 11) })
                emit[r, c] = true;

            var failure = QrRingSplitter.OpenIslandBearingHoles(
                emit, growthUnits: 300, channelWidthUnits: 142,
                out int openedSites, out int droppedCells,
                out Clipper2Lib.Paths64 channels);

            Assert.NotNull(failure);
            Assert.Equal(QrSketchErrorKind.InvalidGeometry, failure!.Kind);
            Assert.Contains("channel", failure.Message);
            Assert.Equal(0, openedSites);
            Assert.Equal(0, droppedCells);
            Assert.Empty(channels);
        }

        [Fact]
        public void All_three_finder_channels_hug_their_corners_inner_edge()
        {
            var split = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { RingSplit = true });

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;

            // 21 data cells in a 29-cell working matrix — sketch centre at
            // 14.5u, +y is matrix row 0. Same corner choice and arm
            // direction as the centred locus had, but the slit body now
            // hugs the void's grown inner-edge line inside the void (near
            // wall w+10 off the edge, so the corner remnant edge clears the
            // floor): TL (window 4,4) vents the top arm beside the left
            // inner edge (x = 5u+g, walls [5u+g+w+10, 5u+g+2w+10]), TR
            // (window 4,18) the top arm beside the right inner edge
            // (x = 24u−g, walls [24u−g−2w−10, 24u−g−w−10]), BL (window
            // 18,4) the bottom arm beside the left inner edge (x = 5u+g).
            // Mouths sit on the band rows' grown outer/inner edges — TL/TR
            // at matrix y 4u−g and 5u+g, BL at 24u−g and 25u+g.
            double u = QrInsertionGuard.GridUnitsPerCell;
            double gUnits = System.Math.Round(Growth * u);
            double mPerUnit = (SizeMm / 1000.0) / (21 * u + 2 * gUnits);
            long wUnits = ExpectedChannelWidthUnits(SizeMm, Growth, 21);
            double inset = wUnits + QrInsertionGuard.MinGridFeatureUnits;

            double leftX = (-9.5 * u + gUnits + inset + 0.5 * wUnits) * mPerUnit;
            double rightX = (9.5 * u - gUnits - inset - 0.5 * wUnits) * mPerUnit;
            double topOuterY = (10.5 * u + gUnits) * mPerUnit;
            double topInnerY = (9.5 * u - gUnits) * mPerUnit;
            double bottomInnerY = (-9.5 * u + gUnits) * mPerUnit;
            double bottomOuterY = (-10.5 * u - gUnits) * mPerUnit;

            var mouths = ChannelMouths(sketch);
            Assert.Equal(6, mouths.Count);
            foreach (var (x, y) in new[]
                     {
                         (leftX, topOuterY), (leftX, topInnerY),      // TL
                         (rightX, topOuterY), (rightX, topInnerY),    // TR
                         (leftX, bottomInnerY), (leftX, bottomOuterY) // BL
                     })
                Assert.Contains(mouths, m =>
                    System.Math.Abs(m.X - x) < 1e-9 &&
                    System.Math.Abs(m.Y - y) < 1e-9);
        }

        [Theory]
        [InlineData(0.01)]   // g < w: a cell-line wall would reach the void —
        [InlineData(0.03)]   // the pinned low-growth case
        [InlineData(0.10)]
        [InlineData(0.25)]
        [InlineData(0.45)]   // g ≫ w: a cell-line wall is buried, and a
                             // straddle leaves a sub-floor fin
        public void The_inner_edge_channel_leaves_no_residual_sliver_at_any_growth(
            double growth)
        {
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            // A fin stranded between the slit wall and the void interior —
            // or any piece of band left over — surfaces as an extra region;
            // the fixture stays exactly band C + promoted island.
            Assert.Equal(2, sketch.Regions.Count);
            Assert.Equal(2, sketch.Contours.Count);
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));

            // The slit hugs the grown inner-edge line at every growth — its
            // walls at [5u+g+w+10, 5u+g+2w+10] keep the tip inside the
            // void's free core even when g exceeds w.
            double u = QrInsertionGuard.GridUnitsPerCell;
            double gUnits = System.Math.Round(growth * u);
            double mPerUnit = (SizeMm / 1000.0) / (7 * u + 2 * gUnits);
            long wUnits = ExpectedChannelWidthUnits(SizeMm, growth, 7);
            double expectedX = (-2.5 * u + gUnits + 1.5 * wUnits +
                QrInsertionGuard.MinGridFeatureUnits) * mPerUnit;
            var mouths = ChannelMouths(sketch);
            Assert.Equal(2, mouths.Count);
            Assert.All(mouths, mouth => Assert.Equal(expectedX, mouth.X, 9));
            Assert.Contains(mouths, mouth =>
                System.Math.Abs(mouth.Y - (3.5 * u + gUnits) * mPerUnit) < 1e-9);
            Assert.Contains(mouths, mouth =>
                System.Math.Abs(mouth.Y - (2.5 * u - gUnits) * mPerUnit) < 1e-9);
        }

        [Fact]
        public void A_channel_that_cannot_fit_the_voids_free_core_is_refused()
        {
            // The void-side placement needs the remnant-edge inset (w+10)
            // plus a w of margin past the slit: at 48% growth the finder
            // void's free core (u−2g = 400 units) is narrower than the
            // corridor plus its margins (3w+10 > 400) — the all-or-nothing
            // refusal names the channel.
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, 0.48, ringSplit: true);

            Assert.False(split.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGeometry, split.Error!.Kind);
            Assert.Contains("channel", split.Error.Message);
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
            // Two island-bearing holes: the outer light ring bears the dark
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
            // The v1 payload's island-bearing holes are exactly its three
            // finder rings — with #53 each opens by a thin channel, so no
            // emitted cell drops at all on this payload.
            Assert.Equal(3, expectedSites);

            var split = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { RingSplit = true });

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(expectedSites, sketch.Info.OpenedSiteCount);
            Assert.Equal(0, sketch.Info.DroppedCellCount);
            Assert.Equal(ExpectedChannelWidthMm(SizeMm, Growth, 21),
                sketch.Info.FinderChannelWidthMm, 9);
            // The three finder bands survive essentially whole: three regions
            // keep the grown 7×7 span (a module-aligned drop would instead
            // leave the band a cell short).
            long u = QrInsertionGuard.GridUnitsPerCell;
            double mPerUnit = (SizeMm / 1000.0) / (21 * u + 2 * 300);
            double bandSpanM = (7 * u + 2 * 300) * mPerUnit;
            Assert.Equal(3, sketch.Regions.Count(r =>
                System.Math.Abs((r.Boundary.Vertices.Max(v => v.X) -
                          r.Boundary.Vertices.Min(v => v.X)) - bandSpanM) < 1e-6));
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
            // The three finder sites open by thin channels — every dropped
            // cell belongs to a data-area site (≥1 drop each).
            Assert.True(split.Sketch.Info.FinderChannelWidthMm > 0.0);
            Assert.True(split.Sketch.Info.DroppedCellCount >= expectedSites - 3,
                $"non-finder sites still open by cell drops: {split.Sketch.Info.DroppedCellCount} " +
                $"dropped for {expectedSites} sites");
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
            // The outer void's egress is all quiet-zone — a thin corridor,
            // no drop; the inner void pays one interior drop crossing the
            // emitted band into it. The leg's margin cells no longer count.
            Assert.Equal(1, sketch.Info.DroppedCellCount);
        }

        [Fact]
        public void The_inverted_finder_ring_vents_to_the_field_edge_by_channel()
        {
            // Under invert the emitted band at the finder site is the LIGHT
            // inner ring and the island-bearing hole is the dark ring itself:
            // the channel slots through the surrounding field to the matrix
            // edge rather than paying whole field cells.
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, invert: true, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.True(sketch.Info.Inverted);
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(1, sketch.Info.OpenedSiteCount);
            Assert.Equal(0, sketch.Info.DroppedCellCount);
            Assert.Equal(ExpectedChannelWidthMm(SizeMm, Growth, 7),
                sketch.Info.FinderChannelWidthMm, 9);

            // The field and the promoted light ring — the ring still carries
            // its own (legally nested, island-free) centre hole.
            Assert.Equal(2, sketch.Regions.Count);
            var ring = sketch.Regions.Single(r => r.Holes.Count == 1);
            Assert.Equal(0, IslandBearingHoles(new[] { ring }));
        }

        [Fact]
        public void Inverted_finder_channels_hug_the_void_rings_inner_edge()
        {
            var split = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth)
                { Invert = true, RingSplit = true });

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));

            // Mirrored corner/edge rule: each slot hugs the void edge ring's
            // INNER grown edge (the emitted interior's edge line) at the ring
            // corner nearest the matrix corner, the same w+10 inset into the
            // void as the band cuts. TL (window 4,4) vents up beside the
            // inner-left edge x = 5u−g (walls [5u−g−2w−10, 5u−g−w−10]); TR
            // (window 4,18) vents up beside the inner-right edge x = 24u+g
            // (walls [24u+g+w+10, 24u+g+2w+10]); BL (window 18,4) vents down
            // beside the inner-left edge x = 5u−g. Mouths sit where the walls
            // meet the field's grown edges and the clipped matrix boundary.
            double u = QrInsertionGuard.GridUnitsPerCell;
            double gUnits = System.Math.Round(Growth * u);
            double mPerUnit = (SizeMm / 1000.0) / (21 * u + 2 * gUnits);
            long wUnits = ExpectedChannelWidthUnits(SizeMm, Growth, 21);
            double inset = wUnits + QrInsertionGuard.MinGridFeatureUnits;

            double leftX = (-9.5 * u - gUnits - inset - 0.5 * wUnits) * mPerUnit;
            double rightX = (9.5 * u + gUnits + inset + 0.5 * wUnits) * mPerUnit;
            double fieldTopY = (10.5 * u - gUnits) * mPerUnit;
            double matrixTopY = 14.5 * u * mPerUnit;
            double fieldBottomY = (-10.5 * u + gUnits) * mPerUnit;
            double matrixBottomY = -14.5 * u * mPerUnit;

            var mouths = ChannelMouths(sketch);
            // The quiet-zone egress corridors add mouths of their own on
            // this payload — count only the mouths on the two finder-locus
            // ordinates, whose positions are pinned below.
            Assert.Equal(6, mouths.Count(m =>
                System.Math.Abs(m.X - leftX) < 1e-9 ||
                System.Math.Abs(m.X - rightX) < 1e-9));
            foreach (var (x, y) in new[]
                     {
                         (leftX, matrixTopY), (leftX, fieldTopY),        // TL up-slot
                         (rightX, matrixTopY), (rightX, fieldTopY),      // TR up-slot
                         (leftX, fieldBottomY), (leftX, matrixBottomY)   // BL down-slot
                     })
                Assert.Contains(mouths, m =>
                    System.Math.Abs(m.X - x) < 1e-9 &&
                    System.Math.Abs(m.Y - y) < 1e-9);
        }

        [Theory]
        [InlineData(0.01)]
        [InlineData(0.03)]
        [InlineData(0.10)]
        [InlineData(0.25)]
        [InlineData(0.45)]
        public void The_vent_channel_hugs_the_inner_edge_at_any_growth(double growth)
        {
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, growth, invert: true, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            // Field + promoted light ring — a stranded fin would add a region.
            Assert.Equal(2, sketch.Regions.Count);

            // The slot hugs the ring's grown inner-left edge x = 5u−g with
            // walls [5u−g−2w−10, 5u−g−w−10] — midline −2.5u−g−1.5w−10 —
            // venting up through the field; mouths at the matrix edge
            // (y = 0 → sketch +7.5u) and the field's grown edge above the
            // ring (4u+g → 3.5u−g).
            double u = QrInsertionGuard.GridUnitsPerCell;
            double gUnits = System.Math.Round(growth * u);
            double mPerUnit = (SizeMm / 1000.0) / (7 * u + 2 * gUnits);
            long wUnits = ExpectedChannelWidthUnits(SizeMm, growth, 7);
            double expectedX = (-2.5 * u - gUnits - 1.5 * wUnits -
                QrInsertionGuard.MinGridFeatureUnits) * mPerUnit;
            var mouths = ChannelMouths(sketch);
            Assert.Equal(2, mouths.Count);
            Assert.All(mouths, mouth => Assert.Equal(expectedX, mouth.X, 9));
            Assert.Contains(mouths, mouth =>
                System.Math.Abs(mouth.Y - 7.5 * u * mPerUnit) < 1e-9);
            Assert.Contains(mouths, mouth =>
                System.Math.Abs(mouth.Y - (3.5 * u - gUnits) * mPerUnit) < 1e-9);
        }

        /// <summary>
        /// The encoded inverted emit mask — the whole working matrix, light
        /// cells emitting, exactly like <see cref="QrSketchGenerator"/> builds
        /// it before the ring-split pass. Internal seam for mask-level checks.
        /// </summary>
        private static bool[,] EncodedEmit(string content, bool invert)
        {
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M))
            {
                var encoded = data.ModuleMatrix;
                int m = encoded.Count;
                var emit = new bool[m, m];
                for (var r = 0; r < m; r++)
                    for (var c = 0; c < m; c++)
                        emit[r, c] = encoded[r][c] != invert;
                return emit;
            }
        }

        [Fact]
        public void The_inverted_quiet_zone_leg_vents_by_thin_channel_not_dropped_cells()
        {
            // User feedback: under invert the quiet zone is emitted field, so
            // a cell-drop path through it showed as a whole-module-wide
            // removed line. The leg must vent like the finder slots instead —
            // a thin corridor through the emitted margin, the quiet-zone
            // cells keeping their emission.
            int q = QrSketchInfo.QuietZoneCellsPerSide;
            var emit = EncodedEmit(PayloadA, invert: true);
            var before = (bool[,])emit.Clone();
            int m = emit.GetLength(0);
            long u = QrInsertionGuard.GridUnitsPerCell;
            long g = (long)System.Math.Round(Growth * u);
            double mmPerUnit = SizeMm / ((m - 2 * q) * (double)u + 2 * g);
            long w = QrRingSplitter.FinderChannelWidthUnits(mmPerUnit);

            var failure = QrRingSplitter.OpenIslandBearingHoles(
                emit, g, w, out int openedSites, out int droppedCells,
                out Clipper2Lib.Paths64 channels);

            Assert.Null(failure);
            Assert.Equal(5, openedSites);

            // Every flipped cell sits INSIDE the quiet-zone band — the leg's
            // corridor is sub-cell, so no margin module drops.
            var dropped = new List<int>();
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if (before[r, c] && !emit[r, c])
                    {
                        Assert.True(
                            r >= q && c >= q && r < m - q && c < m - q,
                            $"drop ({r},{c}) sits in the quiet-zone band");
                        dropped.Add(r * m + c);
                    }
            Assert.Equal(droppedCells, dropped.Count);

            // The egress corridor: thin channel rects running the leg — its
            // stub vents past the matrix edge like a finder slot (the SW-QR-MVP
            // leg runs up column 14 through the top quiet zone).
            Assert.Contains(channels, rect =>
                rect.Min(p => p.Y) < 0 &&
                rect.Min(p => p.X) > 14 * u && rect.Max(p => p.X) < 15 * u);
            Assert.All(channels, rect =>
                Assert.True(
                    rect.Max(p => p.X) - rect.Min(p => p.X) == w ||
                    rect.Max(p => p.Y) - rect.Min(p => p.Y) == w,
                    "every channel rect is one corridor width thick"));
        }

        [Fact]
        public void The_inverted_egress_corridor_registers_as_a_thin_channel()
        {
            var split = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth)
                { Invert = true, RingSplit = true });

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(5, sketch.Info.OpenedSiteCount);
            // The two non-finder sites vent by corridor — between them only
            // one interior path cell still drops; every quiet-zone cell of
            // the legs keeps its emission and no longer counts.
            Assert.Equal(1, sketch.Info.DroppedCellCount);

            // The corridor reads exactly like a finder channel to the
            // wall-pair scan — the same derived width — and its outer mouth
            // sits on the clipped field edge (the keep-out square line).
            var gaps = QrChannelGaps.Find(
                sketch.Edges, sketch.Info.MinLightGapMm / 2000.0);
            double wMm = sketch.Info.FinderChannelWidthMm / 1000.0;
            Assert.True(gaps.Count >= 4,
                "three finder vents plus the quiet-zone egress corridor");
            Assert.All(gaps, gap => Assert.Equal(wMm, gap.GapM, 9));
            double u = QrInsertionGuard.GridUnitsPerCell;
            double mPerUnit = (SizeMm / 1000.0) /
                ((sketch.Info.MatrixCellCount - 2 * QrSketchInfo.QuietZoneCellsPerSide) * u +
                 2 * System.Math.Round(Growth * u));
            double matrixHalf = sketch.Info.MatrixCellCount * u / 2.0 * mPerUnit;
            Assert.Contains(gaps, gap =>
                System.Math.Abs(gap.MouthA.Y - matrixHalf) < 1e-9 ||
                System.Math.Abs(gap.MouthB.Y - matrixHalf) < 1e-9);
        }

        [Fact]
        public void An_egress_corridor_that_cannot_fit_is_refused()
        {
            // The corridor obeys the finder-channel discipline: at 48%
            // growth a cell's free core (u−2g = 400 units) is narrower than
            // the channel plus its margins — the inverted quiet-zone leg
            // cannot vent thin, so the pass refuses by name.
            var split = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), 40.0, 0.48, invert: true, ringSplit: true);

            Assert.False(split.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidGeometry, split.Error!.Kind);
            Assert.Contains("channel", split.Error.Message);
        }

        [Fact]
        public void The_normal_polarity_drop_path_never_meets_the_quiet_zone()
        {
            // Under normal polarity the margin cells are the un-emitted
            // exterior — the pass mutates no quiet-zone cell and appends no
            // corridor: the treatment is invert-only by construction.
            int q = QrSketchInfo.QuietZoneCellsPerSide;
            var emit = EncodedEmit(PayloadA, invert: false);
            var before = (bool[,])emit.Clone();
            int m = emit.GetLength(0);
            long u = QrInsertionGuard.GridUnitsPerCell;
            long g = (long)System.Math.Round(Growth * u);
            double mmPerUnit = SizeMm / ((m - 2 * q) * (double)u + 2 * g);
            long w = QrRingSplitter.FinderChannelWidthUnits(mmPerUnit);

            var failure = QrRingSplitter.OpenIslandBearingHoles(
                emit, g, w, out int openedSites, out int droppedCells,
                out Clipper2Lib.Paths64 channels);

            Assert.Null(failure);
            Assert.Equal(3, openedSites);   // the three finder rings
            Assert.Equal(0, droppedCells);
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if (r < q || c < q || r >= m - q || c >= m - q)
                        Assert.Equal(before[r, c], emit[r, c]);
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
            Assert.True(split.Sketch!.Info.FinderChannelWidthMm > 0.0);
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

            var failure = QrRingSplitter.OpenIslandBearingHoles(
                emit, growthUnits: 0, channelWidthUnits: 0, maxPasses: 0,
                out int openedSites, out int droppedCells,
                out Clipper2Lib.Paths64 channels);

            Assert.NotNull(failure);
            Assert.Equal(QrSketchErrorKind.InvalidGeometry, failure!.Kind);
            Assert.Contains("island-bearing", failure.Message);
            Assert.Equal(0, openedSites);
            Assert.Equal(0, droppedCells);
            Assert.Empty(channels);
        }

        [Fact]
        public void The_post_union_guard_refuses_a_residual_island_bearing_hole()
        {
            // A claimed finder channel is sub-cell — the mask marks the void
            // opened by identity while the union keeps what the channel
            // actually cut, so a diverged claim is exactly what the
            // post-union all-or-nothing check exists for. This fixture
            // builds the divergence: emitted cells outside the data area
            // (the internal seam takes the whole working matrix — a
            // data-cells fixture cannot emit in the quiet zone) block every
            // outward locus AND fuse the finder band into the enclosing
            // band, so the void around the finder stops looking
            // island-bearing mask-side. The only clearing locus then lands
            // the channel in that enclosed pocket: the mask converges —
            // the pocket is claimed-opened, the enclosing void gets its
            // cell drop — while the union retains the ring → hole → island
            // inside a promoted island's own hole (the finder site nested
            // three bands deep in the fixture). The guard must refuse
            // rather than deliver it.
            var emit = new bool[19, 19];
            for (var r = 0; r < 19; r++)
                for (var c = 0; c < 19; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 9), System.Math.Abs(c - 9));
                    if (ring == 9 || ring == 7)
                        emit[r, c] = true;
                }
            var finder = FinderCells();
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 7; c++)
                    emit[r + 4, c + 4] = finder[r, c];
            // Blocked loci at the four outward channel tips — they also
            // bridge the finder band into the enclosing band.
            emit[3, 5] = true;
            emit[3, 9] = true;
            emit[5, 3] = true;
            emit[9, 3] = true;

            var result = QrSketchGenerator.GenerateFromWorkingMatrix(
                emit, dataCells: 11, sizeMm: 60.0,
                growthFractionPerSide: Growth, ringSplit: true);

            Assert.False(result.Succeeded, "the union retains a residual island-bearing hole");
            Assert.Equal(QrSketchErrorKind.InvalidGeometry, result.Error!.Kind);
            // The splitter converged mask-side — the refusal is the
            // post-union all-or-nothing check, not the treatment loop.
            Assert.Contains("unioned contours", result.Error.Message);
        }

        [Fact]
        public void A_ring_off_the_finder_windows_still_opens_by_cell_drop()
        {
            // An 11×11 field (19-cell working matrix, so the three finder
            // windows are distinct): a canonical finder at the TL window
            // PLUS a 5×5 nested mini-ring in the data area — dark ring /
            // light void / dark dot. Its hole is island-bearing but sits
            // off every finder window, so it keeps paying a module-aligned
            // cell drop while the finder site pays only the thin channel.
            var cells = new bool[11, 11];
            var finder = FinderCells();
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 7; c++)
                    cells[r, c] = finder[r, c];
            for (var r = 5; r <= 9; r++)
                for (var c = 6; c <= 10; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 7), System.Math.Abs(c - 8));
                    if (ring == 2 || ring == 0)
                        cells[r, c] = true;
                }

            var split = QrSketchGenerator.GenerateFromCells(
                cells, SizeMm, Growth, ringSplit: true);

            Assert.True(split.Succeeded, split.Error?.Message);
            var sketch = split.Sketch!;
            Assert.Equal(0, IslandBearingHoles(sketch.Regions));
            Assert.Equal(2, sketch.Info.OpenedSiteCount);
            Assert.Equal(1, sketch.Info.DroppedCellCount);
            Assert.Equal(ExpectedChannelWidthMm(SizeMm, Growth, 11),
                sketch.Info.FinderChannelWidthMm, 9);
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
            Assert.Equal(0.0, sketch.Info.FinderChannelWidthMm);
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
            var syncs = windows[0].SyncCount;

            var result = session.SetRingSplit(true);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(session.RingSplit);
            var info = session.CurrentResult!.Sketch!.Info;
            Assert.True(info.OpenedSiteCount >= 3,
                $"the v1 payload's three finder rings open: {info.OpenedSiteCount}");
            // #53: the three finder sites pay a thin channel, not cells —
            // only non-finder sites (none on this payload) drop cells.
            Assert.Equal(0, info.DroppedCellCount);
            Assert.True(info.FinderChannelWidthMm > 0.0);
            // Preview parity: the picture IS a direct render of the split
            // module result — channels and dropped cells, no second renderer.
            var expected = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(ExpectedRequest(ringSplit: true)).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();
            Assert.Equal(expected, session.Picture!.Signature());
            // The 0.05 mm slit is sub-pixel at the window's raster density —
            // its corridor can miss every 240 px sample column on this
            // payload — so check the render differs where the raster
            // actually resolves it (pixel pitch below the channel width).
            const int resolvingPixels = 4 * QrSketchSession.PreviewPixelsAcross;
            var splitDense = QrPictureRenderer.Render(
                session.CurrentResult!.Sketch!, resolvingPixels).Signature();
            var plainDense = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(ExpectedRequest(ringSplit: false)).Sketch!,
                resolvingPixels).Signature();
            Assert.NotEqual(plainDense, splitDense);
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
            Assert.Contains(info.OpenedSiteCount + " site", row);
            Assert.Contains("opened", row);
            Assert.Contains(info.DroppedCellCount + " cell", row);
            Assert.Contains("dropped", row);

            // The v1 payload's three finder rings all open by thin channel,
            // so the derived channel width joins the row.
            Assert.True(info.FinderChannelWidthMm > 0.0);
            Assert.Contains("thin channel ", row);
            Assert.Contains(
                info.FinderChannelWidthMm.ToString(
                    "G4", System.Globalization.CultureInfo.InvariantCulture) + " mm",
                row);
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
