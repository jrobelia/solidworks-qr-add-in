namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The recorded insertion guard for the QR geometry path (ticket 2).
    /// This is an implementation guard for what we are willing to hand to
    /// SolidWorks sketch insertion — NOT a manufacturing or scan-readability
    /// assessment.
    ///
    /// Three layers, all enforced before any host mutation:
    ///
    ///   1. Integer-grid floor: topology runs on a normalized grid of
    ///      <see cref="GridUnitsPerCell"/> units per cell. Clipper2's integer
    ///      imprecision is ±1 unit, so every feature — grown-cell edges and
    ///      the channels of the un-emitted polarity between them, i.e.
    ///      min(2g, U−2g) grid units for per-side growth g in either polarity
    ///      — must span at least
    ///      <see cref="MinGridFeatureUnits"/> units (1000× the noise floor).
    ///      Where a guard reasons about a NOMINAL gap instead of a measured
    ///      edge, <see cref="QuantizationMarginUnits"/> covers what grid
    ///      rounding can shave off the delivered value.
    ///
    ///   2. Physical floor: the smallest possible delivered feature,
    ///      min(2d, p−2d) in mm, must be ≥ <see cref="MinInsertableFeatureMm"/>.
    ///      0.05&nbsp;mm sits ~5000× above SolidWorks' ~1e-5&nbsp;mm
    ///      zero-length tolerance while leaving the fixed 25&nbsp;mm / 3%
    ///      request (smallest feature 0.071&nbsp;mm) a 40% margin.
    ///
    ///   3. Segment ceiling: the delivered contour set must not exceed
    ///      <see cref="MaxSegmentCount"/> segments — the
    ///      sketch-edit-responsiveness bound of ticket #35 (tightened to
    ///      4 400 by ticket #36's live measurement at the bound).
    ///      Polygonized circles
    ///      and rounded corners inflate per-cell segment counts ~4–8×
    ///      over squares at the same density, so a dense shaped QR can
    ///      reach an uneditable sketch long before the floors trip.
    ///      Disjoint dots can never union-merge, so under Circle the dot
    ///      count is exact BEFORE the union runs — an over-budget dots
    ///      request is refused right after emission.
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

        /// <summary>
        /// The quantization margin reserved on top of the grid floor where a
        /// gap is guaranteed NOMINALLY — the M of docs/INSERTION-GUARD.md's
        /// dot-% cap. Grid rounding displaces each polygon vertex up to
        /// ~0.71 units, so two facing dots can lose ≤ ~1.4 units of the
        /// nominal gap; reserving 2 units keeps the DELIVERED gap/edge at or
        /// above the floor.
        /// </summary>
        public const double QuantizationMarginUnits = 2.0;

        /// <summary>
        /// Arc facets per corner for the rounded-square primitive — bounded
        /// at 8 (a 32-vertex cell keeps fidelity without flooding the
        /// segment count; the delivered-edge floor bounds it further per
        /// request).
        /// </summary>
        public const int MaxFacetsPerCorner = 8;

        /// <summary>Declared numeric tolerance on the final grown outline size, in mm.</summary>
        public const double SizeToleranceMm = 1e-6;

        /// <summary>
        /// Most sketch segments the generated contours may deliver to the
        /// host — the sketch-edit-responsiveness ceiling (ticket #35,
        /// tightened by ticket #36's live measurement). The recorded
        /// evidence (docs/INSERTION-GUARD.md): at the measured
        /// 7 722-segment disjoint-dots rung (858 regions) the native
        /// insert ran ≈50.6 s and the sketch's region solve ≈103 s —
        /// past the responsiveness wall — so the provisional 8 000 is
        /// tightened to 4 400: just above the largest live-proven insert
        /// (the ~4 000-segment dense square payload of
        /// docs/ACCEPTANCE.md §F, ≈6.1 s native), keeping every recorded
        /// square payload legal while refusing the dense shaped requests
        /// that deliver minute-scale region solves.
        /// </summary>
        public const int MaxSegmentCount = 4_400;
    }
}
