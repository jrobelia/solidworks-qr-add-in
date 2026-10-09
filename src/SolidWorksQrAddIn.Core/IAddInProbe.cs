using System.Runtime.InteropServices;
using SolidWorksQrAddIn.Core.Registration;

namespace SolidWorksQrAddIn.Core
{
    /// <summary>
    /// COM-visible test hook exposed by the add-in object. A live harness gets
    /// it via ISldWorks.GetAddInObject("{clsid}") and invokes the SAME code
    /// path the SolidWorks menu command uses — without popping UI.
    /// Lives in Core (not the add-in) so the harness only needs this assembly.
    /// </summary>
    [ComVisible(true)]
    [Guid("4813B19B-E14E-4201-B961-4C7AC791DDD8")] // == AddinIdentity.ProbeIid
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IAddInProbe
    {
        /// <summary>
        /// Runs the Create QR Sketch command (fixed content/size/growth)
        /// against the live host. Returns "OK &lt;sketchName&gt; &lt;segmentCount&gt;"
        /// or "ERROR &lt;message&gt;". Never shows UI.
        /// </summary>
        string RunQrSketchProbe();

        /// <summary>
        /// Runs the same command with line-3 insertion failure injected, so the
        /// real abort/cleanup path executes against the live host.
        /// Returns "FAILSAFE &lt;message&gt;" — the command must NOT succeed here.
        /// </summary>
        string RunFailingQrSketchProbe();

        /// <summary>
        /// Reports whether ConnectToSW wired the command surface up:
        /// "CONNECTED group=&lt;n&gt;" or "NOT-CONNECTED".
        /// </summary>
        string AddinStateProbe();
    }
}
