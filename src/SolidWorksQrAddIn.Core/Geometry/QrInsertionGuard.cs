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

        /// <summary>
        /// Most sketch segments the generated contours may deliver to the
        /// host — the sketch-edit-responsiveness ceiling (spec-13 tickets
        /// #35/#36, ported by ticket #38). The recorded evidence: at the
        /// measured 7 722-segment disjoint-dots rung (858 regions) the
        /// native insert ran ≈50.6 s and the sketch's region solve ≈103 s —
        /// past the responsiveness wall — so the provisional 8 000 was
        /// tightened to 4 400: just above the largest live-proven insert
        /// (the ~4 000-segment dense square payload of docs/ACCEPTANCE.md
        /// §F, ≈6.1 s native), keeping every recorded square payload legal
        /// while refusing requests that would deliver minute-scale region
        /// solves. Applies to the contour pipeline only — Circle emits a
        /// circle plan of native pattern instances, not segments.
        /// </summary>
        public const int MaxSegmentCount = 4_400;

        /// <summary>
        /// Most pattern instances — delivered circle dots — a Circle
        /// request may hand to the host's sketch linear pattern (spec #13,
        /// ticket #41). Under real circles there are no contour segments:
        /// each kept instance is one ARC entity and one closed sketch
        /// region, so the edit-responsiveness driver that
        /// <see cref="MaxSegmentCount"/> proxies under Square becomes the
        /// emitted-dot count itself — this bound counts the plan's
        /// post-invert EMITTED dots, not the N² grid. Sized by the live
        /// production-path ladder (docs/INSERTION-GUARD.md): the largest
        /// rung inside the responsiveness budget emitted 3 232 dots
        /// (v12-invert, ≈71 s create / ≈62 s sketch re-solve) and the next
        /// rung, 4 045 dots (v18), ran ≈122 s to create and ≈97 s to
        /// re-solve — past the wall — so 3 600 sits ~1.1× over the
        /// largest proven insert, the same methodology that set
        /// <see cref="MaxSegmentCount"/>.
        /// </summary>
        public const int MaxCircleInstances = 3_600;
    }
}
