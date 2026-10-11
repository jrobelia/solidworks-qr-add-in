using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;

namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>
    /// Everything a host needs to register a custom undo step for the sketch
    /// a committed insertion just produced — the step's display name, the
    /// created sketch feature's name, and the STAGED model: the contour
    /// edges in sketch-space metres at the final size plus the construction
    /// layout (construction squares + dimension plan). A redo re-stages all
    /// of it through the same buffered insertion session — block instance,
    /// squares and dimension included.
    /// Contains no SolidWorks objects; the host binds it to its own
    /// document/feature references.
    /// </summary>
    public sealed class SketchUndoRegistration
    {
        public SketchUndoRegistration(
            string undoName, string sketchName, IReadOnlyList<SketchEdge> edges,
            IReadOnlyList<QrConstructionSquare>? constructionSquares = null,
            QrDimensionSpec? dimensionSpec = null)
        {
            UndoName = undoName;
            SketchName = sketchName;
            Edges = edges;
            ConstructionSquares = constructionSquares;
            DimensionSpec = dimensionSpec;
        }

        /// <summary>The undo step's display name (SketchHostContract.CreationUndoName).</summary>
        public string UndoName { get; }

        /// <summary>Name of the sketch feature the step must undo.</summary>
        public string SketchName { get; }

        /// <summary>The inserted segments in sketch-space metres, insertion order.</summary>
        public IReadOnlyList<SketchEdge> Edges { get; }

        /// <summary>
        /// The construction squares the command staged, or null when the
        /// creation carried no layout — a redo re-stages them so the square
        /// and its dimension survive an undo/redo cycle.
        /// </summary>
        public IReadOnlyList<QrConstructionSquare>? ConstructionSquares { get; }

        /// <summary>The dimension plan staged with the squares, or null.</summary>
        public QrDimensionSpec? DimensionSpec { get; }
    }
}
