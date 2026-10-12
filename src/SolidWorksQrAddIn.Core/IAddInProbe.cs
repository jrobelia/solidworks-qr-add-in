using System.Runtime.InteropServices;
using SolidWorksQrAddIn.Core.Registration;

namespace SolidWorksQrAddIn.Core
{
    /// <summary>
    /// COM-visible test hook exposed by the add-in object. A live harness gets
    /// it via ISldWorks.GetAddInObject("{clsid}") and invokes the SAME code
    /// path the SolidWorks menu command uses — the session + window manager
    /// behind the real WPF window. Lives in Core (not the add-in) so the
    /// harness only needs this assembly.
    /// </summary>
    [ComVisible(true)]
    [Guid("4813B19B-E14E-4201-B961-4C7AC791DDD8")] // == AddinIdentity.ProbeIid
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IAddInProbe
    {
        /// <summary>
        /// Drives the windowed create path with the fixed probe payload
        /// ("SW-QR-MVP") against the live host: sets the session content and
        /// invokes the same Create path the window's button uses.
        /// Returns "OK &lt;sketchName&gt; &lt;message&gt;" or "ERROR &lt;message&gt;".
        /// Never shows UI beyond the session's window handling.
        /// </summary>
        string RunQrSketchProbe();

        /// <summary>
        /// Runs the same create path with line-3 insertion failure injected,
        /// so the real abort/cleanup path executes against the live host.
        /// Returns "FAILSAFE &lt;message&gt;" — the command must NOT succeed here.
        /// </summary>
        string RunFailingQrSketchProbe();

        /// <summary>
        /// Runs the same create path with insertion failure AND a refused
        /// cleanup injected, so the honest "the partial sketch may remain"
        /// reporting executes against the live host. Returns
        /// "FAILSAFE &lt;message&gt;" — the command must NOT succeed here.
        /// </summary>
        string RunCleanupFailQrSketchProbe();

        /// <summary>
        /// Reports whether ConnectToSW wired the command surface up:
        /// "CONNECTED group=&lt;n&gt;" or "NOT-CONNECTED".
        /// </summary>
        string AddinStateProbe();

        // ---------------- ticket-3 window/session probes ----------------

        /// <summary>
        /// The command entry, probed: focus the live window or create + show
        /// it. Returns "OPEN open=&lt;bool&gt; windows=&lt;n&gt;" or "ERROR …".
        /// windows counts creations — a second open must keep it at 1.
        /// </summary>
        string OpenQrWindowProbe();

        /// <summary>Closes the session's window. Returns the resulting WindowStateProbe text.</summary>
        string CloseQrWindowProbe();

        /// <summary>
        /// Window/session state without changing it:
        /// "STATE open=&lt;bool&gt; windows=&lt;n&gt; hasRequest=&lt;bool&gt;
        /// size=&lt;text&gt; growth=&lt;text&gt; preset=&lt;id&gt; unit=&lt;sym&gt;
        /// cell=&lt;text&gt; ..." — size/growth are the field texts exactly
        /// as shown (initially 25 and 3); preset is the content-type id
        /// (initially "raw").
        /// </summary>
        string WindowStateProbe();

        /// <summary>
        /// Pushes content into the session exactly like the window's text box:
        /// "VALID cells=&lt;n&gt; contours=&lt;c&gt; segs=&lt;s&gt;" or
        /// "INVALID &lt;kind&gt; &lt;message&gt;".
        /// </summary>
        string SetQrContentProbe(string content);

        /// <summary>
        /// Pushes size text into the session exactly like the window's size
        /// box: "VALID cells=&lt;n&gt; contours=&lt;c&gt; segs=&lt;s&gt;
        /// size=&lt;finalMm&gt;" or "INVALID &lt;kind&gt; &lt;message&gt;".
        /// </summary>
        string SetQrSizeProbe(string sizeText);

        /// <summary>
        /// Pushes growth text into the session exactly like the window's
        /// growth box: "VALID cells=&lt;n&gt; contours=&lt;c&gt; segs=&lt;s&gt;
        /// size=&lt;finalMm&gt;" or "INVALID &lt;kind&gt; &lt;message&gt;".
        /// </summary>
        string SetQrGrowthProbe(string growthText);

        /// <summary>
        /// Picks the content-type preset exactly like the pane's dropdown:
        /// "VALID cells=&lt;n&gt; contours=&lt;c&gt; segs=&lt;s&gt;
        /// size=&lt;finalMm&gt;" or "INVALID &lt;kind&gt; &lt;message&gt;" —
        /// an unknown id or a preset whose required fields are missing
        /// reports "INVALID InvalidPayload ...".
        /// </summary>
        string SetQrContentTypeProbe(string presetId);

        /// <summary>
        /// Writes one preset field exactly like the pane's preset inputs —
        /// the literal text for text kinds, the option Value for Choice,
        /// "true"/"false" for Boolean. The composed payload lands in the
        /// content box verbatim: "VALID ..." or
        /// "INVALID InvalidPayload &lt;message naming the bad field&gt;".
        /// </summary>
        string SetQrPresetFieldProbe(string key, string value);

        /// <summary>
        /// The Create Sketch button, probed: builds a fresh host adapter and
        /// runs the session create. Returns "OK &lt;sketchName&gt; …" or
        /// "ERROR &lt;message&gt;".
        /// </summary>
        string CreateQrFromWindowProbe();

        /// <summary>
        /// Undo-recording diagnostic (verification-only): creates a fresh
        /// throwaway part document, creates one small sketch under the
        /// requested arrangement of StartRecordingUndoObject /
        /// FinishRecordingUndoObject(2) / InsertSketch / AddToDB, then
        /// presses EditUndo2 and reports whether the sketch was removed.
        /// Returns a single-line "ARR …" result string. -1 is a ping that
        /// reports the executing thread — no document is touched.
        /// </summary>
        string UndoArrangementProbe(int arrangement);

        /// <summary>
        /// Custom-undo verification (SetApiUndoObject): creates a throwaway
        /// part document, draws a small USER sketch first (prior work that
        /// must survive), runs the real session create for the QR sketch,
        /// then EditUndo2(1) → EditRedo2(1) → EditUndo2(1) and reports what
        /// each step did. Returns one line:
        /// "APIUNDO undoRemoved=&lt;bool&gt; userIntact=&lt;bool&gt;
        ///  redoRestored=&lt;bool&gt; undoneAgain=&lt;bool&gt; ..." plus the
        /// sketch names involved. The document is closed afterwards.
        /// </summary>
        string UndoRedoQrProbe();

        /// <summary>
        /// Sketch-block-instance spike (verification-only): each test number
        /// creates a fresh throwaway part document on the SolidWorks UI
        /// thread, runs one block-API experiment, closes the document, and
        /// returns a one-line "BLK&lt;n&gt; ..." result. -1 is a ping (no
        /// document touched).
        ///   1 block-instance geometry as an extrude/cut profile
        ///   2 driving-dimension rescale + direct instance Scale/Angle
        ///   3 relations from host-sketch entities to block-instance geometry
        ///   4 the MakeSketchBlockFromSelected / InsertSketchBlockInstance
        ///     creation sequence
        ///   5 AddDimension2 inside the definition travelling into the block
        /// </summary>
        string BlockSpikeProbe(int test);

        /// <summary>
        /// Fingerprint of the session's current picture:
        /// "PIC &lt;w&gt;x&lt;h&gt; dark=&lt;n&gt; sha=&lt;hash&gt;" or "NO-PICTURE"
        /// when the current content is invalid. Lets the harness prove the
        /// shown picture equals the module's rendering of the same payload.
        /// </summary>
        string PreviewSignatureProbe();
    }
}
