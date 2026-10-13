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
    /// Ticket #34 — the keep-function-patterns-square carve-out (spec #13).
    /// With the checkbox on, emitted cells at function-pattern positions —
    /// the three corner finders (each as its full 8×8 footprint, 7×7 + the
    /// separator ring), the timing bands on row/column 6, and the
    /// per-version 5×5 alignment patterns — always emit the expanded-square
    /// primitive while data cells take the selected shape. Classification
    /// is by POSITION, not darkness: under invert the same positions emit
    /// square pits. Fixture matrices (version 0) classify nothing — the
    /// carve-out is inert at the deterministic seam. Format- and
    /// version-information cells are NOT function positions (the ticket
    /// names finder/alignment/timing only); they take the selected shape.
    /// Under Circle the lattice is the growth-0 map, so the function
    /// square is the bare cell square — growth never applies under Circle.
    /// </summary>
    public class QrFunctionSquareTests
    {
        private const string Payload = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;
        private const int Pixels = 240;

        /// <summary>Data-cell count N for a QR version (N = 17 + 4·version).</summary>
        private static int CellsFor(int version) => 17 + 4 * version;

        /// <summary>
        /// The full (N+8)×(N+8) working matrix the encoder emits for a
        /// forced version — quiet zone included; data cells at offset 4.
        /// </summary>
        private static bool[,] EncodeWorkingMatrix(string content, int version)
        {
            using (var generator = new QRCodeGenerator())
            using (var data = generator.CreateQrCode(
                       content, QRCodeGenerator.ECCLevel.L,
                       forceUtf8: false, utf8BOM: false,
                       eciMode: QRCodeGenerator.EciMode.Default,
                       requestedVersion: version))
            {
                var encoded = data.ModuleMatrix;
                int m = encoded.Count;
                var matrix = new bool[m, m];
                for (var row = 0; row < m; row++)
                {
                    var bits = encoded[row];
                    for (var col = 0; col < m; col++)
                        matrix[row, col] = bits[col];
                }
                Assert.Equal(version, data.Version);
                return matrix;
            }
        }

        /// <summary>
        /// Does the real matrix carry the alignment-pattern motif centred at
        /// (centreRow, centreCol) in WORKING-matrix coordinates: dark outer
        /// ring (Chebyshev distance 2), light inner band (distance 1), dark
        /// centre — the ISO 18004 5×5 signature, independent of any table.
        /// </summary>
        private static bool HasAlignmentMotif(bool[,] matrix, int centreRow, int centreCol)
        {
            for (var dr = -2; dr <= 2; dr++)
                for (var dc = -2; dc <= 2; dc++)
                {
                    int norm = System.Math.Max(System.Math.Abs(dr), System.Math.Abs(dc));
                    bool expected = norm == 2 || norm == 0;
                    if (matrix[centreRow + dr, centreCol + dc] != expected)
                        return false;
                }
            return true;
        }

        // ---------------- the classifier seam: fixture versions are inert ----------------

        [Fact]
        public void Version_zero_fixtures_have_no_function_cells()
        {
            // The deterministic-fixture seam (GenerateFromCells) reports
            // version 0 — arbitrary fixture matrices are not real QR
            // matrices, so the carve-out must be inert there: every cell
            // takes the selected shape.
            var mask = QrFunctionPatterns.FunctionMask(CellsFor(1), version: 0);
            Assert.Null(mask);
            for (var row = 0; row < 21; row++)
                for (var col = 0; col < 21; col++)
                    Assert.False(QrFunctionPatterns.IsFunctionCell(row, col, 21, 0));
        }

        [Fact]
        public void A_version_matrix_size_mismatch_classifies_nothing()
        {
            // Data-cell count and version travel together in every real QR
            // (N = 17 + 4·version); a mismatch can only be a caller bug —
            // the classifier goes inert rather than classify wrong cells.
            Assert.Null(QrFunctionPatterns.FunctionMask(33, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(0, 0, 33, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(6, 6, 21, 41));
            Assert.False(QrFunctionPatterns.IsFunctionCell(6, 6, 21, -1));
        }

        // ---------------- finders: the full 8×8 (7×7 + separator ring) ----------------

        [Fact]
        public void Version_one_marks_the_three_finder_boxes_and_timing()
        {
            int n = CellsFor(1);   // 21
            // The three 8×8 corner boxes — each is the 7×7 finder plus its
            // one-cell separator ring on the data-facing sides.
            for (var row = 0; row < n; row++)
                for (var col = 0; col < n; col++)
                {
                    bool finder =
                        (row < 8 && col < 8) ||
                        (row < 8 && col >= n - 8) ||
                        (row >= n - 8 && col < 8);
                    bool timing = row == 6 || col == 6;
                    Assert.Equal(finder || timing,
                        QrFunctionPatterns.IsFunctionCell(row, col, n, 1));
                }
        }

        [Fact]
        public void The_separator_ring_is_a_function_position()
        {
            int n = CellsFor(1);
            // Row 7 / column 7 next to the top-left finder — the separator
            // is required whitespace of the finder structure, so it takes
            // the square primitive (visible under invert as square pits).
            Assert.True(QrFunctionPatterns.IsFunctionCell(7, 0, n, 1));
            Assert.True(QrFunctionPatterns.IsFunctionCell(7, 7, n, 1));
            Assert.True(QrFunctionPatterns.IsFunctionCell(0, 7, n, 1));
            // …but one cell further out is ordinary data.
            Assert.False(QrFunctionPatterns.IsFunctionCell(7, 8, n, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(8, 7, n, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(8, 8, n, 1));
        }

        [Fact]
        public void Format_information_cells_are_not_function_positions()
        {
            int n = CellsFor(1);
            // The format-information areas beside the finders (row 8 /
            // column 8 copies) are not named by the ticket — they carry
            // payload data and take the selected shape like any data cell.
            Assert.False(QrFunctionPatterns.IsFunctionCell(8, 0, n, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(8, 8, n, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(0, 8, n, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(8, n - 1, n, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(n - 1, 8, n, 1));
        }

        [Fact]
        public void The_timing_bands_cover_row_and_column_six()
        {
            int n = CellsFor(1);
            for (var k = 8; k < n - 8; k++)
            {
                Assert.True(QrFunctionPatterns.IsFunctionCell(6, k, n, 1));
                Assert.True(QrFunctionPatterns.IsFunctionCell(k, 6, n, 1));
            }
            Assert.False(QrFunctionPatterns.IsFunctionCell(5, 10, n, 1));
            Assert.False(QrFunctionPatterns.IsFunctionCell(10, 7, n, 1));
        }

        // ---------------- alignment: per-version centres minus finder overlaps ----------------

        [Fact]
        public void Version_one_has_no_alignment_centres()
        {
            Assert.Empty(QrFunctionPatterns.AlignmentCentres(1));
        }

        [Fact]
        public void Version_two_has_one_alignment_centre_at_18_18()
        {
            // Coordinates {6, 18}: the (6,6), (6,18) and (18,6) centres all
            // overlap a finder pattern — only the bottom-right one survives
            // the standard's overlap rule.
            var centres = QrFunctionPatterns.AlignmentCentres(2);
            Assert.Equal(new[] { (18, 18) }, centres);
        }

        [Fact]
        public void Version_seven_has_six_alignment_centres()
        {
            // Coordinates {6, 22, 38} — nine grid positions minus the three
            // finder-coincident ones.
            var centres = QrFunctionPatterns.AlignmentCentres(7);
            Assert.Equal(
                new[] { (6, 22), (22, 6), (22, 22), (22, 38), (38, 22), (38, 38) },
                centres);
        }

        [Fact]
        public void Finder_coincident_centres_are_dropped_for_every_version()
        {
            for (var version = 1; version <= 40; version++)
            {
                int n = CellsFor(version);
                var centres = QrFunctionPatterns.AlignmentCentres(version);
                Assert.DoesNotContain(centres, c => c == (6, 6));
                Assert.DoesNotContain(centres, c => c == (6, n - 7));
                Assert.DoesNotContain(centres, c => c == (n - 7, 6));
            }
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(6, 1)]
        [InlineData(7, 6)]
        [InlineData(13, 6)]
        [InlineData(14, 13)]
        [InlineData(20, 13)]
        [InlineData(21, 22)]
        [InlineData(27, 22)]
        [InlineData(28, 33)]
        [InlineData(34, 33)]
        [InlineData(35, 46)]
        [InlineData(40, 46)]
        public void Alignment_centre_counts_match_the_standard_table(int version, int expected)
        {
            Assert.Equal(expected, QrFunctionPatterns.AlignmentCentres(version).Count);
        }

        [Fact]
        public void Alignment_cells_are_the_five_by_five_around_each_centre()
        {
            int n = CellsFor(7);
            // Around centre (6,22): the 5×5 block rows 4–8, cols 20–24.
            for (var row = 4; row <= 8; row++)
                for (var col = 20; col <= 24; col++)
                    Assert.True(QrFunctionPatterns.IsFunctionCell(row, col, n, 7));
            // One cell past the box is plain data.
            Assert.False(QrFunctionPatterns.IsFunctionCell(9, 22, n, 7));
            Assert.False(QrFunctionPatterns.IsFunctionCell(5, 19, n, 7));
            Assert.False(QrFunctionPatterns.IsFunctionCell(5, 25, n, 7));
        }

        // ---------------- every version, verified against the real encoder ----------------

        [Fact]
        public void Every_alignment_centre_carries_the_motif_in_a_real_matrix()
        {
            // The strongest available check: the encoder's OWN output. For
            // every version 1–40 the classified centres must sit on a real
            // 5×5 alignment pattern in the encoded matrix (and the matrix
            // must contain no alignment motif the classifier missed).
            for (var version = 1; version <= 40; version++)
            {
                int n = CellsFor(version);
                int q = QrSketchInfo.QuietZoneCellsPerSide;
                var matrix = EncodeWorkingMatrix("HELLO", version);

                // Forward: every classified centre has the motif.
                foreach (var (cr, cc) in QrFunctionPatterns.AlignmentCentres(version))
                    Assert.True(HasAlignmentMotif(matrix, q + cr, q + cc),
                        $"v{version}: no alignment motif at data ({cr},{cc})");

                // Reverse: every motif found in the matrix is a classified
                // centre — nothing missed, nothing extra. (Motifs cannot
                // occur inside the finder/separator boxes: the 5×5 signature
                // contradicts the separator's all-light ring.)
                var found = new List<(int row, int col)>();
                for (var row = 2; row < n - 2; row++)
                    for (var col = 2; col < n - 2; col++)
                        if (HasAlignmentMotif(matrix, q + row, q + col))
                            found.Add((row, col));
                Assert.Equal(
                    QrFunctionPatterns.AlignmentCentres(version).OrderBy(c => c).ToList(),
                    found.OrderBy(c => c).ToList());
            }
        }

        [Fact]
        public void The_real_matrix_timing_matches_the_classified_band()
        {
            // ISO 18004: the timing pattern alternates dark/light along row
            // 6 and column 6 between the finders, always dark at the ends.
            for (var version = 1; version <= 40; version++)
            {
                int n = CellsFor(version);
                int q = QrSketchInfo.QuietZoneCellsPerSide;
                var matrix = EncodeWorkingMatrix("HELLO", version);
                for (var k = 8; k < n - 8; k++)
                {
                    Assert.True(QrFunctionPatterns.IsFunctionCell(k, 6, n, version));
                    Assert.True(QrFunctionPatterns.IsFunctionCell(6, k, n, version));
                    Assert.Equal(k % 2 == 0, matrix[q + 6, q + k]);
                    Assert.Equal(k % 2 == 0, matrix[q + k, q + 6]);
                }
            }
        }

        [Fact]
        public void The_real_matrix_finders_fill_the_classified_boxes()
        {
            int n = CellsFor(1);
            int q = QrSketchInfo.QuietZoneCellsPerSide;
            var matrix = EncodeWorkingMatrix("HELLO", 1);
            // The 7×7 finder motif (dark ring, light band, dark 3×3) sits in
            // the classified 8×8 box; the separator ring is light.
            for (var row = 0; row < 8; row++)
                for (var col = 0; col < 8; col++)
                {
                    Assert.True(QrFunctionPatterns.IsFunctionCell(row, col, n, 1));
                    if (row == 7 || col == 7)
                        Assert.False(matrix[q + row, q + col]);   // separator is always light
                    else
                    {
                        int norm = System.Math.Max(
                            System.Math.Abs(row - 3), System.Math.Abs(col - 3));
                        Assert.Equal(norm == 3 || norm <= 1, matrix[q + row, q + col]);
                    }
                }
        }

        // ---------------- fixture-seam inertness ----------------

        private static bool[,] Cells(int n, params (int row, int col)[] dark)
        {
            var cells = new bool[n, n];
            foreach (var (row, col) in dark)
                cells[row, col] = true;
            return cells;
        }

        private static void AssertSameEdges(QrSketch expected, QrSketch actual)
        {
            var a = expected.Edges.ToList();
            var b = actual.Edges.ToList();
            Assert.Equal(a.Count, b.Count);
            for (var i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].Start.X, b[i].Start.X);
                Assert.Equal(a[i].Start.Y, b[i].Start.Y);
                Assert.Equal(a[i].End.X, b[i].End.X);
                Assert.Equal(a[i].End.Y, b[i].End.Y);
            }
        }

        [Fact]
        public void The_carve_out_is_inert_at_the_deterministic_fixture_seam()
        {
            // Version 0 fixtures classify no function cells — the flag is
            // accepted but changes nothing, so existing fixtures are
            // unaffected whichever way they pass it.
            var cells = Cells(21, (10, 10));
            var off = QrSketchGenerator.GenerateFromCells(
                cells, 50.0, Growth, invert: false,
                cellShape: QrCellShape.Circle, cellShapeParameter: 0.5,
                keepFunctionPatternsSquare: false).Sketch!;
            var on = QrSketchGenerator.GenerateFromCells(
                cells, 50.0, Growth, invert: false,
                cellShape: QrCellShape.Circle, cellShapeParameter: 0.5,
                keepFunctionPatternsSquare: true).Sketch!;
            AssertSameEdges(off, on);
        }

        // ---------------- generator seam: the carve-out per shape ----------------

        private static QrSketchRequest CarveOutRequest(
            QrCellShape shape, double shapeParameter, double sizeMm = SizeMm,
            bool invert = false, int? version = null) =>
            new QrSketchRequest("HELLO", sizeMm, Growth)
            {
                CellShape = shape,
                CellShapeParameter = shapeParameter,
                Invert = invert,
                KeepFunctionPatternsSquare = true,
                RequestedVersion = version,
            };

        /// <summary>The 4-vertex contour spanning whole cells the square primitive leaves.</summary>
        private static bool IsCellSpanningSquare(
            QrContour contour, double xMin, double yMax, double cellsAcross, double pitchM) =>
            contour.Vertices.Count == 4 &&
            System.Math.Abs(contour.Vertices.Min(v => v.X) - xMin) < 1e-9 &&
            System.Math.Abs(contour.Vertices.Max(v => v.Y) - yMax) < 1e-9 &&
            System.Math.Abs(
                (contour.Vertices.Max(v => v.X) - contour.Vertices.Min(v => v.X))
                - cellsAcross * pitchM) < 1e-9;

        [Fact]
        public void Under_square_the_carve_out_is_byte_identical_to_off()
        {
            // Under Square every emitted cell is already the grown square —
            // the flag can change nothing.
            var off = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth)).Sketch!;
            var on = QrSketchGenerator.Generate(new QrSketchRequest(Payload, SizeMm, Growth)
            {
                KeepFunctionPatternsSquare = true,
            }).Sketch!;

            AssertSameEdges(off, on);
        }

        [Fact]
        public void Under_circle_function_cells_emit_squares_data_cells_emit_dots()
        {
            // Version 2, 50 mm: pitch 2 mm; the top-left finder's 7×7 ring
            // unions into a clean square outline exactly 7 cells across with
            // its top-left corner on the QR square corner (−S/2, +S/2).
            var result = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.Circle, 0.5, sizeMm: 50.0, version: 2));

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.Equal(2, sketch.Info.Version);
            double pitchM = sketch.Info.PitchMm / 1000.0;
            double half = 25.0 / 1000.0;

            // The finder ring: one 4-vertex region outline spanning exactly
            // 7 cells, containing the band hole and the 3×3 centre island —
            // the conventional finder structure amid a field of dots.
            var finder = sketch.Regions.SingleOrDefault(r =>
                IsCellSpanningSquare(r.Boundary, -half, half, 7, pitchM));
            Assert.NotNull(finder);
            var band = Assert.Single(finder!.Holes);
            var centre = Assert.Single(band.Islands);
            Assert.Empty(centre.Holes);

            // Data cells still emit dots — multi-vertex polygon circles.
            Assert.Contains(sketch.Contours, c => c.Vertices.Count > 4);
            Assert.Equal(QrCellShape.Circle, sketch.Info.CellShape);
        }

        [Fact]
        public void Without_the_carve_out_circle_emits_no_square_at_all()
        {
            var result = QrSketchGenerator.Generate(new QrSketchRequest("HELLO", 50.0, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.5,
                RequestedVersion = 2,
            });

            Assert.True(result.Succeeded, result.Error?.Message);
            // Every emitted cell is a dot — no 4-vertex primitive anywhere.
            Assert.All(result.Sketch!.Contours, c => Assert.True(c.Vertices.Count > 4));
        }

        [Fact]
        public void Under_circle_the_function_square_is_the_bare_cell()
        {
            // Growth never applies under Circle (ticket #33's uniform rule):
            // the lattice is the growth-0 map, so the carved-out square is
            // the ungrown cell — adjacent function squares share edges and
            // union into the conventional pattern blocks, while every dot
            // stays strictly inside its own cell and can never touch one.
            var sketch = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.Circle, 0.5, sizeMm: 50.0, version: 2)).Sketch!;
            double pitchM = sketch.Info.PitchMm / 1000.0;

            // The alignment pattern at centre (18,18): its emitted 5×5 ring
            // unions into a square outline exactly 5 bare cells across —
            // g = 0, not the grown 5 + 2r cells the square lattice draws.
            double left = -25.0 / 1000.0 + (18 - 2) * pitchM;
            double top = 25.0 / 1000.0 - (18 - 2) * pitchM;
            Assert.Contains(sketch.Contours,
                c => IsCellSpanningSquare(c, left, top, 5, pitchM));
            // No grown outline exists anywhere: the widest cell-aligned
            // square stays inside the 8-cell finder box + nothing.
            Assert.Equal(0.0, sketch.Info.GrowthMmPerSide, 12);
        }

        /// <summary>
        /// Every island sitting inside a region's holes — one nesting level
        /// (a finder's 3×3 centre inside its band hole), not a recursive
        /// walk.
        /// </summary>
        private static IEnumerable<QrRegion> IslandsOf(QrRegion region) =>
            region.Holes.SelectMany(h => h.Islands);

        [Fact]
        public void Under_rounded_function_cells_emit_sharp_squares_data_cells_round()
        {
            // 50 mm / 3% (the groove regime the other rounded tests use;
            // 11% clears the merge-groove band at this pitch).
            var result = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.RoundedSquare, 0.11, sizeMm: 50.0, version: 2));

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            double pitchM = sketch.Info.PitchMm / 1000.0;
            double grownM = sketch.Info.GrowthMmPerSide / 1000.0;
            double half = 25.0 / 1000.0;

            // The top-left finder's 3×3 centre sits behind the un-emitted
            // light band, so it is always an isolated island: under the
            // carve-out its boundary is the grown square — 4 sharp
            // vertices spanning 3 cells + 2g — not the arc-faceted outline
            // a rounded 3×3 block leaves.
            double left = -half + 2 * pitchM;
            double top = half - 2 * pitchM;
            Assert.Contains(sketch.Regions.SelectMany(IslandsOf),
                island => IsCellSpanningSquare(
                    island.Boundary, left, top, 3 + 2 * grownM / pitchM, pitchM));
            // Data cells still emit the rounded primitive — arc-faceted.
            Assert.Contains(sketch.Contours, c => c.Vertices.Count > 4);
        }

        [Fact]
        public void The_carve_out_composes_with_invert_under_circle()
        {
            // Under invert the emitted polarity flips but the classification
            // does not move: LIGHT cells at function positions emit squares
            // (the separator and timing bands merge into real shapes) while
            // light data cells emit disjoint dot pits.
            var result = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.Circle, 0.5, sizeMm: 50.0,
                    invert: true, version: 2));

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            double pitchM = sketch.Info.PitchMm / 1000.0;

            // The timing cell (6,9) is light in every QR — under invert it
            // emits a bare-cell square pit exactly one cell across, boxed in
            // by dark timing cells and data dots: a shape no dot can form.
            double left = -25.0 / 1000.0 + 9 * pitchM;
            double top = 25.0 / 1000.0 - 6 * pitchM;
            Assert.Contains(sketch.Regions,
                r => IsCellSpanningSquare(r.Boundary, left, top, 1, pitchM));
            // Dot pits still cover the data cells — and function squares
            // merge where dots stayed disjoint, so the count drops.
            Assert.Contains(sketch.Contours, c => c.Vertices.Count > 4);
        }

        [Fact]
        public void No_version_has_a_function_square_corner_contact()
        {
            // The carve-out's only new failure class is a residual
            // corner-only contact: two EMITTED squares meeting diagonally
            // with nothing filling the corner. A data-cell dot can never
            // fill or cause one (it stays strictly inside its own cell), so
            // a contact needs two emitting function cells diagonal with
            // NEITHER orthogonal filler an emitting function cell — a pure
            // mask/matrix property, provable for all 40 versions against
            // the real encoder in both polarities. (Generation at high
            // versions is minutes of Clipper work; this is the exhaustive
            // check — a handful of end-to-end versions runs separately.)
            for (var version = 1; version <= 40; version++)
            {
                int n = CellsFor(version);
                int q = QrSketchInfo.QuietZoneCellsPerSide;
                var matrix = EncodeWorkingMatrix("HELLO", version);
                var mask = QrFunctionPatterns.FunctionMask(n, version)!;
                Assert.NotNull(mask);

                foreach (var invert in new[] { false, true })
                {
                    bool Emits(int r, int c) =>
                        r >= 0 && c >= 0 && r < n && c < n &&
                        matrix[q + r, q + c] != invert;
                    for (var r = 0; r + 1 < n; r++)
                        for (var c = 0; c + 1 < n; c++)
                            foreach (var (dr, dc) in new[] { (1, 1), (1, -1) })
                            {
                                int r2 = r + dr, c2 = c + dc;
                                if (r2 < 0 || c2 < 0 || r2 >= n || c2 >= n)
                                    continue;
                                if (!mask[r, c] || !mask[r2, c2] ||
                                    !Emits(r, c) || !Emits(r2, c2))
                                    continue;
                                // The corner is filled iff an orthogonal
                                // neighbour emits a function square too.
                                bool filled =
                                    mask[r, c2] && Emits(r, c2) ||
                                    mask[r2, c] && Emits(r2, c);
                                Assert.True(filled,
                                    $"v{version} invert={invert}: emitting function " +
                                    $"cells ({r},{c}) and ({r2},{c2}) meet at a bare corner");
                            }
                }
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        public void The_carve_out_generates_at_every_probed_version(int version)
        {
            // End-to-end proof at bounded versions (high-version Clipper
            // work is too slow for the suite; the exhaustive position and
            // corner-contact checks above cover the table).
            foreach (var invert in new[] { false, true })
            {
                // 25 mm keeps the inverted run under the ticket-#35 segment
                // ceiling: the whole working matrix emits disjoint dots
                // (~1 600 pits at v7), so the delivered total is (light
                // cells) × K — a 60 mm cell buys K = 6 ≈ 9 654 segments,
                // over the 8 000 budget; at 25 mm K = 4 lands ~6 400.
                var result = QrSketchGenerator.Generate(
                    CarveOutRequest(QrCellShape.Circle, 0.5, sizeMm: 25.0,
                        invert: invert, version: version));
                Assert.True(result.Succeeded,
                    $"v{version} invert={invert}: {result.Error?.Message}");
            }
        }

        [Fact]
        public void The_carve_out_composes_with_rounded_under_invert()
        {
            var result = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.RoundedSquare, 0.11, sizeMm: 50.0,
                    invert: true, version: 2));

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.True(sketch.Info.Inverted);
            // Square emissions in the function zones amid the rounded field.
            Assert.Contains(sketch.Contours, c => c.Vertices.Count == 4);
            Assert.Contains(sketch.Contours, c => c.Vertices.Count > 4);
        }

        // ---------------- the keepFn × Circle square–dot channel ----------------

        [Fact]
        public void The_dot_cap_tightens_where_function_squares_can_neighbour_dots()
        {
            // 50 mm / v2 (N = 25): the insertion floor is F = 250 grid
            // units, so the plain dot cap sits at f ≤ 1−(F+M)/u ≈ 97.5%.
            // But a LIVE carve-out emits bare-cell function squares: a dot
            // beside one leaves a light channel of u·(1−f)/2 — HALF the
            // dot–dot gap — so the cap tightens to f ≤ 1−2(F+M)/u ≈ 94.96%.
            // f = 0.96 fits the dots-only bound but not the carve-out one.
            var withoutCarveOut = QrSketchGenerator.Generate(new QrSketchRequest(
                "HELLO", 50.0, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.96,
                RequestedVersion = 2,
            });
            var withCarveOut = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.Circle, 0.96, sizeMm: 50.0, version: 2));

            Assert.True(withoutCarveOut.Succeeded, withoutCarveOut.Error?.Message);
            Assert.False(withCarveOut.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, withCarveOut.Error!.Kind);
            Assert.Contains("function", withCarveOut.Error.Message,
                System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_dot_size_under_the_tightened_cap_stays_accepted()
        {
            // f = 0.94 leaves a square–dot channel of u·(1−f)/2 = 300
            // units — over the F + M = 252 floor at 50 mm / v2.
            var result = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.Circle, 0.94, sizeMm: 50.0, version: 2));

            Assert.True(result.Succeeded, result.Error?.Message);
        }

        [Fact]
        public void The_square_dot_adjacency_the_cap_guards_is_reachable_in_both_polarities()
        {
            // The tightened cap guards a REAL layout, not a hypothetical:
            // an emitting function cell (bare square) orthogonally beside
            // an emitting data cell (dot) leaves exactly the u·(1−f)/2
            // channel. Normal mode needs a dark·dark pair — the dark
            // timing cells supply the function side; invert needs a
            // light·light pair — the always-light separator ring emits
            // square pits beside light data cells. Scan the real encoded
            // matrix (classification is positional, never by darkness).
            int n = CellsFor(2);
            int q = QrSketchInfo.QuietZoneCellsPerSide;
            var matrix = EncodeWorkingMatrix("HELLO", 2);
            var mask = QrFunctionPatterns.FunctionMask(n, 2)!;

            bool AdjacentPairExists(bool emittedWhenDark)
            {
                for (var r = 0; r < n; r++)
                    for (var c = 0; c < n; c++)
                        foreach (var (dr, dc) in new[] { (1, 0), (0, 1) })
                        {
                            int r2 = r + dr, c2 = c + dc;
                            if (r2 >= n || c2 >= n || mask[r, c] == mask[r2, c2])
                                continue;   // need exactly one function cell
                            if (matrix[q + r, q + c] == emittedWhenDark &&
                                matrix[q + r2, q + c2] == emittedWhenDark)
                                return true;
                        }
                return false;
            }

            Assert.True(AdjacentPairExists(true),
                "normal polarity: a dark data cell beside a dark function square");
            Assert.True(AdjacentPairExists(false),
                "inverted polarity: a light data cell beside a light function square");
        }

        [Fact]
        public void The_light_gap_info_reports_the_halved_channel_under_the_live_carve_out()
        {
            // Under keepFn + Circle the narrowest un-emitted channel is the
            // square–dot one — u·(1−f)/2. At 50 mm / f = 0.9 that is 500
            // grid units = 0.1 mm, half the 0.2 mm dot–dot figure the
            // dots-only report gives.
            var sketch = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.Circle, 0.9, sizeMm: 50.0, version: 2)).Sketch!;

            Assert.Equal(0.1, sketch.Info.MinLightGapMm, 6);
        }

        [Fact]
        public void An_over_cap_dot_size_with_the_carve_out_rejects_before_the_host()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(host,
                CarveOutRequest(QrCellShape.Circle, 0.96, sizeMm: 50.0, version: 2));

            Assert.False(result.Succeeded);
            Assert.Empty(host.Document!.Sketches);
        }

        [Fact]
        public void The_light_channel_bound_is_not_halved_for_rounded_cells()
        {
            // keepFn × RoundedSquare needs no cap change: the carved-out
            // function square is the same GROWN cell every emitted cell is
            // — both sides of a shared boundary reach ±g, and a rounded
            // cell's corner arcs only recede INWARD, so the narrowest
            // emitted↔emitted channel stays u−2g (unlike Circle's bare
            // u×u function square). The info value keeps reporting it.
            var sketch = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.RoundedSquare, 0.11, sizeMm: 50.0, version: 2))
                .Sketch!;

            Assert.Equal(sketch.Info.PitchMm - 2 * sketch.Info.GrowthMmPerSide,
                sketch.Info.MinLightGapMm, 9);
        }

        // ---------------- quiet zone and keep-out unchanged ----------------

        [Fact]
        public void The_construction_squares_are_identical_with_the_carve_out()
        {
            var off = QrSketchGenerator.Generate(new QrSketchRequest("HELLO", 50.0, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.5,
                RequestedVersion = 2,
            }).Sketch!;
            var on = QrSketchGenerator.Generate(
                CarveOutRequest(QrCellShape.Circle, 0.5, sizeMm: 50.0, version: 2)).Sketch!;

            Assert.Equal(off.ConstructionSquares.Count, on.ConstructionSquares.Count);
            for (var i = 0; i < off.ConstructionSquares.Count; i++)
            {
                var a = off.ConstructionSquares[i];
                var b = on.ConstructionSquares[i];
                Assert.Equal(a.Role, b.Role);
                Assert.Equal(a.Vertices.Count, b.Vertices.Count);
                for (var j = 0; j < a.Vertices.Count; j++)
                {
                    Assert.Equal(a.Vertices[j].X, b.Vertices[j].X);
                    Assert.Equal(a.Vertices[j].Y, b.Vertices[j].Y);
                }
            }
        }

        // ---------------- preview parity ----------------

        [Fact]
        public void The_preview_shows_the_carve_out_through_the_same_render_path()
        {
            var off = QrSketchGenerator.Generate(new QrSketchRequest(Payload, 50.0, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.5,
            }).Sketch!;
            var on = QrSketchGenerator.Generate(new QrSketchRequest(Payload, 50.0, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.5,
                KeepFunctionPatternsSquare = true,
            }).Sketch!;

            var offPic = QrPictureRenderer.Render(off, Pixels);
            var onPic = QrPictureRenderer.Render(on, Pixels);

            Assert.Equal(offPic.MmPerPixel, onPic.MmPerPixel, 12);
            Assert.NotEqual(offPic.Signature(), onPic.Signature());
            Assert.True(onPic.DarkPixelCount > offPic.DarkPixelCount,
                "square finders and timing mark more material than dots there");
        }

        // ---------------- the session seam ----------------

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

        private static string InfoValue(QrSketchSession session, string label)
        {
            foreach (var line in session.InfoLines)
                if (line.Label == label)
                    return line.Value;
            throw new System.Exception($"no info line labelled '{label}'");
        }

        [Fact]
        public void The_request_defaults_the_carve_out_off()
        {
            Assert.False(new QrSketchRequest(Payload, SizeMm, Growth)
                .KeepFunctionPatternsSquare);
        }

        [Fact]
        public void A_fresh_session_has_the_carve_out_off()
        {
            var (session, _) = NewSession();

            Assert.False(session.KeepFunctionPatternsSquare);
        }

        [Fact]
        public void SetKeepFunctionPatternsSquare_revalidates_and_syncs_the_window()
        {
            var (session, windows) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");
            session.OpenOrFocusWindow();
            var dotsSignature = session.Picture!.Signature();
            var syncs = windows[0].SyncCount;

            var result = session.SetKeepFunctionPatternsSquare(true);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(session.KeepFunctionPatternsSquare);
            Assert.NotNull(session.Picture);
            Assert.NotEqual(dotsSignature, session.Picture!.Signature());
            Assert.True(windows[0].SyncCount > syncs);
        }

        [Fact]
        public void Toggling_the_carve_out_back_restores_the_dots_output()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");
            var dotsSignature = session.Picture!.Signature();

            session.SetKeepFunctionPatternsSquare(true);
            session.SetKeepFunctionPatternsSquare(false);

            Assert.False(session.KeepFunctionPatternsSquare);
            Assert.Equal(dotsSignature, session.Picture!.Signature());
        }

        [Fact]
        public void The_carve_out_without_valid_content_stays_invalid_but_the_setting_sticks()
        {
            var (session, _) = NewSession();

            var result = session.SetKeepFunctionPatternsSquare(true);

            Assert.False(result.Succeeded);
            Assert.Null(session.Picture);
            Assert.True(session.KeepFunctionPatternsSquare);
        }

        [Fact]
        public void The_info_line_reports_the_carve_out()
        {
            var (session, _) = NewSession();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");

            session.SetKeepFunctionPatternsSquare(true);

            var value = InfoValue(session, "Function patterns");
            Assert.Contains("square", value, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Create_inserts_the_carve_out_geometry()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");
            session.SetKeepFunctionPatternsSquare(true);

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(new QrSketchRequest(
                    Payload, SizeMm, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.5,
                KeepFunctionPatternsSquare = true,
            }).Sketch!;
            Assert.Equal(expected.Info.SegmentCount, sketch.ProfileSegments.Count());
        }

        [Fact]
        public void The_carve_out_applies_to_future_creates_only()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(Payload);
            session.SetCellShape(QrCellShape.Circle);
            session.SetShapeParameterText("50");
            session.CreateSketch(host);
            host.Document!.ActiveSketch = null;
            var dotsSegs = host.Document.Sketches[0].ProfileSegments.Count();

            session.SetKeepFunctionPatternsSquare(true);
            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var expected = QrSketchGenerator.Generate(new QrSketchRequest(
                    Payload, SizeMm, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.5,
                KeepFunctionPatternsSquare = true,
            }).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document.Sketches[1].ProfileSegments.Count());
            Assert.Equal(dotsSegs, host.Document.Sketches[0].ProfileSegments.Count());
        }

        [Fact]
        public void The_command_passes_the_carve_out_through_to_insertion()
        {
            var host = HostWithSelectedPlane();
            var request = new QrSketchRequest(Payload, SizeMm, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.5,
                KeepFunctionPatternsSquare = true,
            };

            var result = new CreateQrSketchCommand().Execute(host, request);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(request).Sketch!;
            Assert.Equal(expected.Info.SegmentCount, sketch.ProfileSegments.Count());
        }
    }
}
