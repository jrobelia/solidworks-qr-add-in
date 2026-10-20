using System.Collections.Generic;
using System.Globalization;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// One equation entry the circle emitter installs — the driven
    /// dimension's equation-reference name plus its expression, ready for
    /// <c>IEquationMgr.Add(-1, eq.Text)</c> (the spike-proven sequence —
    /// docs/PATTERN-SPIKE.md PAT4).
    /// </summary>
    public readonly struct QrCircleEquation
    {
        public QrCircleEquation(string lhs, string rhs)
        {
            Lhs = lhs;
            Rhs = rhs;
        }

        /// <summary>The driven dimension — bare "D{n}@{sketch}" reference (no quotes).</summary>
        public string Lhs { get; }

        /// <summary>The expression — referenced dims carry their quotes: "D1@Sketch3"/21.</summary>
        public string Rhs { get; }

        /// <summary>The complete Add argument: "\"lhs\" = rhs".</summary>
        public string Text => $"\"{Lhs}\" = {Rhs}";
    }

    /// <summary>
    /// The dimension names a circle-dot sketch's equations reference, in the
    /// equation form "D{n}@{sketch}" (the trailing "@Part…" of
    /// IDimension.FullName already stripped — PAT4). SolidWorks numbers
    /// sketch dimensions in creation order, so the host discovers each name
    /// live rather than assuming it; this scheme is that discovery plus the
    /// sign each point-to-point distance dimension measured at creation —
    /// SolidWorks can sign such dims by direction, so the equation mirrors
    /// the sign: the driven value reproduces the drawn side instead of
    /// flipping the geometry across it.
    ///
    /// The whole derived set funnels through ONE master reference —
    /// <see cref="Box"/> is every equation's root (directly for the two
    /// spacing dims, transitively for everything else). A driven
    /// "echo" dim on the square was tried as the root so a master delete
    /// would orphan nothing — reverted after live evidence: SolidWorks
    /// evaluates equations BEFORE the solve refreshes driven dims, so the
    /// spacing pair lagged one rebuild behind every master edit (25→50 mm
    /// left spacing at the old pitch). Only a DRIVING dim is a single-solve
    /// equation source, and any driving dim is deletable — the
    /// dimensionless end-state therefore keeps the documented transition
    /// (docs/CIRCLE-DIMENSIONS.md): delete the box dim, bind the corners to
    /// part geometry, retarget exactly the two spacing equations at the
    /// part's driving dim — diameter, offsets and quiet-zone dims all
    /// follow (the PAT6 arrangement).
    /// </summary>
    public sealed class QrCircleDimScheme
    {
        /// <summary>
        /// The driving dimension on the QR square — the master the user edits
        /// (SetSystemValue3) to rescale everything. Never an equation's left
        /// side: it stays user-editable driving.
        /// </summary>
        public string Box = "";

        /// <summary>The pattern's X-spacing dimension (the smaller D-number of the pair).</summary>
        public string SpacingX = "";

        /// <summary>The pattern's Y-spacing dimension.</summary>
        public string SpacingY = "";

        /// <summary>The seed circle's diameter dimension.</summary>
        public string Diameter = "";

        /// <summary>The horizontal dim: pattern-anchor box corner → seed centre.</summary>
        public string SeedOffsetX = "";

        /// <summary>The vertical dim: pattern-anchor box corner → seed centre.</summary>
        public string SeedOffsetY = "";

        /// <summary>The horizontal dim: QR-square left edge → keep-out left edge.</summary>
        public string QuietOffsetX = "";

        /// <summary>The vertical dim: QR-square bottom edge → keep-out bottom edge.</summary>
        public string QuietOffsetY = "";

        /// <summary>The keep-out square's edge-length dimension.</summary>
        public string QuietSize = "";

        /// <summary>+1 or −1: the sign the created dimension measured — mirrored into the equation.</summary>
        public int SeedOffsetXSign = 1;

        /// <summary>See <see cref="SeedOffsetXSign"/>.</summary>
        public int SeedOffsetYSign = 1;

        /// <summary>See <see cref="SeedOffsetXSign"/>.</summary>
        public int QuietOffsetXSign = 1;

        /// <summary>See <see cref="SeedOffsetXSign"/>.</summary>
        public int QuietOffsetYSign = 1;
    }

    /// <summary>
    /// The equation table the circle plan hands the host (spec #13,
    /// ticket #40) — pure functions of the plan's constants (N, M, the
    /// dot fraction) over a <see cref="QrCircleDimScheme"/>'s discovered
    /// names, so the exact strings are headless-testable:
    ///
    ///   spacing   = box / N           — the pattern's two spacing dims
    ///   diameter  = spacingX · f      — the seed circle's diameter dim
    ///   seedOffX  = ±spacingX / 2     — box corner → seed centre
    ///   seedOffY  = ±spacingY / 2
    ///   quietOffX = ±spacingX · 4     — the four-cell margin, QR → keep-out edge
    ///   quietOffY = ±spacingY · 4
    ///   quietSize = spacingX · M      — the keep-out spans the whole matrix
    ///
    /// spacing·4 and spacing·M are the ticket's box·4/N and box·(1+8/N)
    /// rewritten through the spacing dims — identical values, and the whole
    /// quiet-zone wiring survives a box-dim retarget for free.
    /// </summary>
    public static class QrCircleEquations
    {
        /// <summary>
        /// The equations to install, in dependency order (the spacing pair
        /// first — everything else references it). A dim name left empty is
        /// skipped, so a partial scheme emits a partial table; the host only
        /// leaves a name empty when the corresponding dim could not be
        /// created at all.
        /// </summary>
        public static IReadOnlyList<QrCircleEquation> For(
            QrCirclePlan plan, QrCircleDimScheme dims)
        {
            int n = plan.DataCells;
            int m = plan.CellsPerSide;
            int q = QrSketchInfo.QuietZoneCellsPerSide;
            string box = $"\"{dims.Box}\"";
            string sx = $"\"{dims.SpacingX}\"";
            string sy = $"\"{dims.SpacingY}\"";
            string dot = plan.DotFraction.ToString("G15", CultureInfo.InvariantCulture);

            var equations = new List<QrCircleEquation>(8);
            Add(equations, dims.SpacingX, $"{box}/{n}");
            Add(equations, dims.SpacingY, $"{box}/{n}");
            Add(equations, dims.Diameter, $"{sx}*{dot}");
            Add(equations, dims.SeedOffsetX, $"{Sign(dims.SeedOffsetXSign)}{sx}/2");
            Add(equations, dims.SeedOffsetY, $"{Sign(dims.SeedOffsetYSign)}{sy}/2");
            Add(equations, dims.QuietOffsetX, $"{Sign(dims.QuietOffsetXSign)}{sx}*{q}");
            Add(equations, dims.QuietOffsetY, $"{Sign(dims.QuietOffsetYSign)}{sy}*{q}");
            Add(equations, dims.QuietSize, $"{sx}*{m}");
            return equations;
        }

        private static void Add(List<QrCircleEquation> equations, string lhs, string rhs)
        {
            if (lhs.Length != 0)
                equations.Add(new QrCircleEquation(lhs, rhs));
        }

        private static string Sign(int sign) => sign < 0 ? "-" : "";
    }
}
