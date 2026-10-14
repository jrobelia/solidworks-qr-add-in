using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;

namespace SolidWorksQrAddIn.Core.Preview
{
    /// <summary>
    /// Renders the validated QR contour result into a filled black-on-white
    /// raster — the "simple picture" of the spec. This is the same
    /// <see cref="QrSketch"/> geometry that sketch insertion consumes, so
    /// holes, nested islands and outline growth are all represented exactly.
    ///
    /// Filling is an even-odd scanline rule over ALL contours (region
    /// outlines, holes, islands) at pixel-centre samples: inside a filled
    /// region → dark, inside a hole → white, inside a nested island → dark
    /// again. The picture footprint is the final grown outline plus
    /// <see cref="MarginCells"/> blank cells on every side — white space, not
    /// a drawn box; under invert the emitted field fills that margin out to
    /// the matrix edge, so the same footprint shows the field with only the
    /// per-side growth left as a white frame. No WPF references: the add-in
    /// maps the Gray8 buffer onto a BitmapSource verbatim.
    /// </summary>
    public static class QrPictureRenderer
    {
        /// <summary>Blank cells of margin around the grown QR extents (spec: four).</summary>
        public const int MarginCells = 4;

        /// <summary>
        /// Renders <paramref name="sketch"/> into a square raster of
        /// <paramref name="pixelsAcross"/> pixels per side. The QR is centred
        /// exactly as the sketch geometry is (origin at picture centre, +y up
        /// so matrix row 0 renders at the top).
        /// </summary>
        public static QrPicture Render(QrSketch sketch, int pixelsAcross)
        {
            if (sketch == null)
                throw new ArgumentNullException(nameof(sketch));
            if (pixelsAcross < 8)
                throw new ArgumentOutOfRangeException(nameof(pixelsAcross),
                    "The preview needs at least a few pixels per side.");

            double pitchM = sketch.Info.PitchMm / 1000.0;
            double extentM = sketch.Info.FinalSizeMm / 1000.0 + 2.0 * MarginCells * pitchM;
            double mPerPixel = extentM / pixelsAcross;
            int w = pixelsAcross;
            int h = pixelsAcross;

            var pixels = new byte[w * h];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = 255; // white

            double minX = -extentM / 2.0;
            double topY = +extentM / 2.0;

            // Circle mode (spec #13, ticket #38): no contours exist — the
            // plan's emitted cells rasterize as true disks, analytically:
            // a pixel is dark iff its centre sits inside the dot. Same
            // footprint as the contour render, so the quiet-zone margin is
            // identical either way.
            if (sketch.CirclePlan != null)
            {
                var circlePlan = sketch.CirclePlan;
                double radiusM = circlePlan.DotDiameterM / 2.0;
                double radius2 = radiusM * radiusM;
                for (var r = 0; r < circlePlan.CellsPerSide; r++)
                    for (var c = 0; c < circlePlan.CellsPerSide; c++)
                    {
                        if (!circlePlan.Emits(r, c))
                            continue;
                        var centre = circlePlan.CellCentreM(r, c);
                        int c0 = (int)Math.Max(0,
                            Math.Ceiling((centre.X - radiusM - minX) / mPerPixel - 0.5));
                        int c1 = (int)Math.Min(w - 1,
                            Math.Ceiling((centre.X + radiusM - minX) / mPerPixel - 0.5) - 1);
                        int r0 = (int)Math.Max(0,
                            Math.Ceiling((topY - (centre.Y + radiusM)) / mPerPixel - 0.5));
                        int r1 = (int)Math.Min(h - 1,
                            Math.Ceiling((topY - (centre.Y - radiusM)) / mPerPixel - 0.5) - 1);
                        for (var pr = r0; pr <= r1; pr++)
                        {
                            double py = topY - (pr + 0.5) * mPerPixel;
                            double dy2 = (py - centre.Y) * (py - centre.Y);
                            int rowStart = pr * w;
                            for (var pc = c0; pc <= c1; pc++)
                            {
                                double px = minX + (pc + 0.5) * mPerPixel;
                                double dx = px - centre.X;
                                if (dx * dx + dy2 <= radius2)
                                    pixels[rowStart + pc] = 0;
                            }
                        }
                    }
                return new QrPicture(w, h, pixels, mPerPixel * 1000.0,
                    MarginCells * sketch.Info.PitchMm);
            }

            var crossings = new List<double>(64);

            for (var row = 0; row < h; row++)
            {
                double y = topY - (row + 0.5) * mPerPixel;
                crossings.Clear();
                foreach (var contour in sketch.Contours)
                {
                    var v = contour.Vertices;
                    for (var i = 0; i < v.Count; i++)
                    {
                        var a = v[i];
                        var b = v[(i + 1) % v.Count];
                        // Half-open edge rule: the edge crosses this scanline
                        // iff exactly one endpoint is at-or-below y. Horizontal
                        // edges never count; every vertex transition is counted
                        // exactly once, so the crossing count stays even.
                        if ((a.Y <= y) == (b.Y <= y))
                            continue;
                        double t = (y - a.Y) / (b.Y - a.Y);
                        crossings.Add(a.X + t * (b.X - a.X));
                    }
                }
                if (crossings.Count == 0)
                    continue;
                crossings.Sort();
                for (var k = 0; k + 1 < crossings.Count; k += 2)
                {
                    // Pixel centre inside [x0, x1) → filled (even-odd parity).
                    int c0 = (int)Math.Ceiling((crossings[k] - minX) / mPerPixel - 0.5);
                    int c1 = (int)Math.Ceiling((crossings[k + 1] - minX) / mPerPixel - 0.5) - 1;
                    if (c0 < 0) c0 = 0;
                    if (c1 >= w) c1 = w - 1;
                    int rowStart = row * w;
                    for (var c = c0; c <= c1; c++)
                        pixels[rowStart + c] = 0;
                }
            }

            return new QrPicture(w, h, pixels, mPerPixel * 1000.0,
                MarginCells * sketch.Info.PitchMm);
        }
    }
}
