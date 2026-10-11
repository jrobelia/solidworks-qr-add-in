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
    /// definition (ADR 0003): four corner vertices in unit block-definition
    /// space — the same space <see cref="QrSketch.Unit"/> contours use, where
    /// the grown QR outline spans exactly 1×1 — closed-loop convention like
    /// <see cref="QrContour"/> (the last vertex connects back to the first).
    /// Always construction geometry inside the definition, so it scales and
    /// moves with the block instance, renders as a dashed bounds/clearance
    /// marker, and never feeds an extrude profile.
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
        /// The four corner vertices in unit block-definition space — NOT
        /// sketch metres; the host draws them unscaled so the definition
        /// carries the squares and the instance projects them.
        /// </summary>
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
}
