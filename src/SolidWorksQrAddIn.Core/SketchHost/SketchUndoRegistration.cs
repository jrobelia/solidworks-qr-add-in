using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;

namespace SolidWorksQrAddIn.Core.Hosting
{
    /// <summary>
    /// Everything a host needs to register a custom undo step for the sketch
    /// a committed insertion just produced — the step's display name, the
    /// created sketch feature's name, and the STAGED model: the contour
    /// edges in sketch-space metres at the final size plus the construction
    /// squares in the same metre space. A redo re-stages all of it
    /// through the same buffered insertion session — block instance,
    /// squares included.
    /// Contains no SolidWorks objects; the host binds it to its own
    /// document/feature references.
    /// </summary>
    public sealed class SketchUndoRegistration
    {
        public SketchUndoRegistration(
            string undoName, string sketchName, IReadOnlyList<SketchEdge> edges,
            IReadOnlyList<QrConstructionSquare>? constructionSquares = null,
            QrCirclePlan? circlePlan = null)
        {
            UndoName = undoName;
            SketchName = sketchName;
            Edges = edges;
            ConstructionSquares = constructionSquares;
            CirclePlan = circlePlan;
        }

        /// <summary>The undo step's display name (SketchHostContract.CreationUndoName).</summary>
        public string UndoName { get; }

        /// <summary>Name of the sketch feature the step must undo.</summary>
        public string SketchName { get; }

        /// <summary>The inserted segments in sketch-space metres, insertion order.</summary>
        public IReadOnlyList<SketchEdge> Edges { get; }

        /// <summary>
        /// The construction squares the command staged, or null when the
        /// creation carried none — a redo re-stages them inside the block
        /// definition (or beside the circle pattern) so the squares survive
        /// an undo/redo cycle.
        /// </summary>
        public IReadOnlyList<QrConstructionSquare>? ConstructionSquares { get; }

        /// <summary>
        /// The circle-dot emission plan the command staged, or null for a
        /// contour/block creation — a redo re-stages it through
        /// <see cref="ISketchInsertion.AddCirclePlan"/>.
        /// </summary>
        public QrCirclePlan? CirclePlan { get; }
    }
}
