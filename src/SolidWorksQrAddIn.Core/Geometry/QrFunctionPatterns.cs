using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The function-pattern classifier of spec #13, ticket #34: which
    /// data-matrix positions are the QR's structural patterns — the three
    /// corner finders, the timing bands, and the per-version alignment
    /// patterns — so the generator can keep them on the expanded-square
    /// primitive while data cells take the selected shape
    /// (<see cref="QrSketchRequest.KeepFunctionPatternsSquare"/>). Pure
    /// matrix-position logic: no SolidWorks types, no cell darkness —
    /// classification is by position, so under invert the same positions
    /// emit square pits.
    ///
    /// Boundary decisions (the ticket names finder/alignment/timing):
    ///
    ///   - Finder positions are the full 8×8 corner boxes — the 7×7 finder
    ///     plus its one-cell separator ring on the data-facing sides. The
    ///     separator is required whitespace of the finder structure, not
    ///     payload: including it keeps the whole finder region conventional
    ///     under invert (light cells there emit square pits like the rest
    ///     of the region).
    ///   - Format- and version-information cells are NOT function
    ///     positions. They carry payload data rather than repeating a
    ///     structural motif; they take the selected shape like any other
    ///     data cell.
    ///   - Timing positions are the whole of row 6 and column 6 — the
    ///     bands between the finders (their ends already lie inside the
    ///     finder boxes, so the full row/column classifies identically).
    ///   - Alignment positions are the 5×5 boxes centred on the version's
    ///     centre list: the Cartesian product of the ISO 18004 normative
    ///     coordinate row for that version, minus every centre whose 5×5
    ///     footprint would overlap a finder pattern (the standard's overlap
    ///     rule — for every version it drops exactly the three
    ///     finder-coincident centres (6,6), (6,N−7), (N−7,6)).
    ///
    /// Version 0 — the <see cref="QrSketchGenerator.GenerateFromCells"/>
    /// fixture seam — has no function cells: arbitrary fixture matrices are
    /// not real QR matrices, so the carve-out is inert there and existing
    /// deterministic fixtures are unaffected. A version/dataCells mismatch
    /// classifies nothing too — N = 17 + 4·version is fixed, so a mismatch
    /// can only be a caller bug; inert beats wrong.
    /// </summary>
    public static class QrFunctionPatterns
    {
        /// <summary>
        /// The ISO/IEC 18004 normative alignment-centre coordinate rows,
        /// indexed [version − 1] for versions 1–40. The alignment pattern
        /// centres are the Cartesian product of a version's row with
        /// itself, minus the finder-overlapping positions. (QRCoder 1.8.0
        /// keeps its copy private, so the standard table is embedded here.)
        /// </summary>
        private static readonly int[][] CoordinateRows =
        {
            new int[0],                          // v1 — no alignment patterns
            new[] { 6, 18 },                     // v2
            new[] { 6, 22 },                     // v3
            new[] { 6, 26 },                     // v4
            new[] { 6, 30 },                     // v5
            new[] { 6, 34 },                     // v6
            new[] { 6, 22, 38 },                 // v7
            new[] { 6, 24, 42 },                 // v8
            new[] { 6, 26, 46 },                 // v9
            new[] { 6, 28, 50 },                 // v10
            new[] { 6, 30, 54 },                 // v11
            new[] { 6, 32, 58 },                 // v12
            new[] { 6, 34, 62 },                 // v13
            new[] { 6, 26, 46, 66 },             // v14
            new[] { 6, 26, 48, 70 },             // v15
            new[] { 6, 26, 50, 74 },             // v16
            new[] { 6, 30, 54, 78 },             // v17
            new[] { 6, 30, 56, 82 },             // v18
            new[] { 6, 30, 58, 86 },             // v19
            new[] { 6, 34, 62, 90 },             // v20
            new[] { 6, 28, 50, 72, 94 },         // v21
            new[] { 6, 26, 50, 74, 98 },         // v22
            new[] { 6, 30, 54, 78, 102 },        // v23
            new[] { 6, 28, 54, 80, 106 },        // v24
            new[] { 6, 32, 58, 84, 110 },        // v25
            new[] { 6, 30, 58, 86, 114 },        // v26
            new[] { 6, 34, 62, 90, 118 },        // v27
            new[] { 6, 26, 50, 74, 98, 122 },    // v28
            new[] { 6, 30, 54, 78, 102, 126 },   // v29
            new[] { 6, 26, 52, 78, 104, 130 },   // v30
            new[] { 6, 30, 56, 82, 108, 134 },   // v31
            new[] { 6, 34, 60, 86, 112, 138 },   // v32
            new[] { 6, 30, 58, 86, 114, 142 },   // v33
            new[] { 6, 34, 62, 90, 118, 146 },   // v34
            new[] { 6, 30, 54, 78, 102, 126, 150 },   // v35
            new[] { 6, 24, 50, 76, 102, 128, 154 },   // v36
            new[] { 6, 28, 54, 80, 106, 132, 158 },   // v37
            new[] { 6, 32, 58, 84, 110, 136, 162 },   // v38
            new[] { 6, 26, 54, 82, 110, 138, 166 },   // v39
            new[] { 6, 30, 58, 86, 114, 142, 170 },   // v40
        };

        /// <summary>
        /// True when (dataCells, version) is a real QR pairing —
        /// N = 17 + 4·version for versions 1–40. Fixture matrices report
        /// version 0 and classify nothing.
        /// </summary>
        private static bool IsRealVersion(int dataCells, int version) =>
            version >= 1 && version <= CoordinateRows.Length &&
            dataCells == 17 + 4 * version;

        /// <summary>
        /// The alignment-pattern centres for <paramref name="version"/> in
        /// data-matrix coordinates, row-major: the Cartesian product of the
        /// version's normative coordinate row with itself, minus every
        /// centre whose 5×5 footprint overlaps a 7×7 finder pattern
        /// (ISO 18004's overlap rule — the three corner-coincident centres
        /// for every version). Empty for version 1 and for fixture
        /// versions.
        /// </summary>
        public static IReadOnlyList<(int row, int col)> AlignmentCentres(int version)
        {
            var centres = new List<(int row, int col)>();
            if (version < 1 || version > CoordinateRows.Length)
                return centres;
            int n = 17 + 4 * version;
            int[] coords = CoordinateRows[version - 1];
            foreach (int cr in coords)
                foreach (int cc in coords)
                    if (!OverlapsFinder(cr, cc, n))
                        centres.Add((cr, cc));
            return centres;
        }

        /// <summary>
        /// Does the 5×5 alignment pattern centred at (row, col) reach into
        /// one of the three 7×7 finder boxes — the overlap the standard
        /// excludes.
        /// </summary>
        private static bool OverlapsFinder(int row, int col, int n) =>
            // The centre's 5×5 footprint is [r−2, r+3); a finder box is
            // [0,7) or [N−7,N) on each side — half-open range intersection.
            row - 2 < 7 && col - 2 < 7 ||
            row - 2 < 7 && col + 3 > n - 7 ||
            row + 3 > n - 7 && col - 2 < 7;

        /// <summary>
        /// Is the data-matrix position (row, col) a function-pattern cell —
        /// inside a finder box (8×8, separator included), on the timing
        /// bands, or inside an alignment pattern's 5×5. Positions are
        /// data-matrix coordinates on the N×N payload — working-matrix
        /// callers subtract the quiet-zone offset. Always false for
        /// fixture/unknown versions and out-of-range positions.
        /// </summary>
        public static bool IsFunctionCell(int row, int col, int dataCells, int version)
        {
            if (!IsRealVersion(dataCells, version) ||
                row < 0 || col < 0 || row >= dataCells || col >= dataCells)
                return false;

            // Timing bands — the whole row/column (the ends lie inside the
            // finder boxes anyway).
            if (row == 6 || col == 6)
                return true;

            // Finder boxes: the 7×7 finder plus its one-cell separator ring
            // on the data-facing sides — 8×8 at three corners.
            if (row < 8 && col < 8 ||
                row < 8 && col >= dataCells - 8 ||
                row >= dataCells - 8 && col < 8)
                return true;

            // Alignment patterns: a 5×5 around each centre.
            foreach (var (cr, cc) in AlignmentCentres(version))
                if (row - cr >= -2 && row - cr <= 2 &&
                    col - cc >= -2 && col - cc <= 2)
                    return true;
            return false;
        }

        /// <summary>
        /// The N×N function-position mask for one generation pass — null
        /// when (dataCells, version) is not a real QR pairing (fixture
        /// version 0, out-of-range version, size mismatch), so the
        /// carve-out is inert exactly where the classifier has nothing
        /// true to say.
        /// </summary>
        public static bool[,]? FunctionMask(int dataCells, int version)
        {
            if (!IsRealVersion(dataCells, version))
                return null;
            var mask = new bool[dataCells, dataCells];
            for (var row = 0; row < dataCells; row++)
                for (var col = 0; col < dataCells; col++)
                    mask[row, col] = IsFunctionCell(row, col, dataCells, version);
            return mask;
        }
    }
}
