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
    /// Pipeline (spec "Geometry generation"; matrix model per ADR 0002):
    ///   1. encode exactly — standard QR, automatic version, the request's
    ///      error-correction level (L/M/Q/H, default M);
    ///   2. the working matrix is the encoder's full (N+8)×(N+8) matrix —
    ///      N×N data cells plus the fixed 4-cell light quiet zone per side;
    ///   3. pitch p = S/(N + 2r′), grown-cell pitch on a normalized integer
    ///      grid of <see cref="QrInsertionGuard.GridUnitsPerCell"/> units/cell
    ///      (S still measures the N-cell data area — size semantics unchanged);
    ///   4. expand every EMITTED cell by g = round(r·U) units on all four
    ///      sides — dark cells emit in normal mode (quiet-zone cells are
    ///      light and never emit), light cells emit under invert (the whole
    ///      matrix becomes the field);
    ///   5. union (Clipper2 NonZero) and clip to the working-matrix square —
    ///      a guaranteed no-op in normal mode (grown dark cells cannot reach
    ///      the 4-cell border), the real field edge under invert — into a
    ///      PolyTree64 with outer/hole/island nesting preserved;
    ///   6. strip redundant collinear vertices (epsilon 0);
    ///   7. validate on the grid: nonzero area, no zero-length edges, no
    ///      self-intersections, no residual corner-only contacts;
    ///   8. convert to physical sketch metres — a pure affine map, so shared
    ///      endpoints stay bit-identical — centred on the origin, matrix row 0
    ///      at +y so the QR is upright rather than mirrored.
    ///
    /// The result carries the grown, unioned contours at the requested size
    /// plus the QR square and keep-out square as construction squares in the
    /// same sketch-space metres (ADR 0002/0003/0004): the host draws all of
    /// it into the real-size block definition at insertion, flagging the
    /// squares construction so they fold in without feeding a profile.
    ///
    /// Contains no SolidWorks objects.
    /// </summary>
    public static class QrSketchGenerator
    {
        /// <summary>
        /// The request at its defaults: encodes <paramref name="content"/>
        /// exactly (standard QR, automatic version selection, error
        /// correction M, normal polarity) at <paramref name="sizeMm"/> final
        /// outline width.
        /// </summary>
        public static QrSketchResult Generate(string content, double sizeMm, double growthFractionPerSide) =>
            Generate(new QrSketchRequest(content, sizeMm, growthFractionPerSide));

        /// <summary>
        /// Encodes <see cref="QrSketchRequest.Content"/> exactly (standard QR,
        /// automatic version selection, the request's
        /// <see cref="QrSketchRequest.EccLevel"/>) and generates the grown,
        /// unioned contour set at <see cref="QrSketchRequest.SizeMm"/> final
        /// outline width, emitting the polarity
        /// <see cref="QrSketchRequest.Invert"/> selects (ADR 0002).
        /// </summary>
        public static QrSketchResult Generate(QrSketchRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrEmpty(request.Content))
                return QrSketchResult.Fail(QrSketchErrorKind.EmptyContent,
                    "QR content is empty. Enter the text or URL to encode.");

            // Spec #51, ticket #52 — the ring split spends real correction
            // budget on dropped cells: refuse below ECC M, naming the field —
            // never silently bumped. Under Circle the flag is inert (dots
            // emit no contours, no cells drop), so the gate does not apply.
            if (request.RingSplit && QrCellShapes.RingSplitApplies(request.CellShape) &&
                request.EccLevel < QRCodeGenerator.ECCLevel.M)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidEccLevel,
                    $"Ring split drops emitted cells — error correction level M or higher " +
                    $"absorbs them, but the request's EccLevel is {request.EccLevel}. " +
                    "Raise EccLevel to M, Q or H, or turn ring split off.");

            bool[,] matrix;
            int version;
            try
            {
                using (var generator = new QRCodeGenerator())
                using (var data = generator.CreateQrCode(request.Content, request.EccLevel))
                {
                    // ModuleMatrix IS the working matrix (ADR 0002): N×N data
                    // cells plus the standard 4-cell light quiet zone on every
                    // side — keep it whole, data cells live at [4, Count−4).
                    var encoded = data.ModuleMatrix;
                    int m = encoded.Count;
                    matrix = new bool[m, m];
                    for (var row = 0; row < m; row++)
                    {
                        BitArray bits = encoded[row];
                        for (var col = 0; col < m; col++)
                            matrix[row, col] = bits[col];
                    }
                    version = data.Version;
                }
            }
            catch (QRCoder.Exceptions.DataTooLongException ex)
            {
                return QrSketchResult.Fail(QrSketchErrorKind.PayloadTooLong,
                    $"The content does not fit in a standard QR code at error correction " +
                    $"{request.EccLevel}: {ex.Message} Shorten the text or URL, or pick a " +
                    "lighter error-correction level.");
            }
            catch (Exception ex)
            {
                return QrSketchResult.Fail(QrSketchErrorKind.EncoderFailure,
                    $"The QR encoder rejected the content: {ex.Message}");
            }

            return Build(matrix, matrix.GetLength(0) - 2 * QrSketchInfo.QuietZoneCellsPerSide,
                request.SizeMm, request.GrowthFractionPerSide, version, request.Invert,
                request.CellShape, request.CellShapeParameter, request.RingSplit);
        }

        /// <summary>
        /// The same pipeline fed directly from a dark-cell matrix (no encoder):
        /// <paramref name="darkCells"/>[row, col] == true means dark, indexed
        /// from the top-left data cell. Deterministic-fixture seam for tests;
        /// the encoded path funnels through it with a full encoded matrix —
        /// both are padded into the same (N+8)×(N+8) working matrix.
        /// <paramref name="invert"/> selects the emitted polarity exactly as in
        /// <see cref="Generate"/>; <paramref name="cellShape"/> and
        /// <paramref name="cellShapeParameter"/> pick the per-cell primitive
        /// exactly like <see cref="QrSketchRequest.CellShape"/>;
        /// <paramref name="ringSplit"/> applies the hybrid treatment like
        /// <see cref="QrSketchRequest.RingSplit"/> (the fixture seam has no
        /// error-correction level — the ECC gate lives on the request).
        /// </summary>
        public static QrSketchResult GenerateFromCells(
            bool[,] darkCells, double sizeMm, double growthFractionPerSide, bool invert = false,
            QrCellShape cellShape = QrCellShape.Square, double cellShapeParameter = 0.0,
            bool ringSplit = false)
        {
            if (darkCells == null)
                throw new ArgumentNullException(nameof(darkCells));
            if (darkCells.GetLength(0) != darkCells.GetLength(1))
                throw new ArgumentException("The QR cell matrix must be square.", nameof(darkCells));

            // Fixtures hand over the N×N data cells; the working matrix pads
            // them with the same fixed light quiet zone the encoder provides.
            int n = darkCells.GetLength(0);
            int q = QrSketchInfo.QuietZoneCellsPerSide;
            var matrix = new bool[n + 2 * q, n + 2 * q];
            for (var row = 0; row < n; row++)
                for (var col = 0; col < n; col++)
                    matrix[row + q, col + q] = darkCells[row, col];
            return Build(matrix, n, sizeMm, growthFractionPerSide, version: 0, invert,
                cellShape, cellShapeParameter, ringSplit);
        }

        /// <summary>
        /// The whole-working-matrix fixture seam — <see cref="GenerateFromCells"/>
        /// minus the quiet-zone padding: the caller supplies the full
        /// (N+8)×(N+8) grid, so a fixture may emit inside the fixed light
        /// margin (blocking a finder-channel locus so the claimed channel
        /// lands in an enclosed pocket — a mask/union divergence no
        /// data-cells fixture can express). <paramref name="dataCells"/> is
        /// the N×N payload extent used for sizing and info reporting.
        /// Internal for the suite (InternalsVisibleTo); normal polarity,
        /// Square shape.
        /// </summary>
        internal static QrSketchResult GenerateFromWorkingMatrix(
            bool[,] darkCells, int dataCells, double sizeMm,
            double growthFractionPerSide, bool ringSplit)
        {
            if (darkCells == null)
                throw new ArgumentNullException(nameof(darkCells));
            if (darkCells.GetLength(0) != darkCells.GetLength(1))
                throw new ArgumentException("The QR cell matrix must be square.", nameof(darkCells));
            return Build(darkCells, dataCells, sizeMm, growthFractionPerSide,
                version: 0, invert: false, QrCellShape.Square, 0.0, ringSplit);
        }

        /// <summary>
        /// Runs the pipeline on a working matrix: <paramref name="matrix"/> is
        /// the full (N+8)×(N+8) grid where <paramref name="dataCells"/> is the
        /// N×N payload extent inside it (the margin cells are always light).
        /// <paramref name="invert"/> flips which cells emit: dark cells
        /// normally, light cells under invert. <paramref name="cellShape"/>
        /// picks the primitive: Square runs the grown-cell union/clip/
        /// validate/convert contour pipeline below; Circle branches away
        /// before any contour machinery into <see cref="BuildCirclePlan"/>.
        /// <paramref name="ringSplit"/> (spec #51, tickets #52/#53) opens
        /// every island-bearing hole before the union — the finder rings by
        /// a thin channel subtracted from the grown subject at a ring
        /// corner, hugging the void's inner edge, every other site by a
        /// minimal module-aligned cell drop — Square-shape only, inert
        /// under Circle.
        /// </summary>
        private static QrSketchResult Build(
            bool[,] matrix, int dataCells, double sizeMm, double growthFraction, int version,
            bool invert, QrCellShape cellShape, double cellShapeParameter, bool ringSplit)
        {
            int m = matrix.GetLength(0);
            int n = dataCells;
            int u = QrInsertionGuard.GridUnitsPerCell;

            // ---------- input validation ----------
            if (double.IsNaN(sizeMm) || double.IsInfinity(sizeMm) || sizeMm <= 0.0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidSize,
                    $"Size must be a finite positive number of millimetres; got '{sizeMm}'.");

            // Spec #13, ticket #38: Circle emits a circle plan — one real
            // circle dot per emitted cell for the host's native linear
            // pattern — never contours. The growth field is inert under
            // Circle (dots are already disjoint; the dot size is the whole
            // sizing knob) and is never consulted on this path.
            if (cellShape == QrCellShape.Circle)
                return BuildCirclePlan(matrix, n, sizeMm, version, invert,
                    cellShapeParameter);

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
                    $"cell, it would close every one-cell {(invert ? "dark" : "light")} channel. " +
                    "Reduce the growth percentage.");

            // The smallest drawable feature in EITHER polarity: emitted cells
            // grown by g neck diagonally at 2g; a one-cell channel of the
            // un-emitted polarity is grown down to u−2g (light gaps normally,
            // dark-cell pockets under invert).
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

            // ---------- expand emitted cells + union, clipped to the matrix ----------
            // The polarity select is the only mode switch (ADR 0002): dark
            // cells emit normally, light cells emit under invert — across the
            // WHOLE working matrix, so the quiet zone becomes field.
            var emit = new bool[m, m];
            for (var row = 0; row < m; row++)
                for (var col = 0; col < m; col++)
                    emit[row, col] = matrix[row, col] != invert;

            // Spec #51 — the opt-in ring split, on the emitted set after the
            // polarity select (invert rides the same path). The three finder
            // rings open by thin finite-width channel rectangles subtracted
            // from the grown subject below (ticket #53); every other
            // island-bearing hole drops the minimal module-aligned cell path
            // joining it to exterior-connected space (ticket #52) — before
            // the union pass, so no ring → hole → island nesting survives.
            var openedSites = 0;
            var droppedCells = 0;
            var channelWidthUnits = 0L;
            Paths64? finderChannels = null;
            if (ringSplit)
            {
                channelWidthUnits = QrRingSplitter.FinderChannelWidthUnits(mmPerUnit);
                var splitError = QrRingSplitter.OpenIslandBearingHoles(
                    emit, g, channelWidthUnits,
                    out openedSites, out droppedCells, out finderChannels);
                if (splitError != null)
                    return QrSketchResult.Fail(splitError.Kind, splitError.Message);
            }

            var subject = new Paths64();
            int emittedCount = 0;
            for (var row = 0; row < m; row++)
                for (var col = 0; col < m; col++)
                {
                    if (!emit[row, col]) continue;
                    emittedCount++;
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
            if (emittedCount == 0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    invert
                        ? "The cell matrix contains no light cells; nothing would be sketched."
                        : "The cell matrix contains no dark cells; nothing would be sketched.");

            // The finder channels ride the same union pass: NonZero fills
            // the overlapping grown rects, so subtracting the channel
            // rectangles here removes them from the merged material — the
            // grown subject minus the thin corridors, still in grid space,
            // before the matrix-square clip below (the second Clipper-hook
            // shape of docs/RING-SPLIT-RESEARCH.md §4).
            if (finderChannels != null && finderChannels.Count > 0)
            {
                subject = Clipper.Difference(subject, finderChannels, FillRule.NonZero);
            }

            // ADR 0002: all geometry is clipped to the working-matrix square
            // [0, M·U] — a guaranteed no-op in normal mode (grown dark cells
            // stay g < U/2 < 4U inside the border), the clean rectangular
            // field edge under invert.
            long matrixEdge = (long)m * u;
            var clip = new Paths64
            {
                new Path64(4)
                {
                    new Point64(0, 0), new Point64(matrixEdge, 0),
                    new Point64(matrixEdge, matrixEdge), new Point64(0, matrixEdge),
                },
            };

            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Intersection, subject, clip, tree, FillRule.NonZero);
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

            // All-or-nothing on the delivered set: the union's tree is the
            // geometry the host consumes — if a hole still bears an island
            // the cell model diverged from the union, so refuse rather than
            // deliver a partially opened QR.
            if (ringSplit && gridRegions.Exists(HasIslandBearingHole))
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    "Ring split left an island-bearing hole in the unioned contours — " +
                    "the request is refused rather than partially opened.");

            // ---------- segment ceiling (spec-13 ticket #35, ported #38) ----------
            // The delivered segment total is known as soon as the contours
            // are flat — count it BEFORE the quadratic self-intersection
            // sweep: a request we are about to refuse should not pay the
            // O(n²) validation cost on tens of thousands of segments.
            int segmentCount = 0;
            foreach (var path in flatPaths)
                segmentCount += path.Count;
            if (segmentCount > QrInsertionGuard.MaxSegmentCount)
                return QrSketchResult.Fail(QrSketchErrorKind.TooManySegments,
                    SegmentCeilingMessage(segmentCount));

            var violation = ContourValidator.FindViolation(flatPaths);
            if (violation != null)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    $"Generated contours failed validation: {violation}.");

            // ---------- convert to physical metres ----------
            // S measures the N-cell grown data area (unchanged); the matrix is
            // symmetric about the data, so centring on M·U/2 keeps the data
            // centred on the origin.
            double mPerUnit = (sizeMm / 1000.0) / ((double)n * u + 2 * g);
            double gridCentre = m * (double)u / 2.0;
            var regions = new List<QrRegion>(gridRegions.Count);
            var contours = new List<QrContour>(flatPaths.Count);
            foreach (var gr in gridRegions)
                regions.Add(ConvertRegion(gr, contours,
                    p => ToMetres(p, mPerUnit, gridCentre)));

            double grownUnits = n * (double)u + 2 * g;

            // Actual smallest delivered edge (integer units → mm).
            // segmentCount was already totalled for the ceiling check.
            long minEdgeUnits = long.MaxValue;
            foreach (var path in flatPaths)
            {
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
                matrixCellCount: m,
                pitchMm: u * mmPerUnit,
                growthFractionPerSide: (double)g / u,
                growthMmPerSide: g * mmPerUnit,
                finalSizeMm: (n * (double)u + 2 * g) * mmPerUnit,
                minEdgeMm: minEdgeMm,
                minLightGapMm: (u - 2 * g) * mmPerUnit,
                contourCount: flatPaths.Count,
                segmentCount: segmentCount,
                inverted: invert,
                openedSiteCount: openedSites,
                droppedCellCount: droppedCells,
                finderChannelWidthMm: finderChannels != null && finderChannels.Count > 0
                    ? channelWidthUnits * mmPerUnit
                    : 0.0);

            // ---------- construction squares (sketch-space metres) ----------
            // The squares ride INSIDE the block definition as construction
            // geometry (ADR 0003): they scale and move with the instance, so
            // the footprint and the four-cell quiet zone track live — the
            // host-square + dimension/equation machinery of rounds 2–3 is
            // abandoned (BLOCK-GATES.md: no public route can bind host
            // geometry to instance-projected edges or read instance Scale).
            // They emit in the same sketch-space metres at the requested
            // size as the contours: the QR square IS the grown outline —
            // ±S/2 — so the drawn square marks the outline even when the
            // dark cells never reach it; the keep-out square is the
            // working-matrix boundary M·u·mmPerUnit per side (ADR 0002,
            // not "4 cells outside the QR square").
            double grownHalfM = grownUnits * mPerUnit / 2.0;
            double matrixHalfM = m * (double)u * mPerUnit / 2.0;
            var squares = new List<QrConstructionSquare>(2)
            {
                new QrConstructionSquare(QrSquareRole.QrSquare, SquareVertices(grownHalfM)),
                new QrConstructionSquare(QrSquareRole.KeepOutSquare, SquareVertices(matrixHalfM)),
            };

            return QrSketchResult.Ok(new QrSketch(regions, contours, squares, info));
        }

        /// <summary>
        /// The Circle pipeline (spec #13, ticket #38): instead of contours,
        /// emits a <see cref="QrCirclePlan"/> — the post-invert emission
        /// mask over the working matrix, the cell pitch, the dot diameter
        /// and the linear-pattern grid placement the host consumes verbatim
        /// (the seed circle + two-directional pattern + create-time
        /// instance skips of docs/PATTERN-SPIKE.md). No Clipper, no union,
        /// no corner-contact machinery — disjoint dots cannot produce that
        /// failure class at all, and growth never applies.
        ///
        /// The pattern grid is asymmetric by polarity because the seed
        /// instance (1,1) can never be skipped: under normal polarity the
        /// grid spans the N×N data square seeded on data cell (0,0) — the
        /// guaranteed-dark finder corner (the working matrix's own corners
        /// are all quiet-zone light and would leave an unskippable stray
        /// dot in the margin); under invert the grid spans the whole M×M
        /// matrix seeded on matrix cell (0,0) — a quiet-zone cell that is
        /// light by construction, hence emitting. The mask is always
        /// recorded over the full matrix; the pattern origin tells the
        /// emitter where instance (1,1) sits inside it.
        /// </summary>
        private static QrSketchResult BuildCirclePlan(
            bool[,] matrix, int dataCells, double sizeMm, int version,
            bool invert, double dotFraction)
        {
            int m = matrix.GetLength(0);
            int n = dataCells;
            int q = QrSketchInfo.QuietZoneCellsPerSide;

            // ---------- the dot-size parameter ----------
            // Dot diameter d = f·p, pitch p = S/N (no growth under Circle).
            // The per-request cap keeps the DELIVERED orthogonal dot-to-dot
            // gap p−d at or above the recorded insertion floor: reject
            // f ≥ cap outright (acceptance: strict), plus the obvious floor
            // on the dot itself — it is the smallest delivered feature.
            if (double.IsNaN(dotFraction) || double.IsInfinity(dotFraction) ||
                dotFraction <= 0.0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                    $"Dot size must be a finite positive percentage of cell pitch; " +
                    $"got '{dotFraction}'.");
            double pitchMm = sizeMm / n;
            double capFraction = 1.0 - QrInsertionGuard.MinInsertableFeatureMm / pitchMm;
            if (dotFraction >= capFraction)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                    $"Dot size {dotFraction * 100.0:G4}% of cell pitch leaves a gap of at " +
                    $"most {pitchMm * (1.0 - dotFraction):F4} mm between neighbouring " +
                    $"dots — at or under the recorded " +
                    $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion floor. " +
                    $"Stay under {capFraction * 100.0:G4}% for this size and payload — " +
                    "lower the dot size; a larger QR size or a sparser payload " +
                    "(shorter content or lighter error correction) raises the cap.");
            double dotDiameterMm = pitchMm * dotFraction;
            if (dotDiameterMm < QrInsertionGuard.MinInsertableFeatureMm)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                    $"Dot size {dotFraction * 100.0:G4}% of cell pitch delivers a " +
                    $"{dotDiameterMm:F4} mm dot — below the recorded " +
                    $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion floor. " +
                    "Increase the dot size or the QR size.");

            // ---------- the post-invert emission mask over the matrix ----------
            var emit = new bool[m, m];
            int emitted = 0;
            for (var row = 0; row < m; row++)
                for (var col = 0; col < m; col++)
                {
                    emit[row, col] = matrix[row, col] != invert;
                    if (emit[row, col]) emitted++;
                }
            if (emitted == 0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    invert
                        ? "The cell matrix contains no light cells; nothing would be sketched."
                        : "The cell matrix contains no dark cells; nothing would be sketched.");

            // ---------- the pattern grid ----------
            // The seed instance can never be skipped (docs/PATTERN-SPIKE.md
            // PAT2), so the pattern origin sits on a cell that always emits:
            // normal → the data square's top-left corner (data (0,0) is a
            // dark finder corner on every real QR); invert → the matrix's
            // top-left corner (a quiet-zone cell, light by construction).
            int originRow, originCol, patternCells;
            if (invert)
            {
                originRow = 0;
                originCol = 0;
                patternCells = m;
            }
            else
            {
                originRow = q;
                originCol = q;
                patternCells = n;
            }
            // The mask records the delivered truth: the seed emits whether
            // the matrix wanted it or not (real QRs always do).
            if (!emit[originRow, originCol])
            {
                emit[originRow, originCol] = true;
                emitted++;
            }

            double pitchM = pitchMm / 1000.0;
            var plan = new QrCirclePlan(emit, n, patternCells, originRow, originCol,
                pitchM, dotDiameterMm / 1000.0, invert);

            // ---------- instance ceiling (spec-13 ticket #41) ----------
            // Under real circles the edit-responsiveness driver is the
            // delivered DOT count — every kept instance is one ARC entity
            // and one closed sketch region — so the segment ceiling's
            // sibling bound counts the plan's post-invert EmittedCount
            // (never N²), still inside generation, before any host
            // mutation.
            if (plan.EmittedCount > QrInsertionGuard.MaxCircleInstances)
                return QrSketchResult.Fail(QrSketchErrorKind.TooManyInstances,
                    InstanceCeilingMessage(plan.EmittedCount));

            var info = new QrSketchInfo(
                version: version,
                cellCount: n,
                matrixCellCount: m,
                pitchMm: pitchMm,
                growthFractionPerSide: 0.0,
                growthMmPerSide: 0.0,
                finalSizeMm: sizeMm,
                minEdgeMm: dotDiameterMm,
                minLightGapMm: pitchMm - dotDiameterMm,
                contourCount: 0,
                segmentCount: 0,
                inverted: invert,
                cellShape: QrCellShape.Circle,
                cellShapeParameter: dotFraction,
                instanceCount: plan.EmittedCount);

            // The same construction squares the contour path stages: the QR
            // square IS the data extent ±S/2 (no growth under Circle), the
            // keep-out square the working-matrix edge ±M·p/2 (ADR 0002).
            double halfDataM = n * pitchM / 2.0;
            double halfMatrixM = m * pitchM / 2.0;
            var squares = new List<QrConstructionSquare>(2)
            {
                new QrConstructionSquare(QrSquareRole.QrSquare, SquareVertices(halfDataM)),
                new QrConstructionSquare(QrSquareRole.KeepOutSquare, SquareVertices(halfMatrixM)),
            };

            return QrSketchResult.Ok(new QrSketch(
                new List<QrRegion>(), new List<QrContour>(), squares, info, plan));
        }

        /// <summary>
        /// The actionable segment-ceiling refusal — the count, the budget
        /// and the levers (smaller payload, lighter ECC, or the Circle
        /// shape whose native pattern emits no segments at all).
        /// </summary>
        private static string SegmentCeilingMessage(long segmentCount) =>
            $"The generated QR would draw {segmentCount} sketch segments — over " +
            $"the recorded {QrInsertionGuard.MaxSegmentCount}-segment insertion " +
            "guard (the sketch-edit-responsiveness bound of " +
            "docs/INSERTION-GUARD.md). Shorten the content, pick a lighter " +
            "error-correction level, or use Circle cells — native pattern " +
            "dots deliver no segments.";

        /// <summary>
        /// The actionable instance-ceiling refusal — the emitted-dot count,
        /// the budget and the levers (smaller payload, lighter ECC, or the
        /// Square shape whose merged contours carry the densest payloads).
        /// </summary>
        private static string InstanceCeilingMessage(int instanceCount) =>
            $"The generated QR would draw {instanceCount} circle-dot instances — over " +
            $"the recorded {QrInsertionGuard.MaxCircleInstances}-instance insertion " +
            "guard (the sketch-edit-responsiveness bound of " +
            "docs/INSERTION-GUARD.md). Shorten the content, pick a lighter " +
            "error-correction level, or use Square cells — merged contours " +
            "carry the densest payloads.";

        private static SketchPoint2[] SquareVertices(double half) =>
            // Centred on the origin like the contours; CCW from bottom-left.
            new[]
            {
                new SketchPoint2(-half, -half), new SketchPoint2(+half, -half),
                new SketchPoint2(+half, +half), new SketchPoint2(-half, +half),
            };

        // ----- grid-space tree -----

        /// <summary>Whether any hole in <paramref name="region"/>'s tree still
        /// bears a filled island, at any depth — the doubly-nested condition
        /// (spec #51). The recursion matches <see cref="QrNestingCensus"/>'s
        /// walk: a residual can sit inside a promoted island's hole too, so
        /// every hole at every depth is inspected.</summary>
        private static bool HasIslandBearingHole(GridRegion region)
        {
            foreach (var hole in region.Holes)
            {
                if (hole.Islands.Count > 0)
                    return true;
                foreach (var island in hole.Islands)
                    if (HasIslandBearingHole(island))
                        return true;
            }
            return false;
        }

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

        private static QrContour ToContour(
            Path64 path, bool isHole, Func<Point64, SketchPoint2> map)
        {
            var vertices = new List<SketchPoint2>(path.Count);
            foreach (var p in path)
                vertices.Add(map(p));
            return new QrContour(vertices, isHole);
        }

        private static QrRegion ConvertRegion(
            GridRegion grid, List<QrContour> flat, Func<Point64, SketchPoint2> map)
        {
            var boundary = ToContour(grid.Path, isHole: false, map);
            flat.Add(boundary);
            var holes = new List<QrHole>(grid.Holes.Count);
            foreach (var gh in grid.Holes)
            {
                var holeBoundary = ToContour(gh.Path, isHole: true, map);
                flat.Add(holeBoundary);
                var islands = new List<QrRegion>(gh.Islands.Count);
                foreach (var island in gh.Islands)
                    islands.Add(ConvertRegion(island, flat, map));
                holes.Add(new QrHole(holeBoundary, islands));
            }
            return new QrRegion(boundary, holes);
        }
    }
}
