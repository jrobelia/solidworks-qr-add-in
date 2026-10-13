namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The per-cell primitive the generator emits (spec #13, ticket #32).
    /// <see cref="Square"/> is the default and the historical output — the
    /// grown rectangle every earlier ticket validated. <see cref="RoundedSquare"/>
    /// emits one polygonized rounded rectangle per emitted cell through the
    /// same union/clip/nest/validate/convert pipeline. <see cref="Circle"/>
    /// emits one K-sided polygon dot per emitted cell — always disjoint, so
    /// the corner-contact failure class disappears entirely and growth does
    /// not apply (the layout is the growth-0 map). The enum names the
    /// shape family; its single parameter
    /// (<see cref="QrSketchRequest.CellShapeParameter"/>) means whatever the
    /// shape's descriptor declares — the corner-radius fraction of the grown
    /// cell's half-width for RoundedSquare, the dot diameter as a fraction
    /// of cell pitch for Circle, unused by Square.
    /// </summary>
    public enum QrCellShape
    {
        /// <summary>The grown cell outline, unchanged (default).</summary>
        Square = 0,

        /// <summary>The grown cell with each corner cut by a polygonized quarter-arc.</summary>
        RoundedSquare = 1,

        /// <summary>
        /// One disjoint dot per emitted cell — a K-sided polygon circle of
        /// diameter = dot size % of cell pitch. Never merges: the merged-blob
        /// regimes of spec story 7 are deliberately excluded (ticket #33).
        /// </summary>
        Circle = 2,
    }

    /// <summary>
    /// Parses the cell-shape text the UI surfaces accept — the pane's shape
    /// dropdown and the probe's <c>SetQrCellShapeProbe</c> argument both
    /// speak the canonical codes below (case-insensitive, surrounding
    /// whitespace ignored). Anything else is refused by returning false —
    /// an unrecognized shape is never silently coerced to another one.
    /// Host-free Core code.
    /// </summary>
    public static class QrCellShapes
    {
        /// <summary>Canonical code for <see cref="QrCellShape.Square"/>.</summary>
        public const string SquareCode = "square";

        /// <summary>Canonical code for <see cref="QrCellShape.RoundedSquare"/>.</summary>
        public const string RoundedSquareCode = "rounded";

        /// <summary>Canonical code for <see cref="QrCellShape.Circle"/>.</summary>
        public const string CircleCode = "circle";

        /// <summary>
        /// Parses <paramref name="text"/> as a cell shape. On failure
        /// <paramref name="shape"/> is the enum default and the caller owns
        /// the error text (the probe reports the entered text verbatim).
        /// </summary>
        public static bool TryParse(string? text, out QrCellShape shape)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case SquareCode:
                case "squares":
                    shape = QrCellShape.Square;
                    return true;
                case RoundedSquareCode:
                case "roundedsquare":
                case "rounded-square":
                    shape = QrCellShape.RoundedSquare;
                    return true;
                case CircleCode:
                case "circles":
                case "dot":
                case "dots":
                    shape = QrCellShape.Circle;
                    return true;
                default:
                    shape = default;
                    return false;
            }
        }

        /// <summary>The canonical code a selector/probe round-trips for <paramref name="shape"/>.</summary>
        public static string CodeFor(QrCellShape shape) =>
            shape == QrCellShape.RoundedSquare ? RoundedSquareCode
                : shape == QrCellShape.Circle ? CircleCode
                : SquareCode;
    }
}
