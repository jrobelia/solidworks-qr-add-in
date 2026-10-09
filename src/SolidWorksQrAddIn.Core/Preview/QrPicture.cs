using System;
using System.Security.Cryptography;
using System.Text;

namespace SolidWorksQrAddIn.Core.Preview
{
    /// <summary>
    /// The monochrome QR preview raster: Gray8 pixels (0 = black, 255 = white),
    /// row-major, row 0 at the top. Rendered from the validated
    /// <see cref="QrSketch"/> contour result — the same grown geometry handed
    /// to sketch insertion — so the picture is always a faithful reference of
    /// what would be sketched. Contains no WPF objects; the add-in converts
    /// <see cref="Pixels"/> straight into a Gray8 BitmapSource.
    /// </summary>
    public sealed class QrPicture
    {
        public QrPicture(int widthPixels, int heightPixels, byte[] pixels,
            double mmPerPixel, double marginMm)
        {
            WidthPixels = widthPixels;
            HeightPixels = heightPixels;
            Pixels = pixels;
            MmPerPixel = mmPerPixel;
            MarginMm = marginMm;
        }

        public int WidthPixels { get; }
        public int HeightPixels { get; }

        /// <summary>Row-major Gray8 buffer: 0 = black (filled QR material), 255 = white.</summary>
        public byte[] Pixels { get; }

        /// <summary>Physical size of one pixel — the picture is square.</summary>
        public double MmPerPixel { get; }

        /// <summary>Blank white margin around the grown QR extents, in mm (4 cells).</summary>
        public double MarginMm { get; }

        public bool IsDark(int x, int y) => Pixels[y * WidthPixels + x] == 0;

        public int DarkPixelCount
        {
            get
            {
                int n = 0;
                foreach (var p in Pixels)
                    if (p == 0) n++;
                return n;
            }
        }

        /// <summary>
        /// Stable fingerprint of the rendered raster (dimensions + pixels).
        /// Two pictures of equal geometry have equal signatures — the live
        /// probe uses this to prove the window's picture matches the module's
        /// rendered result for the same content.
        /// </summary>
        public string Signature()
        {
            using (var sha = SHA256.Create())
            {
                var dims = new byte[8];
                Buffer.BlockCopy(BitConverter.GetBytes(WidthPixels), 0, dims, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(HeightPixels), 0, dims, 4, 4);
                sha.TransformBlock(dims, 0, dims.Length, null, 0);
                sha.TransformFinalBlock(Pixels, 0, Pixels.Length);
                var hex = new StringBuilder(64);
                foreach (var b in sha.Hash!)
                    hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
