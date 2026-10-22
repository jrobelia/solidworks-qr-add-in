using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The ring→hole→island nesting census of a generated sketch (spec #51,
    /// ticket #55) — the module-side expectation the live gates assert
    /// against the host's sketch-region census. Pure geometry: walks the
    /// region/hole/island tree of <see cref="QrSketch.Regions"/> and counts
    /// what SolidWorks' region solver enumerates on a datum-plane support —
    /// every material face (a top-level region or an island nested in a
    /// hole) is one <c>ISketchRegion</c>, and every hole interior is one
    /// void region, whether or not it bears islands.
    /// </summary>
    public sealed class QrNestingCensus
    {
        public QrNestingCensus(
            int topLevelRegions, int holeCount, int islandCount,
            int islandBearingHoleCount)
        {
            TopLevelRegions = topLevelRegions;
            HoleCount = holeCount;
            IslandCount = islandCount;
            IslandBearingHoleCount = islandBearingHoleCount;
        }

        /// <summary>Top-level filled regions (<see cref="QrSketch.Regions"/>).</summary>
        public int TopLevelRegions { get; }

        /// <summary>Every enclosed hole boundary, at every nesting depth.</summary>
        public int HoleCount { get; }

        /// <summary>Every filled island nested inside a hole, at every depth.</summary>
        public int IslandCount { get; }

        /// <summary>
        /// Holes that still hold at least one island — the "doubly nested"
        /// condition SolidWorks' automatic profile selection refuses and the
        /// ring split must eliminate (an option-on census is always 0).
        /// </summary>
        public int IslandBearingHoleCount { get; }

        /// <summary>No ring → hole → island nesting survives.</summary>
        public bool DoublyNestedFree => IslandBearingHoleCount == 0;

        /// <summary>
        /// The material faces the host's region solver enumerates: every
        /// top-level region and every island is one selectable region.
        /// </summary>
        public int MaterialRegionCount => TopLevelRegions + IslandCount;

        /// <summary>
        /// The expected <c>ISketch.GetSketchRegionCount()</c> on the exploded
        /// sketch: material faces plus one void region per hole interior.
        /// Equals <see cref="QrSketch.Contours"/>' count — one loop bounds
        /// each face and each void — but deriving it from the census keeps
        /// the expectation stated in region terms.
        /// </summary>
        public int SketchRegionCount => MaterialRegionCount + HoleCount;

        /// <summary>Walks <paramref name="sketch"/>'s region tree once.</summary>
        public static QrNestingCensus Of(QrSketch sketch)
        {
            int holes = 0, islands = 0, bearing = 0;
            WalkRegions(sketch.Regions);
            return new QrNestingCensus(
                sketch.Regions.Count, holes, islands, bearing);

            void WalkRegions(IReadOnlyList<QrRegion> regions)
            {
                foreach (var region in regions)
                    foreach (var hole in region.Holes)
                    {
                        holes++;
                        islands += hole.Islands.Count;
                        if (hole.Islands.Count > 0)
                            bearing++;
                        WalkRegions(hole.Islands);
                    }
            }
        }
    }
}
