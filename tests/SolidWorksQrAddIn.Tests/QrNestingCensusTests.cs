using System.Collections.Generic;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #55 — <see cref="QrNestingCensus"/>: the module-side
    /// region/hole/island census the live gates assert against the host's
    /// sketch-region census. The census walks the same
    /// <see cref="QrSketch.Regions"/> tree the tests' inline island-bearing
    /// counter does, so these cases pin the numbers the fixtures already
    /// established, plus the material-face and sketch-region totals the
    /// exploded-sketch route measures.
    /// </summary>
    public class QrNestingCensusTests
    {
        private const string PayloadA = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;

        private static bool[,] FinderCells()
        {
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
            var cells = new bool[11, 11];
            for (var r = 0; r < 11; r++)
                for (var c = 0; c < 11; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 5), System.Math.Abs(c - 5));
                    cells[r, c] = ring == 5 || ring == 3 || ring <= 1;
                }
            return cells;
        }

        private static QrSketch Generate(
            string content, double sizeMm, double growth, bool ringSplit = false,
            bool invert = false) =>
            QrSketchGenerator.Generate(new QrSketchRequest(content, sizeMm, growth)
            { RingSplit = ringSplit, Invert = invert }).Sketch!;

        [Fact]
        public void The_baseline_payload_reports_three_island_bearing_holes()
        {
            var census = QrNestingCensus.Of(Generate(PayloadA, SizeMm, Growth));

            // The recorded v1 nesting: 8 top-level regions, 16 hole loops at
            // every depth, the three finder centres nested as islands.
            Assert.Equal(8, census.TopLevelRegions);
            Assert.Equal(16, census.HoleCount);
            Assert.Equal(3, census.IslandCount);
            Assert.Equal(3, census.IslandBearingHoleCount);
            Assert.False(census.DoublyNestedFree);
            Assert.Equal(11, census.MaterialRegionCount);
            Assert.Equal(27, census.SketchRegionCount);
        }

        [Fact]
        public void The_split_payload_is_doubly_nested_free_with_promoted_islands()
        {
            var plain = Generate(PayloadA, SizeMm, Growth);
            var split = Generate(PayloadA, SizeMm, Growth, ringSplit: true);
            var census = QrNestingCensus.Of(split);

            Assert.True(census.DoublyNestedFree);
            Assert.Equal(0, census.IslandBearingHoleCount);
            Assert.Equal(0, census.IslandCount);
            // The three finder centres promote to top-level contours.
            Assert.Equal(plain.Regions.Count + 3, census.TopLevelRegions);
            // Each opened void merges into its band — the island-bearing
            // loops are gone, every other hole survives.
            Assert.Equal(16 - 3, census.HoleCount);
            Assert.Equal(11, census.MaterialRegionCount);
            Assert.Equal(24, census.SketchRegionCount);
            Assert.Equal(split.Contours.Count, census.SketchRegionCount);
        }

        [Fact]
        public void A_dense_split_payload_censuses_cleanly()
        {
            var content = new string('K', 200);
            var plain = Generate(content, 40.0, 0.10);
            var split = Generate(content, 40.0, 0.10, ringSplit: true);
            var plainCensus = QrNestingCensus.Of(plain);
            var census = QrNestingCensus.Of(split);

            Assert.True(plainCensus.IslandBearingHoleCount > 3,
                $"the dense payload nests beyond the finders: {plainCensus.IslandBearingHoleCount}");
            Assert.True(census.DoublyNestedFree);
            Assert.Equal(0, census.IslandCount);
            Assert.Equal(plainCensus.TopLevelRegions + plainCensus.IslandCount,
                census.TopLevelRegions);
            // The island-bearing loops are gone — and cell drops can merge
            // adjacent voids into one, closing still more hole loops; the
            // treatment never creates a hole, so the delta is a ceiling.
            Assert.True(census.HoleCount <=
                plainCensus.HoleCount - plainCensus.IslandBearingHoleCount,
                $"drops merge or open voids, never create holes: " +
                $"{census.HoleCount} vs ceiling " +
                $"{plainCensus.HoleCount - plainCensus.IslandBearingHoleCount}");
            Assert.Equal(split.Contours.Count, census.SketchRegionCount);
        }

        [Fact]
        public void The_finder_fixture_promotes_its_centre_island()
        {
            var plain = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth).Sketch!;
            var split = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, ringSplit: true).Sketch!;

            var plainCensus = QrNestingCensus.Of(plain);
            Assert.Equal(1, plainCensus.TopLevelRegions);
            Assert.Equal(1, plainCensus.HoleCount);
            Assert.Equal(1, plainCensus.IslandCount);
            Assert.Equal(1, plainCensus.IslandBearingHoleCount);

            var census = QrNestingCensus.Of(split);
            Assert.True(census.DoublyNestedFree);
            Assert.Equal(2, census.TopLevelRegions);
            Assert.Equal(0, census.HoleCount);
            Assert.Equal(2, census.MaterialRegionCount);
            Assert.Equal(2, census.SketchRegionCount);
        }

        [Fact]
        public void Stacked_bands_census_tracks_promotion_at_every_depth()
        {
            var plain = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), SizeMm, Growth).Sketch!;
            var split = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), SizeMm, Growth, ringSplit: true).Sketch!;

            var plainCensus = QrNestingCensus.Of(plain);
            Assert.Equal(1, plainCensus.TopLevelRegions);
            Assert.Equal(2, plainCensus.HoleCount);
            Assert.Equal(2, plainCensus.IslandCount);
            Assert.Equal(2, plainCensus.IslandBearingHoleCount);
            Assert.Equal(3, plainCensus.MaterialRegionCount);

            var census = QrNestingCensus.Of(split);
            Assert.True(census.DoublyNestedFree);
            Assert.Equal(3, census.TopLevelRegions);
            Assert.Equal(0, census.HoleCount);
            Assert.Equal(0, census.IslandCount);
            Assert.Equal(split.Contours.Count, census.SketchRegionCount);
        }

        [Fact]
        public void The_inverted_split_is_doubly_nested_free_too()
        {
            var split = QrSketchGenerator.GenerateFromCells(
                NestedBandsCells(), SizeMm, Growth,
                invert: true, ringSplit: true).Sketch!;

            var census = QrNestingCensus.Of(split);
            Assert.True(census.DoublyNestedFree);
            Assert.Equal(0, census.IslandCount);
            Assert.Equal(split.Contours.Count, census.SketchRegionCount);
        }

        [Fact]
        public void The_census_identity_holds_across_payloads()
        {
            foreach (var content in new[] { PayloadA, "HTTPS://QR-MVP.DEV/T3" })
                foreach (var ringSplit in new[] { false, true })
                {
                    var sketch = Generate(content, SizeMm, Growth, ringSplit);
                    var census = QrNestingCensus.Of(sketch);
                    // One loop bounds each material face and each void —
                    // the region total equals the contour count.
                    Assert.Equal(sketch.Contours.Count, census.SketchRegionCount);
                    Assert.Equal(census.TopLevelRegions + census.IslandCount,
                        census.MaterialRegionCount);
                }
        }

        [Fact]
        public void A_circle_plan_has_an_empty_census()
        {
            var sketch = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth,
                cellShape: QrCellShape.Circle, cellShapeParameter: 0.9,
                ringSplit: true).Sketch!;

            var census = QrNestingCensus.Of(sketch);
            Assert.Equal(0, census.TopLevelRegions);
            Assert.Equal(0, census.HoleCount);
            Assert.Equal(0, census.IslandCount);
            Assert.True(census.DoublyNestedFree);
        }
    }
}
