using System;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// The document's length unit as a host fact: reads
    /// <see cref="IModelDoc2.LengthUnit"/> (an swLengthUnit_e) and maps it to
    /// metres-per-unit plus a display symbol. The host state snapshot
    /// carries it for the pane's cell-size readout (the insertion session
    /// draws the block definition at the staged real size — ADR 0004 —
    /// so no conversion happens there any more).
    /// </summary>
    internal static class DocLengthUnits
    {
        /// <summary>
        /// The document's length unit, or millimetres when the read fails —
        /// an unreadable unit must never break a create; the block then sizes
        /// in mm, the add-in's baseline unit.
        /// </summary>
        public static HostLengthUnit Of(IModelDoc2 doc)
        {
            try
            {
                return FromSwUnit((swLengthUnit_e)doc.LengthUnit);
            }
            catch (Exception ex)
            {
                Diag.Log($"LengthUnit read failed ({ex.Message}) — assuming mm");
                return HostLengthUnit.Millimetres;
            }
        }

        /// <summary>swLengthUnit_e → (metres per unit, display symbol).</summary>
        private static HostLengthUnit FromSwUnit(swLengthUnit_e unit)
        {
            switch (unit)
            {
                case swLengthUnit_e.swCM: return new HostLengthUnit(0.01, "cm");
                case swLengthUnit_e.swMETER: return new HostLengthUnit(1.0, "m");
                case swLengthUnit_e.swINCHES: return new HostLengthUnit(0.0254, "in");
                case swLengthUnit_e.swFEET: return new HostLengthUnit(0.3048, "ft");
                // Feet-and-inches docs measure in feet with a fractional-inch
                // display — the leading unit is feet.
                case swLengthUnit_e.swFEETINCHES: return new HostLengthUnit(0.3048, "ft");
                case swLengthUnit_e.swANGSTROM: return new HostLengthUnit(1e-10, "Å");
                case swLengthUnit_e.swNANOMETER: return new HostLengthUnit(1e-9, "nm");
                case swLengthUnit_e.swMICRON: return new HostLengthUnit(1e-6, "µm");
                case swLengthUnit_e.swMIL: return new HostLengthUnit(2.54e-5, "mil");
                case swLengthUnit_e.swUIN: return new HostLengthUnit(2.54e-8, "µin");
                case swLengthUnit_e.swMM:
                default: return HostLengthUnit.Millimetres;
            }
        }
    }
}
