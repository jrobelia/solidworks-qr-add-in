namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The recorded insertion guard for the QR geometry path (ticket 2).
    /// This is an implementation guard for what we are willing to hand to
    /// SolidWorks sketch insertion — NOT a manufacturing or scan-readability
    /// assessment.
    ///
    /// Two layers, both enforced before any host mutation:
    ///
    ///   1. Integer-grid floor: topology runs on a normalized grid of
    ///      <see cref="GridUnitsPerCell"/> units per cell. Clipper2's integer
    ///      imprecision is ±1 unit, so every feature — grown-cell edges and
    ///      the light channels between them, i.e. min(2g, U−2g) grid units for
    ///      per-side growth g — must span at least
    ///      <see cref="MinGridFeatureUnits"/> units (1000× the noise floor).
    ///
    ///   2. Physical floor: the smallest possible delivered feature,
    ///      min(2d, p−2d) in mm, must be ≥ <see cref="MinInsertableFeatureMm"/>.
    ///      0.05&nbsp;mm sits ~5000× above SolidWorks' ~1e-5&nbsp;mm
    ///      zero-length tolerance while leaving the fixed 25&nbsp;mm / 3%
    ///      request (smallest feature 0.071&nbsp;mm) a 40% margin.
    ///
    /// Requests that violate either layer are rejected with an actionable
    /// error — never silently substituted.
    /// </summary>
    public static class QrInsertionGuard
    {
        /// <summary>Integer grid units per QR data cell used for topology ops.</summary>
        public const int GridUnitsPerCell = 10_000;

        /// <summary>Every feature must span at least this many grid units.</summary>
        public const int MinGridFeatureUnits = 10;

        /// <summary>Smallest deliverable edge or light channel, in mm.</summary>
        public const double MinInsertableFeatureMm = 0.05;

        /// <summary>Declared numeric tolerance on the final grown outline size, in mm.</summary>
        public const double SizeToleranceMm = 1e-6;
    }
}
