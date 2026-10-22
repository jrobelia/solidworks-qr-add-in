using System.Collections.Generic;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #55 — <see cref="QrChannelGaps"/>: the facing wall-pair scan the
    /// live gate measures on the module side (the expectation) and on the
    /// block definition's and exploded sketch's enumerable segments (the
    /// delivered geometry). The scan must find exactly the finder-ring thin
    /// channels — never the module-aligned cell drops (a full light gap
    /// wide), never a grown-neighbour gap — on both real payloads and
    /// synthetic edge sets.
    /// </summary>
    public class QrChannelGapsTests
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

        /// <summary>Half the reported minimum light gap — the gate's scan cap.</summary>
        private static double MaxGapFor(QrSketch sketch) =>
            sketch.Info.MinLightGapMm / 2000.0;

        private static QrSketch Split(string content, double sizeMm, double growth) =>
            QrSketchGenerator.Generate(new QrSketchRequest(content, sizeMm, growth)
            { RingSplit = true }).Sketch!;

        [Fact]
        public void An_untreated_payload_reports_no_channel_pairs()
        {
            var plain = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth)).Sketch!;

            Assert.Empty(QrChannelGaps.Find(plain.Edges, MaxGapFor(plain)));
        }

        [Fact]
        public void The_split_payload_reports_exactly_the_three_finder_channels()
        {
            var sketch = Split(PayloadA, SizeMm, Growth);
            var gaps = QrChannelGaps.Find(sketch.Edges, MaxGapFor(sketch));

            Assert.Equal(3, gaps.Count);
            Assert.All(gaps, gap =>
                Assert.Equal(sketch.Info.FinderChannelWidthMm / 1000.0, gap.GapM, 9));
        }

        [Fact]
        public void The_finder_fixture_reports_one_channel_with_two_mouths()
        {
            var sketch = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, ringSplit: true).Sketch!;

            var gap = Assert.Single(
                QrChannelGaps.Find(sketch.Edges, MaxGapFor(sketch)));
            Assert.Equal(sketch.Info.FinderChannelWidthMm / 1000.0, gap.GapM, 9);
            // The channel crosses the top arm hugging the void's left inner
            // edge: mouths share the locus x and sit at the band's outer and
            // inner grown edges.
            Assert.Equal(gap.MouthA.X, gap.MouthB.X, 9);
            Assert.NotEqual(gap.MouthA.Y, gap.MouthB.Y);
            Assert.True(gap.MouthA.Y > 0 && gap.MouthB.Y > 0, "the top arm");
        }

        [Fact]
        public void The_dense_split_reports_three_thin_channels_not_the_cell_drops()
        {
            // Data-area sites open by whole-cell drops — a full light gap
            // wide, never a facing wall-pair under the scan cap.
            var sketch = Split(new string('K', 200), 40.0, 0.10);
            Assert.True(sketch.Info.DroppedCellCount > 0);

            var gaps = QrChannelGaps.Find(sketch.Edges, MaxGapFor(sketch));

            Assert.Equal(3, gaps.Count);
            Assert.All(gaps, gap =>
                Assert.Equal(sketch.Info.FinderChannelWidthMm / 1000.0, gap.GapM, 9));
        }

        [Fact]
        public void The_inverted_finder_channel_registers_too()
        {
            var sketch = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth,
                invert: true, ringSplit: true).Sketch!;

            var gap = Assert.Single(
                QrChannelGaps.Find(sketch.Edges, MaxGapFor(sketch)));
            Assert.Equal(sketch.Info.FinderChannelWidthMm / 1000.0, gap.GapM, 9);
        }

        [Fact]
        public void Synthetic_facing_walls_register_by_gap_direction()
        {
            const double m = 1e-3; // mm in metres
            var edges = new List<SketchEdge>
            {
                // A horizontal channel: two horizontal walls, same extent,
                // 0.05 mm apart — mouths on the paired x ends.
                new SketchEdge(new SketchPoint2(0.0, 0.0), new SketchPoint2(2 * m, 0.0)),
                new SketchEdge(new SketchPoint2(2 * m, 0.05 * m), new SketchPoint2(0.0, 0.05 * m)),
                // A vertical channel at x=10 mm, mouths on the paired y ends.
                new SketchEdge(new SketchPoint2(10 * m, 5 * m), new SketchPoint2(10 * m, 1 * m)),
                new SketchEdge(new SketchPoint2(10.05 * m, 5 * m), new SketchPoint2(10.05 * m, 1 * m)),
                // A grown-neighbour pair a full light gap apart — filtered.
                new SketchEdge(new SketchPoint2(0.0, 20 * m), new SketchPoint2(2 * m, 20 * m)),
                new SketchEdge(new SketchPoint2(0.0, 21 * m), new SketchPoint2(2 * m, 21 * m)),
                // A same-gap pair with DIFFERENT extents — not a corridor.
                new SketchEdge(new SketchPoint2(0.0, 30 * m), new SketchPoint2(3 * m, 30 * m)),
                new SketchEdge(new SketchPoint2(0.0, 30.05 * m), new SketchPoint2(1 * m, 30.05 * m)),
            };

            var gaps = QrChannelGaps.Find(edges, 0.5 * m);

            Assert.Equal(2, gaps.Count);
            var horizontal = gaps.Single(g => g.MouthA.Y == g.MouthB.Y);
            Assert.Equal(0.05 * m, horizontal.GapM, 9);
            Assert.Equal(new SketchPoint2(0.0, 0.025 * m).X, horizontal.MouthA.X, 9);
            Assert.Equal(0.025 * m, horizontal.MouthA.Y, 9);
            Assert.Equal(2 * m, horizontal.MouthB.X, 9);
            var vertical = gaps.Single(g => g.MouthA.X == g.MouthB.X);
            Assert.Equal(10.025 * m, vertical.MouthA.X, 9);
            Assert.Equal(1 * m, vertical.MouthA.Y, 9);
            Assert.Equal(5 * m, vertical.MouthB.Y, 9);
        }

        [Fact]
        public void The_scan_tolerates_native_side_rounding()
        {
            // The exploded sketch carries module positions verbatim; the
            // def sketch carries them through its own offset frame — the
            // scan's tolerance absorbs sub-nanometre transform noise.
            var sketch = Split(PayloadA, SizeMm, Growth);
            var shifted = sketch.Edges.Select(e => new SketchEdge(
                new SketchPoint2(e.Start.X + 1.3e-10, e.Start.Y - 7e-11),
                new SketchPoint2(e.End.X + 1.3e-10, e.End.Y - 7e-11))).ToList();

            var gaps = QrChannelGaps.Find(shifted, MaxGapFor(sketch), toleranceM: 5e-9);

            Assert.Equal(3, gaps.Count);
            Assert.All(gaps, gap =>
                Assert.Equal(sketch.Info.FinderChannelWidthMm / 1000.0, gap.GapM, 7));
        }
    }
}
