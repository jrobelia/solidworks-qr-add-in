using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// Which construction square of the QR layout a model element describes
    /// (GLOSSARY.md: QR square, keep-out square).
    /// </summary>
    public enum QrSquareRole
    {
        /// <summary>
        /// The square bounding the grown QR-cell extent — carries the driven
        /// dimension (the live size readout).
        /// </summary>
        QrSquare,

        /// <summary>
        /// The working-matrix boundary square — the quiet-zone clearance
        /// marker (ADR 0002), equation-linked to the QR square's dimension.
        /// </summary>
        KeepOutSquare,
    }

    /// <summary>
    /// One construction square emitted for the host sketch: four corner
    /// vertices in sketch-space metres, closed-loop convention like
    /// <see cref="QrContour"/> (the last vertex connects back to the first).
    /// Always construction geometry — a bounds/clearance marker the engineer
    /// can constrain and dimension, never cuttable profile.
    /// </summary>
    public sealed class QrConstructionSquare
    {
        public QrConstructionSquare(QrSquareRole role, IReadOnlyList<SketchPoint2> vertices)
        {
            Role = role;
            Vertices = vertices;
        }

        /// <summary>Which square this is in the QR layout.</summary>
        public QrSquareRole Role { get; }

        /// <summary>The four corner vertices in sketch-space metres.</summary>
        public IReadOnlyList<SketchPoint2> Vertices { get; }

        /// <summary>Always true — squares emit as construction geometry.</summary>
        public bool IsConstruction => true;

        /// <summary>The square's four edges; the final edge closes the loop.</summary>
        public IEnumerable<SketchEdge> Edges
        {
            get
            {
                for (var i = 0; i < Vertices.Count; i++)
                    yield return new SketchEdge(Vertices[i], Vertices[(i + 1) % Vertices.Count]);
            }
        }
    }

    /// <summary>
    /// The dimension plan for the construction squares — model types only, no
    /// host objects (ADR 0001). The QR square carries the driven dimension —
    /// a readout that tracks the size, never drives it; the keep-out square's
    /// side dimension is equation-linked to it so the total footprint stays a
    /// live readout when the block is rescaled.
    /// </summary>
    public sealed class QrDimensionSpec
    {
        public QrDimensionSpec(double keepOutSideOffsetCells)
        {
            KeepOutSideOffsetCells = keepOutSideOffsetCells;
        }

        /// <summary>The square carrying the driven (reference readout) dimension.</summary>
        public QrSquareRole DrivenSquare => QrSquareRole.QrSquare;

        /// <summary>The square whose side dimension is equation-linked to the driven one.</summary>
        public QrSquareRole EquationLinkedSquare => QrSquareRole.KeepOutSquare;

        /// <summary>
        /// The equation relation in cell-pitch units: keep-out side = QR-square
        /// side + this many pitches. Per ADR 0002 the keep-out is the
        /// working-matrix boundary, so the offset is 2·(quiet − r′) = 8 − 2r′
        /// cell pitches (per side: 4 − r′ cells of light margin past the grown
        /// outline).
        /// </summary>
        public double KeepOutSideOffsetCells { get; }
    }
}
