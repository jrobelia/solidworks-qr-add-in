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
    }
}
