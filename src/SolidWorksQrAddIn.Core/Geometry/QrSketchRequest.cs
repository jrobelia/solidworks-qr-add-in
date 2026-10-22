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
        /// The per-cell primitive (spec #13, ticket #38) —
        /// <see cref="QrCellShape.Square"/>, the grown contour output and the
        /// default, or <see cref="QrCellShape.Circle"/>, one real circle dot
        /// per emitted cell delivered as a <see cref="QrCirclePlan"/> for the
        /// host's native linear-pattern emitter. Default Square — identical
        /// requests stay byte-identical to the historical output.
        /// </summary>
        public QrCellShape CellShape { get; set; } = QrCellShape.Square;

        /// <summary>
        /// The shape's one parameter — under Circle the dot DIAMETER as a
        /// fraction of cell pitch, bounded per request so the delivered
        /// dot-to-dot gap stays at or above the insertion floor
        /// (docs/INSERTION-GUARD.md). Inert under Square — the value is
        /// carried, never consulted.
        /// </summary>
        public double CellShapeParameter { get; set; }

        /// <summary>
        /// Opt-in ring split (spec #51, tickets #52/#53): under the Square
        /// cell shape, open every island-bearing hole in the emitted set —
        /// the three finder rings by a thin corner-locus channel subtracted
        /// from the grown subject, every other site by the minimal
        /// module-aligned cell path joining the hole's void to the
        /// exterior-connected un-emitted flood — so no ring → hole →
        /// island nesting survives for SolidWorks' automatic profile
        /// selection. Default false — option-off output is byte-identical
        /// to the historical pipeline. Inert under Circle (dots have no
        /// contours); refused below error-correction level M.
        /// </summary>
        public bool RingSplit { get; set; }
    }
}
