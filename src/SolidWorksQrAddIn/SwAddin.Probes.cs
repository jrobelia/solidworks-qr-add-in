using System;
using QRCoder;
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
                  $"size={_session.SizeText} growth={_session.GrowthText} " +
                  $"preset={_session.ContentPresetId} " +
                  $"ecc={_session.EccLevel} invert={_session.Invert} " +
                  $"unit={_session.LengthUnit.Symbol} cell={_session.CellSizeReadout} " +
                  (_pane?.InfoSnapshot ?? "view=none");

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

        /// <summary>
        /// The pane's error-correction selector, probed: parse the picked
        /// level and apply it through the session setter like every other
        /// field probe. An unrecognised level never reaches the session.
        /// </summary>
        public string SetQrEccProbe(string level)
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            QRCodeGenerator.ECCLevel ecc;
            switch ((level ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "L": ecc = QRCodeGenerator.ECCLevel.L; break;
                case "M": ecc = QRCodeGenerator.ECCLevel.M; break;
                case "Q": ecc = QRCodeGenerator.ECCLevel.Q; break;
                case "H": ecc = QRCodeGenerator.ECCLevel.H; break;
                default:
                    return $"ERROR unknown ECC level '{level}' — expected L, M, Q or H.";
            }
            return Describe(session.SetEccLevel(ecc));
        }

        public string SetQrInvertProbe(bool invert) =>
            FieldProbe(s => s.SetInvert(invert));

        public string SetQrContentTypeProbe(string presetId) =>
            FieldProbe(s => s.SetContentType(presetId));

        public string SetQrPresetFieldProbe(string key, string value) =>
            FieldProbe(s => s.SetPresetField(key, value));

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

        // ---------------- custom undo object (SetApiUndoObject) verification ----------------

        /// <summary>
        /// Live check for the custom undo object. On a throwaway part it
        /// draws a small USER sketch first (prior work that must survive),
        /// runs the real session create for a QR sketch, exits it, then
        /// presses EditUndo2 → EditRedo2 → EditUndo2 and reports what each
        /// did to the feature tree. Runs on the SolidWorks UI thread via the
        /// pane's Invoke — the real Create-button context.
        /// </summary>
        public string UndoRedoQrProbe()
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            try
            {
                var pane = _pane;
                if (pane != null && pane.IsHandleCreated)
                    return "APIUNDO " + (pane.Invoke(
                        new Func<string>(RunUndoRedoQrProbe)) as string);
                return "APIUNDO " + RunUndoRedoQrProbe();
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        private string RunUndoRedoQrProbe()
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

                // Prior work: a small user sketch on Front Plane.
                ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                mgr.InsertSketch(true);
                var userName = (mgr.ActiveSketch as IFeature)?.Name ?? "SketchUser";
                mgr.CreateLine(-0.01, 0.0, 0, 0.01, 0.0, 0);
                mgr.InsertSketch(false);

                // The real create path, in the window's call context.
                doc.ClearSelection2(true);
                ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                var session = ReadySession!;
                session.SetContent(ProbeContent);
                var result = session.CreateSketch(new SolidWorksSketchHost(sw));
                if (!result.Succeeded)
                    return $"createFailed: {result.Message}";
                string qrName = result.CreatedSketchName ?? "";
                try { mgr.InsertSketch(false); } catch { }   // engineer exits the sketch

                bool Present(string name) => FindSketch(doc, name) != null;
                // Any sketch that is not the user's = the QR sketch (by
                // whatever name its current incarnation carries).
                bool HasQrSketch()
                {
                    for (var f = doc.FirstFeature() as IFeature; f != null;
                         f = f.GetNextFeature() as IFeature)
                        if (string.Equals(f.GetTypeName2(), "ProfileFeature",
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(f.Name, userName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    return false;
                }

                int n0 = CountSketchFeatures(doc);
                var trace = new System.Text.StringBuilder();
                // The QR creation leaves the sketch open; exiting it pushes
                // edit-session steps ABOVE our API undo step. Press undo until
                // the QR sketch is gone (bounded) — mimics a user holding
                // Ctrl+Z; each press is reported so the stack shape is visible.
                int undoPresses = 0;
                try
                {
                    while (undoPresses < 6 && HasQrSketch())
                    {
                        doc.EditUndo2(1);
                        undoPresses++;
                        trace.Append(
                            $" u{undoPresses}:qr=T user={Present(userName)} " +
                            $"n={CountSketchFeatures(doc)} edit={(mgr.ActiveSketch != null)} |");
                        // An undo that re-enters sketch edit mode only
                        // restores the pre-exit state — the user exits the
                        // reopened sketch before pressing undo again.
                        if (mgr.ActiveSketch != null)
                            try { mgr.InsertSketch(false); } catch { }
                    }
                }
                catch (Exception ex) { trace.Append($" undoEX:{ex.GetType().Name} |"); }
                bool removed = !HasQrSketch();

                // Native redo of an API undo item: verified dead — SW never
                // calls the handler. Two presses prove it stays that way.
                int redoPresses = 0;
                try
                {
                    while (redoPresses < 2 && !HasQrSketch())
                    {
                        doc.EditRedo2(1);
                        redoPresses++;
                        trace.Append(
                            $" r{redoPresses}:qr={HasQrSketch()} " +
                            $"n={CountSketchFeatures(doc)} edit={(mgr.ActiveSketch != null)} |");
                    }
                }
                catch (Exception ex) { trace.Append($" redoEX:{ex.GetType().Name} |"); }

                // The restore path this API offers: the handler re-registered
                // itself, so another undo press re-fires DoUndo and the
                // toggle reinserts the sketch.
                int restorePresses = 0;
                try
                {
                    while (restorePresses < 4 && !HasQrSketch())
                    {
                        doc.EditUndo2(1);
                        restorePresses++;
                        trace.Append(
                            $" x{restorePresses}:qr={HasQrSketch()} user={Present(userName)} " +
                            $"n={CountSketchFeatures(doc)} edit={(mgr.ActiveSketch != null)} |");
                        if (mgr.ActiveSketch != null)
                            try { mgr.InsertSketch(false); } catch { }
                    }
                }
                catch (Exception ex) { trace.Append($" restoreEX:{ex.GetType().Name} |"); }

                return $"user='{userName}' qr='{qrName}' n0={n0} undoPresses={undoPresses} " +
                       $"removed={removed} nativeRedoPresses={redoPresses} " +
                       $"restored={HasQrSketch()} restorePresses={restorePresses} " +
                       $"userIntact={Present(userName)} |{trace}";
            }
            finally
            {
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
            }
        }

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

        // ---------------- sketch-block-instance spike (verification only) ----------------

        /// <summary>
        /// The block-instance spike. Runs every sub-test on the SolidWorks UI
        /// thread via the pane's Invoke (block APIs may need the STA context)
        /// against a fresh throwaway document each time.
        ///   -1 ping
        ///    1 block-instance geometry as an extrude/cut profile (kill test)
        ///    2 driving-dim rescale + direct instance Scale/Angle
        ///    3 relations host-sketch ↔ block-instance geometry
        ///    4 MakeSketchBlockFromSelected / InsertSketchBlockInstance sequence
        ///    5 AddDimension2 inside the definition travelling into the block
        /// </summary>
        public string BlockSpikeProbe(int test)
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            if (test == -1)
            {
                var t = System.Threading.Thread.CurrentThread;
                return $"PING thread='{t.Name}' id={t.ManagedThreadId} " +
                       $"apt={t.GetApartmentState()} swVisible={_sw.Visible}";
            }
            try
            {
                var pane = _pane;
                if (pane != null && pane.IsHandleCreated)
                    return $"BLK{test} " + (pane.Invoke(
                        new Func<string>(() => RunBlockSpike(test))) as string);
                return $"BLK{test} " + RunBlockSpike(test);
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        private string RunBlockSpike(int test)
        {
            switch (test)
            {
                case 1: return WithSpikeDoc(SpikeExtrudeFromBlock);
                case 2: return WithSpikeDoc(SpikeDimRescale);
                case 3: return WithSpikeDoc(SpikeRelationsToBlock);
                case 4: return WithSpikeDoc(SpikeBlockCreation);
                case 5: return WithSpikeDoc(SpikeDimInsideBlock);
                default: return $"unknown-test={test}";
            }
        }

        /// <summary>Fresh throwaway part per test; closed in a finally.</summary>
        private string WithSpikeDoc(Func<IModelDoc2, string> body)
        {
            var sw = _sw!;
            string template = sw.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            if (string.IsNullOrEmpty(template))
                return "no-template";
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null) return "no-doc";
            try { return body(doc); }
            catch (Exception ex) { return $"EX {ex.GetType().Name}: {ex.Message}"; }
            finally { try { sw.CloseDoc(doc.GetTitle()); } catch { } }
        }

        /// <summary>GetSketchSegments without a dynamic conversion (no binder in this project).</summary>
        private static object[]? SpikeSegments(ISketch sketch)
        {
            object? raw = null;
            try { raw = sketch.GetSketchSegments(); } catch { }
            return raw as object[];
        }

        private static bool OpenFrontSketch(IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr)
        {
            doc.ClearSelection2(true);
            if (!ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
                return false;
            mgr.InsertSketch(true);
            return mgr.ActiveSketch != null;
        }

        /// <summary>Draws an s×s axis-aligned square with a corner at the origin.</summary>
        private static void DrawSquare(ISketchManager mgr, double s)
        {
            bool prevInf = mgr.AutoInference;
            try
            {
                mgr.AutoInference = false;
                mgr.CreateLine(0, 0, 0, s, 0, 0);
                mgr.CreateLine(s, 0, 0, s, s, 0);
                mgr.CreateLine(s, s, 0, 0, s, 0);
                mgr.CreateLine(0, s, 0, 0, 0, 0);
            }
            finally { mgr.AutoInference = prevInf; }
        }

        private static MathPoint SpikePt(IMathUtility mu, double x, double y)
        {
            object raw = mu.CreatePoint(new[] { x, y, 0.0 });
            return (MathPoint)raw;
        }

        /// <summary>
        /// Adds a driving dimension to the square's bottom edge while the
        /// sketch is active: IAddDimension2 (aligned) first, then
        /// IAddHorizontalDimension2. Appends the attempt to the log.
        /// </summary>
        private static IDisplayDimension? TryDimBottomEdge(
            IModelDoc2 doc, IModelDocExtension ext, double s, System.Text.StringBuilder sb)
        {
            doc.ClearSelection2(true);
            bool selLine = ext.SelectByID2("", "SKETCHSEGMENT", s / 2, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            sb.Append($"selLine={selLine} ");
            IDisplayDimension? dd = null;
            try { dd = doc.IAddDimension2(s / 2, -0.004, 0) as IDisplayDimension; }
            catch (Exception ex) { sb.Append($"addDim2EX:{ex.GetType().Name} "); }
            if (dd == null)
            {
                try { dd = doc.IAddHorizontalDimension2(s / 2, -0.004, 0); }
                catch (Exception ex) { sb.Append($"addHDimEX:{ex.GetType().Name} "); }
            }
            sb.Append($"dim={(dd == null ? "null" : "ok")} ");
            return dd;
        }

        /// <summary>
        /// Selects everything in the active sketch (plus the dimension object
        /// when one was created — a block only carries dims it is handed) and
        /// calls MakeSketchBlockFromSelected.
        /// </summary>
        private static SketchBlockDefinition? MakeBlockDef(
            IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr, IMathUtility mu,
            IDisplayDimension? dd, System.Text.StringBuilder sb)
        {
            doc.ClearSelection2(true);
            ext.SelectAll();
            var selMgr = doc.ISelectionManager as ISelectionMgr;
            int sel = selMgr?.GetSelectedObjectCount() ?? -1;
            if (dd != null)
            {
                var dim = dd.GetDimension2(0) as IDimension;
                if (dim != null)
                {
                    bool selDim = ext.SelectByID2(dim.FullName, "DIMENSION", 0, 0, 0,
                        true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                    sb.Append($"selDim={selDim}({dim.FullName}) ");
                }
            }
            var def = mgr.MakeSketchBlockFromSelected(SpikePt(mu, 0, 0));
            sb.Append($"selCount={sel} def={(def == null ? "null" : "ok")} ");
            return def;
        }

        /// <summary>
        /// Host-sketch feature bbox (model space, mm) via IFeature.GetBox —
        /// reflects the effective geometry including instance scale.
        /// </summary>
        private static string SketchBBox(IModelDoc2 doc)
        {
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                if (!string.Equals(f.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                    continue;
                object? box = null;
                try { f.GetBox(box); } catch { }
                if (box is double[] b && b.Length >= 6)
                    return $"w={(b[3] - b[0]) * 1000:F3} h={(b[4] - b[1]) * 1000:F3}";
                return "box=null";
            }
            return "noSketchFeat";
        }

        /// <summary>
        /// Point counts visible through the sketch while a block edit is open
        /// (GetSketchPointsCount vs Count2) plus the bbox of whatever points
        /// are exposed — evidence for which space block-edit coordinates use.
        /// </summary>
        private static string MeasureBlock(ISketchManager mgr, ISketchBlockInstance inst)
        {
            bool sel;
            try { sel = inst.Select(true, null); }
            catch (Exception ex) { return "selEX:" + ex.GetType().Name; }
            if (!sel) return "sel=false";
            try { mgr.EditSketchBlock(); }
            catch (Exception ex) { return "editEX:" + ex.GetType().Name; }
            var sk = mgr.ActiveSketch as ISketch;
            string result;
            if (sk == null)
            {
                result = "noActiveSketch";
            }
            else
            {
                int n1 = -1, n2 = -1;
                try { n1 = sk.GetSketchPointsCount(); } catch { }
                try { n2 = sk.GetSketchPointsCount2(); } catch { }
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                int used = 0;
                // Block-edit exposes the definition's points through the v1
                // API only (verified live: Count=8 vs Count2=0).
                object? pts = null;
                string ptsInfo = "threw";
                try
                {
                    pts = sk.GetSketchPoints();
                    ptsInfo = pts == null ? "null" : pts.GetType().Name;
                    if (pts is System.Array a) ptsInfo += $"[{a.Length}]";
                }
                catch (Exception ex) { ptsInfo = "ex:" + ex.GetType().Name; }
                if (pts is object[] arr)
                    foreach (var po in arr)
                    {
                        if (!(po is ISketchPoint p)) continue;
                        used++;
                        minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                        minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    }
                result = $"pts1={n1} pts2={n2} pts={ptsInfo} used={used}" +
                    (used == 0 ? "" : $" w={(maxX - minX) * 1000:F3} h={(maxY - minY) * 1000:F3} " +
                      $"x0={minX * 1000:F3} y0={minY * 1000:F3}");
            }
            try { mgr.EndEditSketchBlock(true); } catch { }
            return result;
        }

        /// <summary>Q1 — can a region inside a block instance feed FeatureExtrusion3 / FeatureCut4?</summary>
        private string SpikeExtrudeFromBlock(IModelDoc2 doc)
        {
            var sw = _sw!;
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var mu = (IMathUtility)sw.IGetMathUtility();
            var fm = (IFeatureManager)doc.FeatureManager;
            var sb = new System.Text.StringBuilder();

            if (!OpenFrontSketch(doc, ext, mgr)) return "openSketch=fail";
            DrawSquare(mgr, 0.01);
            var def = MakeBlockDef(doc, ext, mgr, mu, null, sb);
            if (def == null) return "dead: " + sb;
            var hostSketch = mgr.ActiveSketch as ISketch;
            sb.Append($"instInSketch={hostSketch?.GetSketchBlockInstanceCount() ?? -1} ");
            mgr.InsertSketch(false);   // exit sketch edit

            doc.ClearSelection2(true);
            string sketchName = "";
            ISketchBlockInstance? inst = null;
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                if (!string.Equals(f.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                    continue;
                sketchName = f.Name;
                hostSketch = f.GetSpecificFeature2() as ISketch;
                if (hostSketch != null && hostSketch.GetSketchBlockInstanceCount() > 0)
                    inst = hostSketch.IGetSketchBlockInstances(0) as ISketchBlockInstance;
                break;
            }
            sb.Append($"host='{sketchName}' instName='{inst?.Name}' ");

            IFeature? boss = null;
            if (ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
            {
                try
                {
                    boss = fm.FeatureExtrusion3(true, false, false, 0, 0, 0.002, 0.002,
                        false, false, false, false, 0, 0, false, false, false, false,
                        true, true, true, 0, 0, false) as IFeature;
                }
                catch (Exception ex) { sb.Append($"extrudeA_EX:{ex.GetType().Name} "); }
            }
            sb.Append($"extrudeSketchSel={(boss == null ? "null" : "'" + boss.Name + "'")} ");

            // Route B: the block instance object itself selected.
            if (boss == null && inst != null)
            {
                doc.ClearSelection2(true);
                bool selB = inst.Select(true, null);
                var sm = doc.ISelectionManager as ISelectionMgr;
                int selType = sm != null && sm.GetSelectedObjectCount() > 0
                    ? sm.GetSelectedObjectType2(1) : -999;
                sb.Append($"selInst={selB} selType={selType} ");
                try
                {
                    boss = fm.FeatureExtrusion3(true, false, false, 0, 0, 0.002, 0.002,
                        false, false, false, false, 0, 0, false, false, false, false,
                        true, true, true, 0, 0, false) as IFeature;
                }
                catch (Exception ex) { sb.Append($"extrudeB_EX:{ex.GetType().Name} "); }
                sb.Append($"extrudeInstSel={(boss == null ? "null" : "'" + boss.Name + "'")} ");
            }

            // Route C: a region inside the block, contour-selection style.
            if (boss == null)
            {
                doc.ClearSelection2(true);
                var sm = doc.ISelectionManager as ISelectionMgr;
                if (sm != null) sm.EnableContourSelection = true;
                bool selC = ext.SelectByID2("", "SKETCHREGION", 0.005, 0.005, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                int selTypeC = sm != null && sm.GetSelectedObjectCount() > 0
                    ? sm.GetSelectedObjectType2(1) : -999;
                sb.Append($"selRegion={selC} type={selTypeC} ");
                try
                {
                    boss = fm.FeatureExtrusion3(true, false, false, 0, 0, 0.002, 0.002,
                        false, false, false, false, 0, 0, false, false, false, false,
                        true, true, true, 0, 0, false) as IFeature;
                }
                catch (Exception ex) { sb.Append($"extrudeC_EX:{ex.GetType().Name} "); }
                sb.Append($"extrudeRegionSel={(boss == null ? "null" : "'" + boss.Name + "'")} ");
                if (sm != null) sm.EnableContourSelection = false;
            }

            // Control: explode the instance into normal segments, then the
            // same selection+extrude MUST work — isolates "block instance"
            // from "geometry is somehow broken".
            if (boss == null && inst != null)
            {
                doc.ClearSelection2(true);
                if (ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault))
                {
                    mgr.InsertSketch(true);
                    try { mgr.ExplodeSketchBlockInstance((SketchBlockInstance)inst); }
                    catch (Exception ex) { sb.Append($"explodeEX:{ex.GetType().Name} "); }
                    mgr.InsertSketch(false);
                    doc.ClearSelection2(true);
                    ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                    try
                    {
                        boss = fm.FeatureExtrusion3(true, false, false, 0, 0, 0.002, 0.002,
                            false, false, false, false, 0, 0, false, false, false, false,
                            true, true, true, 0, 0, false) as IFeature;
                    }
                    catch (Exception ex) { sb.Append($"extrudeD_EX:{ex.GetType().Name} "); }
                    sb.Append($"extrudeExploded={(boss == null ? "null" : "'" + boss.Name + "'")} ");
                }
            }

            // Cut route: only if the boss produced a body — same region, through it.
            if (boss != null)
            {
                doc.ClearSelection2(true);
                ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                IFeature? cut = null;
                try
                {
                    doc.FeatureCut4(true, false, false, 0, 0, 0.005, 0.005,
                        false, false, false, false, 0, 0, false, false, 0, true);
                    for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                        if (f.GetTypeName2().IndexOf("cut", StringComparison.OrdinalIgnoreCase) >= 0)
                        { cut = f; break; }
                }
                catch (Exception ex) { sb.Append($"cut_EX:{ex.GetType().Name} "); }
                sb.Append($"cut={(cut == null ? "none" : "'" + cut.Name + "'")} ");
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Measures effective block geometry by inserting a throwaway
        /// instance at (x,y), exploding it into normal sketch entities and
        /// reading the segment bbox — the only reliable live observable of
        /// block contents found (GetSketchPoints is null in block edit).
        /// </summary>
        private static string ExplodeMeasure(
            IModelDoc2 doc, ISketchManager mgr, IMathUtility mu,
            SketchBlockDefinition def, double x, double y)
        {
            try
            {
                var inst = mgr.InsertSketchBlockInstance(def, SpikePt(mu, x, y), 1.0, 1.0)
                    as ISketchBlockInstance;
                if (inst == null) return "insert=null";
                mgr.ExplodeSketchBlockInstance((SketchBlockInstance)inst);
                var sketch = mgr.ActiveSketch as ISketch;
                if (sketch == null) return "noSketch";
                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                int n = 0;
                if (SpikeSegments(sketch) is object[] segs)
                    foreach (var so in segs)
                    {
                        if (!(so is ISketchLine l)) continue;
                        foreach (var p in new[] { l.GetStartPoint2() as ISketchPoint,
                                                  l.GetEndPoint2() as ISketchPoint })
                        {
                            if (p == null) continue;
                            n++;
                            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                        }
                    }
                if (n == 0) return "segs=0";
                // bbox includes every exploded instance so far — report the
                // window around the insertion point instead.
                double w = 0;
                foreach (var so in SpikeSegments(sketch) ?? System.Array.Empty<object>())
                {
                    if (!(so is ISketchLine l)) continue;
                    var p1 = l.GetStartPoint2() as ISketchPoint;
                    var p2 = l.GetEndPoint2() as ISketchPoint;
                    if (p1 == null || p2 == null) continue;
                    double len = Math.Sqrt((p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y));
                    if (Math.Abs(p1.X - x) < 0.1 && Math.Abs(p1.Y - y) < 0.1 ||
                        Math.Abs(p2.X - x) < 0.1 && Math.Abs(p2.Y - y) < 0.1)
                        w = Math.Max(w, len);
                }
                return $"segs={n} edgeNearIns={w * 1000:F2}mm";
            }
            catch (Exception ex) { return "EX:" + ex.GetType().Name; }
        }

        /// <summary>ExplodeMeasure variant honouring the (scaleX, scaleY) insert args.</summary>
        private static string ExplodeMeasureArgs(
            IModelDoc2 doc, ISketchManager mgr, IMathUtility mu,
            SketchBlockDefinition def, double x, double y, double sx, double sy)
        {
            try
            {
                var inst = mgr.InsertSketchBlockInstance(def, SpikePt(mu, x, y), sx, sy)
                    as ISketchBlockInstance;
                if (inst == null) return "insert=null";
                mgr.ExplodeSketchBlockInstance((SketchBlockInstance)inst);
                var sketch = mgr.ActiveSketch as ISketch;
                if (sketch == null) return "noSketch";
                double maxX = 0, maxY = 0; int n = 0;
                if (SpikeSegments(sketch) is object[] segs)
                    foreach (var so in segs)
                    {
                        if (!(so is ISketchLine l)) continue;
                        var p1 = l.GetStartPoint2() as ISketchPoint;
                        var p2 = l.GetEndPoint2() as ISketchPoint;
                        if (p1 == null || p2 == null) continue;
                        n++;
                        double dx = Math.Abs(p2.X - p1.X), dy = Math.Abs(p2.Y - p1.Y);
                        // only edges near this insertion point count
                        if ((Math.Abs(p1.X - x) < 0.15 && Math.Abs(p1.Y - y) < 0.15) ||
                            (Math.Abs(p2.X - x) < 0.15 && Math.Abs(p2.Y - y) < 0.15))
                        {
                            maxX = Math.Max(maxX, dx); maxY = Math.Max(maxY, dy);
                        }
                    }
                return $"segs={n} edgeX={maxX * 1000:F2} edgeY={maxY * 1000:F2}";
            }
            catch (Exception ex) { return "EX:" + ex.GetType().Name; }
        }

        /// <summary>
        /// Q2 — two clean phases; every measurement is "insert instance →
        /// explode → longest segment", the only live observable that worked.
        /// Phase A (scale): def without a dim; explode an instance at scale 1
        /// (baseline 10 mm), set instance Scale=2 / Angle, then explode a
        /// FRESH instance to see the effect, and one inserted at (2,3) scale
        /// args to disambiguate the (scaleX, scaleY) parameter order.
        /// Phase B (driving dim): def with a driving dim; set
        /// SetSystemValue3(30 mm) FIRST, then insert+explode — a 30 mm edge
        /// proves the def's dim drives geometry; 10 mm proves it does not.
        /// </summary>
        private string SpikeDimRescale(IModelDoc2 doc)
        {
            var sw = _sw!;
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var mu = (IMathUtility)sw.IGetMathUtility();
            var sb = new System.Text.StringBuilder();

            // ---------- Phase A: instance Scale/Angle ----------
            sb.Append("A[");
            if (!OpenFrontSketch(doc, ext, mgr)) return "openSketch=fail";
            DrawSquare(mgr, 0.01);
            var defA = MakeBlockDef(doc, ext, mgr, mu, null, sb);
            if (defA == null) return "deadA: " + sb;
            var sketch = mgr.ActiveSketch as ISketch;
            var instA = sketch?.IGetSketchBlockInstances(0) as ISketchBlockInstance;
            sb.Append($"e1[{ExplodeMeasure(doc, mgr, mu, defA, 0.05, 0)}] ");
            if (instA != null)
            {
                try
                {
                    instA.Scale = 2.0;
                    sb.Append($"scale→{instA.Scale:F2}/{instA.Scale2:F2} ");
                    instA.Angle = 0.5;
                    sb.Append($"angle→{instA.Angle:F2} ");
                }
                catch (Exception ex) { sb.Append($"propEX:{ex.GetType().Name} "); }
            }
            // scale-2 fresh instance via the (scaleX, scaleY) args — if the
            // 2nd/3rd doubles are X and Y scale, exploding gives 20 mm edges.
            sb.Append($"e2arg[{ExplodeMeasureArgs(doc, mgr, mu, defA, 0.10, 0, 2.0, 3.0)}] ");
            try { instA!.DimensionDisplay = true; sb.Append($"dimDisp→{instA.DimensionDisplay} "); }
            catch (Exception ex) { sb.Append($"dimDispEX:{ex.GetType().Name} "); }
            sb.Append("] ");

            // ---------- Phase B: driving dim inside the def ----------
            sb.Append("B[");
            // Fresh sketch on the same doc keeps phases independent.
            mgr.InsertSketch(false);
            doc.ClearSelection2(true);
            if (!ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
                return sb.ToString() + " plane2=fail";
            mgr.InsertSketch(true);
            DrawSquare(mgr, 0.01);
            var dd = TryDimBottomEdge(doc, ext, 0.01, sb);
            var def = MakeBlockDef(doc, ext, mgr, mu, dd, sb);
            if (def == null) return sb.ToString() + " deadB";
            var defI = (ISketchBlockDefinition)def;
            sb.Append($"defDims={defI.GetDisplayDimensionCount()} ");

            // Drive the dim BEFORE any measurement: 10 mm → 30 mm.
            IDisplayDimension? ddi = null;
            object? ddarr = null;
            try { ddarr = defI.GetDisplayDimensions(); } catch { }
            if (ddarr is object[] ddobjs)
                foreach (var d in ddobjs)
                    if (d is IDisplayDimension x) { ddi = x; break; }
            IDimension? dim = null;
            object? dobj = null;
            try { if (ddi != null) dobj = ddi.GetDimension(); } catch { }
            dim = (dobj as IDimension) ?? (ddi?.GetDimension2(0) as IDimension);
            if (dim == null)
            {
                for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                    if (dim == null && f.EnumDisplayDimensions() != null)
                        if (f.GetFirstDisplayDimension() is IDisplayDimension dd2i)
                            dim = dd2i.GetDimension2(0) as IDimension;
            }
            sb.Append($"eBefore[{ExplodeMeasure(doc, mgr, mu, def, 0.15, 0)}] ");
            if (dim != null)
            {
                sb.Append($"dim='{dim.FullName}' v0={dim.SystemValue * 1000:F2} ");
                int r = -99;
                try { r = dim.SetSystemValue3(0.03, 2, null); }
                catch (Exception ex) { sb.Append($"setEX:{ex.GetType().Name} "); }
                sb.Append($"set={r} v={(dim.SystemValue * 1000):F2} ");
                try { doc.EditRebuild3(); } catch { }
            }
            else sb.Append("dim=unreachable ");
            // The decisive observation: fresh instance + explode AFTER the
            // dim drive — 30 mm proves the dim drives the def's geometry.
            // EditRebuild3 exits sketch edit mode (verified live: the first
            // post-dim insert returned null) — re-enter first.
            if (mgr.ActiveSketch == null)
            {
                doc.ClearSelection2(true);
                for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                    if (string.Equals(f.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase) &&
                        (f.GetSpecificFeature2() as ISketch)?.GetSketchBlockInstanceCount() > 0)
                    {
                        ext.SelectByID2(f.Name, "SKETCH", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault);
                        break;
                    }
                mgr.InsertSketch(true);
                sb.Append($"reenter={(mgr.ActiveSketch != null)} ");
            }
            sb.Append($"eAfter[{ExplodeMeasure(doc, mgr, mu, def, 0.05, 0)}] ");
            sb.Append("]");
            return sb.ToString().Trim();
        }
        /// <summary>Q3 — SketchAddConstraints from host entities to block geometry.</summary>
        private string SpikeRelationsToBlock(IModelDoc2 doc)
        {
            var sw = _sw!;
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var mu = (IMathUtility)sw.IGetMathUtility();
            var sb = new System.Text.StringBuilder();

            if (!OpenFrontSketch(doc, ext, mgr)) return "openSketch=fail";
            DrawSquare(mgr, 0.01);
            var def = MakeBlockDef(doc, ext, mgr, mu, null, sb);
            if (def == null) return "dead: " + sb;
            var sketch = mgr.ActiveSketch as ISketch;
            if (sketch == null) return "no-sketch " + sb;

            bool prevInf = mgr.AutoInference;
            try { mgr.AutoInference = false; mgr.CreateLine(0.05, 0.05, 0, 0.06, 0.05, 0); }
            finally { mgr.AutoInference = prevInf; }

            int relBefore = -1;
            try { relBefore = sketch.RelationManager.GetRelationsCount(-1); }
            catch (Exception ex) { sb.Append($"relCntEX:{ex.GetType().Name} "); }

            // A: host endpoint ↔ block corner point (origin).
            doc.ClearSelection2(true);
            bool s1 = ext.SelectByID2("", "SKETCHPOINT", 0.05, 0.05, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            bool s2 = ext.SelectByID2("", "SKETCHPOINT", 0.0, 0.0, 0, true, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            sb.Append($"selHostPt={s1} selBlockPt={s2} ");
            if (s1 && s2)
            {
                try { doc.SketchAddConstraints("sgCOINCIDENT"); }
                catch (Exception ex) { sb.Append($"coincidentEX:{ex.GetType().Name} "); }
            }
            bool hostAtOrigin = false;
            var ptList = new System.Text.StringBuilder();
            {
                int n = sketch.GetSketchPointsCount2();
                ptList.Append($"n={n}:");
                for (var i = 0; i < n && i < 12; i++)
                {
                    var p = sketch.IGetSketchPoints2(i);
                    if (p == null) continue;
                    ptList.Append($"({p.X * 1000:F1},{p.Y * 1000:F1})");
                    if (Math.Abs(p.X) < 1e-9 && Math.Abs(p.Y) < 1e-9) hostAtOrigin = true;
                }
            }
            // Point-selection probe: if the coincident actually applied, the
            // host endpoint left (0.05,0.05) and sits at the block corner.
            doc.ClearSelection2(true);
            bool stillThere = ext.SelectByID2("", "SKETCHPOINT", 0.05, 0.05, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            doc.ClearSelection2(true);
            bool cornerHasPt = ext.SelectByID2("", "SKETCHPOINT", 0.0, 0.0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            sb.Append($"hostPtAtOrigin={hostAtOrigin} pts[{ptList}] " +
                $"ptStill050={stillThere} ptAtCorner={cornerHasPt} ");
            // Rigorous check: the host line's own endpoint must sit at the
            // block corner — a block point existing at (0,0) is not proof.
            bool hostLineMoved = false; string lineInfo = "noHostLine";
            try
            {
                if (SpikeSegments(sketch) is object[] segs)
                    foreach (var so in segs)
                    {
                        if (!(so is ISketchLine l)) continue;
                        var a = l.GetStartPoint2() as ISketchPoint;
                        var b = l.GetEndPoint2() as ISketchPoint;
                        if (a == null || b == null) continue;
                        // the host line is the non-axis-aligned one (was
                        // horizontal at y=0.05; if it moved it went diagonal)
                        bool isHost = (Math.Abs(a.Y - b.Y) > 1e-9) ||
                            (Math.Abs(a.X - 0.05) < 1e-6 && Math.Abs(a.Y - 0.05) < 1e-6) ||
                            (Math.Abs(b.X - 0.05) < 1e-6 && Math.Abs(b.Y - 0.05) < 1e-6) ||
                            (Math.Abs(a.X - 0.06) < 1e-6) || (Math.Abs(b.X - 0.06) < 1e-6);
                        if (!isHost) continue;
                        lineInfo = $"({a.X * 1000:F1},{a.Y * 1000:F1})->({b.X * 1000:F1},{b.Y * 1000:F1})";
                        if (Math.Abs(a.X) < 1e-9 && Math.Abs(a.Y) < 1e-9) hostLineMoved = true;
                        if (Math.Abs(b.X) < 1e-9 && Math.Abs(b.Y) < 1e-9) hostLineMoved = true;
                    }
            }
            catch (Exception ex) { lineInfo = "ex:" + ex.GetType().Name; }
            sb.Append($"hostLine={lineInfo} movedToCorner={hostLineMoved} ");

            // B: host line ↔ block bottom edge, coincident/colinear.
            doc.ClearSelection2(true);
            // The coincident moved the host line (start now at origin) —
            // pick a point on its new span; fall back to the original.
            bool s3 = ext.SelectByID2("", "SKETCHSEGMENT", 0.03, 0.025, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault)
                || ext.SelectByID2("", "SKETCHSEGMENT", 0.055, 0.05, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
            bool s4 = ext.SelectByID2("", "SKETCHSEGMENT", 0.005, 0.0, 0, true, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            sb.Append($"selHostSeg={s3} selBlockSeg={s4} ");
            if (s3 && s4)
            {
                try { doc.SketchAddConstraints("sgCOLINEAR"); }
                catch (Exception ex) { sb.Append($"colinearEX:{ex.GetType().Name} "); }
            }
            int relAfter = -1;
            try { relAfter = sketch.RelationManager.GetRelationsCount(-1); }
            catch { }
            sb.Append($"rels {relBefore}→{relAfter} ");

            // C: dimension between a host entity and block geometry.
            doc.ClearSelection2(true);
            bool s5 = ext.SelectByID2("", "SKETCHSEGMENT", 0.03, 0.025, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault)
                || ext.SelectByID2("", "SKETCHSEGMENT", 0.055, 0.05, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
            bool s6 = ext.SelectByID2("", "SKETCHSEGMENT", 0.005, 0.0, 0, true, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            IDisplayDimension? dd = null;
            if (s5 && s6)
            {
                try { dd = doc.IAddDimension2(0.03, 0.02, 0) as IDisplayDimension; }
                catch (Exception ex) { sb.Append($"dimEX:{ex.GetType().Name} "); }
            }
            sb.Append($"hostBlockDim={(dd == null ? "null" : "ok")} ");
            return sb.ToString().Trim();
        }

        /// <summary>Q4 — the creation API sequence itself.</summary>
        private string SpikeBlockCreation(IModelDoc2 doc)
        {
            var sw = _sw!;
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var mu = (IMathUtility)sw.IGetMathUtility();
            var sb = new System.Text.StringBuilder();

            if (!OpenFrontSketch(doc, ext, mgr)) return "openSketch=fail";
            DrawSquare(mgr, 0.01);
            int defsBefore = mgr.GetSketchBlockDefinitionCount();
            var def = MakeBlockDef(doc, ext, mgr, mu, null, sb);
            if (def == null) return "dead: " + sb;
            sb.Append($"defs {defsBefore}→{mgr.GetSketchBlockDefinitionCount()} ");

            var sketch = mgr.ActiveSketch as ISketch;
            sb.Append($"instInSketch={sketch?.GetSketchBlockInstanceCount() ?? -1} ");

            var inst2 = mgr.InsertSketchBlockInstance(
                def, SpikePt(mu, 0.03, 0.03), 1.0, 1.0) as ISketchBlockInstance;
            sb.Append($"insert2={(inst2 == null ? "null" : "ok")} ");
            if (inst2 != null)
            {
                var pos = inst2.InstancePosition?.ArrayData as double[];
                sb.Append($"inst2 name='{inst2.Name}' pos=({(pos != null ? $"{pos[0] * 1000:F1},{pos[1] * 1000:F1}" : "?")}) " +
                    $"scale={inst2.Scale:F2} scale2={inst2.Scale2:F2} angle={inst2.Angle:F2} ");
                sb.Append($"instInSketch={sketch?.GetSketchBlockInstanceCount() ?? -1} ");
                var xform = inst2.BlockToSketchTransform;
                sb.Append($"xform={(xform == null ? "null" : "ok")} ");
            }
            try { sb.Append($"defInstCount={((ISketchBlockDefinition)def).GetInstanceCount()} "); }
            catch (Exception ex) { sb.Append($"defInstEX:{ex.GetType().Name} "); }
            return sb.ToString().Trim();
        }

        /// <summary>Q5 — does an AddDimension2 inside the selection travel into the block?</summary>
        private string SpikeDimInsideBlock(IModelDoc2 doc)
        {
            var sw = _sw!;
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var mu = (IMathUtility)sw.IGetMathUtility();
            var sb = new System.Text.StringBuilder();

            if (!OpenFrontSketch(doc, ext, mgr)) return "openSketch=fail";
            DrawSquare(mgr, 0.01);
            var dd = TryDimBottomEdge(doc, ext, 0.01, sb);
            if (dd != null)
            {
                var dim = dd.GetDimension2(0) as IDimension;
                if (dim != null)
                    sb.Append($"dimName='{dim.FullName}' selName='{dd.GetNameForSelection()}' v={dim.SystemValue * 1000:F2}mm ");
            }
            var def = MakeBlockDef(doc, ext, mgr, mu, dd, sb);
            if (def == null) return "dead: " + sb;
            var defI5 = (ISketchBlockDefinition)def;
            sb.Append($"defDims={defI5.GetDisplayDimensionCount()} ");
            if (defI5.GetDisplayDimensionCount() > 0)
            {
                var d0 = defI5.IGetDisplayDimensions(0) as IDisplayDimension;
                var d0dim = d0?.GetDimension2(0) as IDimension;
                sb.Append($"defDim0='{d0dim?.FullName}' v={d0dim?.SystemValue * 1000:F2}mm ");
            }
            return sb.ToString().Trim();
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
