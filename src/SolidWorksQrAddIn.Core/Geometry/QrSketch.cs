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
        public IEnumerable<SketchEdge> Edges
        {
            get
            {
                for (var i = 0; i < Vertices.Count; i++)
                    yield return new SketchEdge(Vertices[i], Vertices[(i + 1) % Vertices.Count]);
            }
        }
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
            int contourCount, int segmentCount)
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

        /// <summary>Narrowest remaining light channel between grown regions (mm).</summary>
        public double MinLightGapMm { get; }

        /// <summary>Total closed contours (region outlines + holes + islands).</summary>
        public int ContourCount { get; }

        /// <summary>Total line segments the contour set delivers to the host.</summary>
        public int SegmentCount { get; }
    }

    /// <summary>
    /// The validated geometry the host inserts: nested filled regions plus a
    /// flat contour list in insertion order. Contains no SolidWorks objects.
    /// </summary>
    public sealed class QrSketch
    {
        public QrSketch(IReadOnlyList<QrRegion> regions, IReadOnlyList<QrContour> contours, QrSketchInfo info)
        {
            Regions = regions;
            Contours = contours;
            Info = info;
        }

        /// <summary>Top-level filled regions (disjoint dark areas of the QR pattern).</summary>
        public IReadOnlyList<QrRegion> Regions { get; }

        /// <summary>Every contour — region outlines, hole loops, island outlines — flat.</summary>
        public IReadOnlyList<QrContour> Contours { get; }

        public QrSketchInfo Info { get; }

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
