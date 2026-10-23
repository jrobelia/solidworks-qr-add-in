using System;
using System.Collections.Generic;
using QRCoder;

namespace SolidWorksQrAddIn.Core.Icons
{
    /// <summary>
    /// The module layouts for the add-in's toolbar/menu icons, all
    /// deterministic — the same glyphs on every build and every machine:
    /// a real QR code of the repo URL (ECC L → 29×29 version-3) for sizes
    /// that can resolve it, a real version-1 QR (21×21) that scans to the
    /// product name for mid sizes, and a stylized 15×15 motif for tiny
    /// renders. ECC L keeps each symbol one version smaller — damage
    /// tolerance buys nothing on a screen icon.
    /// </summary>
    public static class QrIconGlyph
    {
        private const string Payload = "https://github.com/jrobelia/solidworks-qr-add-in";
        private const QRCodeGenerator.ECCLevel Ecc = QRCodeGenerator.ECCLevel.L;

        // Short payload for the mid-size tier: alphanumeric mode keeps it a
        // version-1 (21x21) symbol — the smallest real, decodable QR — and it
        // scans to the product name.
        private const string CompactPayload = "SOLIDWORKS-QR-ADD-IN";

        private static readonly Lazy<bool[,]> Modules = new Lazy<bool[,]>(() => Build(Payload));
        private static readonly Lazy<bool[,]> CompactModules = new Lazy<bool[,]>(() => Build(CompactPayload));

        /// <summary>Width and height of the glyph in modules (29 for the repo URL at L).</summary>
        public static int Size => Modules.Value.GetLength(0);

        /// <summary>True when the module at (row, col) is dark.</summary>
        public static bool IsDark(int row, int col) => Modules.Value[row, col];

        /// <summary>Width and height of the compact glyph in modules (21 for a version-1 symbol).</summary>
        public static int CompactSize => CompactModules.Value.GetLength(0);

        /// <summary>True when the compact glyph's module at (row, col) is dark.</summary>
        public static bool IsDarkCompact(int row, int col) => CompactModules.Value[row, col];

        /// <summary>
        /// Stylized 17×17 QR motif for renders too small for any real symbol
        /// to resolve — three spaced finder eyes at real QR proportions
        /// (ring : gap : core = 1 : 1 : 3) plus a sparse data field in the
        /// bottom-right quadrant. It is an icon, not a scannable code;
        /// below ~32px even the compact 21-module glyph blurs into noise.
        /// The renderer draws the eyes as QR-target rectangles so ring
        /// thickness and centering stay exact at fractional cell sizes.
        /// </summary>
        public const int MiniSize = 17;

        /// <summary>Module span of one mini finder eye (7, like a real finder).</summary>
        public const int MiniEyeSpan = 7;

        /// <summary>Module offset of the right/bottom eyes (10 — leaves a 3-module gap).</summary>
        public const int MiniEyeOffset = 10;

        private static readonly string[] MiniRows =
        {
            "XXXXXXX...XXXXXXX",
            "X.....X...X.....X",
            "X.XXX.X...X.XXX.X",
            "X.XXX.X...X.XXX.X",
            "X.XXX.X...X.XXX.X",
            "X.....X...X.....X",
            "XXXXXXX...XXXXXXX",
            ".................",
            ".................",
            ".................",
            "XXXXXXX...XX.X.X.",
            "X.....X....X.X.XX",
            "X.XXX.X...X.X.X..",
            "X.XXX.X....XX..X.",
            "X.XXX.X...X..X.X.",
            "X.....X....X.XX..",
            "XXXXXXX...XX..X.X",
        };

        /// <summary>
        /// True when (row, col) sits inside one of the three mini finder
        /// eyes — those cells are drawn geometrically, not as modules.
        /// </summary>
        public static bool IsMiniEyeCell(int row, int col)
        {
            bool inTop = row < MiniEyeSpan;
            bool inLeft = col < MiniEyeSpan;
            bool inRight = col >= MiniEyeOffset;
            bool inBottom = row >= MiniEyeOffset;
            return (inTop && inLeft) || (inTop && inRight) || (inBottom && inLeft);
        }

        /// <summary>True when the mini glyph's module at (row, col) is dark.</summary>
        public static bool IsDarkMini(int row, int col) => MiniRows[row][col] == 'X';

        private static bool[,] Build(string payload)
        {
            // QRCoder's ModuleMatrix includes a 4-module quiet-zone border.
            // Crop to the bounding box of dark modules so the icon fills the
            // canvas edge-to-edge (we add our own pixel margin at render time).
            List<BitMatrixRowShim> rows;
            using (var generator = new QRCodeGenerator())
            using (QRCodeData data = generator.CreateQrCode(payload, Ecc))
            {
                rows = new List<BitMatrixRowShim>(data.ModuleMatrix.Count);
                foreach (System.Collections.BitArray row in data.ModuleMatrix)
                    rows.Add(new BitMatrixRowShim(row));
            }

            int minRow = int.MaxValue, maxRow = -1, minCol = int.MaxValue, maxCol = -1;
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    if (rows[r][c])
                    {
                        if (r < minRow) minRow = r;
                        if (r > maxRow) maxRow = r;
                        if (c < minCol) minCol = c;
                        if (c > maxCol) maxCol = c;
                    }

            if (maxRow < 0)
                throw new InvalidOperationException("QR glyph produced no dark modules.");

            int n = Math.Max(maxRow - minRow + 1, maxCol - minCol + 1);
            var modules = new bool[n, n];
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    modules[r, c] = rows[minRow + r][minCol + c];
            return modules;
        }

        // Thin shim so we do not keep QRCoder's QRCodeData (a COM-free but
        // disposable-heavy object) alive in the static cache.
        private sealed class BitMatrixRowShim
        {
            private readonly bool[] _cells;
            public BitMatrixRowShim(System.Collections.BitArray row)
            {
                _cells = new bool[row.Count];
                row.CopyTo(_cells, 0);
            }
            public int Length => _cells.Length;
            public bool this[int i] => _cells[i];
        }
    }
}
