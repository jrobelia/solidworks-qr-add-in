namespace SolidWorksQrAddIn.Core.Payload
{
    /// <summary>
    /// Why payload composition failed — stable codes the caller/tests can
    /// branch on, mirroring <c>QrSketchErrorKind</c> in the geometry module.
    /// </summary>
    public enum PayloadComposeErrorKind
    {
        /// <summary>The preset id names no declared preset.</summary>
        UnknownPreset,

        /// <summary>A required field was absent or blank.</summary>
        MissingField,

        /// <summary>
        /// A field held a value its preset cannot encode — a non-option
        /// choice, a non-numeric coordinate, a non-phone number, an address
        /// without '@', a password for a secured network left empty.
        /// </summary>
        InvalidField,

        /// <summary>The underlying PayloadGenerator threw (reported as-is).</summary>
        GeneratorFailure,
    }

    /// <summary>An actionable composition failure — which field is wrong and why.</summary>
    public sealed class PayloadComposeError
    {
        public PayloadComposeError(PayloadComposeErrorKind kind, string? fieldKey, string message)
        {
            Kind = kind;
            FieldKey = fieldKey;
            Message = message;
        }

        public PayloadComposeErrorKind Kind { get; }

        /// <summary>
        /// The <see cref="PayloadFieldDescriptor.Key"/> of the offending
        /// field so the pane can flag the right input; null when the failure
        /// is not field-specific (unknown preset, generator failure).
        /// </summary>
        public string? FieldKey { get; }

        public string Message { get; }
    }

    /// <summary>
    /// Either the literal content string that would be encoded — whitespace
    /// and line breaks preserved exactly — or one actionable error. Mirrors
    /// the <c>QrSketchResult</c> idiom: failures return, never throw.
    /// </summary>
    public sealed class PayloadComposeResult
    {
        private PayloadComposeResult(string? content, PayloadComposeError? error)
        {
            Content = content;
            Error = error;
        }

        /// <summary>
        /// The literal payload text, or null on failure. This is exactly what
        /// the content box shows and what the encoder consumes — verbatim.
        /// </summary>
        public string? Content { get; }

        public PayloadComposeError? Error { get; }

        public bool Succeeded => Content != null;

        public static PayloadComposeResult Ok(string content) =>
            new PayloadComposeResult(content, null);

        public static PayloadComposeResult Fail(
            PayloadComposeErrorKind kind, string? fieldKey, string message) =>
            new PayloadComposeResult(null, new PayloadComposeError(kind, fieldKey, message));
    }
}
