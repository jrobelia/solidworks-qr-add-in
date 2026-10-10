using System;
using System.Collections;
using System.Collections.Generic;
using Clipper2Lib;
using QRCoder;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The host-independent geometry-generation module: QR content + final
    /// outline size + per-side growth in, validated closed contours with
    /// nesting and size info out — or one actionable error.
    ///
    /// Pipeline (spec "Geometry generation"):
    ///   1. encode exactly — standard QR, automatic version, ECC M;
    ///   2. strip the encoder's 4-cell quiet-zone padding (N = Count − 8);
    ///   3. pitch p = S/(N + 2r′), grown-cell pitch on a normalized integer
    ///      grid of <see cref="QrInsertionGuard.GridUnitsPerCell"/> units/cell;
    ///   4. expand every dark cell by g = round(r·U) units on all four sides;
    ///   5. union (Clipper2 NonZero) into a PolyTree64 — outer/hole/island
    ///      nesting preserved;
    ///   6. strip redundant collinear vertices (epsilon 0);
    ///   7. validate on the grid: nonzero area, no zero-length edges, no
    ///      self-intersections, no residual corner-only contacts;
    ///   8. convert to physical sketch metres — a pure affine map, so shared
    ///      endpoints stay bit-identical — centred on the origin, matrix row 0
    ///      at +y so the QR is upright rather than mirrored.
    ///
    /// Contains no SolidWorks objects.
    /// </summary>
    public static class QrSketchGenerator
    {
        /// <summary>
        /// Encodes <paramref name="content"/> exactly (standard QR, automatic
        /// version selection, error correction M) and generates the grown,
        /// unioned contour set at <paramref name="sizeMm"/> final outline width.
        /// </summary>
        public static QrSketchResult Generate(string content, double sizeMm, double growthFractionPerSide)
        {
            if (string.IsNullOrEmpty(content))
                return QrSketchResult.Fail(QrSketchErrorKind.EmptyContent,
                    "QR content is empty. Enter the text or URL to encode.");

            bool[,] cells;
            int version;
            try
            {
                using (var generator = new QRCodeGenerator())
                using (var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M))
                {
                    // ModuleMatrix INCLUDES a 4-cell quiet zone on every side —
                    // strip it: data cells live at indices [4, Count−4).
                    var matrix = data.ModuleMatrix;
                    int n = matrix.Count - 8;
                    cells = new bool[n, n];
                    for (var row = 0; row < n; row++)
                    {
                        BitArray bits = matrix[row + 4];
                        for (var col = 0; col < n; col++)
                            cells[row, col] = bits[col + 4];
                    }
                    version = data.Version;
                }
            }
            catch (QRCoder.Exceptions.DataTooLongException ex)
            {
                return QrSketchResult.Fail(QrSketchErrorKind.PayloadTooLong,
                    $"The content does not fit in a standard QR code at error correction M: {ex.Message} " +
                    "Shorten the text or URL.");
            }
            catch (Exception ex)
            {
                return QrSketchResult.Fail(QrSketchErrorKind.EncoderFailure,
                    $"The QR encoder rejected the content: {ex.Message}");
            }

            return Build(cells, sizeMm, growthFractionPerSide, version);
        }

        /// <summary>
        /// The same pipeline fed directly from a dark-cell matrix (no encoder):
        /// <paramref name="darkCells"/>[row, col] == true means dark, indexed
        /// from the top-left data cell. Deterministic-fixture seam for tests;
        /// the encoded path funnels through it after stripping the quiet zone.
        /// </summary>
        public static QrSketchResult GenerateFromCells(bool[,] darkCells, double sizeMm, double growthFractionPerSide)
        {
            if (darkCells == null)
                throw new ArgumentNullException(nameof(darkCells));
            if (darkCells.GetLength(0) != darkCells.GetLength(1))
                throw new ArgumentException("The QR cell matrix must be square.", nameof(darkCells));
            return Build(darkCells, sizeMm, growthFractionPerSide, version: 0);
        }

        private static QrSketchResult Build(bool[,] cells, double sizeMm, double growthFraction, int version)
        {
            int n = cells.GetLength(0);
            int u = QrInsertionGuard.GridUnitsPerCell;

            // ---------- input validation ----------
            if (double.IsNaN(sizeMm) || double.IsInfinity(sizeMm) || sizeMm <= 0.0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidSize,
                    $"Size must be a finite positive number of millimetres; got '{sizeMm}'.");
            if (double.IsNaN(growthFraction) || double.IsInfinity(growthFraction) ||
                growthFraction <= 0.0 || growthFraction >= 0.5)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGrowth,
                    $"Outline growth must be a finite fraction in the open interval (0, 0.5) " +
                    $"— i.e. more than 0% and less than 50% of cell width per side; got '{growthFraction}'.");

            // ---------- normalized integer grid ----------
            long g = (long)Math.Round(growthFraction * u);
            if (g < 1)
                return QrSketchResult.Fail(QrSketchErrorKind.GrowthRoundsToZero,
                    $"Outline growth {growthFraction:P2} of cell width is smaller than one grid unit " +
                    $"({1.0 / u:P2}) and rounds away — diagonal cells would keep corner-only contacts. " +
                    "Increase the growth percentage.");
            if (2 * g >= u)
                return QrSketchResult.Fail(QrSketchErrorKind.GrowthClosesGaps,
                    $"Outline growth {growthFraction:P2} rounds to {g} grid units/side — at or beyond half a " +
                    "cell, it would close every one-cell light channel. Reduce the growth percentage.");

            long minGridFeature = Math.Min(2 * g, u - 2 * g);
            if (minGridFeature < QrInsertionGuard.MinGridFeatureUnits)
                return QrSketchResult.Fail(QrSketchErrorKind.BelowInsertionLimit,
                    $"With growth {growthFraction:P2} the smallest feature spans {minGridFeature} of " +
                    $"{u} grid units — below the {QrInsertionGuard.MinGridFeatureUnits}-unit resolution " +
                    "floor (Clipper2 integer noise is ±1 unit). Adjust growth into a resolvable range.");

            // ---------- physical guard (recorded insertion limit) ----------
            double mmPerUnit = sizeMm / ((double)n * u + 2 * g);
            double minFeatureMm = minGridFeature * mmPerUnit;
            if (minFeatureMm < QrInsertionGuard.MinInsertableFeatureMm)
                return QrSketchResult.Fail(QrSketchErrorKind.BelowInsertionLimit,
                    $"Smallest feature would be {minFeatureMm:F4} mm — below the recorded " +
                    $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion guard. Increase the size " +
                    "or adjust the growth percentage.");

            // ---------- expand dark cells + union ----------
            var subject = new Paths64();
            int darkCount = 0;
            for (var row = 0; row < n; row++)
                for (var col = 0; col < n; col++)
                {
                    if (!cells[row, col]) continue;
                    darkCount++;
                    long x0 = (long)col * u - g;
                    long y0 = (long)row * u - g;
                    long x1 = ((long)col + 1) * u + g;
                    long y1 = ((long)row + 1) * u + g;
                    var rect = new Path64(4)
                    {
                        new Point64(x0, y0), new Point64(x1, y0),
                        new Point64(x1, y1), new Point64(x0, y1),
                    };
                    subject.Add(rect);
                }
            if (darkCount == 0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    "The cell matrix contains no dark cells; nothing would be sketched.");

            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Union, subject, null, tree, FillRule.NonZero);
            if (tree.Count == 0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    "The union of expanded cells produced no contours.");

            // ---------- tree → validated grid contours ----------
            var gridRegions = new List<GridRegion>(tree.Count);
            var flatPaths = new List<Path64>();
            var flatIsHole = new List<bool>();
            for (var i = 0; i < tree.Count; i++)
            {
                var node = tree.Child(i);
                if (node.IsHole)
                    return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                        "Union returned a hole at the top level — unexpected nesting.");
                var region = ReadRegion(node);
                gridRegions.Add(region);
                Flatten(region, flatPaths, flatIsHole);
            }

            var violation = ContourValidator.FindViolation(flatPaths);
            if (violation != null)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    $"Generated contours failed validation: {violation}.");

            // ---------- convert to physical metres ----------
            double mPerUnit = (sizeMm / 1000.0) / ((double)n * u + 2 * g);
            double gridCentre = n * (double)u / 2.0;
            var regions = new List<QrRegion>(gridRegions.Count);
            var contours = new List<QrContour>(flatPaths.Count);
            foreach (var gr in gridRegions)
                regions.Add(ConvertRegion(gr, contours, mPerUnit, gridCentre));

            // Actual smallest delivered edge (integer units → mm).
            long minEdgeUnits = long.MaxValue;
            int segmentCount = 0;
            foreach (var path in flatPaths)
            {
                segmentCount += path.Count;
                for (var i = 0; i < path.Count; i++)
                {
                    var a = path[i];
                    var b = path[(i + 1) % path.Count];
                    long len = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                    if (len < minEdgeUnits) minEdgeUnits = len;
                }
            }
            double minEdgeMm = minEdgeUnits * mmPerUnit;
            if (minEdgeMm < QrInsertionGuard.MinInsertableFeatureMm)
                return QrSketchResult.Fail(QrSketchErrorKind.BelowInsertionLimit,
                    $"Smallest delivered edge is {minEdgeMm:F4} mm — below the recorded " +
                    $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion guard.");

            var info = new QrSketchInfo(
                version: version,
                cellCount: n,
                pitchMm: u * mmPerUnit,
                growthFractionPerSide: (double)g / u,
                growthMmPerSide: g * mmPerUnit,
                finalSizeMm: (n * (double)u + 2 * g) * mmPerUnit,
                minEdgeMm: minEdgeMm,
                minLightGapMm: (u - 2 * g) * mmPerUnit,
                contourCount: flatPaths.Count,
                segmentCount: segmentCount);

            return QrSketchResult.Ok(new QrSketch(regions, contours, info));
        }

        // ----- grid-space tree -----

        private sealed class GridRegion
        {
            public Path64 Path = new Path64();
            public readonly List<GridHole> Holes = new List<GridHole>();
        }

        private sealed class GridHole
        {
            public Path64 Path = new Path64();
            public readonly List<GridRegion> Islands = new List<GridRegion>();
        }

        private static Path64 Cleaned(PolyPath64 node) =>
            // epsilon 0: strip collinear vertices only — no shape change.
            Clipper.SimplifyPath(node.Polygon!, 0.0, true);

        private static GridRegion ReadRegion(PolyPath64 node)
        {
            var region = new GridRegion { Path = Cleaned(node) };
            for (var i = 0; i < node.Count; i++)
            {
                var child = node.Child(i);
                var hole = new GridHole { Path = Cleaned(child) };
                for (var j = 0; j < child.Count; j++)
                    hole.Islands.Add(ReadRegion(child.Child(j)));
                region.Holes.Add(hole);
            }
            return region;
        }

        private static void Flatten(GridRegion region, List<Path64> paths, List<bool> isHole)
        {
            paths.Add(region.Path);
            isHole.Add(false);
            foreach (var hole in region.Holes)
            {
                paths.Add(hole.Path);
                isHole.Add(true);
                foreach (var island in hole.Islands)
                    Flatten(island, paths, isHole);
            }
        }

        // ----- physical conversion: pure affine map per coordinate value, so
        // shared endpoints stay bit-identical. y flips so matrix row 0 is +y
        // (upright QR, not mirrored). -----

        private static SketchPoint2 ToMetres(Point64 p, double mPerUnit, double gridCentre) =>
            new SketchPoint2(
                (p.X - gridCentre) * mPerUnit,
                (gridCentre - p.Y) * mPerUnit);

        private static QrContour ToContour(Path64 path, bool isHole, double mPerUnit, double gridCentre)
        {
            var vertices = new List<SketchPoint2>(path.Count);
            foreach (var p in path)
                vertices.Add(ToMetres(p, mPerUnit, gridCentre));
            return new QrContour(vertices, isHole);
        }

        private static QrRegion ConvertRegion(
            GridRegion grid, List<QrContour> flat, double mPerUnit, double gridCentre)
        {
            var boundary = ToContour(grid.Path, isHole: false, mPerUnit, gridCentre);
            flat.Add(boundary);
            var holes = new List<QrHole>(grid.Holes.Count);
            foreach (var gh in grid.Holes)
            {
                var holeBoundary = ToContour(gh.Path, isHole: true, mPerUnit, gridCentre);
                flat.Add(holeBoundary);
                var islands = new List<QrRegion>(gh.Islands.Count);
                foreach (var island in gh.Islands)
                    islands.Add(ConvertRegion(island, flat, mPerUnit, gridCentre));
                holes.Add(new QrHole(holeBoundary, islands));
            }
            return new QrRegion(boundary, holes);
        }
    }
}
