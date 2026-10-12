using QRCoder;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #28 — the ECC-level text the probe/UI surfaces pass in
    /// ("L", "M", "Q", "H") parsed at a Core seam, so probe argument
    /// plumbing is covered without SolidWorks. An unrecognized level must
    /// be refused, never silently coerced to another level.
    /// </summary>
    public class QrEccLevelsTests
    {
        [Theory]
        [InlineData("L", QRCodeGenerator.ECCLevel.L)]
        [InlineData("M", QRCodeGenerator.ECCLevel.M)]
        [InlineData("Q", QRCodeGenerator.ECCLevel.Q)]
        [InlineData("H", QRCodeGenerator.ECCLevel.H)]
        [InlineData("l", QRCodeGenerator.ECCLevel.L)]
        [InlineData("m", QRCodeGenerator.ECCLevel.M)]
        [InlineData("q", QRCodeGenerator.ECCLevel.Q)]
        [InlineData("h", QRCodeGenerator.ECCLevel.H)]
        [InlineData("  H  ", QRCodeGenerator.ECCLevel.H)]
        [InlineData("\tQ\n", QRCodeGenerator.ECCLevel.Q)]
        public void The_four_levels_parse_case_insensitively_with_whitespace_trimmed(
            string text, QRCodeGenerator.ECCLevel expected)
        {
            bool ok = QrEccLevels.TryParse(text, out QRCodeGenerator.ECCLevel level);

            Assert.True(ok);
            Assert.Equal(expected, level);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("bogus")]
        [InlineData("ECCH")]
        [InlineData("LM")]
        [InlineData("7")]
        [InlineData("error")]
        public void Anything_outside_the_four_levels_is_refused(string? text)
        {
            bool ok = QrEccLevels.TryParse(text, out QRCodeGenerator.ECCLevel level);

            Assert.False(ok);
            Assert.Equal(default(QRCodeGenerator.ECCLevel), level);
        }
    }
}
