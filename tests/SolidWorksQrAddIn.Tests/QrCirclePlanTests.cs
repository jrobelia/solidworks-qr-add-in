using System;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core.Geometry;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Spec #13, ticket #38 — the circle plan: the host-independent emission
    /// description ticket #39's native linear-pattern emitter consumes
    /// (docs/PATTERN-SPIKE.md). The plan carries the post-invert emission
    /// mask over the working matrix, the cell pitch, the dot diameter, N,
    /// the pattern grid placement (origin, extent, seed centre) and the
    /// create-time skip string in the spike-proven 1-based "(col,row)"
    /// format — the emitter renders exactly what the plan says, recomputing
    /// nothing.
    ///
    /// The pattern grid is asymmetric by polarity, forced by the
    /// unskippable seed: under normal polarity the pattern spans the N×N
    /// data square (seed = data cell (0,0), always a dark finder corner —
    /// quiet-zone cells are light and never emit anyway); under invert the
    /// pattern spans the whole M×M working matrix (seed = matrix cell
    /// (0,0), always a light quiet-zone cell → emits).
    /// </summary>
    public class QrCirclePlanTests
    {
        private const string Payload = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;
        private const double Dot = 0.90;

        private static bool[,] Cells(int n, params (int row, int col)[] dark)
        {
            var cells = new bool[n, n];
            foreach (var (row, col) in dark)
                cells[row, col] = true;
            return cells;
        }

        private static QrSketchRequest CircleRequest(
            string content = Payload, double sizeMm = SizeMm,
            double dot = Dot, bool invert = false) =>
            new QrSketchRequest(content, sizeMm, Growth)
            {
                CellShape = QrCellShape.Circle,
                CellShapeParameter = dot,
                Invert = invert,
            };

        // ---------------- the plan shape ----------------

        [Fact]
        public void A_circle_request_yields_a_plan_instead_of_contours()
        {
            var result = QrSketchGenerator.Generate(CircleRequest());

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.NotNull(sketch.CirclePlan);
            Assert.Empty(sketch.Contours);
            Assert.Empty(sketch.Regions);
            Assert.Empty(sketch.Edges);
            // The construction squares still ride along — the QR square and
            // the keep-out square are plain entities beside the pattern.
            Assert.Equal(2, sketch.ConstructionSquares.Count);
        }

        [Fact]
        public void A_square_request_keeps_contours_and_no_plan()
        {
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth));

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.Null(result.Sketch!.CirclePlan);
            Assert.NotEmpty(result.Sketch.Contours);
        }

        [Fact]
        public void The_plan_carries_mask_pitch_diameter_and_n()
        {
            var result = QrSketchGenerator.Generate(CircleRequest(dot: 0.90));
            var plan = result.Sketch!.CirclePlan!;
            var info = result.Sketch.Info;

            Assert.Equal(info.CellCount, plan.DataCells);
            Assert.Equal(info.MatrixCellCount, plan.CellsPerSide);
            Assert.Equal(info.PitchMm / 1000.0, plan.PitchM, 12);
            Assert.Equal(0.90 * plan.PitchM, plan.DotDiameterM, 12);
            Assert.Equal(0.90, plan.DotFraction, 12);
            Assert.False(plan.Inverted);
            Assert.Equal(plan.EmittedCount, info.InstanceCount);
            Assert.True(plan.EmittedCount > 0);
        }

        [Fact]
        public void The_emission_mask_is_the_post_invert_dark_cells()
        {
            // A real QR under normal polarity: the mask reproduces the
            // encoder's working matrix dark cells exactly — dark cells emit,
            // quiet-zone cells never do.
            var result = QrSketchGenerator.Generate(CircleRequest());
            var plan = result.Sketch!.CirclePlan!;

            using (var gen = new QRCodeGenerator())
            using (var data = gen.CreateQrCode(Payload, QRCodeGenerator.ECCLevel.M))
            {
                var mm = data.ModuleMatrix;
                int dark = 0;
                for (var r = 0; r < plan.CellsPerSide; r++)
                    for (var c = 0; c < plan.CellsPerSide; c++)
                    {
                        bool expect = mm[r][c];
                        Assert.Equal(expect, plan.Emits(r, c));
                        if (expect) dark++;
                    }
                Assert.Equal(dark, plan.EmittedCount);
            }
        }

        [Fact]
        public void Under_invert_the_mask_flips_and_the_grid_covers_the_matrix()
        {
            var result = QrSketchGenerator.Generate(CircleRequest(invert: true));
            var plan = result.Sketch!.CirclePlan!;

            Assert.True(plan.Inverted);
            Assert.Equal(plan.CellsPerSide, plan.PatternCells);
            Assert.Equal(0, plan.PatternOriginRow);
            Assert.Equal(0, plan.PatternOriginCol);

            using (var gen = new QRCodeGenerator())
            using (var data = gen.CreateQrCode(Payload, QRCodeGenerator.ECCLevel.M))
            {
                var mm = data.ModuleMatrix;
                int m = plan.CellsPerSide;
                for (var r = 0; r < m; r++)
                    for (var c = 0; c < m; c++)
                        Assert.Equal(!mm[r][c], plan.Emits(r, c));
            }
            // The quiet-zone ring emits — every margin cell is a dot.
            int margin = plan.CellsPerSide - plan.DataCells;
            Assert.True(plan.EmittedCount > plan.DataCells * plan.DataCells / 2);
            Assert.Equal(
                plan.PatternCells * plan.PatternCells - plan.EmittedCount,
                plan.SkippedCount);
        }

        [Fact]
        public void Under_normal_polarity_the_pattern_spans_the_data_square()
        {
            var plan = QrSketchGenerator.Generate(CircleRequest()).Sketch!.CirclePlan!;
            int q = QrSketchInfo.QuietZoneCellsPerSide;

            Assert.Equal(plan.DataCells, plan.PatternCells);
            Assert.Equal(q, plan.PatternOriginRow);
            Assert.Equal(q, plan.PatternOriginCol);
        }

        [Fact]
        public void Cell_centres_match_the_square_layouts_geometry()
        {
            // Same sketch-space convention as the contour pipeline: the
            // working matrix is centred on the origin, row 0 at +y.
            var plan = QrSketchGenerator.Generate(CircleRequest()).Sketch!.CirclePlan!;
            double p = plan.PitchM;
            double half = plan.CellsPerSide * p / 2.0;

            var c00 = plan.CellCentreM(0, 0);
            Assert.Equal(-half + p / 2.0, c00.X, 12);
            Assert.Equal(+half - p / 2.0, c00.Y, 12);
            var cLast = plan.CellCentreM(plan.CellsPerSide - 1, plan.CellsPerSide - 1);
            Assert.Equal(+half - p / 2.0, cLast.X, 12);
            Assert.Equal(-half + p / 2.0, cLast.Y, 12);
        }

        [Fact]
        public void The_seed_sits_on_the_data_corner_under_normal_polarity()
        {
            var plan = QrSketchGenerator.Generate(CircleRequest()).Sketch!.CirclePlan!;
            // The seed is pattern instance (1,1) — the data square's
            // top-left cell, the guaranteed-dark QR corner.
            var seed = plan.SeedCentreM;
            var expected = plan.CellCentreM(plan.PatternOriginRow, plan.PatternOriginCol);
            Assert.Equal(expected.X, seed.X, 12);
            Assert.Equal(expected.Y, seed.Y, 12);
            Assert.Equal(-plan.DataCells * plan.PitchM / 2.0 + plan.PitchM / 2.0, seed.X, 12);
            Assert.Equal(+plan.DataCells * plan.PitchM / 2.0 - plan.PitchM / 2.0, seed.Y, 12);
        }

        [Fact]
        public void The_seed_cell_always_emits()
        {
            // Even a fixture that leaves data (0,0) light: the pattern seed
            // cannot be skipped (docs/PATTERN-SPIKE.md PAT2) — the plan
            // records the dot the emitter will actually draw.
            var result = QrSketchGenerator.GenerateFromCells(
                Cells(3, (1, 1)), 30.0, Growth, cellShape: QrCellShape.Circle,
                cellShapeParameter: Dot);
            var plan = result.Sketch!.CirclePlan!;
            Assert.True(plan.Emits(plan.PatternOriginRow, plan.PatternOriginCol));
            Assert.Equal(2, plan.EmittedCount);
        }

        [Fact]
        public void The_skip_string_lists_light_cells_as_one_based_pairs()
        {
            var result = QrSketchGenerator.GenerateFromCells(
                Cells(3, (0, 0), (1, 1)), 30.0, Growth,
                cellShape: QrCellShape.Circle, cellShapeParameter: Dot);
            var plan = result.Sketch!.CirclePlan!;

            Assert.Equal("(2,1) (3,1) (1,2) (3,2) (1,3) (2,3) (3,3)",
                plan.DeleteInstances);
            Assert.Equal(7, plan.SkippedCount);
        }

        [Fact]
        public void The_skip_string_never_contains_the_seed_pair()
        {
            var result = QrSketchGenerator.GenerateFromCells(
                Cells(3, (1, 1)), 30.0, Growth, cellShape: QrCellShape.Circle,
                cellShapeParameter: Dot);
            var plan = result.Sketch!.CirclePlan!;
            Assert.DoesNotContain("(1,1)", plan.DeleteInstances);
            Assert.Equal(7, plan.SkippedCount);
        }

        // ---------------- dot-size validation ----------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.5)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Non_positive_or_non_finite_dot_sizes_reject(double dot)
        {
            var result = QrSketchGenerator.Generate(CircleRequest(dot: dot));
            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
        }

        [Fact]
        public void The_dot_cap_is_computed_per_request_and_enforced_strictly()
        {
            // v1 / 25 mm: pitch = 25/21 mm; cap = 1 − 0.05/pitch ≈ 0.958 —
            // the parent spec's "≈95.8% at 25 mm v1". ≥ cap rejects.
            var request = CircleRequest(dot: 0.0);
            double pitchMm = SizeMm / 21.0;
            double cap = 1.0 - QrInsertionGuard.MinInsertableFeatureMm / pitchMm;

            var atCap = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = cap,
                });
            Assert.False(atCap.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, atCap.Error!.Kind);

            var under = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, Growth)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = cap - 0.001,
                });
            Assert.True(under.Succeeded, under.Error?.Message);
        }

        [Fact]
        public void The_cap_error_names_the_bound_and_the_lever()
        {
            var result = QrSketchGenerator.Generate(CircleRequest(dot: 0.999));

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            var message = result.Error!.Message;
            Assert.Contains("95.8", message);          // the per-request cap, in %
            Assert.Contains("0.05", message);          // the floor it protects
            Assert.Contains("dot", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_dot_below_the_insertion_floor_rejects()
        {
            // 2% of a 1.19 mm pitch ≈ 0.024 mm dot — under the 0.05 mm floor.
            var result = QrSketchGenerator.Generate(CircleRequest(dot: 0.02));
            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.InvalidShapeParameter, result.Error!.Kind);
            Assert.Contains("0.05", result.Error!.Message);
        }

        [Fact]
        public void Growth_is_never_consulted_for_circles()
        {
            // A growth value the square path would reject outright must not
            // matter under Circle — the field is inert.
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(Payload, SizeMm, 0.99)
                {
                    CellShape = QrCellShape.Circle,
                    CellShapeParameter = Dot,
                });
            Assert.True(result.Succeeded, result.Error?.Message);
        }

        [Fact]
        public void The_info_reports_shape_parameter_and_instance_count()
        {
            var sketch = QrSketchGenerator.Generate(CircleRequest()).Sketch!;
            var info = sketch.Info;

            Assert.Equal(QrCellShape.Circle, info.CellShape);
            Assert.Equal(0.90, info.CellShapeParameter, 12);
            Assert.Equal(sketch.CirclePlan!.EmittedCount, info.InstanceCount);
            Assert.Equal(0, info.SegmentCount);
            Assert.Equal(0, info.ContourCount);
            Assert.Equal(0.0, info.GrowthFractionPerSide, 12);
            // The delivered dot gap is the info's light-gap analogue.
            Assert.Equal(info.PitchMm - info.PitchMm * 0.90, info.MinLightGapMm, 9);
            Assert.Equal(SizeMm, info.FinalSizeMm, 9);
        }

        [Fact]
        public void Square_info_stays_shape_aware_with_no_instances()
        {
            var info = QrSketchGenerator.Generate(Payload, SizeMm, Growth).Sketch!.Info;
            Assert.Equal(QrCellShape.Square, info.CellShape);
            Assert.Equal(0, info.InstanceCount);
        }
    }
}
