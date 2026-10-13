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
    ///      (S still measures the N-cell data area — size semantics unchanged;
    ///      circles do not grow — ticket #33's growth field does not apply,
    ///      so the map runs at r′ = 0 and p = S/N);
    ///   4. expand every EMITTED cell by g = round(r·U) units on all four
    ///      sides — dark cells emit in normal mode (quiet-zone cells are
    ///      light and never emit), light cells emit under invert (the whole
    ///      matrix becomes the field) — as one primitive per cell in the
    ///      request's <see cref="QrCellShape"/>: the grown rectangle, a
    ///      polygonized rounded rect for RoundedSquare, or a disjoint K-sided
    ///      polygon dot for Circle (always disjoint — under invert each light
    ///      cell becomes a separate circular pit);
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
                request.CellShape, request.CellShapeParameter);
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
        /// exactly like <see cref="QrSketchRequest.CellShape"/>.
        /// </summary>
        public static QrSketchResult GenerateFromCells(
            bool[,] darkCells, double sizeMm, double growthFractionPerSide, bool invert = false,
            QrCellShape cellShape = QrCellShape.Square, double cellShapeParameter = 0.0)
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
                cellShape, cellShapeParameter);
        }

        /// <summary>
        /// Runs the pipeline on a working matrix: <paramref name="matrix"/> is
        /// the full (N+8)×(N+8) grid where <paramref name="dataCells"/> is the
        /// N×N payload extent inside it (the margin cells are always light).
        /// <paramref name="invert"/> flips which cells emit: dark cells
        /// normally, light cells under invert — the only mode switch; growth,
        /// union, clipping, validation and conversion run identically.
        /// <paramref name="cellShape"/> picks the primitive each emitted cell
        /// contributes — the union/validate/convert downstream is shape-blind.
        /// </summary>
        private static QrSketchResult Build(
            bool[,] matrix, int dataCells, double sizeMm, double growthFraction, int version,
            bool invert, QrCellShape cellShape, double cellShapeParameter)
        {
            int m = matrix.GetLength(0);
            int n = dataCells;
            int u = QrInsertionGuard.GridUnitsPerCell;

            // ---------- input validation ----------
            if (double.IsNaN(sizeMm) || double.IsInfinity(sizeMm) || sizeMm <= 0.0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidSize,
                    $"Size must be a finite positive number of millimetres; got '{sizeMm}'.");

            // Spec #13, ticket #33: the growth field does NOT apply to
            // circles — the dot size is the sizing knob and the layout is
            // the growth-0 map (pitch p = S/N; the QR square is the data
            // extent, the keep-out the matrix edge, both unchanged in
            // semantics). The growth value is never consulted for circles —
            // an inert field the way the shape parameter is under Square —
            // so its validation is skipped outright.
            bool circle = cellShape == QrCellShape.Circle;
            if (!circle && (double.IsNaN(growthFraction) || double.IsInfinity(growthFraction) ||
                growthFraction <= 0.0 || growthFraction >= 0.5))
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGrowth,
                    $"Outline growth must be a finite fraction in the open interval (0, 0.5) " +
                    $"— i.e. more than 0% and less than 50% of cell width per side; got '{growthFraction}'.");

            // ---------- normalized integer grid ----------
            long g = circle ? 0 : (long)Math.Round(growthFraction * u);
            double mmPerUnit = sizeMm / ((double)n * u + 2 * g);
            if (!circle)
            {
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
                double minFeatureMm = minGridFeature * mmPerUnit;
                if (minFeatureMm < QrInsertionGuard.MinInsertableFeatureMm)
                    return QrSketchResult.Fail(QrSketchErrorKind.BelowInsertionLimit,
                        $"Smallest feature would be {minFeatureMm:F4} mm — below the recorded " +
                        $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion guard. Increase the size " +
                        "or adjust the growth percentage.");
            }

            // ---------- cell shape ----------
            // Spec #13 (tickets #32/#33): the per-cell primitive. Square is
            // today's grown rectangle, emitted unchanged; RoundedSquare cuts
            // each corner with a polygonized quarter-arc of radius ρ = f·h
            // where h is the GROWN cell's half-width (u/2 + g); Circle emits
            // a disjoint K-sided polygon dot of diameter = f·u centred in the
            // cell — union, clipping, nesting, validation and conversion
            // downstream run identically on every primitive. The recorded
            // bounds below are the radius cap / the dot-% cap of
            // docs/INSERTION-GUARD.md.
            bool rounded = false;
            double radiusUnits = 0.0;
            int facetsPerCorner = 0;
            double dotRadiusUnits = 0.0;
            int dotSides = 0;
            if (cellShape == QrCellShape.RoundedSquare)
            {
                double p = cellShapeParameter;
                if (double.IsNaN(p) || double.IsInfinity(p) || p < 0.0)
                    return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                        $"Corner radius must be a finite fraction of the grown cell's " +
                        $"half-width in [0, 1]; got '{p}'.");
                // The edge-inversion bound: each cell edge keeps a straight
                // segment of length 2h−2ρ; ρ > h makes adjacent corner arcs
                // pass each other and invert the edge.
                if (p > 1.0)
                    return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                        $"Corner radius {p:P1} of the grown cell's half-width is over " +
                        "100% — the corner arcs on each edge would pass each other and " +
                        "invert the edge. Stay within 0–100%.");
                if (p > 0.0)
                {
                    double halfWidthUnits = u / 2.0 + g;
                    radiusUnits = p * halfWidthUnits;
                    // The insertion floor in grid units — the same two bounds
                    // the lattice checks just enforced.
                    double floorUnits = Math.Max(QrInsertionGuard.MinGridFeatureUnits,
                        QrInsertionGuard.MinInsertableFeatureMm / mmPerUnit);
                    // 1. The diagonal-overlap cap (docs/INSERTION-GUARD.md):
                    //    grown diagonal cells overlap in a 2g×2g square; while
                    //    ρ ≤ 2g each corner cap stays on its own side of that
                    //    square — the union provably covers it whole for ANY
                    //    polygonization, identical to squares. Past 2g the
                    //    facing corner caps meet: diagonal cells pinch to a
                    //    lens and then part — growth's finite diagonal
                    //    overlap is lost, so the radius is capped at 2g.
                    if (radiusUnits > 2.0 * g)
                        return QrSketchResult.Fail(QrSketchErrorKind.BelowInsertionLimit,
                            $"Corner radius {p:P1} of the grown cell's half-width passes " +
                            $"the diagonal-overlap bound {2.0 * g / halfWidthUnits:P1} " +
                            $"({2 * g} of {u} grid units): past it the corner arcs of " +
                            "diagonally touching cells leave the overlap square and the " +
                            "finite overlap growth guarantees is lost. Reduce the corner " +
                            "radius or increase the outline growth.");
                    // 2. The merge-groove band (docs/INSERTION-GUARD.md):
                    //    where two EMITTED cells share a side their grown
                    //    outlines overlap by 2g. Radius ρ ≤ g keeps each
                    //    corner arc inside that overlap — merges stay flush,
                    //    identical to squares. Past g a V-groove opens on
                    //    the shared boundary whose edges scale with ρ−g;
                    //    under the floor it is a sliver. The band
                    //    g < ρ < g+F rejects before the union can fail
                    //    downstream (spec story 9's near-tangent band).
                    if (radiusUnits > g && radiusUnits - g < floorUnits)
                        return QrSketchResult.Fail(QrSketchErrorKind.BelowInsertionLimit,
                            $"Corner radius {p:P1} of the grown cell's half-width opens a " +
                            $"groove where neighbouring cells merge of about " +
                            $"{(radiusUnits - g) * mmPerUnit:F4} mm — below the recorded " +
                            $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion floor. " +
                            $"Stay at or under {g / halfWidthUnits:P1} (corners merge flush) " +
                            $"or raise the radius past {(g + floorUnits) / halfWidthUnits:P1}.");
                    // 3. The primitive must itself resolve above the floor.
                    //    In the clean regime (ρ ≤ g) no arc ever lies on an
                    //    interior boundary — interior corners are covered by
                    //    the neighbouring grown cells — so the finest
                    //    polygonization whose edges clear the floor, up to
                    //    MaxFacetsPerCorner, is emitted. In the groove
                    //    regime (ρ > g) corner arcs of side-adjacent cells
                    //    cross: at most 2 facets per corner keep every
                    //    crossing a clean vertex (the facet touching each
                    //    edge is collinear with the chamfer — verified on
                    //    the grid); a finer polygonization could strand a
                    //    mid-facet stub below the floor. A radius too small
                    //    for even one facet is refused, like growth that
                    //    rounds to zero; so is a radius whose straight edges
                    //    vanish (ρ near h).
                    for (int k = radiusUnits > g ? 2 : MaxFacetsPerCorner; k >= 1; k--)
                    {
                        var probe = RoundedCellPath(0, 0, u + 2 * g, u + 2 * g, radiusUnits, k);
                        if (MinEdgeUnits(probe) >= floorUnits)
                        {
                            facetsPerCorner = k;
                            break;
                        }
                    }
                    if (facetsPerCorner == 0)
                        return QrSketchResult.Fail(QrSketchErrorKind.BelowInsertionLimit,
                            $"Corner radius {p:P1} of the grown cell's half-width resolves " +
                            $"to {radiusUnits:F1} grid units — its cell edges would sit below " +
                            $"the {QrInsertionGuard.MinInsertableFeatureMm} mm insertion floor. " +
                            "Adjust the corner radius or pick Square cells.");
                    rounded = true;
                }
            }
            else if (circle)
            {
                // Spec #13 (ticket #33): dots only — the merged-blob regimes
                // are deliberately excluded, so a dot is ALWAYS disjoint and
                // the corner-contact failure class disappears entirely. The
                // parameter is the dot size: the circle DIAMETER as a
                // fraction of the cell pitch, ρ = f·u/2 grid units.
                double p = cellShapeParameter;
                if (double.IsNaN(p) || double.IsInfinity(p) || p <= 0.0)
                    return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                        $"Dot size must be a finite positive percentage of cell pitch; " +
                        $"got '{p}'.");
                if (p > 1.0)
                    return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                        $"Dot size {p:P1} of cell pitch is over 100% — the circles " +
                        "would overlap their neighbours outright and dots must stay " +
                        "disjoint. Stay within 0–100%.");

                // The same insertion floor the lattice checks enforce, in
                // grid units; plus the quantization margin — grid rounding
                // displaces each vertex up to ~0.71 units, so two facing
                // dots lose ≤ √2 of the nominal gap. Reserving 2 units
                // keeps the DELIVERED gap/edge at or above the floor.
                double floorUnits = Math.Max(QrInsertionGuard.MinGridFeatureUnits,
                    QrInsertionGuard.MinInsertableFeatureMm / mmPerUnit);
                const double marginUnits = 2.0;

                // The one-sided cap (the 2r = p tangency of the merged
                // regime is gone): the orthogonal neighbour gap
                // u·(1−f) is the binding one — the diagonal gap u·(√2−f)
                // is always larger — and it must clear the floor.
                double r = p * u / 2.0;
                double dotDiameterUnits = 2.0 * r;
                double gapUnits = u - dotDiameterUnits;
                if (gapUnits < floorUnits + marginUnits)
                    return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                        $"Dot size {p:P1} of cell pitch leaves a gap of at most " +
                        $"{(gapUnits - marginUnits) * mmPerUnit:F4} mm between " +
                        "neighbouring dots — below the recorded " +
                        $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion " +
                        $"floor. Stay at or under " +
                        $"{(u - floorUnits - marginUnits) / u:P1}.");

                // The polygon-circle fidelity rule (docs/INSERTION-GUARD.md):
                // an inscribed K-gon's chord sagitta ρ·(1−cos(π/K)) must stay
                // under the floor — K = ⌈π / arccos(1 − F/ρ)⌉, the coarsest
                // polygon that qualifies (fewest segments for ticket #35's
                // budget) — while every delivered chord clears the floor.
                // Smallest K also gives the longest chords, so it is checked
                // on the emitted (grid-rounded) path itself, in the same
                // Chebyshev metric the delivered-edge guard re-measures —
                // if it fails there is no finer polygonization to fall back
                // on: the dot is simply too small to resolve.
                double cosStep = 1.0 - floorUnits / r;
                int sides = cosStep <= -1.0 ? 3
                    : Math.Max(3, (int)Math.Ceiling(Math.PI / Math.Acos(cosStep)));
                if (MinEdgeUnits(CircleCellPath(0, 0, r, sides)) < floorUnits)
                    return QrSketchResult.Fail(QrSketchErrorKind.InvalidShapeParameter,
                        $"Dot size {p:P1} of cell pitch resolves to a dot of about " +
                        $"{dotDiameterUnits * mmPerUnit:F4} mm — its polygon edges " +
                        "cannot reach the recorded " +
                        $"{QrInsertionGuard.MinInsertableFeatureMm} mm insertion " +
                        "floor. Increase the dot size.");
                dotRadiusUnits = r;
                dotSides = sides;
            }

            // ---------- expand emitted cells + union, clipped to the matrix ----------
            // The polarity select is the only mode switch (ADR 0002): dark
            // cells emit normally, light cells emit under invert — across the
            // WHOLE working matrix, so the quiet zone becomes field. The
            // shape select dispatches the primitive inside this same loop.
            var subject = new Paths64();
            int emittedCount = 0;
            for (var row = 0; row < m; row++)
                for (var col = 0; col < m; col++)
                {
                    if (matrix[row, col] == invert) continue;
                    emittedCount++;
                    long x0 = (long)col * u - g;
                    long y0 = (long)row * u - g;
                    long x1 = ((long)col + 1) * u + g;
                    long y1 = ((long)row + 1) * u + g;
                    var cell = rounded
                        ? RoundedCellPath(x0, y0, x1, y1, radiusUnits, facetsPerCorner)
                        : circle
                            ? CircleCellPath(
                                (long)col * u + u / 2, (long)row * u + u / 2,
                                dotRadiusUnits, dotSides)
                            : new Path64(4)
                            {
                                new Point64(x0, y0), new Point64(x1, y0),
                                new Point64(x1, y1), new Point64(x0, y1),
                            };
                    subject.Add(cell);
                }
            if (emittedCount == 0)
                return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                    invert
                        ? "The cell matrix contains no light cells; nothing would be sketched."
                        : "The cell matrix contains no dark cells; nothing would be sketched.");

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
                matrixCellCount: m,
                pitchMm: u * mmPerUnit,
                growthFractionPerSide: (double)g / u,
                growthMmPerSide: g * mmPerUnit,
                finalSizeMm: (n * (double)u + 2 * g) * mmPerUnit,
                minEdgeMm: minEdgeMm,
                // Circles: the narrowest un-emitted channel is the
                // delivered gap between neighbouring dots — the cap keeps it
                // at or over the floor.
                minLightGapMm: (u - 2 * (circle ? dotRadiusUnits : g)) * mmPerUnit,
                contourCount: flatPaths.Count,
                segmentCount: segmentCount,
                inverted: invert,
                cellShape: cellShape,
                cellShapeParameter: rounded || circle ? cellShapeParameter : 0.0);

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
        /// Arc facets per corner for the rounded-square primitive — bounded
        /// at 8 (a 32-vertex cell keeps fidelity without flooding the segment
        /// count; the delivered-edge floor bounds it further per request).
        /// </summary>
        private const int MaxFacetsPerCorner = 8;

        /// <summary>
        /// One rounded-rectangle cell on the integer grid: the rect
        /// [x0,y0]–[x1,y1] with each corner replaced by a quarter-arc of
        /// <paramref name="radiusUnits"/> polygonized into
        /// <paramref name="facetsPerCorner"/> facets. Vertices are emitted
        /// CCW like the square primitive — the straight edges come free as
        /// the chords between consecutive corner arcs. Duplicate points a
        /// degenerate radius would emit are dropped (the caller guarantees
        /// the radius clears the grid floor, so the path keeps &gt;4 vertices
        /// and nonzero area).
        /// </summary>
        private static Path64 RoundedCellPath(
            long x0, long y0, long x1, long y1, double radiusUnits, int facetsPerCorner)
        {
            var path = new Path64(4 * (facetsPerCorner + 1));
            double r = radiusUnits;
            // Corner arcs in CCW order — BL 180°→270°, BR 270°→360°,
            // TR 0°→90°, TL 90°→180°; each arc's first point is the incoming
            // straight edge's endpoint.
            AppendCornerArc(path, x0 + r, y0 + r, r, 180.0, facetsPerCorner);
            AppendCornerArc(path, x1 - r, y0 + r, r, 270.0, facetsPerCorner);
            AppendCornerArc(path, x1 - r, y1 - r, r, 0.0, facetsPerCorner);
            AppendCornerArc(path, x0 + r, y1 - r, r, 90.0, facetsPerCorner);
            // An exact-half-width radius makes the left edge vanish — never
            // leave a duplicated closing point.
            if (path.Count > 1 && path[path.Count - 1] == path[0])
                path.RemoveAt(path.Count - 1);
            return path;
        }

        /// <summary>
        /// Emits one quarter-arc: the arc's start point plus one vertex per
        /// facet end, skipping any vertex that rounds onto the previous one
        /// (grid quantization can collapse facets for very small radii).
        /// </summary>
        private static void AppendCornerArc(
            Path64 path, double cx, double cy, double r, double startDegrees, int facets)
        {
            double stepDegrees = 90.0 / facets;
            for (var j = 0; j <= facets; j++)
            {
                double a = (startDegrees + j * stepDegrees) * Math.PI / 180.0;
                var pt = new Point64(
                    (long)Math.Round(cx + r * Math.Cos(a)),
                    (long)Math.Round(cy + r * Math.Sin(a)));
                if (path.Count == 0 || path[path.Count - 1] != pt)
                    path.Add(pt);
            }
        }

        /// <summary>
        /// One circle cell on the integer grid: the inscribed regular
        /// <paramref name="sides"/>-gon of radius
        /// <paramref name="radiusUnits"/> centred on (cx, cy). Inscribed —
        /// every vertex lies ON the nominal circle — so the polygon never
        /// reaches past the dot's diameter and neighbouring dots stay
        /// disjoint by construction (ticket #33). Vertices are emitted in
        /// the square primitive's winding (positive signed area, starting at
        /// the leftmost point); vertices that round onto their predecessor
        /// or the start point are dropped so no zero-length edge survives.
        /// </summary>
        private static Path64 CircleCellPath(
            long centreX, long centreY, double radiusUnits, int sides)
        {
            var path = new Path64(sides);
            for (var i = 0; i < sides; i++)
            {
                double a = Math.PI + i * (2.0 * Math.PI / sides);
                var pt = new Point64(
                    (long)Math.Round(centreX + radiusUnits * Math.Cos(a)),
                    (long)Math.Round(centreY + radiusUnits * Math.Sin(a)));
                if (path.Count == 0 || path[path.Count - 1] != pt)
                    path.Add(pt);
            }
            if (path.Count > 1 && path[path.Count - 1] == path[0])
                path.RemoveAt(path.Count - 1);
            return path;
        }

        /// <summary>The shortest edge of a grid path under the Chebyshev metric the guard uses.</summary>
        private static long MinEdgeUnits(Path64 path)
        {
            long min = long.MaxValue;
            for (var i = 0; i < path.Count; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % path.Count];
                long len = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
                if (len < min) min = len;
            }
            return min;
        }

        private static SketchPoint2[] SquareVertices(double half) =>
            // Centred on the origin like the contours; CCW from bottom-left.
            new[]
            {
                new SketchPoint2(-half, -half), new SketchPoint2(+half, -half),
                new SketchPoint2(+half, +half), new SketchPoint2(-half, +half),
            };

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
