using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// One closed contour loop of the QR pattern, in sketch-space metres.
    /// The last vertex connects back to the first (no duplicated closing point).
    /// <see cref="IsHole"/> distinguishes hole loops from filled-region outlines;
    /// a hole's filled islands hang off <see cref="QrHole.Islands"/>, keeping the
    /// region relationships the sketch must preserve.
    /// </summary>
    public sealed class QrContour
    {
        public QrContour(IReadOnlyList<SketchPoint2> vertices, bool isHole)
        {
            Vertices = vertices;
            IsHole = isHole;
        }

        public IReadOnlyList<SketchPoint2> Vertices { get; }

        public bool IsHole { get; }

        /// <summary>The contour's edges; the final edge closes the loop.</summary>
        public IEnumerable<SketchEdge> Edges => SketchClosedLoop.Edges(Vertices);
    }

    /// <summary>A hole loop inside a filled region, plus any filled islands nested in it.</summary>
    public sealed class QrHole
    {
        public QrHole(QrContour boundary, IReadOnlyList<QrRegion> islands)
        {
            Boundary = boundary;
            Islands = islands;
        }

        public QrContour Boundary { get; }

        /// <summary>Filled regions enclosed by this hole (e.g. a finder pattern's centre).</summary>
        public IReadOnlyList<QrRegion> Islands { get; }
    }

    /// <summary>A filled region: an outer boundary plus the holes it encloses.</summary>
    public sealed class QrRegion
    {
        public QrRegion(QrContour boundary, IReadOnlyList<QrHole> holes)
        {
            Boundary = boundary;
            Holes = holes;
        }

        public QrContour Boundary { get; }

        public IReadOnlyList<QrHole> Holes { get; }
    }

    /// <summary>Size information about a generated QR sketch (all physical values in mm).</summary>
    public sealed class QrSketchInfo
    {
        /// <summary>
        /// The fixed quiet zone every working matrix carries: 4 light cells per
        /// side (ISO 18004, ADR 0002) — always present, never a setting.
        /// </summary>
        public const int QuietZoneCellsPerSide = 4;

        public QrSketchInfo(
            int version, int cellCount, int matrixCellCount, double pitchMm,
            double growthFractionPerSide, double growthMmPerSide,
            double finalSizeMm, double minEdgeMm, double minLightGapMm,
            int contourCount, int segmentCount, bool inverted = false,
            QrCellShape cellShape = QrCellShape.Square,
            double cellShapeParameter = 0.0, int instanceCount = 0,
            int openedSiteCount = 0, int droppedCellCount = 0,
            double finderChannelWidthMm = 0.0)
        {
            Version = version;
            CellCount = cellCount;
            MatrixCellCount = matrixCellCount;
            PitchMm = pitchMm;
            GrowthFractionPerSide = growthFractionPerSide;
            GrowthMmPerSide = growthMmPerSide;
            FinalSizeMm = finalSizeMm;
            MinEdgeMm = minEdgeMm;
            MinLightGapMm = minLightGapMm;
            ContourCount = contourCount;
            SegmentCount = segmentCount;
            Inverted = inverted;
            CellShape = cellShape;
            CellShapeParameter = cellShapeParameter;
            InstanceCount = instanceCount;
            OpenedSiteCount = openedSiteCount;
            DroppedCellCount = droppedCellCount;
            FinderChannelWidthMm = finderChannelWidthMm;
        }

        /// <summary>QR version the encoder chose (1..40; 0 for a fixture matrix).</summary>
        public int Version { get; }

        /// <summary>Data cells per side N (quiet zone excluded).</summary>
        public int CellCount { get; }

        /// <summary>
        /// Working-matrix cells per side M = N + 2·<see cref="QuietZoneCellsPerSide"/> —
        /// the data cells plus the fixed 4-cell light margin (ADR 0002).
        /// </summary>
        public int MatrixCellCount { get; }

        /// <summary>Cell pitch p = S/(N + 2r′) where r′ is the effective growth fraction.</summary>
        public double PitchMm { get; }

        /// <summary>Effective per-side growth as actually applied on the integer grid (≈ requested).</summary>
        public double GrowthFractionPerSide { get; }

        /// <summary>Physical per-side growth d = r′·p.</summary>
        public double GrowthMmPerSide { get; }

        /// <summary>Final grown QR outline width — equals the requested size.</summary>
        public double FinalSizeMm { get; }

        /// <summary>Smallest delivered contour edge (mm) — must clear the insertion guard.</summary>
        public double MinEdgeMm { get; }

        /// <summary>
        /// Narrowest remaining channel of the UN-emitted polarity between
        /// grown regions (mm): light channels in normal mode, dark-cell
        /// pockets under invert.
        /// </summary>
        public double MinLightGapMm { get; }

        /// <summary>Total closed contours (region outlines + holes + islands).</summary>
        public int ContourCount { get; }

        /// <summary>Total line segments the contour set delivers to the host.</summary>
        public int SegmentCount { get; }

        /// <summary>
        /// Output polarity (ADR 0002): true when the contours are the working
        /// matrix's light cells — a dark field with the code left unmarked.
        /// </summary>
        public bool Inverted { get; }

        /// <summary>
        /// The per-cell primitive this sketch carries (spec #13, ticket #38).
        /// </summary>
        public QrCellShape CellShape { get; }

        /// <summary>
        /// The shape's parameter as applied — under Circle the dot diameter
        /// as a fraction of cell pitch; 0 under Square (the field is inert).
        /// </summary>
        public double CellShapeParameter { get; }

        /// <summary>
        /// The emitted primitive instances — under Circle the delivered dot
        /// count (the sketch pattern's kept instances); 0 under Square,
        /// where emitted cells merge into contours and "instances" is not
        /// meaningful (contour/segment counts describe the square output).
        /// </summary>
        public int InstanceCount { get; }

        /// <summary>
        /// Island-bearing voids the ring split opened (spec #51, ticket #52)
        /// — 0 when the option is off or nothing nested.
        /// </summary>
        public int OpenedSiteCount { get; }

        /// <summary>
        /// Emitted cells the ring split dropped to open those voids — 0 when
        /// the option is off or nothing nested.
        /// </summary>
        public int DroppedCellCount { get; }

        /// <summary>
        /// The thin channel width the ring split cut through the three finder
        /// rings (spec #51, ticket #53), in mm — derived from the integer-grid
        /// feature floor and the recorded insertion floor, never user-entered.
        /// 0 when the option is off or no finder ring needed opening.
        /// </summary>
        public double FinderChannelWidthMm { get; }
    }

    /// <summary>
    /// The validated geometry the host inserts: nested filled regions plus a
    /// flat contour list in insertion order. Contains no SolidWorks objects.
    /// </summary>
    public sealed class QrSketch
    {
        public QrSketch(
            IReadOnlyList<QrRegion> regions, IReadOnlyList<QrContour> contours,
            IReadOnlyList<QrConstructionSquare> constructionSquares,
            QrSketchInfo info, QrCirclePlan? circlePlan = null)
        {
            Regions = regions;
            Contours = contours;
            ConstructionSquares = constructionSquares;
            Info = info;
            CirclePlan = circlePlan;
        }

        /// <summary>
        /// Top-level filled regions (disjoint dark areas of the QR pattern), in
        /// sketch-space metres at the requested size.
        /// </summary>
        public IReadOnlyList<QrRegion> Regions { get; }

        /// <summary>Every contour — region outlines, hole loops, island outlines — flat.</summary>
        public IReadOnlyList<QrContour> Contours { get; }

        /// <summary>
        /// The QR square and keep-out square in the same sketch-space metres
        /// at the requested size — construction geometry the host normalizes
        /// INTO the block definition (ADR 0003), so the footprint and the
        /// four-cell quiet-zone boundary scale and move with the instance.
        /// The QR square IS the grown outline — its span is the insertion's
        /// authoritative size measurement. Never cuttable profile and never
        /// part of <see cref="Contours"/>.
        /// </summary>
        public IReadOnlyList<QrConstructionSquare> ConstructionSquares { get; }

        public QrSketchInfo Info { get; }

        /// <summary>
        /// The circle-dot emission plan (spec #13, ticket #38) — present iff
        /// <see cref="QrSketchInfo.CellShape"/> is
        /// <see cref="QrCellShape.Circle"/>. Under Circle the contour lists
        /// are empty: the plan is the whole payload — emission mask, pitch,
        /// dot diameter, pattern grid placement and the create-time skip
        /// string for the host's native linear-pattern emitter (ticket #39).
        /// </summary>
        public QrCirclePlan? CirclePlan { get; }

        /// <summary>All contour edges in insertion order.</summary>
        public IEnumerable<SketchEdge> Edges
        {
            get
            {
                foreach (var contour in Contours)
                    foreach (var edge in contour.Edges)
                        yield return edge;
            }
        }
    }
}
