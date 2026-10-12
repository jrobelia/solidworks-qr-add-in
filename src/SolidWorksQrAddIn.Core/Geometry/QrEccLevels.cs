using QRCoder;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// Parses the error-correction level text the UI surfaces accept —
    /// the pane's ECC dropdown and the probe's <c>SetQrEccProbe</c> argument
    /// both speak "L", "M", "Q" or "H" (case-insensitive, surrounding
    /// whitespace ignored). Anything else is refused by returning false —
    /// an unrecognized level is never silently coerced to another one.
    /// Host-free Core code.
    /// </summary>
    public static class QrEccLevels
    {
        /// <summary>
        /// Parses <paramref name="text"/> as an ECC level. On failure
        /// <paramref name="level"/> is the enum default and the caller owns
        /// the error text (the probe reports the entered text verbatim).
        /// </summary>
        public static bool TryParse(string? text, out QRCodeGenerator.ECCLevel level)
        {
            switch ((text ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "L": level = QRCodeGenerator.ECCLevel.L; return true;
                case "M": level = QRCodeGenerator.ECCLevel.M; return true;
                case "Q": level = QRCodeGenerator.ECCLevel.Q; return true;
                case "H": level = QRCodeGenerator.ECCLevel.H; return true;
                default: level = default; return false;
            }
        }
    }
}
