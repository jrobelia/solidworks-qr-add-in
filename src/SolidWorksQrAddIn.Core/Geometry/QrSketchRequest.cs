using QRCoder;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// Everything the geometry-generation module needs to encode and lay out
    /// one QR sketch: the payload, the final QR code size, the per-side
    /// outline growth, and the settings that shape the encoding. The request
    /// is the seam's input data — settings grow here as fields (error
    /// correction today) instead of adding parameters or methods.
    /// Contains no SolidWorks objects.
    /// </summary>
    public sealed class QrSketchRequest
    {
        public QrSketchRequest(string content, double sizeMm, double growthFractionPerSide)
        {
            Content = content;
            SizeMm = sizeMm;
            GrowthFractionPerSide = growthFractionPerSide;
        }

        /// <summary>The payload to encode — verbatim, meaningful whitespace included.</summary>
        public string Content { get; }

        /// <summary>Final QR code outline width (mm), excluding the quiet zone.</summary>
        public double SizeMm { get; }

        /// <summary>Per-side outline growth as a fraction of cell width, in (0, 0.5).</summary>
        public double GrowthFractionPerSide { get; }

        /// <summary>
        /// QR error-correction level L/M/Q/H — maps 1:1 onto
        /// <see cref="QRCodeGenerator.ECCLevel"/>: a denser level buys damage
        /// tolerance with more cells (a higher QR version for the same
        /// payload). Default M — the historical behaviour.
        /// </summary>
        public QRCodeGenerator.ECCLevel EccLevel { get; set; } = QRCodeGenerator.ECCLevel.M;

        /// <summary>
        /// Output polarity (ADR 0002): false emits the dark cells — black code
        /// on a light field; true emits the LIGHT cells of the whole working
        /// matrix — a dark field with the code left unmarked, bounded at the
        /// matrix edge, for marking/engraving workflows. Default false.
        /// </summary>
        public bool Invert { get; set; }

        /// <summary>
        /// The per-cell primitive to emit (spec #13, ticket #32): the
        /// grown-cell outline shape the union is built from. Default
        /// <see cref="QrCellShape.Square"/> — the historical output; existing
        /// requests are unchanged.
        /// </summary>
        public QrCellShape CellShape { get; set; } = QrCellShape.Square;

        /// <summary>
        /// The selected shape's parameter — for
        /// <see cref="QrCellShape.RoundedSquare"/> the corner radius as a
        /// fraction of the grown cell's half-width in [0, 1] (0 emits the
        /// square primitive); for <see cref="QrCellShape.Circle"/> the dot
        /// size — the circle diameter as a fraction of cell pitch in (0, 1],
        /// further capped so neighbouring dots keep a full-floor gap
        /// (docs/INSERTION-GUARD.md). Inert for shapes that declare no
        /// parameter — Square ignores it entirely, so the pane can keep the
        /// field disabled without clearing it.
        /// </summary>
        public double CellShapeParameter { get; set; }
    }
}
