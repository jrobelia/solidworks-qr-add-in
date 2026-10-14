using System;
using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>The verdict of one centre-census comparison.</summary>
    public sealed class QrCentreCensusVerdict
    {
        internal QrCentreCensusVerdict(
            int arcSegments, int castCentres, int siblingArcs, int matched,
            int pointOnly, int missing, int stray, int forbiddenPresent,
            int emitted, int skipped)
        {
            ArcSegments = arcSegments;
            CastCentres = castCentres;
            SiblingArcs = siblingArcs;
            Matched = matched;
            PointOnly = pointOnly;
            Missing = missing;
            Stray = stray;
            ForbiddenPresent = forbiddenPresent;
            Emitted = emitted;
            Skipped = skipped;
        }

        /// <summary>Non-construction arc-typed segments found.</summary>
        public int ArcSegments { get; }

        /// <summary>Arcs whose centre read succeeded (the ISketchArc cast).</summary>
        public int CastCentres { get; }

        /// <summary>
        /// Arcs that failed the centre read — the PAT0 sibling-COM quirk;
        /// their centres resolve through the sketch-point census instead.
        /// </summary>
        public int SiblingArcs { get; }

        /// <summary>Emitted cells claimed one-to-one by an arc centre.</summary>
        public int Matched { get; }

        /// <summary>
        /// Emitted cells no arc centre claimed but whose centre still
        /// enumerates as a sketch point — the sibling-typed arcs' cells.
        /// </summary>
        public int PointOnly { get; }

        /// <summary>Emitted cells with no centre evidence at all.</summary>
        public int Missing { get; }

        /// <summary>Arc centres that claimed no emitted cell (strays/dupes).</summary>
        public int Stray { get; }

        /// <summary>Skipped cells that nevertheless carry a centre/point.</summary>
        public int ForbiddenPresent { get; }

        /// <summary><see cref="QrCirclePlan.EmittedCount"/>.</summary>
        public int Emitted { get; }

        /// <summary><see cref="QrCirclePlan.SkippedCount"/>.</summary>
        public int Skipped { get; }

        /// <summary>
        /// The exact-census verdict: one arc per emitted cell, every
        /// emitted centre resolved (by cast or by point), every skipped
        /// cell absent, no strays.
        /// </summary>
        public bool Exact =>
            ArcSegments == Emitted && Missing == 0 &&
            PointOnly == SiblingArcs && Stray == 0 && ForbiddenPresent == 0;
    }

    /// <summary>
    /// The one-to-one matching the live circle-census gate performs
    /// (spec #13, ticket #42): the found dot centres — real sketch arcs
    /// enumerated via GetSketchSegments, with the PAT0 sibling-COM
    /// fallback through the sketch-point set — against the plan's emitted
    /// and skipped cells. Pure geometry; the probe feeds it raw
    /// coordinates and prints the verdict's counts.
    /// </summary>
    public static class QrCentreCensus
    {
        /// <summary>
        /// Compares found arc centres (and the sketch-point set the
        /// sibling-typed arcs leave their centres in) against the plan,
        /// within <paramref name="toleranceM"/> — a quarter pitch keeps a
        /// neighbour's centre from ever matching.
        /// </summary>
        public static QrCentreCensusVerdict Evaluate(
            QrCirclePlan plan,
            IReadOnlyList<SketchPoint2> arcCentres,
            IReadOnlyList<SketchPoint2> sketchPoints,
            int arcSegments,
            int siblingArcs,
            double toleranceM)
        {
            // Pattern instance (c,r) maps to matrix cell
            // (PatternOriginRow+r, PatternOriginCol+c) — QrCirclePlan's
            // grid convention.
            var expected = new List<SketchPoint2>();
            var forbidden = new List<SketchPoint2>();
            for (var r = 0; r < plan.PatternCells; r++)
                for (var c = 0; c < plan.PatternCells; c++)
                {
                    int mRow = plan.PatternOriginRow + r;
                    int mCol = plan.PatternOriginCol + c;
                    var m = plan.CellCentreM(mRow, mCol);
                    (plan.Emits(mRow, mCol) ? expected : forbidden).Add(m);
                }

            var hit = new bool[expected.Count];
            int stray = 0;
            foreach (var a in arcCentres)
            {
                int idx = -1;
                for (var i = 0; i < expected.Count; i++)
                    if (!hit[i] && Near(a, expected[i], toleranceM))
                    { idx = i; break; }
                if (idx < 0) stray++; else hit[idx] = true;
            }

            int missing = 0, pointOnly = 0;
            for (var i = 0; i < expected.Count; i++)
            {
                if (hit[i]) continue;
                if (NearSet(sketchPoints, expected[i], toleranceM)) pointOnly++;
                else missing++;
            }
            int forbiddenPresent = 0;
            foreach (var f in forbidden)
                if (NearSet(arcCentres, f, toleranceM) ||
                    NearSet(sketchPoints, f, toleranceM))
                    forbiddenPresent++;

            return new QrCentreCensusVerdict(
                arcSegments, arcCentres.Count, siblingArcs,
                expected.Count - missing - pointOnly, pointOnly, missing,
                stray, forbiddenPresent,
                plan.EmittedCount, plan.SkippedCount);
        }

        private static bool Near(SketchPoint2 a, SketchPoint2 b, double tol) =>
            Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;

        private static bool NearSet(
            IReadOnlyList<SketchPoint2> set, SketchPoint2 at, double tol)
        {
            foreach (var p in set)
                if (Near(p, at, tol)) return true;
            return false;
        }
    }
}
