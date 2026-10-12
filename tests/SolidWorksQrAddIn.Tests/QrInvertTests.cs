using System.Linq;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Tests.Fakes;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// Ticket #25 — the invert toggle at the generator seam. With invert on,
    /// the emitted contours are the working matrix's LIGHT cells across the
    /// whole (N+8)×(N+8) grid: one dark field with the code left unmarked,
    /// clipped to a clean square at the matrix edge — the keep-out square
    /// (ADR 0002). The pipeline after cell selection is identical, so the
    /// same insertion guard and the same preview render path apply.
    /// </summary>
    public class QrInvertTests
    {
        private const string PayloadA = "SW-QR-MVP";
        private const double SizeMm = 25.0;
        private const double Growth = 0.03;
        private const int Pixels = 240;

        private static bool[,] Cells(int n, params (int row, int col)[] dark)
        {
            var cells = new bool[n, n];
            foreach (var (row, col) in dark)
                cells[row, col] = true;
            return cells;
        }

        /// <summary>The 7×7 finder-pattern fixture: dark ring, light band, dark centre.</summary>
        private static bool[,] FinderCells()
        {
            var cells = new bool[7, 7];
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 7; c++)
                {
                    int ring = System.Math.Max(System.Math.Abs(r - 3), System.Math.Abs(c - 3));
                    cells[r, c] = ring == 3 || ring <= 1;
                }
            return cells;
        }

        /// <summary>Millimetres, centred, y-up — the same space as the sketch.</summary>
        private static bool DarkAt(QrPicture pic, double xMm, double yMm)
        {
            double extentMm = pic.MmPerPixel * pic.WidthPixels;
            int px = (int)System.Math.Floor((xMm + extentMm / 2.0) / pic.MmPerPixel);
            int py = (int)System.Math.Floor((extentMm / 2.0 - yMm) / pic.MmPerPixel);
            return pic.IsDark(px, py);
        }

        // ---------------- generator seam ----------------

        [Fact]
        public void Normal_output_is_the_default_polarity()
        {
            var normal = QrSketchGenerator.Generate(PayloadA, SizeMm, Growth).Sketch!;
            var fixture = QrSketchGenerator.GenerateFromCells(Cells(3, (1, 1)), SizeMm, Growth).Sketch!;

            Assert.False(normal.Info.Inverted);
            Assert.False(fixture.Info.Inverted);
        }

        [Fact]
        public void Inverted_output_emits_the_whole_matrix_as_one_dark_field()
        {
            // One dark cell in a 3×3 fixture: under invert the emitted
            // geometry is the grown union of every light cell — the whole
            // working matrix, quiet zone included — clipped to a clean
            // square at the matrix edge.
            var result = QrSketchGenerator.GenerateFromCells(
                Cells(3, (1, 1)), SizeMm, Growth, invert: true);

            Assert.True(result.Succeeded, result.Error?.Message);
            var sketch = result.Sketch!;
            Assert.True(sketch.Info.Inverted);
            var field = Assert.Single(sketch.Regions);

            // The field's outer edge IS the working-matrix boundary — the
            // same square the keep-out construction square marks.
            var keepOut = Assert.Single(sketch.ConstructionSquares,
                s => s.Role == QrSquareRole.KeepOutSquare);
            var xs = field.Boundary.Vertices.Select(v => v.X).ToList();
            var ys = field.Boundary.Vertices.Select(v => v.Y).ToList();
            Assert.Equal(keepOut.Vertices.Max(v => v.X), xs.Max(), 9);
            Assert.Equal(keepOut.Vertices.Min(v => v.X), xs.Min(), 9);
            Assert.Equal(keepOut.Vertices.Max(v => v.Y), ys.Max(), 9);
            Assert.Equal(keepOut.Vertices.Min(v => v.Y), ys.Min(), 9);
            Assert.Equal(4, field.Boundary.Vertices.Count);

            // The single dark cell survives as one shrunken hole centred
            // on its position (the fixture's centre cell → the origin).
            var hole = Assert.Single(field.Holes);
            Assert.Empty(hole.Islands);
            double holeHalfM =
                (sketch.Info.PitchMm / 2.0 - sketch.Info.GrowthMmPerSide) / 1000.0;
            Assert.Equal(holeHalfM, hole.Boundary.Vertices.Max(v => v.X), 9);
            Assert.Equal(-holeHalfM, hole.Boundary.Vertices.Min(v => v.X), 9);
            Assert.Equal(holeHalfM, hole.Boundary.Vertices.Max(v => v.Y), 9);
            Assert.Equal(-holeHalfM, hole.Boundary.Vertices.Min(v => v.Y), 9);

            Assert.Equal(2, sketch.Contours.Count);
            Assert.Equal(2, sketch.Info.ContourCount);
        }

        [Fact]
        public void Inversion_maps_each_normal_contour_one_level_deeper()
        {
            // Polarity is the only switch — the same pipeline emits the
            // complement, so every normal contour reappears one nesting
            // level deeper: a dark region becomes a hole in the field, a
            // light hole becomes an emitted island inside that hole.
            var normal = QrSketchGenerator.GenerateFromCells(FinderCells(), SizeMm, Growth).Sketch!;
            var inverted = QrSketchGenerator.GenerateFromCells(
                FinderCells(), SizeMm, Growth, invert: true).Sketch!;

            Assert.Equal(normal.Contours.Count + 1, inverted.Contours.Count);

            var field = Assert.Single(inverted.Regions);
            var ringHole = Assert.Single(field.Holes);          // was the dark ring
            var bandIsland = Assert.Single(ringHole.Islands);   // was the light band
            var centreHole = Assert.Single(bandIsland.Holes);   // was the dark centre
            Assert.Empty(centreHole.Islands);
            Assert.Equal(4, inverted.Contours.Count);
        }

        [Fact]
        public void Inverted_real_payload_is_one_field_with_the_code_left_as_holes()
        {
            var normal = QrSketchGenerator.Generate(PayloadA, SizeMm, Growth).Sketch!;
            var inverted = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true }).Sketch!;

            Assert.True(inverted.Info.Inverted);
            var field = Assert.Single(inverted.Regions);
            // Every un-emitted dark pocket is a hole in the field. Grown
            // light cells fill the 2g corner necks that merge diagonal dark
            // cells in normal mode, so pockets are the 4-connected dark
            // components — never fewer than the normal regions.
            Assert.True(field.Holes.Count >= normal.Regions.Count,
                $"inverted field has {field.Holes.Count} holes, " +
                $"normal output has {normal.Regions.Count} regions");
            // The field edge is the keep-out square — the matrix boundary.
            var keepOut = inverted.ConstructionSquares
                .Single(s => s.Role == QrSquareRole.KeepOutSquare);
            Assert.Equal(keepOut.Vertices.Max(v => v.X),
                field.Boundary.Vertices.Max(v => v.X), 9);
            Assert.Equal(keepOut.Vertices.Min(v => v.X),
                field.Boundary.Vertices.Min(v => v.X), 9);

            // The code is left unmarked: the finder-corner cell — dark in
            // every QR — lies inside one of the field's holes, not in the
            // emitted material. Its centre maps to (−10p, +10p) mm.
            double p = inverted.Info.PitchMm;
            Assert.True(field.Holes.Any(h =>
                Contains(-10.0 * p / 1000.0, 10.0 * p / 1000.0, h.Boundary)),
                "the finder-corner cell must be an unmarked pocket in the field");
        }

        private static bool Contains(double x, double y, QrContour c) =>
            x > c.Vertices.Min(v => v.X) && x < c.Vertices.Max(v => v.X) &&
            y > c.Vertices.Min(v => v.Y) && y < c.Vertices.Max(v => v.Y);

        [Fact]
        public void Inverted_output_clears_the_same_insertion_guard()
        {
            // Emitted light cells grown by g leave u−2g dark gaps and 2g
            // diagonal necks — the same smallest-feature math, the same
            // recorded floor.
            var inverted = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true }).Sketch!;

            Assert.True(inverted.Info.MinEdgeMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min edge {inverted.Info.MinEdgeMm} mm");
            Assert.True(inverted.Info.MinLightGapMm >= QrInsertionGuard.MinInsertableFeatureMm,
                $"min gap {inverted.Info.MinLightGapMm} mm");
        }

        [Fact]
        public void The_insertion_guard_rejects_an_inverted_request_the_same_way()
        {
            // 5 mm at 3% growth puts the smallest feature below 0.05 mm in
            // either polarity — refused, never silently substituted.
            var result = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, 5.0, Growth) { Invert = true });

            Assert.False(result.Succeeded);
            Assert.Equal(QrSketchErrorKind.BelowInsertionLimit, result.Error!.Kind);
        }

        // ---------------- session & command: the toggle end-to-end ----------------

        private static (QrSketchSession session, System.Collections.Generic.List<FakeQrWindow> windows)
            NewSession()
        {
            var windows = new System.Collections.Generic.List<FakeQrWindow>();
            var session = new QrSketchSession(() =>
            {
                var w = new FakeQrWindow();
                windows.Add(w);
                return w;
            });
            return (session, windows);
        }

        private static TestSketchHost HostWithSelectedPlane()
        {
            var host = new TestSketchHost { Document = FakeDocument.FreshPart() };
            host.Document!.Selection.Add(FakeSelType.DatumPlane);
            return host;
        }

        private static string InfoValue(QrSketchSession session, string label)
        {
            foreach (var line in session.InfoLines)
                if (line.Label == label)
                    return line.Value;
            throw new System.Exception($"no info line labelled '{label}'");
        }

        [Fact]
        public void A_fresh_session_uses_normal_polarity()
        {
            var (session, _) = NewSession();

            Assert.False(session.Invert);
        }

        [Fact]
        public void SetInvert_regenerates_the_request_and_picture_through_the_same_path()
        {
            var (session, windows) = NewSession();
            session.SetContent(PayloadA);
            session.OpenOrFocusWindow();
            var normalSignature = session.Picture!.Signature();
            var syncs = windows[0].SyncCount;

            var result = session.SetInvert(true);

            Assert.True(result.Succeeded, result.Error?.Message);
            Assert.True(session.Invert);
            // Preview parity: the session's picture IS a direct render of the
            // inverted module result — identical signature.
            var expected = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(new QrSketchRequest(PayloadA,
                    QrSketchSession.InitialSizeMm,
                    QrSketchSession.InitialGrowthFractionPerSide)
                { Invert = true }).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();
            Assert.Equal(expected, session.Picture!.Signature());
            Assert.NotEqual(normalSignature, session.Picture!.Signature());
            // The open view re-pulls: session→view sync like any field edit.
            Assert.True(windows[0].SyncCount > syncs);
        }

        [Fact]
        public void Toggling_invert_back_restores_the_normal_output()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);
            var normal = session.Picture!.Signature();

            session.SetInvert(true);
            session.SetInvert(false);

            Assert.False(session.Invert);
            Assert.Equal(normal, session.Picture!.Signature());
        }

        [Fact]
        public void Invert_without_valid_content_stays_invalid_but_the_setting_sticks()
        {
            var (session, _) = NewSession();

            var result = session.SetInvert(true);

            Assert.False(result.Succeeded);
            Assert.Null(session.Picture);
            Assert.True(session.Invert);

            // Content typed afterwards previews the inverted appearance.
            session.SetContent(PayloadA);
            Assert.True(session.HasValidRequest);
            var expected = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(new QrSketchRequest(PayloadA,
                    QrSketchSession.InitialSizeMm,
                    QrSketchSession.InitialGrowthFractionPerSide)
                { Invert = true }).Sketch!,
                QrSketchSession.PreviewPixelsAcross).Signature();
            Assert.Equal(expected, session.Picture!.Signature());
        }

        [Fact]
        public void The_info_section_states_the_polarity()
        {
            var (session, _) = NewSession();
            session.SetContent(PayloadA);

            Assert.Equal("Dark code on a light field", InfoValue(session, "Polarity"));

            session.SetInvert(true);

            Assert.Equal("Inverted — light code on a dark field",
                InfoValue(session, "Polarity"));
        }

        [Fact]
        public void Create_under_invert_inserts_the_inverted_geometry()
        {
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.SetInvert(true);

            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(new QrSketchRequest(PayloadA,
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide)
            { Invert = true }).Sketch!;
            Assert.Equal(expected.Info.SegmentCount, sketch.ProfileSegments.Count());
        }

        [Fact]
        public void Settings_apply_to_future_creates_only()
        {
            // A sketch keeps the polarity it was created with — a later
            // toggle never mutates existing geometry.
            var (session, _) = NewSession();
            var host = HostWithSelectedPlane();
            session.SetContent(PayloadA);
            session.CreateSketch(host);
            host.Document!.ActiveSketch = null;
            var normalSegs = host.Document.Sketches[0].ProfileSegments.Count();

            session.SetInvert(true);
            var result = session.CreateSketch(host);

            Assert.True(result.Succeeded, result.Message);
            var expected = QrSketchGenerator.Generate(new QrSketchRequest(PayloadA,
                QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide)
            { Invert = true }).Sketch!;
            Assert.Equal(expected.Info.SegmentCount,
                host.Document.Sketches[1].ProfileSegments.Count());
            Assert.Equal(normalSegs, host.Document.Sketches[0].ProfileSegments.Count());
            Assert.NotEqual(normalSegs, host.Document.Sketches[1].ProfileSegments.Count());
        }

        [Fact]
        public void The_command_passes_invert_through_to_the_generator()
        {
            var host = HostWithSelectedPlane();

            var result = new CreateQrSketchCommand().Execute(
                host, new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true });

            Assert.True(result.Succeeded, result.Message);
            var sketch = host.Document!.Sketches.Single();
            var expected = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true }).Sketch!;
            var effective = sketch.ProfileSegments.ToList();
            Assert.Equal(expected.Info.SegmentCount, effective.Count);
            var expectedEdges = expected.Edges.ToList();
            for (var i = 0; i < expectedEdges.Count; i++)
            {
                Assert.Equal(expectedEdges[i].Start.X, effective[i].X1, 12);
                Assert.Equal(expectedEdges[i].End.Y, effective[i].Y2, 12);
            }
        }

        // ---------------- preview: same render path ----------------

        [Fact]
        public void The_inverted_picture_is_the_same_render_path_showing_a_dark_field()
        {
            // The contours ARE the picture source — no second renderer. The
            // inverted result renders the white-on-black appearance through
            // the identical path at the identical extent.
            var normal = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(PayloadA, SizeMm, Growth).Sketch!, Pixels);
            var inverted = QrPictureRenderer.Render(
                QrSketchGenerator.Generate(
                    new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true }).Sketch!,
                Pixels);

            Assert.Equal(normal.MmPerPixel, inverted.MmPerPixel, 12);
            Assert.True(inverted.DarkPixelCount > 2 * normal.DarkPixelCount,
                $"inverted {inverted.DarkPixelCount} dark px vs normal {normal.DarkPixelCount}");
            Assert.NotEqual(normal.Signature(), inverted.Signature());
        }

        [Fact]
        public void The_inverted_picture_fills_the_quiet_zone_and_leaves_dark_cells_unmarked()
        {
            var sketch = QrSketchGenerator.Generate(
                new QrSketchRequest(PayloadA, SizeMm, Growth) { Invert = true }).Sketch!;
            var pic = QrPictureRenderer.Render(sketch, Pixels);
            double p = sketch.Info.PitchMm;

            // Inside the quiet zone — between the QR square (S/2 = 12.5 mm)
            // and the keep-out edge (14.5·p ≈ 17.2 mm) — is field material.
            Assert.True(DarkAt(pic, 12.5 + 2.0 * p, 0.0),
                "the field fills the quiet zone out to the matrix edge");
            // The finder-corner cell (0,0) — dark in every QR — is left
            // unmarked: a white pocket in the rendered field.
            Assert.False(DarkAt(pic, -10.0 * p, 10.0 * p),
                "dark cells of the code stay unmarked under invert");
            // The separator cell (7,7) — light in every QR — is emitted
            // field, rendered dark.
            Assert.True(DarkAt(pic, -3.0 * p, 3.0 * p),
                "light cells are the emitted field under invert");
        }
    }
}
