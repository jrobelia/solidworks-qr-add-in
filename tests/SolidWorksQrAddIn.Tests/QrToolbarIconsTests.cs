using System;
using System.Drawing;
using System.IO;
using SolidWorksQrAddIn.Core.Icons;
using Xunit;

namespace SolidWorksQrAddIn.Tests
{
    /// <summary>
    /// The command-group icon seam: the glyph layout, the PNG renderer, and
    /// the on-disk extraction that ConnectToSW hands to
    /// ICommandGroup.IconList/MainIconList.
    /// </summary>
    public class QrToolbarIconsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(), "qr-icons-test-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
            catch { }
        }

        // ---------- glyph ----------

        [Fact]
        public void Glyph_is_a_version3_symbol_29x29()
        {
            // Payload is the repo URL at ECC L — scans to the project page.
            Assert.Equal(29, QrIconGlyph.Size);
        }

        [Fact]
        public void Glyph_has_finder_patterns_in_three_corners()
        {
            int n = QrIconGlyph.Size; // 21; finders occupy 7x7 at three corners
            int[][] corners = { new[] { 0, 0 }, new[] { 0, n - 7 }, new[] { n - 7, 0 } };
            foreach (var corner in corners)
            {
                int r0 = corner[0], c0 = corner[1];
                // Outer ring dark, inner ring light, 3x3 core dark.
                for (int i = 0; i < 7; i++)
                {
                    Assert.True(QrIconGlyph.IsDark(r0, c0 + i));
                    Assert.True(QrIconGlyph.IsDark(r0 + 6, c0 + i));
                    Assert.True(QrIconGlyph.IsDark(r0 + i, c0));
                    Assert.True(QrIconGlyph.IsDark(r0 + i, c0 + 6));
                }
                Assert.False(QrIconGlyph.IsDark(r0 + 1, c0 + 1));
                for (int r = 2; r <= 4; r++)
                    for (int c = 2; c <= 4; c++)
                        Assert.True(QrIconGlyph.IsDark(r0 + r, c0 + c));
            }
            // Bottom-right corner has no finder pattern (it carries data).
            Assert.False(
                QrIconGlyph.IsDark(n - 1, n - 1) && QrIconGlyph.IsDark(n - 1, n - 2)
                && QrIconGlyph.IsDark(n - 2, n - 1) && QrIconGlyph.IsDark(n - 1, n - 7));
        }

        [Fact]
        public void Mini_glyph_has_finder_eyes_in_three_corners()
        {
            // 17x17; eyes use real finder proportions (ring, gap, 3x3 core)
            // with a 3-module gap between them.
            int e = QrIconGlyph.MiniEyeOffset; // 10
            int[][] corners = { new[] { 0, 0 }, new[] { 0, e }, new[] { e, 0 } };
            foreach (var corner in corners)
            {
                int r0 = corner[0], c0 = corner[1];
                for (int i = 0; i < 7; i++)
                {
                    Assert.True(QrIconGlyph.IsDarkMini(r0, c0 + i));
                    Assert.True(QrIconGlyph.IsDarkMini(r0 + 6, c0 + i));
                    Assert.True(QrIconGlyph.IsDarkMini(r0 + i, c0));
                    Assert.True(QrIconGlyph.IsDarkMini(r0 + i, c0 + 6));
                }
                Assert.False(QrIconGlyph.IsDarkMini(r0 + 1, c0 + 1));
                for (int r = 2; r <= 4; r++)
                    for (int c = 2; c <= 4; c++)
                        Assert.True(QrIconGlyph.IsDarkMini(r0 + r, c0 + c));
            }
            // The band between the eyes stays light — the eyes need
            // visible separation at tiny sizes.
            for (int c = 7; c < e; c++) Assert.False(QrIconGlyph.IsDarkMini(0, c));
        }

        [Theory]
        [InlineData(18)]
        [InlineData(32)]
        [InlineData(48)]
        [InlineData(64)]
        [InlineData(128)]
        public void Every_size_renders_the_mini_motif(int px)
        {
            // One icon at every size: the light separator band between the
            // eyes is unique to the motif — a real QR fills it with data.
            byte[] png = QrToolbarIcons.RenderPng(px);
            using (var bmp = new Bitmap(new MemoryStream(png)))
            {
                int n = QrIconGlyph.MiniSize;
                int margin = Math.Max(1, (int)Math.Round(px / (double)(n + 2)));
                double unit = (px - 2.0 * margin) / n;
                int ring = Math.Max(1, (int)Math.Round(unit));
                int core = Math.Max(1, (int)Math.Round(3 * unit));
                int eye = (int)Math.Round(margin + QrIconGlyph.MiniEyeSpan * unit) - margin;
                int c = core;
                int slack = eye - 2 * ring - c;
                if ((slack & 1) != 0)
                    c += Math.Abs(c + 1 - 3 * unit) <= Math.Abs(c - 1 - 3 * unit) ? 1 : -1;
                int gap = Math.Max(0, (eye - 2 * ring - c) / 2);

                // TL eye: dark ring, light gap, dark centered core.
                int mid = margin + eye / 2;
                Assert.Equal(Color.Black.ToArgb(), bmp.GetPixel(mid, mid).ToArgb());
                Assert.Equal(Color.Black.ToArgb(),
                    bmp.GetPixel(margin + ring / 2, mid).ToArgb());
                Assert.Equal(Color.White.ToArgb(),
                    bmp.GetPixel(margin + ring + gap / 2, mid).ToArgb());

                // The empty band between the top eyes stays white — a real
                // symbol would carry timing/data modules through it.
                int bandY = (int)(margin + 8.0 * unit);
                for (int x = margin; x < px - margin; x++)
                    Assert.Equal(Color.White.ToArgb(), bmp.GetPixel(x, bandY).ToArgb());
            }
        }

        // ---------- PNG rendering ----------

        [Theory]
        [InlineData(20)]
        [InlineData(32)]
        [InlineData(40)]
        [InlineData(64)]
        [InlineData(96)]
        [InlineData(128)]
        public void RenderPng_produces_an_exact_size_png(int size)
        {
            byte[] png = QrToolbarIcons.RenderPng(size);
            // PNG signature
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take4());
            using (var bmp = new Bitmap(new MemoryStream(png)))
            {
                Assert.Equal(size, bmp.Width);
                Assert.Equal(size, bmp.Height);
                // Corner pixel is inside the white quiet margin — opaque so
                // the icon stays legible on dark surfaces.
                Assert.Equal(Color.White.ToArgb(), bmp.GetPixel(0, 0).ToArgb());
                // The top-left finder pattern leaves dark pixels somewhere
                // inside the margin.
                bool anyDark = false;
                for (int y = 1; y < size / 3 && !anyDark; y++)
                    for (int x = 1; x < size / 3 && !anyDark; x++)
                        anyDark = bmp.GetPixel(x, y).ToArgb() == Color.Black.ToArgb();
                Assert.True(anyDark, "expected dark finder-pattern pixels");
            }
        }

        [Fact]
        public void RenderPng_rejects_nonpositive_size()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => QrToolbarIcons.RenderPng(0));
        }

        // ---------- extraction ----------

        [Fact]
        public void Sizes_cover_the_six_solidworks_resolutions_in_order()
        {
            Assert.Equal(new[] { 20, 32, 40, 64, 96, 128 }, QrToolbarIcons.Sizes);
        }

        [Fact]
        public void EnsureExtracted_writes_one_png_per_size_and_returns_paths()
        {
            string[] paths = QrToolbarIcons.EnsureExtracted(_dir);
            Assert.Equal(QrToolbarIcons.Sizes.Length, paths.Length);
            for (int i = 0; i < paths.Length; i++)
            {
                Assert.True(File.Exists(paths[i]), paths[i]);
                Assert.Equal($"qr-cmd-{QrToolbarIcons.Sizes[i]}.png",
                    Path.GetFileName(paths[i]));
            }
        }

        // ---------- task-pane tab icon ----------

        [Fact]
        public void EnsurePaneIcon_writes_a_small_png_and_returns_its_path()
        {
            string path = QrToolbarIcons.EnsurePaneIcon(_dir);

            Assert.Equal(QrToolbarIcons.PaneTabFileName, Path.GetFileName(path));
            Assert.True(File.Exists(path));
            using (var bmp = new Bitmap(path))
            {
                // SolidWorks draws task-pane tab icons at ~16-18px and ignores
                // larger images — the tab file must stay in that size class.
                Assert.Equal(QrToolbarIcons.PaneTabSize, bmp.Width);
                Assert.Equal(QrToolbarIcons.PaneTabSize, bmp.Height);
                Assert.True(bmp.Width <= 18, "tab icons must stay in the 16-18px class");
            }
        }

        [Fact]
        public void EnsurePaneIcon_is_idempotent_and_repairs_drift()
        {
            string path = QrToolbarIcons.EnsurePaneIcon(_dir);
            var stamp = File.GetLastWriteTimeUtc(path);

            Assert.Equal(path, QrToolbarIcons.EnsurePaneIcon(_dir));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));

            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            QrToolbarIcons.EnsurePaneIcon(_dir);
            byte[] repaired = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, repaired.Take4());
        }

        [Fact]
        public void RenderPng_produces_an_exact_size_pane_icon()
        {
            byte[] png = QrToolbarIcons.RenderPng(QrToolbarIcons.PaneTabSize);
            using (var bmp = new Bitmap(new MemoryStream(png)))
            {
                Assert.Equal(QrToolbarIcons.PaneTabSize, bmp.Width);
                // Even at 18px the finder pattern must leave dark pixels.
                bool anyDark = false;
                for (int y = 0; y < bmp.Height && !anyDark; y++)
                    for (int x = 0; x < bmp.Width && !anyDark; x++)
                        anyDark = bmp.GetPixel(x, y).ToArgb() == Color.Black.ToArgb();
                Assert.True(anyDark, "expected dark finder-pattern pixels at tab size");
            }
        }

        [Fact]
        public void EnsureExtracted_is_idempotent_and_repairs_drift()
        {
            string[] first = QrToolbarIcons.EnsureExtracted(_dir);
            var stamp = File.GetLastWriteTimeUtc(first[0]);

            // A second run over an identical set does not rewrite the file.
            string[] second = QrToolbarIcons.EnsureExtracted(_dir);
            Assert.Equal(first, second);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(first[0]));

            // If a cached icon drifts (older build, tampering) it is repaired.
            File.WriteAllBytes(first[1], new byte[] { 1, 2, 3 });
            QrToolbarIcons.EnsureExtracted(_dir);
            byte[] repaired = File.ReadAllBytes(first[1]);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, repaired.Take4());
        }
    }

    internal static class ByteArrayExt
    {
        public static byte[] Take4(this byte[] b)
        {
            var r = new byte[4];
            Array.Copy(b, r, 4);
            return r;
        }
    }
}
