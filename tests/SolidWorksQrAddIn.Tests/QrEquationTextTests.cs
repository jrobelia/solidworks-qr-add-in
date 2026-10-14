using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #42 (spec #13) — the deleted-sketch/orphan-equation defect fix.
    /// A deleted sketch leaves its <c>"D{n}@{sketch}" = …</c> entries behind
    /// in the document's equation manager, and SolidWorks refuses any later
    /// IEquationMgr.Add while those orphans exist — a second circle create
    /// (which installs the <see cref="QrCircleEquations"/> table) fails with
    /// Add → −1. The host sweeps the dead entries at create start and on the
    /// delete path; these tests pin the pure parsing the sweep decides on.
    /// </summary>
    public class QrEquationTextTests
    {
        // ---------------- local feature references ----------------

        [Fact]
        public void A_circle_spacing_equation_reports_its_sketch_reference()
        {
            var refs = QrEquationText.LocalFeatureReferences(
                "\"D5@Sketch1\" = \"D1@Sketch1\"/21");

            Assert.Equal(new[] { "Sketch1", "Sketch1" }, refs);
        }

        [Fact]
        public void References_report_every_quoted_feature_atom()
        {
            var refs = QrEquationText.LocalFeatureReferences(
                "\"D7@Sketch2\" = \"D5@Sketch2\"*\"D1@Plate\" / 2");

            Assert.Equal(new[] { "Sketch2", "Sketch2", "Plate" }, refs);
        }

        [Fact]
        public void Atoms_without_a_feature_reference_report_nothing()
        {
            // Global variables and plain numbers carry no "@feature" part.
            Assert.Empty(QrEquationText.LocalFeatureReferences("\"scale\" = 0.9"));
            Assert.Empty(QrEquationText.LocalFeatureReferences("\"D1\" = 25"));
        }

        [Fact]
        public void A_document_contexted_atom_is_not_a_local_reference()
        {
            // "D1@Sketch1@Part2.sldprt" carries a second '@' — the doc
            // context — so the reference may resolve in ANOTHER document.
            // The sweep must never condemn what it cannot prove dead; only
            // the genuine local atom reports.
            Assert.Equal(new[] { "Other" }, QrEquationText.LocalFeatureReferences(
                "\"D1@Other\" = \"D1@Sketch1@Part2.sldprt\""));
        }

        [Fact]
        public void A_two_part_atom_naming_a_document_is_not_a_feature_reference()
        {
            // "var@Doc.SLDPRT" binds a name to a document, not a feature.
            Assert.Equal(new[] { "Sketch1" }, QrEquationText.LocalFeatureReferences(
                "\"D1@Sketch1\" = \"D1@Part3.SLDPRT\""));
        }

        [Fact]
        public void Junk_and_empty_text_yield_no_references()
        {
            Assert.Empty(QrEquationText.LocalFeatureReferences(""));
            Assert.Empty(QrEquationText.LocalFeatureReferences("   "));
            Assert.Empty(QrEquationText.LocalFeatureReferences("unquoted tokens @ everywhere"));
        }

        // ---------------- the orphan decision ----------------

        [Fact]
        public void An_equation_whose_references_all_live_is_not_orphaned()
        {
            bool orphan = QrEquationText.IsOrphaned(
                "\"D5@Sketch1\" = \"D1@Sketch1\"/21",
                name => name == "Sketch1");

            Assert.False(orphan);
        }

        [Fact]
        public void An_equation_referencing_a_deleted_feature_is_orphaned()
        {
            // The delete-then-recreate shape: every entry the deleted sketch
            // owned now dangles.
            bool orphan = QrEquationText.IsOrphaned(
                "\"D5@Sketch1\" = \"D1@Sketch1\"/21",
                _ => false);

            Assert.True(orphan);
        }

        [Fact]
        public void A_mix_of_live_and_dead_references_is_orphaned()
        {
            bool orphan = QrEquationText.IsOrphaned(
                "\"D5@Sketch2\" = \"D1@Sketch9\"/21",
                name => name == "Sketch2");   // Sketch9 is gone

            Assert.True(orphan);
        }

        [Fact]
        public void Reference_free_equations_are_never_orphaned()
        {
            Assert.False(QrEquationText.IsOrphaned("\"x\" = 5", _ => false));
            Assert.False(QrEquationText.IsOrphaned("", _ => false));
        }

        [Fact]
        public void A_contexted_reference_cannot_mark_an_equation_orphaned()
        {
            // Dead-or-alive is undecidable across documents — kept.
            Assert.False(QrEquationText.IsOrphaned(
                "\"D1@Sketch1\" = \"D1@Sketch9@Part2.sldprt\"/2",
                name => name == "Sketch1"));
        }

        [Fact]
        public void The_emitted_circle_table_orphans_exactly_with_its_sketch()
        {
            // End-to-end on the real table: every equation names the sketch,
            // so deleting the sketch orphans the whole set and nothing else.
            // emitCells is the 29×29 working matrix; the 21×21 pattern grid
            // sits at origin (4,4) — the quiet-zone margin stays light.
            var cells = new bool[29, 29];
            for (var i = 0; i < 200; i++) cells[4 + i / 21, 4 + i % 21] = true;
            var plan = new QrCirclePlan(cells, 21, 21, 4, 4, 0.001, 0.0009, false);
            var scheme = new QrCircleDimScheme
            {
                Box = "D1@Sketch4", SpacingX = "D5@Sketch4", SpacingY = "D6@Sketch4",
                Diameter = "D7@Sketch4", SeedOffsetX = "D8@Sketch4", SeedOffsetY = "D9@Sketch4",
                QuietOffsetX = "D2@Sketch4", QuietOffsetY = "D3@Sketch4",
                QuietSize = "D4@Sketch4",
            };

            var equations = QrCircleEquations.For(plan, scheme);
            Assert.Equal(8, equations.Count);

            // Live sketch present → nothing orphaned.
            Assert.All(equations, e => Assert.False(
                QrEquationText.IsOrphaned(e.Text, name => name == "Sketch4")));
            // Sketch4 deleted → every entry orphaned.
            Assert.All(equations, e => Assert.True(
                QrEquationText.IsOrphaned(e.Text, _ => false)));
            // A different sketch's equation alongside ours is untouched.
            Assert.False(QrEquationText.IsOrphaned(
                "\"D1@Sketch7\" = 0.05", name => name == "Sketch4" || name == "Sketch7"));
        }

        // ---------------- full-name → equation reference ----------------
        // The insertion path turns a created dimension's FullName into the
        // "D{n}@{sketch}" atom the equations bind — the review's EqName
        // guard: an unexpected shape must refuse, never silently mint a
        // dead reference. The pure decision lives here for headless tests;
        // SwSketchInsertion wraps a null in SketchInsertionException.

        [Theory]
        [InlineData("D6@Sketch1@Part5.Part", "Sketch1", "D6@Sketch1")]
        [InlineData("D6@Sketch1@Part2.SLDPRT", "Sketch1", "D6@Sketch1")]
        [InlineData("D6@Sketch1", "Sketch1", "D6@Sketch1")]
        [InlineData("D9@Sketch 4@Doc@tail", "Sketch 4", "D9@Sketch 4")]
        public void Full_names_reduce_to_the_two_part_reference(
            string fullName, string sketch, string expected)
        {
            Assert.Equal(expected, QrEquationText.LocalReferenceFor(fullName, sketch));
        }

        [Theory]
        [InlineData("D6", "Sketch1")]                 // no feature part at all
        [InlineData("D6@OtherSketch@Part5", "Sketch1")] // another sketch's dim
        [InlineData("D6@Sketch1Extra@Part5", "Sketch1")] // prefix ≠ the feature
        [InlineData("", "Sketch1")]
        [InlineData(null, "Sketch1")]
        [InlineData("D6@Sketch1@Doc", "")]
        public void Malformed_or_mismatched_full_names_are_refused(
            string? fullName, string sketch)
        {
            Assert.Null(QrEquationText.LocalReferenceFor(fullName, sketch));
        }
    }
}
