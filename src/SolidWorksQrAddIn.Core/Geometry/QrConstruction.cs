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
        /// The square bounding the grown QR-cell extent — the block
        /// instance's visible footprint, spanning exactly ±0.5 in
        /// block-definition units.
        /// </summary>
        QrSquare,

        /// <summary>
        /// The working-matrix boundary square — the quiet-zone clearance
        /// marker (ADR 0002), the QR square enlarged by the fixed four-cell
        /// margin per side.
        /// </summary>
        KeepOutSquare,
    }

    /// <summary>
    /// One construction square that rides INSIDE the QR sketch block
    /// definition (ADR 0003): four corner vertices in sketch-space metres
    /// at the requested size — the same space <see cref="QrSketch.Contours"/>
    /// uses — closed-loop convention like <see cref="QrContour"/> (the last
    /// vertex connects back to the first). The host normalizes the square
    /// into the unit-proportioned definition alongside the contours and
    /// flags it construction, so it scales and moves with the block
    /// instance, renders as a dashed bounds/clearance marker, and never
    /// feeds an extrude profile. The QR square IS the grown outline — its
    /// span is the insertion's authoritative size measurement; the
    /// keep-out square deliberately exceeds it.
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

        /// <summary>
        /// The four corner vertices in sketch-space metres at the requested
        /// size — NOT normalized; the host divides by the block scale to
        /// land the square inside the unit-proportioned definition.
        /// </summary>
        public IReadOnlyList<SketchPoint2> Vertices { get; }

        /// <summary>The square's four edges; the final edge closes the loop.</summary>
        public IEnumerable<SketchEdge> Edges => SketchClosedLoop.Edges(Vertices);
    }
}
