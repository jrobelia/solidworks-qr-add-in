using System.Collections.Generic;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #52 — the opt-in ring split at the request→result seam. With the
    /// flag on under the Square cell shape, the emitted set loses every
    /// island-bearing void: un-emitted cells flooded from the working-matrix
    /// border mark the exterior-connected void, and a minimal module-aligned
    /// cell path dropped through each enclosing band joins the hole's void to
    /// that flood — iterated to any nesting depth, under either polarity. The
    /// option is refused below ECC M, Circle output ignores it, and with it
    /// off the output is byte-identical to the historical pipeline.
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
    }
}
