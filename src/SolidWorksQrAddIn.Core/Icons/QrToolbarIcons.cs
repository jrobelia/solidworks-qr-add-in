using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SolidWorksQrAddIn.Core.Icons
{
    /// <summary>
    /// Produces the SolidWorks command-group icon set for the QR add-in.
    ///
    /// ICommandGroup.IconList/MainIconList take a string[] of image-file paths:
    /// one PNG per supported toolbar resolution, each file being a horizontal
    /// strip of that many pixels high containing every command's icon at that
    /// size. This add-in has a single command, so each strip is one square
    /// icon — the same file serves IconList (command item, ImageIndex 0) and
    /// MainIconList (the group's own button).
    /// </summary>
    public static class QrToolbarIcons
    {
        /// <summary>
        /// The six resolutions SolidWorks selects from, smallest first
        /// (SolidWorks 2016+ high-DPI convention). SolidWorks picks the best
        /// match per DPI; a partial set is tolerated, but we ship all six.
        /// </summary>
        public static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };

        /// <summary>PNG bytes of the QR glyph rendered at <paramref name="sizePx"/>.</summary>
        public static byte[] RenderPng(int sizePx)
        {
            if (sizePx <= 0) throw new ArgumentOutOfRangeException(nameof(sizePx));

            using (var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);

                int modules = QrIconGlyph.Size;
                // A small quiet-zone margin (≈1 module at small sizes) keeps
                // the finder patterns readable against SolidWorks' toolbar
                // background; scaled module spans stay pixel-aligned.
                int margin = Math.Max(1, (int)Math.Round(sizePx / (double)(modules + 2)));
                double inner = sizePx - 2.0 * margin;
                double cell = inner / modules;

                using (var dark = new SolidBrush(Color.Black))
                {
                    for (int r = 0; r < modules; r++)
                    {
                        int y0 = (int)Math.Round(margin + r * cell);
                        int y1 = (int)Math.Round(margin + (r + 1) * cell);
                        for (int c = 0; c < modules; c++)
                        {
                            if (!QrIconGlyph.IsDark(r, c)) continue;
                            int x0 = (int)Math.Round(margin + c * cell);
                            int x1 = (int)Math.Round(margin + (c + 1) * cell);
                            g.FillRectangle(dark, x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
                        }
                    }
                }

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Writes the full icon set as <c>qr-cmd-{size}.png</c> into
        /// <paramref name="directory"/> (created if needed) and returns the
        /// file paths in <see cref="Sizes"/> order — the exact array shape for
        /// both ICommandGroup.IconList and MainIconList. Idempotent: existing
        /// files are overwritten only when their content differs, so a
        /// reconnect after an icon change refreshes the cache while an
        /// unchanged set is a no-op. Throws on I/O failure — the caller
        /// decides whether icons are fatal (the add-in logs and continues).
        /// </summary>
        public static string[] EnsureExtracted(string directory)
        {
            Directory.CreateDirectory(directory);
            var paths = new string[Sizes.Length];
            for (int i = 0; i < Sizes.Length; i++)
            {
                string path = Path.Combine(directory, $"qr-cmd-{Sizes[i]}.png");
                byte[] png = RenderPng(Sizes[i]);
                if (!File.Exists(path) || !ContentEquals(path, png))
                    File.WriteAllBytes(path, png);
                paths[i] = path;
            }
            return paths;
        }

        private static bool ContentEquals(string path, byte[] expected)
        {
            var actual = File.ReadAllBytes(path);
            if (actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }
    }
}
