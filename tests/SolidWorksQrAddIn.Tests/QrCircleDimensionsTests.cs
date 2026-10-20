using System.Linq;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #13, ticket #40 — the master-geometry equation table
    /// (docs/CIRCLE-DIMENSIONS.md). The table is pure functions of the
    /// plan's constants over the dimension names the host discovered:
    /// every driven dim funnels through the one master (the QR-square
    /// dim), so the same tests pin the dimensionless-mode wiring (only the
    /// two spacing equations carry the root reference).
    /// </summary>
    public class QrCircleDimensionsTests
    {
        private static QrCirclePlan Plan(bool invert = false)
        {
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest("SW-QR-MVP", 25.0, 0.03)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = 0.90,
                    Invert = invert,
                });
            Assert.True(result.Succeeded, result.Error?.Message);
            return result.Sketch!.CirclePlan!;
        }

        private static QrCircleDimScheme Scheme(int sign = 1) => new QrCircleDimScheme
        {
            Box = "D1@Sketch7",
            QuietOffsetX = "D2@Sketch7", QuietOffsetXSign = sign,
            QuietOffsetY = "D3@Sketch7", QuietOffsetYSign = sign,
            QuietSize = "D4@Sketch7",
            SpacingX = "D5@Sketch7",
            SpacingY = "D6@Sketch7",
            Diameter = "D7@Sketch7",
            SeedOffsetX = "D8@Sketch7", SeedOffsetXSign = sign,
            SeedOffsetY = "D9@Sketch7", SeedOffsetYSign = sign,
        };

        [Fact]
        public void The_table_wires_every_driven_dim_to_the_master()
        {
            var plan = Plan();
            var eqs = QrCircleEquations.For(plan, Scheme());

            Assert.Equal(8, eqs.Count);
            Assert.Equal(
                new[] { "D5@Sketch7", "D6@Sketch7", "D7@Sketch7", "D8@Sketch7",
                        "D9@Sketch7", "D2@Sketch7", "D3@Sketch7", "D4@Sketch7" },
                eqs.Select(e => e.Lhs).ToArray());
        }

        [Fact]
        public void Only_the_spacing_pair_references_the_master_directly()
        {
            // The dimensionless retarget edits exactly these two lines;
            // every other dim follows through the spacing names. (The
            // driven-echo alternative was reverted — driven dims lag the
            // equation pass by one rebuild, live-verified.)
            var eqs = QrCircleEquations.For(Plan(), Scheme());
            int roots = eqs.Count(e => e.Rhs.Contains("\"D1@Sketch7\""));
            Assert.Equal(2, roots);
            Assert.All(eqs.Skip(2),
                e => Assert.DoesNotContain("\"D1@Sketch7\"", e.Rhs));
        }

        [Fact]
        public void The_expressions_follow_the_ticket_formulas()
        {
            var plan = Plan();   // v1: N=21, M=29, dot=0.9
            var eqs = QrCircleEquations.For(plan, Scheme());
            var byLhs = eqs.ToDictionary(e => e.Lhs, e => e.Rhs);

            Assert.Equal("\"D1@Sketch7\"/21", byLhs["D5@Sketch7"]);
            Assert.Equal("\"D1@Sketch7\"/21", byLhs["D6@Sketch7"]);
            Assert.Equal("\"D5@Sketch7\"*0.9", byLhs["D7@Sketch7"]);
            Assert.Equal("\"D5@Sketch7\"/2", byLhs["D8@Sketch7"]);
            Assert.Equal("\"D6@Sketch7\"/2", byLhs["D9@Sketch7"]);
            // quiet offset = spacing·4 == box·4/N; quiet size = spacing·M
            // == box·(1+8/N) — identical values, spacing-rooted so a box
            // retarget keeps them.
            Assert.Equal("\"D5@Sketch7\"*4", byLhs["D2@Sketch7"]);
            Assert.Equal("\"D6@Sketch7\"*4", byLhs["D3@Sketch7"]);
            Assert.Equal("\"D5@Sketch7\"*29", byLhs["D4@Sketch7"]);
        }

        [Fact]
        public void Equation_text_is_the_quoted_add_argument()
        {
            var eq = QrCircleEquations.For(Plan(), Scheme()).First();
            Assert.Equal("\"D5@Sketch7\" = \"D1@Sketch7\"/21", eq.Text);
        }

        [Fact]
        public void No_equation_reference_carries_a_part_suffix()
        {
            var eqs = QrCircleEquations.For(Plan(), Scheme());
            Assert.All(eqs, e => Assert.DoesNotContain(".Part", e.Text));
            Assert.All(eqs, e => Assert.Equal(1, e.Lhs.Count(c => c == '@')));
        }

        [Fact]
        public void Negative_measured_dims_mirror_the_sign_in_the_equation()
        {
            var eqs = QrCircleEquations.For(Plan(), Scheme(sign: -1));
            var byLhs = eqs.ToDictionary(e => e.Lhs, e => e.Rhs);
            Assert.Equal("-\"D5@Sketch7\"/2", byLhs["D8@Sketch7"]);
            Assert.Equal("-\"D5@Sketch7\"*4", byLhs["D2@Sketch7"]);
        }

        [Fact]
        public void Empty_names_emit_a_partial_table_never_an_empty_lhs()
        {
            var scheme = Scheme();
            scheme.Diameter = "";   // the dia dim could not be created
            var eqs = QrCircleEquations.For(Plan(), scheme);
            Assert.Equal(7, eqs.Count);
            Assert.All(eqs, e => Assert.NotEqual("", e.Lhs));
            Assert.DoesNotContain(eqs, e => e.Lhs == "D7@Sketch7");
        }

        [Fact]
        public void The_table_is_polarity_neutral()
        {
            // Same N/M/dot → same expressions under invert; only the
            // anchor corner differs, and that is the emitter's pick, not
            // the table's.
            Assert.Equal(
                QrCircleEquations.For(Plan(), Scheme()).Select(e => e.Text),
                QrCircleEquations.For(Plan(invert: true), Scheme()).Select(e => e.Text));
        }

        // ---------------- the #42 COM surface ----------------

        [Fact]
        public void The_probe_interface_declares_the_dims_probe()
        {
            var method = typeof(IAddInProbe).GetMethod("PatternDimsProbe");
            Assert.NotNull(method);
            Assert.Equal(typeof(string), method!.ReturnType);
            var p = method.GetParameters();
            Assert.Single(p);
            Assert.Equal(typeof(string), p[0].ParameterType);
        }

        [Fact]
        public void The_probe_interface_declares_the_dim_setter()
        {
            var method = typeof(IAddInProbe).GetMethod("SetPatternDimProbe");
            Assert.NotNull(method);
            Assert.Equal(typeof(string), method!.ReturnType);
            var p = method.GetParameters();
            Assert.Equal(2, p.Length);
            Assert.Equal(typeof(string), p[0].ParameterType);
            Assert.Equal(typeof(double), p[1].ParameterType);
        }
    }
}
