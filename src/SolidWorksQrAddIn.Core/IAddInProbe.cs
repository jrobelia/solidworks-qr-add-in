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
        /// size=&lt;text&gt; growth=&lt;text&gt; preset=&lt;id&gt; ecc=&lt;level&gt;
        /// invert=&lt;bool&gt; ringsplit=&lt;bool&gt; shape=&lt;code&gt;
        /// param=&lt;text&gt;
        /// unit=&lt;sym&gt; cell=&lt;text&gt; ..." —
        /// size/growth are the field texts exactly as shown (initially 25
        /// and 3); preset is the content-type id (initially "raw");
        /// shape is the cell-shape code (initially "square") and param the
        /// shape-parameter field text exactly as shown (initially "90" —
        /// Circle's dot size); ringsplit is the open-nested-rings option
        /// (initially false).
        /// </summary>
        string WindowStateProbe();

        /// <summary>
        /// Writes one session field by name exactly like the pane's
        /// controls — the same vocabulary <see cref="WindowStateProbe"/>
        /// echoes (<see cref="QrProbeFields"/>): "content", "size",
        /// "growth", "ecc", "invert", "ringsplit", "shape", "param" and
        /// "preset" (the content-type id), matched case-insensitively; any
        /// other name is a preset-field key written verbatim. Values are
        /// field text: the literal text for text fields, the option Value
        /// for choices, "true"/"false" for toggles and Boolean preset
        /// fields. Returns "VALID cells=&lt;n&gt; contours=&lt;c&gt;
        /// segs=&lt;s&gt; size=&lt;finalMm&gt;" — under Circle
        /// "dots=&lt;n&gt;" replaces the contour/segment counts — or
        /// "INVALID &lt;kind&gt; &lt;message&gt;" for the revalidated
        /// request, and a harness-level "ERROR ..." for input that can
        /// never reach the session (an unknown ECC level, cell shape or
        /// toggle value, or a missing field name).
        /// </summary>
        string SetQrField(string name, string value);

        /// <summary>
        /// The Create Sketch button, probed: builds a fresh host adapter and
        /// runs the session create. Returns "OK &lt;sketchName&gt; …" or
        /// "ERROR &lt;message&gt;".
        /// </summary>
        string CreateQrFromWindowProbe();

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
        /// Fingerprint of the session's current picture:
        /// "PIC &lt;w&gt;x&lt;h&gt; dark=&lt;n&gt; sha=&lt;hash&gt;" or "NO-PICTURE"
        /// when the current content is invalid. Lets the harness prove the
        /// shown picture equals the module's rendering of the same payload.
        /// </summary>
        string PreviewSignatureProbe();

        /// <summary>
        /// Pattern/entity census for the ticket-#42 circle live gates
        /// (spec #13): reads a sketch's segment census — entity type tallies
        /// (arc/line/other via the segment type code, robust against the
        /// sibling-COM-type instances docs/PATTERN-SPIKE.md PAT0 reports),
        /// construction-flag count, closed-region count — plus the feature
        /// type string, and echoes the circle plan the session's current
        /// result carries (emitted/skipped instance counts, pattern cells,
        /// pitch, dot diameter, invert) so the harness can compare the
        /// emitted-vs-skipped expectation with what the sketch actually
        /// holds. Pass the sketch name a create returned; an empty or null
        /// name censuses the sketch open in edit mode. Returns
        /// "PATCENSUS ..." or "ERROR ..." — a square-payload session echoes
        /// "plan=none".
        /// </summary>
        string PatternCensusProbe(string sketchName);

        /// <summary>
        /// Dimension + equation surface for the ticket-#42 circle live
        /// gates (spec #13, ticket #40): resolves every "D{n}@&lt;sketch&gt;"
        /// parameter — mm value and driven-state code each — in creation
        /// order (the QR-square master dim is the FIRST dim the emitter
        /// creates, so the smallest D-number), then lists the document's
        /// equations verbatim. Pass the sketch name a create returned; an
        /// empty or null name inspects the sketch open in edit mode.
        /// Returns "PATDIMS sketch='&lt;name&gt;' dims=&lt;n&gt;
        /// D1=&lt;mm&gt;mm(s&lt;state&gt;) ... eqs=&lt;n&gt; '&lt;eq&gt;' ...
        /// mode=&lt;circle|square&gt;" or "ERROR ...".
        /// </summary>
        string PatternDimsProbe(string sketchName);

        /// <summary>
        /// Drives one driving dimension by name — the "D{n}@{sketch}"
        /// equation-reference form or a full name; resolved through
        /// IParameter — via SetSystemValue3(valueM, this-config) +
        /// EditRebuild3, then echoes the value SolidWorks actually stored.
        /// The live gate resizes the master dim and re-censuses the
        /// pattern. Returns "PATSET '&lt;name&gt;'=&lt;mm&gt;mm rc=&lt;n&gt;"
        /// or "ERROR ...".
        /// </summary>
        string SetPatternDimProbe(string dimensionName, double valueM);

        // ---------------- part-property source (ticket-#44 probes) ----------------

        /// <summary>
        /// Enumerates the resolvable property names of the active part and
        /// its active configuration through the add-in's real
        /// part-property source — the names a "$PRP:"name"" reference could
        /// resolve to, configuration-store names first, each listed once.
        /// Returns "SWPROPS kind=part doc='&lt;title&gt;' config='&lt;cfg&gt;'
        /// names=&lt;n&gt; '&lt;name&gt;' ..." or "SWPROPS kind=&lt;kind&gt;
        /// names=0" when the active document is not a part.
        /// </summary>
        string SwPropertiesProbe();

        /// <summary>
        /// Resolves one property reference by name against the active part's
        /// active configuration — the verdict is always named, never a
        /// silent empty string:
        /// "SWPROP '&lt;name&gt;' RESOLVED '&lt;value&gt;'" / "EMPTY" /
        /// "MISSING" / "NODOC".
        /// </summary>
        string SwPropertyResolveProbe(string name);
    }
}
