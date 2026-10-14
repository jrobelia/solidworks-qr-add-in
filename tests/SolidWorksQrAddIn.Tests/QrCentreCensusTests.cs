using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #13, ticket #42 review — the live census gate's centre matching.
    /// <see cref="QrCentreCensus"/> is the pure half of the probe's
    /// CheckCentreCensus: found arc centres (plus the sketch-point fallback
    /// for the PAT0 sibling-typed arc) matched one-to-one against the
    /// plan's emitted cells, with every skipped cell required absent.
    /// </summary>
    public class QrCentreCensusTests
    {
        private const double Pitch = 0.002;       // 2 mm
        private const double Tol = Pitch * 0.25;

        /// <summary>
        /// A 5×5 pattern over the whole working matrix (origin 0,0 — the
        /// invert shape): dots at (0,0),(0,1),(1,0),(2,2),(4,4).
        /// </summary>
        private static QrCirclePlan Plan()
        {
            var cells = new bool[5, 5];
            cells[0, 0] = cells[0, 1] = cells[1, 0] = cells[2, 2] = cells[4, 4] = true;
            return new QrCirclePlan(cells, 5, 5, 0, 0, Pitch, Pitch * 0.9, true);
        }

        private static List<SketchPoint2> EmittedCentres(QrCirclePlan plan)
        {
            var list = new List<SketchPoint2>();
            for (var r = 0; r < plan.PatternCells; r++)
                for (var c = 0; c < plan.PatternCells; c++)
                {
                    int mRow = plan.PatternOriginRow + r;
                    int mCol = plan.PatternOriginCol + c;
                    if (plan.Emits(mRow, mCol))
                        list.Add(plan.CellCentreM(mRow, mCol));
                }
            return list;
        }

        private static QrCentreCensusVerdict Eval(
            QrCirclePlan plan, List<SketchPoint2> centres,
            List<SketchPoint2>? points = null, int siblingArcs = 0) =>
            QrCentreCensus.Evaluate(plan, centres,
                points ?? new List<SketchPoint2>(),
                centres.Count + siblingArcs, siblingArcs, Tol);

        [Fact]
        public void An_exact_field_matches_every_emitted_cell()
        {
            var plan = Plan();
            var v = Eval(plan, EmittedCentres(plan));

            Assert.True(v.Exact);
            Assert.Equal(plan.EmittedCount, v.Matched);
            Assert.Equal(0, v.Missing);
            Assert.Equal(0, v.Stray);
            Assert.Equal(0, v.ForbiddenPresent);
        }

        [Fact]
        public void A_missing_dot_fails_the_census()
        {
            var plan = Plan();
            var centres = EmittedCentres(plan);
            centres.RemoveAt(0);

            var v = Eval(plan, centres);

            Assert.False(v.Exact);
            Assert.Equal(1, v.Missing);
            Assert.Equal(plan.EmittedCount - 1, v.Matched);
        }

        [Fact]
        public void A_dot_on_a_skipped_cell_fails_the_census()
        {
            var plan = Plan();
            var centres = EmittedCentres(plan);
            // Cell (1,1) does not emit — a centre there means the skip
            // string was ignored.
            centres.Add(plan.CellCentreM(1, 1));

            var v = Eval(plan, centres, siblingArcs: 0);

            Assert.False(v.Exact);
            Assert.Equal(1, v.ForbiddenPresent);
            Assert.Equal(0, v.Missing);
        }

        [Fact]
        public void An_off_plan_arc_counts_as_stray()
        {
            var plan = Plan();
            var centres = EmittedCentres(plan);
            centres.Add(new SketchPoint2(100 * Pitch, 100 * Pitch));

            var v = Eval(plan, centres);

            Assert.False(v.Exact);
            Assert.Equal(1, v.Stray);
        }

        [Fact]
        public void A_duplicated_centre_counts_as_stray()
        {
            var plan = Plan();
            var centres = EmittedCentres(plan);
            centres.Add(centres[0]);   // two arcs claiming one cell

            var v = Eval(plan, centres);

            Assert.False(v.Exact);
            Assert.Equal(1, v.Stray);
        }

        [Fact]
        public void A_sibling_typed_arc_resolves_through_the_point_census()
        {
            // The PAT0 quirk: one arc fails the ISketchArc cast — its
            // centre still enumerates as a sketch point.
            var plan = Plan();
            var centres = EmittedCentres(plan);
            var dropped = centres[0];
            centres.RemoveAt(0);
            var points = new List<SketchPoint2> { dropped };

            var v = Eval(plan, centres, points, siblingArcs: 1);

            Assert.True(v.Exact);
            Assert.Equal(1, v.PointOnly);
            Assert.Equal(0, v.Missing);
        }

        [Fact]
        public void A_sibling_arc_without_its_point_is_missing()
        {
            var plan = Plan();
            var centres = EmittedCentres(plan);
            centres.RemoveAt(0);

            var v = Eval(plan, centres, siblingArcs: 1);

            Assert.False(v.Exact);
            Assert.Equal(1, v.Missing);
        }

        [Fact]
        public void A_sketch_point_on_a_skipped_cell_fails_the_census()
        {
            // A stray point (construction corner, a leftover) at a skipped
            // cell's centre is still evidence the skip did not take.
            var plan = Plan();
            var points = new List<SketchPoint2> { plan.CellCentreM(3, 3) };

            var v = Eval(plan, EmittedCentres(plan), points);

            Assert.False(v.Exact);
            Assert.Equal(1, v.ForbiddenPresent);
        }

        [Fact]
        public void The_tolerance_never_reaches_a_neighbouring_cell()
        {
            var plan = Plan();
            var centres = EmittedCentres(plan);
            // Shift one centre 0.1 pitch — inside tolerance, still matches.
            var first = centres[0];
            centres[0] = new SketchPoint2(first.X + Pitch * 0.1, first.Y);
            // And remove cell (0,1)'s centre entirely — its neighbour at
            // (0,0) is a full pitch away, far beyond tolerance.
            centres.RemoveAt(1);

            var v = Eval(plan, centres);

            Assert.False(v.Exact);
            Assert.Equal(1, v.Missing);
        }
    }
}
