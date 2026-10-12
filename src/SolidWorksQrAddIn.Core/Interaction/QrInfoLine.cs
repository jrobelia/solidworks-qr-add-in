namespace SolidWorksQrAddIn.Core.Interaction
{
    /// <summary>
    /// One label/value row of the pane's info section — plain text pairs the
    /// view renders as uneditable text. Produced by
    /// <see cref="QrSketchSession"/> so every formatted value is headlessly
    /// testable; the view never formats numbers itself.
    /// </summary>
    public sealed class QrInfoLine
    {
        public QrInfoLine(string label, string value)
        {
            Label = label;
            Value = value;
        }

        /// <summary>What the fact is called ("QR outline", "Quiet zone").</summary>
        public string Label { get; }

        /// <summary>The formatted value, already carrying its unit symbol.</summary>
        public string Value { get; }
    }
}
