namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>Why geometry generation failed — stable codes the caller/tests can branch on.</summary>
    public enum QrSketchErrorKind
    {
        /// <summary>Content was null or empty.</summary>
        EmptyContent,

        /// <summary>The encoder could not fit the payload in a version-40 QR at ECC M.</summary>
        PayloadTooLong,

        /// <summary>The encoder threw for another reason (reported as-is).</summary>
        EncoderFailure,

        /// <summary>Size was non-finite or not positive.</summary>
        InvalidSize,

        /// <summary>Growth fraction was non-finite or outside the open interval (0, 0.5).</summary>
        InvalidGrowth,

        /// <summary>Positive growth still rounds to zero on the normalized integer grid.</summary>
        GrowthRoundsToZero,

        /// <summary>Growth is close enough to 50% that it would close every
        /// one-cell channel of the un-emitted polarity.</summary>
        GrowthClosesGaps,

        /// <summary>A contour edge or un-emitted channel fell below the recorded insertion guard.</summary>
        BelowInsertionLimit,

        /// <summary>Union output failed validation (zero area, zero-length edges,
        /// self-intersection, or a residual corner-only contact).</summary>
        InvalidGeometry,
    }

    /// <summary>An actionable generation failure — what is wrong and what to change.</summary>
    public sealed class QrSketchError
    {
        public QrSketchError(QrSketchErrorKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public QrSketchErrorKind Kind { get; }
        public string Message { get; }
    }

    /// <summary>Either the validated QR sketch or one actionable error.</summary>
    public sealed class QrSketchResult
    {
        private QrSketchResult(QrSketch? sketch, QrSketchError? error)
        {
            Sketch = sketch;
            Error = error;
        }

        public QrSketch? Sketch { get; }
        public QrSketchError? Error { get; }

        public bool Succeeded => Sketch != null;

        public static QrSketchResult Ok(QrSketch sketch) => new QrSketchResult(sketch, null);

        public static QrSketchResult Fail(QrSketchErrorKind kind, string message) =>
            new QrSketchResult(null, new QrSketchError(kind, message));
    }
}
