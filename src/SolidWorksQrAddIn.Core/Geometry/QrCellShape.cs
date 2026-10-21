using System;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The per-cell primitive the generator emits (spec #13, ticket #38 —
    /// the re-scoped two-option selector; PR #37's rounded/polygonized
    /// machinery does not port). <see cref="Square"/> is the default and the
    /// historical output — the grown, unioned contour every earlier ticket
    /// validated. <see cref="Circle"/> emits a <see cref="QrCirclePlan"/> —
    /// one real circle dot per emitted cell, delivered through SolidWorks'
    /// native sketch linear pattern (docs/PATTERN-SPIKE.md): no Clipper, no
    /// contours, no block. Its single parameter
    /// (<see cref="QrSketchRequest.CellShapeParameter"/>) is the dot
    /// DIAMETER as a fraction of the cell pitch; under Square the parameter
    /// is unused.
    /// </summary>
    public enum QrCellShape
    {
        /// <summary>The grown cell outline, unchanged (default).</summary>
        Square = 0,

        /// <summary>
        /// One disjoint true-circle dot per emitted cell — diameter = dot
        /// size % of cell pitch. Growth never applies: dots are already
        /// disjoint, so the dot size is the whole sizing knob.
        /// </summary>
        Circle = 1,
    }

    /// <summary>
    /// Parses the cell-shape text the UI surfaces accept — the pane's shape
    /// dropdown and the probe's <c>SetQrCellShapeProbe</c> argument both
    /// speak the canonical codes below (case-insensitive, surrounding
    /// whitespace ignored) — the same strictness
    /// <see cref="QrEccLevels"/> applies. Anything else is refused by
    /// returning false — an unrecognized shape is never silently coerced
    /// to another one. Host-free Core code.
    /// </summary>
    public static class QrCellShapes
    {
        /// <summary>Canonical code for <see cref="QrCellShape.Square"/>.</summary>
        public const string SquareCode = "square";

        /// <summary>Canonical code for <see cref="QrCellShape.Circle"/>.</summary>
        public const string CircleCode = "circle";

        /// <summary>
        /// Parses <paramref name="text"/> as a cell shape. On failure
        /// <paramref name="shape"/> is the enum default and the caller owns
        /// the error text (the probe reports the entered text verbatim).
        /// </summary>
        public static bool TryParse(string? text, out QrCellShape shape)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case SquareCode:
                    shape = QrCellShape.Square;
                    return true;
                case CircleCode:
                    shape = QrCellShape.Circle;
                    return true;
                default:
                    shape = default;
                    return false;
            }
        }

        /// <summary>
        /// The canonical code a selector/probe round-trips for
        /// <paramref name="shape"/> — strict, symmetric with
        /// <see cref="TryParse"/>: an enum value with no code has no
        /// canonical spelling, and silently spelling it "square" would
        /// write the wrong shape into the session/probe surface.
        /// </summary>
        public static string CodeFor(QrCellShape shape)
        {
            switch (shape)
            {
                case QrCellShape.Square: return SquareCode;
                case QrCellShape.Circle: return CircleCode;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape,
                        "The cell shape has no canonical code.");
            }
        }

        // ----- the small per-shape descriptor -----
        // The facts the session's field plumbing switches on, kept beside
        // the codes so a new shape family updates one place (the generator's
        // primitive dispatch stays in QrSketchGenerator).

        /// <summary>
        /// Whether the per-side growth field applies to
        /// <paramref name="shape"/> — false under Circle, whose dots are
        /// already disjoint and size off the shape parameter alone (the
        /// growth text is kept verbatim but never consulted — the same
        /// inert-field treatment the shape parameter gets under Square).
        /// </summary>
        public static bool GrowthApplies(QrCellShape shape) =>
            shape != QrCellShape.Circle;

        /// <summary>
        /// Whether the shape-parameter field applies to
        /// <paramref name="shape"/> — only Circle's dot size; the pane
        /// disables the field under Square.
        /// </summary>
        public static bool ShapeParameterApplies(QrCellShape shape) =>
            shape == QrCellShape.Circle;

        /// <summary>
        /// Whether the ring-split option (spec #51) applies to
        /// <paramref name="shape"/> — only Square, the sole contour-producing
        /// variant. Under Circle the toggle is inert (dots emit no contours,
        /// nothing nests) and the pane hides the control: a shape with no
        /// contours never offers a meaningless control. The stored flag
        /// itself round-trips verbatim either way — the same inert-field
        /// discipline the growth and dot-size texts follow.
        /// </summary>
        public static bool RingSplitApplies(QrCellShape shape) =>
            shape == QrCellShape.Square;

        /// <summary>
        /// The shape-parameter field's label as the pane should show it —
        /// Circle's dot size as a percent of cell pitch (the dot diameter).
        /// </summary>
        public static string ShapeParameterLabel(QrCellShape shape) =>
            "Dot size (% of cell pitch):";
    }
}
