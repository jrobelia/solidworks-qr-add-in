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
        /// PNG bytes of the add-in icon rendered at <paramref name="sizePx"/>.
        /// Every size uses the same stylized motif — one consistent icon at
        /// 16px through 256px rather than a real QR symbol that dissolves
        /// into noise at icon sizes. (The wizard banner asset still draws
        /// the real repo-URL QR: it is large enough to scan.)
        /// </summary>
        public static byte[] RenderPng(int sizePx)
        {
            if (sizePx <= 0) throw new ArgumentOutOfRangeException(nameof(sizePx));
            return RenderMiniPng(sizePx);
        }

        /// <summary>
        /// Renders the mini motif. The finder eyes are drawn as QR-target
        /// rectangles anchored to the module grid — each eye spans exactly
        /// <see cref="QrIconGlyph.MiniEyeSpan"/> units so the gap between
        /// eyes keeps the same proportion at every size. Inside an eye the
        /// ring gets a whole-pixel thickness, the core sits centered, and
        /// the remaining pixels split symmetrically as the gap — ring and
        /// core stay near their 1:3 proportions and nothing lands 1px off
        /// center. Data modules draw as uniform squares.
        /// </summary>
        private static byte[] RenderMiniPng(int sizePx)
        {
            int n = QrIconGlyph.MiniSize;
            int margin = Math.Max(1, (int)Math.Round(sizePx / (double)(n + 2)));
            double unit = (sizePx - 2.0 * margin) / n;

            int ring = Math.Max(1, (int)Math.Round(unit));
            int core = Math.Max(1, (int)Math.Round(3 * unit));
            int dot = Math.Max(1, (int)Math.Round(unit));  // uniform data modules
            int e = QrIconGlyph.MiniEyeSpan;
            int o = QrIconGlyph.MiniEyeOffset;

            using (var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var dark = new SolidBrush(Color.Black))
            {
                g.Clear(Color.White);
                DrawEye(g, dark,
                    margin, margin,
                    (int)Math.Round(margin + e * unit), (int)Math.Round(margin + e * unit),
                    ring, core, 3 * unit);
                DrawEye(g, dark,
                    (int)Math.Round(margin + o * unit), margin,
                    sizePx - margin, (int)Math.Round(margin + e * unit),
                    ring, core, 3 * unit);
                DrawEye(g, dark,
                    margin, (int)Math.Round(margin + o * unit),
                    (int)Math.Round(margin + e * unit), sizePx - margin,
                    ring, core, 3 * unit);
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

        // One finder eye: black square, white inset for the ring, centered
        // core. w/h come from grid anchors so all eyes share the same span;
        // the core nudges ±1px if needed so the gap splits symmetrically.
        private static void DrawEye(Graphics g, Brush dark,
            int x, int y, int x1, int y1, int ring, int core, double idealCore)
        {
            int w = Math.Max(3, x1 - x), h = Math.Max(3, y1 - y);
            g.FillRectangle(dark, x, y, w, h);
            g.FillRectangle(Brushes.White, x + ring, y + ring,
                Math.Max(1, w - 2 * ring), Math.Max(1, h - 2 * ring));
            int c = core;
            int slack = w - 2 * ring - c;
            if ((slack & 1) != 0)
                c += Math.Abs(c + 1 - idealCore) <= Math.Abs(c - 1 - idealCore) ? 1 : -1;
            int cin = ring + Math.Max(0, (w - 2 * ring - c) / 2);
            int cs = Math.Max(1, w - 2 * cin);
            g.FillRectangle(dark, x + cin, y + cin, cs, cs);
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
