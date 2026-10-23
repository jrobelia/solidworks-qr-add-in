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

        /// <summary>
        /// Task-pane tab icon size. SolidWorks' tab strip renders icons at
        /// ~16-18px (the API docs historically specify 16x18; the sibling
        /// InvenTree add-in's working tab icon is 18x18). A 32px PNG passed
        /// to <c>CreateTaskpaneView2</c> is silently ignored — the menu strips
        /// keep <see cref="Sizes"/>; the tab gets its own dedicated render.
        /// </summary>
        public const int PaneTabSize = 18;

        /// <summary>File name of the extracted task-pane tab icon.</summary>
        public const string PaneTabFileName = "qr-tab-18.png";

        /// <summary>
        /// Below this pixel size even the compact 21-module symbol can't
        /// resolve — renders switch to <see cref="QrIconGlyph.IsDarkMini"/>'s
        /// stylized 15-module motif so the icon still reads as a QR code.
        /// </summary>
        public const int MiniGlyphThresholdPx = 32;

        /// <summary>
        /// Below this pixel size the real 29-module symbol can't resolve —
        /// renders switch to <see cref="QrIconGlyph.IsDarkCompact"/>'s real
        /// version-1 symbol (21×21), which stays crisp into the mid-30px range.
        /// </summary>
        public const int CompactGlyphThresholdPx = 64;

        /// <summary>PNG bytes of the QR glyph rendered at <paramref name="sizePx"/>.</summary>
        public static byte[] RenderPng(int sizePx)
        {
            if (sizePx <= 0) throw new ArgumentOutOfRangeException(nameof(sizePx));

            if (sizePx < MiniGlyphThresholdPx)
                return RenderMiniPng(sizePx);

            Func<int, int, bool> isDark;
            int modules;
            if (sizePx < CompactGlyphThresholdPx)
            {
                modules = QrIconGlyph.CompactSize;
                isDark = QrIconGlyph.IsDarkCompact;
            }
            else
            {
                modules = QrIconGlyph.Size;
                isDark = QrIconGlyph.IsDark;
            }

            using (var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                // Opaque white canvas: the QR quiet zone is spec-required
                // (the glyph must sit on light) and it keeps the icon legible
                // on dark toolbar/tab themes and dark taskbars — black modules
                // on a transparent canvas vanish there.
                g.Clear(Color.White);
                // A small quiet-zone margin (≈1 module at small sizes) keeps
                // the finder patterns readable; scaled module spans stay
                // pixel-aligned.
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
                            if (!isDark(r, c)) continue;
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
        /// Renders the mini motif. The finder eyes are drawn as QR-target
        /// rectangles — ring and gap get the same whole-pixel thickness and
        /// the core is centered by construction — so proportions and
        /// centering stay exact at fractional cell sizes (24px/17 modules
        /// etc. made a cell-rendered ring 1px on one side and 2px on the
        /// other). Data modules draw as uniform squares.
        /// </summary>
        private static byte[] RenderMiniPng(int sizePx)
        {
            int n = QrIconGlyph.MiniSize;
            int margin = Math.Max(1, (int)Math.Round(sizePx / (double)(n + 2)));
            double unit = (sizePx - 2.0 * margin) / n;

            int ring = Math.Max(1, (int)Math.Round(unit));
            int core = Math.Max(1, (int)Math.Round(3 * unit));
            int eye = 4 * ring + core;                     // ring-gap-core-gap-ring
            int dot = Math.Max(1, (int)Math.Round(unit));  // uniform data modules

            using (var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var dark = new SolidBrush(Color.Black))
            {
                g.Clear(Color.White);
                DrawEye(g, dark, margin, margin, eye, ring, core);
                DrawEye(g, dark, sizePx - margin - eye, margin, eye, ring, core);
                DrawEye(g, dark, margin, sizePx - margin - eye, eye, ring, core);
                for (int r = 0; r < n; r++)
                    for (int c = 0; c < n; c++)
                    {
                        if (!QrIconGlyph.IsDarkMini(r, c) || QrIconGlyph.IsMiniEyeCell(r, c)) continue;
                        g.FillRectangle(dark,
                            (int)Math.Round(margin + c * unit),
                            (int)Math.Round(margin + r * unit), dot, dot);
                    }
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private static void DrawEye(Graphics g, Brush dark, int x, int y, int w, int ring, int core)
        {
            g.FillRectangle(dark, x, y, w, w);
            g.FillRectangle(Brushes.White, x + ring, y + ring, w - 2 * ring, w - 2 * ring);
            int cin = 2 * ring; // ring + gap — symmetric on both sides
            g.FillRectangle(dark, x + cin, y + cin, core, core);
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

        /// <summary>
        /// Writes the dedicated task-pane tab icon (<see cref="PaneTabSize"/>,
        /// <see cref="PaneTabFileName"/>) into <paramref name="directory"/> —
        /// the same extraction dir the menu strips went to — and returns its
        /// path. Same idempotent repair semantics as
        /// <see cref="EnsureExtracted"/>. Throws on I/O failure; the caller
        /// treats the pane icon as best-effort.
        /// </summary>
        public static string EnsurePaneIcon(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, PaneTabFileName);
            byte[] png = RenderPng(PaneTabSize);
            if (!File.Exists(path) || !ContentEquals(path, png))
                File.WriteAllBytes(path, png);
            return path;
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
