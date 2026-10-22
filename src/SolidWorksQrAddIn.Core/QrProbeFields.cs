using System;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;

namespace SolidWorksQrAddIn.Core
{
    /// <summary>
    /// The field vocabulary behind <see cref="IAddInProbe.SetQrField"/> —
    /// the same names <see cref="IAddInProbe.WindowStateProbe"/> echoes for
    /// the session fields (<c>size</c>, <c>growth</c>, <c>preset</c>,
    /// <c>ecc</c>, <c>invert</c>, <c>ringsplit</c>, <c>shape</c>,
    /// <c>param</c>) plus <c>content</c> for the payload box itself, so a
    /// new session field stops touching the probe interface. Any other
    /// name is a preset-field key (<c>ssid</c>, <c>template</c>, …), passed
    /// through verbatim — the session names an undeclared key in its own
    /// refusal, so no second vocabulary lives here. Lives in Core (not the
    /// add-in) so the mapping is host-free and headlessly testable.
    /// </summary>
    public static class QrProbeFields
    {
        /// <summary>The payload box — <see cref="QrSketchSession.SetContent"/>.</summary>
        public const string ContentField = "content";
        /// <summary>The size text — <see cref="QrSketchSession.SetSizeText(string)"/>.</summary>
        public const string SizeField = "size";
        /// <summary>The growth text — <see cref="QrSketchSession.SetGrowthText"/>.</summary>
        public const string GrowthField = "growth";
        /// <summary>The ECC selector — <see cref="QrSketchSession.SetEccLevel"/>.</summary>
        public const string EccField = "ecc";
        /// <summary>The polarity toggle — <see cref="QrSketchSession.SetInvert"/>.</summary>
        public const string InvertField = "invert";
        /// <summary>The ring-split toggle — <see cref="QrSketchSession.SetRingSplit"/>.</summary>
        public const string RingSplitField = "ringsplit";
        /// <summary>The cell-shape selector — <see cref="QrSketchSession.SetCellShape"/>.</summary>
        public const string ShapeField = "shape";
        /// <summary>The shape-parameter text — <see cref="QrSketchSession.SetShapeParameterText"/>.</summary>
        public const string ShapeParameterField = "param";
        /// <summary>The content-type selector — <see cref="QrSketchSession.SetContentType"/>.</summary>
        public const string ContentTypeField = "preset";

        /// <summary>
        /// Resolves a probe (name, value) pair into the session edit it
        /// stands for. Session-field names match case-insensitively on the
        /// trimmed text; preset-field keys go through verbatim (they are
        /// case-sensitive — <c>firstName</c>, never <c>firstname</c>).
        /// <c>content</c> always names the payload box: under Raw the
        /// preset-field path converges on the same setter anyway, so the
        /// one colliding key is unambiguous.
        ///
        /// Returns false only for a harness-level refusal — a value that
        /// can never reach the session (an unparseable ECC level, cell
        /// shape or toggle text, or a missing field name). Those never
        /// invalidate the pending request, exactly like the setters they
        /// replace; <paramref name="error"/> then carries the full
        /// "ERROR ..." line the probe answers.
        /// </summary>
        public static bool TryResolve(
            string? name, string? value,
            out Func<QrSketchSession, QrSketchResult>? edit, out string? error)
        {
            edit = null;
            error = null;
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case ContentField:
                    edit = s => s.SetContent(value ?? string.Empty);
                    return true;
                case SizeField:
                    edit = s => s.SetSizeText(value ?? string.Empty);
                    return true;
                case GrowthField:
                    edit = s => s.SetGrowthText(value ?? string.Empty);
                    return true;
                case EccField:
                    if (!QrEccLevels.TryParse(value, out var ecc))
                    {
                        error = $"ERROR unknown ECC level '{value}' — expected L, M, Q or H.";
                        return false;
                    }
                    edit = s => s.SetEccLevel(ecc);
                    return true;
                case InvertField:
                    if (!TryParseToggle(value, out var invert))
                    {
                        error = $"ERROR unknown invert value '{value}' — expected true or false.";
                        return false;
                    }
                    edit = s => s.SetInvert(invert);
                    return true;
                case RingSplitField:
                    if (!TryParseToggle(value, out var ringSplit))
                    {
                        error = $"ERROR unknown ringsplit value '{value}' — expected true or false.";
                        return false;
                    }
                    edit = s => s.SetRingSplit(ringSplit);
                    return true;
                case ShapeField:
                    if (!QrCellShapes.TryParse(value, out var cellShape))
                    {
                        error = $"ERROR unknown cell shape '{value}' — expected " +
                                $"{QrCellShapes.SquareCode} or {QrCellShapes.CircleCode}.";
                        return false;
                    }
                    edit = s => s.SetCellShape(cellShape);
                    return true;
                case ShapeParameterField:
                    edit = s => s.SetShapeParameterText(value ?? string.Empty);
                    return true;
                case ContentTypeField:
                    edit = s => s.SetContentType(value);
                    return true;
                default:
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        error = "ERROR missing field name — expected one of " +
                            $"{ContentField}, {SizeField}, {GrowthField}, {EccField}, {InvertField}, " +
                            $"{RingSplitField}, {ShapeField}, {ShapeParameterField}, {ContentTypeField} " +
                            "or a preset-field key.";
                        return false;
                    }
                    edit = s => s.SetPresetField(name!, value);
                    return true;
            }
        }

        /// <summary>The pane's checkbox text — "true"/"false", case-insensitive, trimmed.</summary>
        private static bool TryParseToggle(string? text, out bool on)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "true": on = true; return true;
                case "false": on = false; return true;
                default: on = default; return false;
            }
        }
    }
}
