using System;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorksQrAddIn.Host;
using SolidWorksQrAddIn.UI;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn
{
    /// <summary>
    /// The <see cref="IAddInProbe"/> surface of <see cref="SwAddin"/> — the
    /// COM test hook LiveProbe drives. Every probe answers with a string: a
    /// probe call can never throw back across the COM boundary into
    /// SolidWorks, and every entry point gates on <see cref="ReadySession"/>
    /// so "not connected" means exactly one thing.
    ///
    /// Probe-driven session edits (SetQr*Probe, RunQrSketchProbe) go through
    /// the same session setters the window uses — the session notifies the
    /// open window itself, so the shown fields and picture can never diverge
    /// from what a probe observes.
    /// </summary>
    public partial class SwAddin
    {
        /// <summary>Payload the fixed-path probes use (was the ticket-2 fixed content).</summary>
        private const string ProbeContent = "SW-QR-MVP";

        /// <summary>The probe-facing not-connected answer — one string, one source.</summary>
        private const string NotConnectedError = "ERROR " + NotConnectedMessage;

        /// <summary>The live session while connected — the single gate every probe shares.</summary>
        private QrSketchSession? ReadySession =>
            _connected && _sw != null ? _session : null;

        public string RunQrSketchProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            session.SetContent(ProbeContent);
            return CreateQrFromWindowProbe();
        }

        public string RunFailingQrSketchProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            session.SetContent(ProbeContent);
            // Wrap the real native host so line 3 of the insertion throws —
            // the command must run the native abort/cleanup path for real.
            var host = new FailureInjectingHost(new SolidWorksSketchHost(_sw!), failOnLine: 3);
            return DescribeCreate(session, host);
        }

        public string RunCleanupFailQrSketchProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            session.SetContent(ProbeContent);
            // Line 3 throws AND the first cleanup attempt reports failure —
            // the "partial sketch may remain" reporting runs for real. The
            // teardown path still makes the host's best-effort second attempt.
            var host = new FailureInjectingHost(
                new SolidWorksSketchHost(_sw!), failOnLine: 3, failCleanup: true);
            return DescribeCreate(session, host);
        }

        /// <summary>
        /// The probe/COM boundary: a create through a probe surface can never
        /// throw back into SolidWorks — the result string is the contract.
        /// </summary>
        private string DescribeCreate(QrSketchSession session, ISketchHost host)
        {
            try
            {
                var result = session.CreateSketch(host);
                return result.Succeeded
                    ? "UNEXPECTED-OK " + result.CreatedSketchName
                    : $"FAILSAFE {result.Message}";
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        public string OpenQrWindowProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            try
            {
                session.OpenOrFocusWindow();
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
            return $"OPEN open={session.HasOpenWindow} windows={session.WindowsCreated}";
        }

        public string CloseQrWindowProbe()
        {
            _session?.CloseWindow();
            return WindowStateProbe();
        }

        public string WindowStateProbe() =>
            _session == null
                ? "STATE session=none"
                : $"STATE open={_session.HasOpenWindow} windows={_session.WindowsCreated} " +
                  $"hasRequest={_session.HasValidRequest} " +
                  $"size={_session.SizeText} growth={_session.GrowthText}";

        /// <summary>
        /// The shared setter-probe shape: gate on the connected session,
        /// apply the field edit verbatim through the session setter (which
        /// also refreshes the shown window), describe the revalidated result.
        /// </summary>
        private string FieldProbe(Func<QrSketchSession, QrSketchResult> edit)
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            return Describe(edit(session));
        }

        public string SetQrContentProbe(string content) =>
            FieldProbe(s => s.SetContent(content));

        public string SetQrSizeProbe(string sizeText) =>
            FieldProbe(s => s.SetSizeText(sizeText));

        public string SetQrGrowthProbe(string growthText) =>
            FieldProbe(s => s.SetGrowthText(growthText));

        private static string Describe(QrSketchResult result)
        {
            if (!result.Succeeded)
                return $"INVALID {result.Error!.Kind} {result.Error.Message}";
            var info = result.Sketch!.Info;
            return $"VALID cells={info.CellCount} contours={info.ContourCount} " +
                   $"segs={info.SegmentCount} size={info.FinalSizeMm:F3}";
        }

        public string CreateQrFromWindowProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            // A probe answer is a string — a create failure or a host
            // exception surfaces as "ERROR …", never as an exception that
            // escapes across the COM boundary into SolidWorks.
            try
            {
                var result = session.CreateSketch(new SolidWorksSketchHost(_sw!));
                return result.Succeeded
                    ? $"OK {result.CreatedSketchName} {result.Message}"
                    : $"ERROR {result.Message}";
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        public string PreviewSignatureProbe()
        {
            var pic = _session?.Picture;
            return pic == null
                ? "NO-PICTURE"
                : $"PIC {pic.WidthPixels}x{pic.HeightPixels} dark={pic.DarkPixelCount} sha={pic.Signature()}";
        }

        public string AddinStateProbe() =>
            _connected ? $"CONNECTED group={AddinIdentity.CommandGroupUserId}" : "NOT-CONNECTED";

        // ---------------- undo-recording diagnostic (verification only) ----------------

        /// <summary>
        /// Arrangement matrix for the named-undo investigation. Every
        /// arrangement draws one two-segment sketch on a FRESH part document
        /// it creates and closes itself, then presses EditUndo2 and reports
        /// whether that removed the sketch.
        ///   -1 ping (thread context report, no document)
        ///    0 control: no recording, interactive-mode line
        ///    1 start BEFORE InsertSketch, AddToDB off, finish after exit
        ///    2 start BEFORE InsertSketch, AddToDB on,  finish after exit
        ///    3 start AFTER  InsertSketch, AddToDB off, finish after exit
        ///    4 start AFTER  InsertSketch, AddToDB on,  finish after exit (= product shape)
        ///    5 start after InsertSketch,  AddToDB on,  finish WHILE sketch open
        ///    6 = 4 but FinishRecordingUndoObject (v1)
        ///    7 = 4 but the whole arrangement runs on the SolidWorks STA UI
        ///        thread (via the task-pane control's Invoke) — the real
        ///        Create-button call context
        ///    8 start before InsertSketch, AddToDB on,  finish while open
        ///    9 empty recording: start + finish, no sketch at all
        ///   10 start after lines drawn (mid-sketch), AddToDB on, finish after exit
        ///   11 = 4 but FinishRecordingUndoObject2(name, MakeHidden=true)
        ///   12 = 1 but the whole arrangement runs on the SW UI thread
        ///   13 control on SW UI thread: no recording, interactive line
        ///   14 start before the SELECTION itself, AddToDB on, finish after exit
        /// </summary>
        public string UndoArrangementProbe(int arrangement)
        {
            if (!_connected || _sw == null)
                return NotConnectedError;
            if (arrangement == -1)
            {
                var t = System.Threading.Thread.CurrentThread;
                return $"PING thread='{t.Name}' id={t.ManagedThreadId} " +
                       $"apt={t.GetApartmentState()} swVisible={_sw.Visible}";
            }
            try
            {
                int inner = arrangement;
                if (arrangement == 7) inner = 4;
                else if (arrangement == 12) inner = 1;
                else if (arrangement == 13) inner = 0;
                if (arrangement is 7 or 12 or 13)
                {
                    // STA = SolidWorks' own UI thread: the task-pane control's
                    // handle was created there, so WinForms Invoke marshals
                    // onto it — the same context the pane's Create button uses.
                    var pane = _pane;
                    if (pane == null || !pane.IsHandleCreated)
                        return $"ARR{arrangement} no-ui-thread";
                    var staResult = pane.Invoke(
                        new Func<string>(() => RunUndoArrangement(inner))) as string;
                    return $"ARR{arrangement}(inner={inner}) {staResult}";
                }
                return $"ARR{arrangement} " + RunUndoArrangement(inner);
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Executes one arrangement against a fresh document. Reports the
        /// finish call's verdict and the actual undo behaviour. Runs on
        /// whatever thread the caller picked — that choice is itself one of
        /// the variables under test.
        /// </summary>
        private string RunUndoArrangement(int arrangement)
        {
            var sw = _sw!;
            string template = sw.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            if (string.IsNullOrEmpty(template))
                return "no-template";
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null) return "no-doc";
            try
            {
                var ext = (IModelDocExtension)doc.Extension;
                var mgr = (ISketchManager)doc.SketchManager;
                int featsBefore = CountSketchFeatures(doc);

                bool recBefore   = arrangement is 1 or 2 or 8 or 14;
                bool recAfter    = arrangement is 3 or 4 or 5 or 6 or 10 or 11;
                bool addToDb     = arrangement is 2 or 4 or 5 or 6 or 8 or 10 or 11 or 14;
                bool finishOpen  = arrangement is 5 or 8;        // finish before exiting edit
                bool v1          = arrangement == 6;
                bool hidden      = arrangement == 11;
                bool noRecording = arrangement == 0;
                bool emptyRec    = arrangement == 9;

                string finishResult = "not-called";
                string sketchName = "";

                void StartRec()
                {
                    try { ext.StartRecordingUndoObject(); }
                    catch (Exception ex) { finishResult = "startEX:" + ex.GetType().Name; }
                }
                void FinishRec()
                {
                    try
                    {
                        bool ok = v1
                            ? ext.FinishRecordingUndoObject("QRDiag")
                            : ext.FinishRecordingUndoObject2("QRDiag", hidden);
                        finishResult = ok ? "TRUE" : "false";
                    }
                    catch (Exception ex) { finishResult = "finishEX:" + ex.GetType().Name; }
                }

                if (emptyRec)
                {
                    StartRec();
                    FinishRec();
                }
                else
                {
                    if (arrangement == 14) StartRec();   // widest scope: wraps selection too
                    ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                    if (recBefore && arrangement != 14) StartRec();
                    mgr.InsertSketch(true);
                    if (mgr.ActiveSketch is IFeature f) sketchName = f.Name;
                    if (addToDb)
                    {
                        try { mgr.AddToDB = true; } catch { }
                        try { mgr.DisplayWhenAdded = false; } catch { }
                    }
                    if (recAfter && arrangement != 10) StartRec();
                    mgr.CreateLine(-0.005, -0.005, 0, 0.005, -0.005, 0);
                    mgr.CreateLine(0.005, -0.005, 0, 0.005, 0.005, 0);
                    if (arrangement == 10) StartRec();   // mid-sketch, after geometry
                    if (finishOpen) FinishRec();
                    try { if (addToDb) mgr.AddToDB = false; } catch { }
                    mgr.InsertSketch(false);             // exit edit mode
                    if (!finishOpen && !noRecording) FinishRec();
                }

                bool present = FindSketch(doc, sketchName) != null;
                // One undo press, then re-check — the observable contract.
                string undo = "skipped";
                if (present)
                {
                    try { doc.EditUndo2(1); } catch (Exception ex) { undo = "undoEX:" + ex.GetType().Name; }
                    bool gone = FindSketch(doc, sketchName) == null;
                    undo = $"undoRemoved={gone} featsDelta={CountSketchFeatures(doc) - featsBefore}";
                }
                else undo = $"sketch-missing featsDelta={CountSketchFeatures(doc) - featsBefore}";

                var t = System.Threading.Thread.CurrentThread;
                return $"finish={finishResult} sketch='{sketchName}' {undo} " +
                       $"thread={t.GetApartmentState()}#{t.ManagedThreadId}";
            }
            finally
            {
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
            }
        }

        private static int CountSketchFeatures(IModelDoc2 doc)
        {
            int n = 0;
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                if (string.Equals(f.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                    n++;
            return n;
        }

        private static IFeature? FindSketch(IModelDoc2 doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                if (string.Equals(f.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }
    }
}
