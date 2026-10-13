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
    ///      the channels of the un-emitted polarity between them, i.e.
    ///      min(2g, U−2g) grid units for per-side growth g in either polarity
    ///      — must span at least
    ///      <see cref="MinGridFeatureUnits"/> units (1000× the noise floor).
    ///
    ///   2. Physical floor: the smallest possible delivered feature,
    ///      min(2d, p−2d) in mm, must be ≥ <see cref="MinInsertableFeatureMm"/>.
    ///      0.05&nbsp;mm sits ~5000× above SolidWorks' ~1e-5&nbsp;mm
    ///      zero-length tolerance while leaving the fixed 25&nbsp;mm / 3%
    ///      request (smallest feature 0.071&nbsp;mm) a 40% margin.
    ///
    ///   3. Segment ceiling: the delivered contour set must not exceed
    ///      <see cref="MaxSegmentCount"/> segments — the
    ///      sketch-edit-responsiveness bound of ticket #35 (provisional
    ///      pending ticket #36's live measurement). Polygonized circles
    ///      and rounded corners inflate per-cell segment counts ~4–8×
    ///      over squares at the same density, so a dense shaped QR can
    ///      reach an uneditable sketch long before the floors trip.
    ///
    /// Requests that violate any layer are rejected with an actionable
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

        /// <summary>
        /// Most sketch segments the generated contours may deliver to the
        /// host — the sketch-edit-responsiveness ceiling (ticket #35).
        /// PROVISIONAL pending ticket #36's live measurement of insertion
        /// time and sketch-edit responsiveness at the bound, which may
        /// tighten it. The recorded evidence (docs/INSERTION-GUARD.md):
        /// the largest live-proven insert is the ~3 900-segment dense
        /// square payload of docs/ACCEPTANCE.md §F (≈6.1 s native) —
        /// 8 000 leaves ~2× headroom over it while refusing the
        /// ≥8.3k-segment requests the shaped cells inflate the same
        /// matrices into.
        /// </summary>
        public const int MaxSegmentCount = 8_000;
    }
}
