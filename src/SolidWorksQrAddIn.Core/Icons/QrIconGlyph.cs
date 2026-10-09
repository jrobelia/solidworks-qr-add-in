using System;
using System.Collections.Generic;
using QRCoder;

namespace SolidWorksQrAddIn.Core.Icons
{
    /// <summary>
    /// The module layout for the add-in's toolbar/menu icon: a real QR code
    /// (the payload "QR" at ECC level M → a 21×21 version-1 symbol) with the
    /// quiet zone cropped off. Deterministic — the same glyph on every build
    /// and every machine.
    /// </summary>
    public static class QrIconGlyph
    {
        private const string Payload = "QR";
        private const QRCodeGenerator.ECCLevel Ecc = QRCodeGenerator.ECCLevel.M;

        private static readonly Lazy<bool[,]> Modules = new Lazy<bool[,]>(Build);

        /// <summary>Width and height of the glyph in modules (21 for "QR"/M).</summary>
        public static int Size => Modules.Value.GetLength(0);

        /// <summary>True when the module at (row, col) is dark.</summary>
        public static bool IsDark(int row, int col) => Modules.Value[row, col];

        private static bool[,] Build()
        {
            // QRCoder's ModuleMatrix includes a 4-module quiet-zone border.
            // Crop to the bounding box of dark modules so the icon fills the
            // canvas edge-to-edge (we add our own pixel margin at render time).
            List<BitMatrixRowShim> rows;
            using (var generator = new QRCodeGenerator())
            using (QRCodeData data = generator.CreateQrCode(Payload, Ecc))
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
