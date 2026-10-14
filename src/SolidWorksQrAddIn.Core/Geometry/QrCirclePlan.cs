using System.Text;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The circle-dot emission plan (spec #13, ticket #38): everything a
    /// host needs to emit the QR as real circle sketch entities through a
    /// two-directional sketch linear pattern — the recipe proven live in
    /// docs/PATTERN-SPIKE.md (spike/qr-dot-pattern). The host consumes this
    /// verbatim; it recomputes no mask and no geometry:
    ///
    ///   1. draw the seed circle at <see cref="SeedCentreM"/> with diameter
    ///      <see cref="DotDiameterM"/> (CreateCircleByRadius);
    ///   2. CreateLinearSketchStepAndRepeat over
    ///      <see cref="PatternCells"/> × <see cref="PatternCells"/> at
    ///      <see cref="PitchM"/> spacing — columns step +x, rows step −y
    ///      (AngleX = 0, AngleY = −π/2 in the API's terms — row 0 sits at
    ///      the top of the upright code);
    ///   3. pass <see cref="DeleteInstances"/> verbatim as the create-time
    ///      skip string — the proven 1-based "(col,row)" pairs; the seed
    ///      instance (1,1) is never listed and can never be skipped.
    ///
    /// The pattern grid is asymmetric by polarity — the seed cell can never
    /// be skipped, so it must sit on a cell that always emits: under normal
    /// polarity the pattern spans the N×N DATA square, seeded at data cell
    /// (0,0) (always a dark finder corner; the light quiet-zone cells never
    /// emit so nothing is lost by not patterning them); under invert the
    /// pattern spans the whole M×M working matrix, seeded at matrix cell
    /// (0,0) (always a light quiet-zone corner — emits by definition).
    /// <see cref="Emits"/> is indexed by working-matrix (row, col); the
    /// pattern's instance (c+1, r+1) maps to matrix cell
    /// (<see cref="PatternOriginRow"/> + r, <see cref="PatternOriginCol"/> + c).
    ///
    /// Contains no SolidWorks objects.
    /// </summary>
    public sealed class QrCirclePlan
    {
        private readonly bool[,] _emit;

        /// <param name="emitCells">
        /// The post-invert emission mask over the full working matrix —
        /// emitCells[row, col] == true means a dot is delivered at that
        /// cell's centre. The constructor takes ownership of the array.
        /// </param>
        /// <param name="dataCells">N — the QR data-cell extent.</param>
        /// <param name="patternCells">
        /// The linear pattern's NumX = NumY — N under normal polarity
        /// (the data square), M under invert (the whole working matrix).
        /// </param>
        /// <param name="patternOriginRow">
        /// Working-matrix row of pattern instance (1,1) — the seed cell.
        /// </param>
        /// <param name="patternOriginCol">
        /// Working-matrix column of pattern instance (1,1).
        /// </param>
        public QrCirclePlan(
            bool[,] emitCells, int dataCells, int patternCells,
            int patternOriginRow, int patternOriginCol,
            double pitchM, double dotDiameterM, bool inverted)
        {
            _emit = emitCells;
            DataCells = dataCells;
            CellsPerSide = emitCells.GetLength(0);
            PatternCells = patternCells;
            PatternOriginRow = patternOriginRow;
            PatternOriginCol = patternOriginCol;
            PitchM = pitchM;
            DotDiameterM = dotDiameterM;
            DotFraction = dotDiameterM / pitchM;
            Inverted = inverted;
            SeedCentreM = CellCentreM(patternOriginRow, patternOriginCol);

            var skips = new StringBuilder();
            for (var r = 0; r < patternCells; r++)
                for (var c = 0; c < patternCells; c++)
                {
                    if (_emit[patternOriginRow + r, patternOriginCol + c])
                        continue;
                    if (skips.Length > 0)
                        skips.Append(' ');
                    skips.Append('(').Append(c + 1).Append(',')
                        .Append(r + 1).Append(')');
                    SkippedCount++;
                }
            DeleteInstances = skips.ToString();
            EmittedCount = patternCells * patternCells - SkippedCount;
        }

        /// <summary>QR data cells per side N (quiet zone excluded).</summary>
        public int DataCells { get; }

        /// <summary>Working-matrix cells per side M — the emission mask extent.</summary>
        public int CellsPerSide { get; }

        /// <summary>
        /// Whether a dot is delivered at working-matrix cell
        /// (<paramref name="row"/>, <paramref name="col"/>) — the post-invert
        /// emission mask: dark cells under normal polarity, light cells of
        /// the whole matrix under invert.
        /// </summary>
        public bool Emits(int row, int col) => _emit[row, col];

        /// <summary>The dots the pattern delivers — the instance count.</summary>
        public int EmittedCount { get; }

        /// <summary>Pattern instances the skip list omits.</summary>
        public int SkippedCount { get; }

        /// <summary>
        /// The pattern grid extent — CreateLinearSketchStepAndRepeat's
        /// NumX = NumY. N under normal polarity, M under invert.
        /// </summary>
        public int PatternCells { get; }

        /// <summary>
        /// Working-matrix row of the seed cell — pattern instance (1,1).
        /// <see cref="QrSketchInfo.QuietZoneCellsPerSide"/> under normal
        /// polarity (the data corner), 0 under invert.
        /// </summary>
        public int PatternOriginRow { get; }

        /// <summary>Working-matrix column of the seed cell — see <see cref="PatternOriginRow"/>.</summary>
        public int PatternOriginCol { get; }

        /// <summary>
        /// The seed circle's centre in sketch-space metres — pattern
        /// instance (1,1); the grid grows +x per column and −y per row
        /// from here.
        /// </summary>
        public SketchPoint2 SeedCentreM { get; }

        /// <summary>Cell pitch in sketch-space metres — the pattern spacing.</summary>
        public double PitchM { get; }

        /// <summary>The dot diameter in sketch-space metres.</summary>
        public double DotDiameterM { get; }

        /// <summary>The dot diameter as a fraction of <see cref="PitchM"/>.</summary>
        public double DotFraction { get; }

        /// <summary>
        /// The create-time skip list, verbatim: 1-based "(col,row)" pairs
        /// in the pattern grid, space-separated, row-major — the format
        /// docs/PATTERN-SPIKE.md PAT2 proved (single-number and 0-based
        /// forms silently no-op). The seed pair (1,1) is never present.
        /// </summary>
        public string DeleteInstances { get; }

        /// <summary>True when the dots land on the matrix's light cells.</summary>
        public bool Inverted { get; }

        /// <summary>
        /// The sketch-space centre (metres) of working-matrix cell
        /// (<paramref name="row"/>, <paramref name="col"/>) — same
        /// convention as the contour pipeline: the matrix centred on the
        /// origin, row 0 at +y.
        /// </summary>
        public SketchPoint2 CellCentreM(int row, int col)
        {
            double half = CellsPerSide * PitchM / 2.0;
            return new SketchPoint2(
                -half + (col + 0.5) * PitchM,
                +half - (row + 0.5) * PitchM);
        }
    }
}
