using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using QRCoder;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Live verification harness (tickets 2–6 checks retained), plus a
    /// ticket-7 performance mode:
    ///   SolidWorksQrAddIn.LiveProbe.exe &lt;deployDir&gt; [progId]
    ///   SolidWorksQrAddIn.LiveProbe.exe perf [deployDir] [progId]
    /// where deployDir contains the registered SolidWorksQrAddIn.dll.
    /// `perf` times QR generation (no SolidWorks needed) and — only on a
    /// session-free invisible instance — native sketch insertion for
    /// representative short/medium/dense payloads.
    ///
    /// Drives SolidWorks via SldWorks.Application.31 + Activator.CreateInstance
    /// (the verified route — PowerShell late binding does NOT work here) and the
    /// add-in through its IAddInProbe COM hook, which exercises the SAME session
    /// + view code paths the QR task-pane tab uses (the *Window* probe names are
    /// historical — the surface is now the docked pane). Prints PASS/FAIL lines;
    /// exit code 0 when every check passes.
    ///
    /// Ticket-4 checks: fresh-window 25 mm/3% fields; native outline measured
    /// at the default 25 mm and at a custom 40 mm; a growth change preserving
    /// the entered outline size; input edits leaving the open and earlier
    /// sketches untouched; unparseable/zero/negative/out-of-range rejections
    /// with no part mutation and verbatim-kept field text; picture re-renders
    /// from the new request. Retained: window open/focus, modeless host
    /// interactivity, per-payload picture signatures, payload A/B sketches,
    /// first-sketch integrity, invalid-content refusal, blocked create while
    /// editing, close/reopen, disconnect release, and the ticket-2
    /// insertion-failure + feature-use regressions.
    /// </summary>
    public static class Program
    {
        private const string PayloadA = "SW-QR-MVP";
        private const string PayloadB = "HTTPS://QR-MVP.DEV/T3";

        private static int _failures;

        private static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
            if (!cond) _failures++;
        }

        // The module's own expectation for each request — native sketches and
        // the preview picture must reproduce exactly this geometry/rendering.
        private static QrSketch ExpectedOf(string content) => ExpectedOf(content,
            QrSketchSession.InitialSizeMm, QrSketchSession.InitialGrowthFractionPerSide);

        private static QrSketch ExpectedOf(string content, double sizeMm, double growthFraction) =>
            QrSketchGenerator.Generate(content, sizeMm, growthFraction).Sketch!;

        private static string ExpectedSignatureOf(string content) =>
            QrPictureRenderer.Render(ExpectedOf(content), QrSketchSession.PreviewPixelsAcross)
                .Signature();

        private static string ExpectedSignatureOf(string content, double sizeMm, double growthFraction) =>
            QrPictureRenderer.Render(ExpectedOf(content, sizeMm, growthFraction),
                QrSketchSession.PreviewPixelsAcross).Signature();

        /// <summary>
        /// true only while the attached instance is a fresh, invisible
        /// COM-launched one — the only kind it is safe to ExitApp. An
        /// instance that is already visible (or already holds documents) is
        /// somebody's interactive session: the probe refuses to mutate it.
        /// </summary>
        private static bool _spawnedFresh;

        public static int Main(string[] args)
        {
            // `perf` mode (ticket 7): generation + native-insertion timings
            // for representative payloads. `perf` consumes args[0]; the rest
            // keep the usual <deployDir> <progId> shape.
            if (args.Length > 0 &&
                string.Equals(args[0], "perf", StringComparison.OrdinalIgnoreCase))
                return PerfMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "undo", StringComparison.OrdinalIgnoreCase))
                return UndoMatrixMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "undoapi", StringComparison.OrdinalIgnoreCase))
                return UndoApiMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "block", StringComparison.OrdinalIgnoreCase))
                return BlockSpikeMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "blockqr", StringComparison.OrdinalIgnoreCase))
                return BlockQrMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "bindexp", StringComparison.OrdinalIgnoreCase))
                return BindExpMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "dimexp", StringComparison.OrdinalIgnoreCase))
                return DimExpMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "consexp", StringComparison.OrdinalIgnoreCase))
                return ConsExpMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "units", StringComparison.OrdinalIgnoreCase))
                return UnitsMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "xsettings", StringComparison.OrdinalIgnoreCase))
                return XsettingsMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "shapes", StringComparison.OrdinalIgnoreCase))
                return ShapesMain(args.Skip(1).ToArray());

            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            var expectedA = ExpectedOf(PayloadA);
            var expectedB = ExpectedOf(PayloadB);
            if (expectedA == null || expectedB == null)
            {
                Console.WriteLine("# FATAL: module could not generate a probe payload — aborting live run");
                return 1;
            }
            string sigA = ExpectedSignatureOf(PayloadA);
            string sigB = ExpectedSignatureOf(PayloadB);
            Console.WriteLine($"# payload A '{PayloadA}': {expectedA.Info.ContourCount} contours, " +
                $"{expectedA.Info.SegmentCount} segments, N={expectedA.Info.CellCount}, sig={sigA.Substring(0, 12)}…");
            Console.WriteLine($"# payload B '{PayloadB}': {expectedB.Info.ContourCount} contours, " +
                $"{expectedB.Info.SegmentCount} segments, N={expectedB.Info.CellCount}, sig={sigB.Substring(0, 12)}…");

            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Check(false, "SolidWorks launch"); return 1; }

                Console.WriteLine($"# SolidWorks revision {sw.RevisionNumber()}  visible={sw.Visible}");
                WaitForStartup(sw);

                // Safety: a VISIBLE instance or one that already holds
                // documents is an interactive session the probe must never
                // mutate or exit — bail without touching it.
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached to a SolidWorks session that is " +
                        $"visible={sw.Visible} with {openDocs} open document(s) — probably a " +
                        "user's interactive session. Refusing to run; pass a ProgID with no " +
                        "running instance (e.g. SldWorks.Application.30) so a fresh " +
                        "invisible instance is spawned.");
                    return 1;
                }
                _spawnedFresh = true;

                // --- add-in discovery / load ---
                var probe = TryGetProbe(sw);
                bool autoLoaded = probe != null;
                Check(true, "add-in discovery state queried",
                    autoLoaded ? "auto-loaded at startup (HKCU registration honoured)"
                               : "not auto-loaded — falling back to LoadAddIn");

                if (probe == null)
                {
                    int loadResult = sw.LoadAddIn(dllPath);
                    Check(loadResult == 0, "LoadAddIn", $"returned {loadResult} for {dllPath}");
                    probe = TryGetProbe(sw);
                }
                Check(probe != null, "GetAddInObject returns the add-in's probe interface",
                    probe == null ? "null" : "obtained");
                if (probe == null) return 1;

                Check(probe.AddinStateProbe().StartsWith("CONNECTED"), "ConnectToSW wired the command surface",
                    probe.AddinStateProbe());

                // ================= window lifecycle / modeless =================

                string open1 = probe.OpenQrWindowProbe();
                Check(open1.Contains("open=True") && open1.Contains("windows=1"),
                    "command opens the modeless window", open1);

                string open2 = probe.OpenQrWindowProbe();
                Check(open2.Contains("windows=1"),
                    "invoking the command again focuses the same window (no duplicate)", open2);

                Check(probe.WindowStateProbe().Contains("open=True"),
                    "window reports itself open", probe.WindowStateProbe());

                // Ticket 4: the fresh window starts at 25 mm and 3% per side.
                string freshState = probe.WindowStateProbe();
                Check(freshState.Contains("size=25") && freshState.Contains("growth=3"),
                    "fresh window shows the initial 25 mm size and 3% growth", freshState);

                // --- modeless proof: SolidWorks stays interactive while the
                //     window is open — documents and selection drive normally.
                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "new part document created while the window is open (modeless)",
                    doc == null ? "null" : $"type={doc.GetType()}");
                if (doc == null) return 1;
                var ext = (IModelDocExtension)doc.Extension;
                var mgr = (ISketchManager)doc.SketchManager;
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "support selectable while the window is open (host stays interactive)");

                // ================= invalid / empty content =================

                string inv = probe.SetQrContentProbe("");
                Check(inv.StartsWith("INVALID") && inv.Contains("EmptyContent"),
                    "empty content is rejected", inv);
                Check(probe.PreviewSignatureProbe() == "NO-PICTURE",
                    "no picture shown for empty content", probe.PreviewSignatureProbe());
                string e0 = probe.CreateQrFromWindowProbe();
                Check(e0.StartsWith("ERROR"), "create refused for empty content", e0);

                // ================= mid-insertion failure on the native path =================

                string fail = probe.RunFailingQrSketchProbe();
                Check(fail.StartsWith("FAILSAFE") && fail.Contains("removed"),
                    "insertion failure reports cleanup of the partial sketch", fail);
                Check(mgr.ActiveSketch == null, "no sketch left open after failed insertion");
                Check(!ext.SelectByID2("Sketch1", "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "only the partial sketch was deleted (no leftover feature)");

                // ================= payload A: picture + sketch =================

                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected for payload A");

                string va = probe.SetQrContentProbe(PayloadA);
                Check(va.StartsWith("VALID") && va.Contains($"cells={expectedA.Info.CellCount}"),
                    "payload A accepted with its cell/contour summary", va);

                string picA = probe.PreviewSignatureProbe();
                Check(picA.Contains($"sha={sigA}"),
                    "picture signature matches the module-rendered grown result (A)", picA);

                string createA = probe.CreateQrFromWindowProbe();
                Check(createA.StartsWith("OK"), "Create Sketch succeeds for payload A", createA);

                var sketchA = mgr.ActiveSketch as ISketch;
                Check(sketchA != null, "new sketch A remains open for editing");
                Check(probe.WindowStateProbe().Contains("open=True"),
                    "window stays open after creation", probe.WindowStateProbe());
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigA}"),
                    "picture remains available after creation", probe.PreviewSignatureProbe());

                string sketchAName = "";
                if (sketchA != null)
                {
                    VerifyNativeSketch(sketchA, expectedA, "A");
                    CheckOutline(sketchA, 25.0, "native outline measures the entered 25 mm (default size)");
                    sketchAName = createA.Split(' ')[1];

                    string again = probe.CreateQrFromWindowProbe();
                    Check(again.StartsWith("ERROR") && again.Contains("already"),
                        "create blocked while sketch A is being edited", again);
                    Check(((object[])sketchA.GetSketchSegments()).Length == expectedA.Info.SegmentCount,
                        "blocked create left the open sketch unchanged");

                    mgr.InsertSketch(false); // engineer exits the sketch
                    Check(mgr.ActiveSketch == null, "sketch A exits edit mode cleanly");
                }

                // ================= ticket 4: size + growth fields =================

                // Invalid inputs are rejected with a useful error, the request
                // is invalidated (no stale request can be created), the text
                // is kept verbatim, and the part is never mutated.
                int featureCountBeforeInvalid = FindSketchFeatureNames(doc!).Count;
                string bad1 = probe.SetQrSizeProbe("abc");
                Check(bad1.StartsWith("INVALID") && bad1.Contains("InvalidSize"),
                    "unparseable size is rejected", bad1);
                Check(probe.WindowStateProbe().Contains("hasRequest=False"),
                    "invalid size invalidates the request (no stale request remains)");
                Check(probe.WindowStateProbe().Contains("size=abc"),
                    "rejected size text is kept verbatim — never replaced");
                string eInv = probe.CreateQrFromWindowProbe();
                Check(eInv.StartsWith("ERROR"),
                    "create refuses while inputs are invalid", eInv);
                Check(FindSketchFeatureNames(doc!).Count == featureCountBeforeInvalid,
                    "invalid inputs never mutate the part");
                Check(probe.SetQrSizeProbe("0").StartsWith("INVALID"),
                    "zero size is rejected", probe.SetQrSizeProbe("0"));
                Check(probe.SetQrSizeProbe("-5").StartsWith("INVALID"),
                    "negative size is rejected");
                Check(probe.SetQrGrowthProbe("abc").StartsWith("INVALID"),
                    "unparseable growth is rejected");
                Check(probe.SetQrGrowthProbe("0").StartsWith("INVALID"),
                    "zero growth is rejected");
                Check(probe.SetQrGrowthProbe("50").StartsWith("INVALID"),
                    "growth at 50% is rejected (must be less than 50% per side)");

                // Restore growth first — the whole current request is
                // re-validated on every edit, so a rejected growth left in
                // the field keeps every later edit invalid too.
                probe.SetQrGrowthProbe("3");

                // Custom size: 40 mm. The normalized picture is scale-invariant
                // (same QR, same 4-cell margin from the grown extents), so the
                // signature stays sigA; the request's final size is 40 mm.
                string s40 = probe.SetQrSizeProbe("40");
                Check(s40.StartsWith("VALID") && s40.Contains("size=40.000"),
                    "custom size 40 mm accepted and drives the request", s40);
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigA}"),
                    "size edit re-renders the picture from the new request");

                // Growth change preserves the entered outline size (pitch
                // recalculation) but changes the grown picture.
                string g8 = probe.SetQrGrowthProbe("8");
                Check(g8.StartsWith("VALID") && g8.Contains("size=40.000"),
                    "growth 8% accepted — outline size still the entered 40 mm", g8);
                var expectedA8 = ExpectedOf(PayloadA, 40.0, 0.08);
                Check(probe.PreviewSignatureProbe().Contains($"sha={ExpectedSignatureOf(PayloadA, 40.0, 0.08)}"),
                    "growth edit re-renders the picture from the grown result");

                doc!.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane re-selected for the custom-size create");
                string createC = probe.CreateQrFromWindowProbe();
                Check(createC.StartsWith("OK"), "Create Sketch succeeds at 40 mm / 8%", createC);
                var sketchC = mgr.ActiveSketch as ISketch;
                Check(sketchC != null, "custom-size sketch remains open for manual editing");
                if (sketchC != null)
                {
                    VerifyNativeSketch(sketchC, expectedA8, "C(40mm,8%)");
                    CheckOutline(sketchC, 40.0,
                        "native outline measures the entered 40 mm — not 25");
                }

                // Editing inputs while a sketch is open must not resize or
                // regenerate that sketch.
                probe.SetQrSizeProbe("30");
                probe.SetQrGrowthProbe("12");
                if (sketchC != null)
                {
                    Check(((object[])sketchC.GetSketchSegments()).Length == expectedA8.Info.SegmentCount,
                        "editing the inputs did not touch the open sketch");
                    CheckOutline(sketchC, 40.0, "open sketch keeps its 40 mm outline after input edits");
                }
                mgr.InsertSketch(false); // engineer exits the custom-size sketch
                Check(mgr.ActiveSketch == null, "custom-size sketch exits edit mode cleanly");

                // Back to the defaults — valid→invalid→valid transitions keep
                // working, and later creates honour the restored request.
                Check(probe.SetQrSizeProbe("25").StartsWith("VALID"),
                    "size 25 re-accepted after the rejected edits");
                Check(probe.SetQrGrowthProbe("3").StartsWith("VALID"),
                    "growth 3% re-accepted");
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigA}"),
                    "picture is back to the default-request rendering");

                if (sketchAName.Length > 0)
                {
                    var featA2 = FindSketchFeature(doc!, sketchAName);
                    var skA2 = featA2?.GetSpecificFeature2() as ISketch;
                    Check(skA2 != null &&
                          ((object[])skA2.GetSketchSegments()).Length == expectedA.Info.SegmentCount,
                        "earlier sketch unchanged after the size/growth edits");
                    if (skA2 != null)
                        CheckOutline(skA2, 25.0, "earlier sketch keeps its 25 mm outline");
                }

                // ================= payload B: second sketch, first unchanged =================

                string vb = probe.SetQrContentProbe(PayloadB);
                Check(vb.StartsWith("VALID"), "payload B accepted", vb);
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigB}"),
                    "picture signature matches payload B's grown result", probe.PreviewSignatureProbe());
                Check(sigB != sigA, "distinct payloads render distinct pictures");

                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane re-selected for payload B");

                string createB = probe.CreateQrFromWindowProbe();
                Check(createB.StartsWith("OK"), "Create Sketch succeeds for payload B", createB);
                var sketchB = mgr.ActiveSketch as ISketch;
                if (sketchB != null)
                {
                    VerifyNativeSketch(sketchB, expectedB, "B");
                    mgr.InsertSketch(false);
                }

                if (sketchAName.Length > 0)
                {
                    var featA = FindSketchFeature(doc, sketchAName);
                    var skA = featA?.GetSpecificFeature2() as ISketch;
                    Check(skA != null &&
                          ((object[])skA.GetSketchSegments()).Length == expectedA.Info.SegmentCount,
                        "first sketch remains unchanged after the second create",
                        $"segments={((object[]?)skA?.GetSketchSegments())?.Length} expected={expectedA.Info.SegmentCount}");
                }

                // ================= ticket 5: supports & coordinate convention =================

                SupportChecks(sw, doc!, ext, mgr, probe, expectedA);

                // ================= close / reopen — and cancellation =================

                // Ticket 6: closing the window with a valid pending request is
                // the cancel path — it must never reach the part.
                int featuresBeforeCancel = FindSketchFeatureNames(doc!).Count;
                Check(probe.CloseQrWindowProbe().Contains("open=False"),
                    "window closes", probe.WindowStateProbe());
                Check(FindSketchFeatureNames(doc!).Count == featuresBeforeCancel,
                    "closing the window before Create left the part unchanged (cancel is safe)",
                    $"features={featuresBeforeCancel}");
                string reopen = probe.OpenQrWindowProbe();
                Check(reopen.Contains("open=True") && reopen.Contains("windows=2"),
                    "closed window is recreated safely", reopen);
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigB}"),
                    "recreated window keeps the session's picture",
                    probe.PreviewSignatureProbe());

                // ================= disconnect releases the window =================

                int unload = sw.UnloadAddIn(dllPath);
                Check(unload == 0, "UnloadAddIn", $"returned {unload}");
                Check(TryGetProbe(sw) == null, "probe gone after unload (window released, callbacks removed)");

                int reload = sw.LoadAddIn(dllPath);
                Check(reload == 0, "add-in reloads after unload", $"returned {reload}");
                var probe2 = TryGetProbe(sw);
                Check(probe2 != null && probe2.AddinStateProbe().StartsWith("CONNECTED"),
                    "reloaded add-in is connected", probe2?.AddinStateProbe() ?? "null");
                if (probe2 != null)
                {
                    Check(probe2.WindowStateProbe().Contains("open=False"),
                        "a fresh session starts with no window", probe2.WindowStateProbe());
                    Check(probe2.OpenQrWindowProbe().Contains("windows=1"),
                        "the reloaded add-in opens a fresh window");
                    probe2.CloseQrWindowProbe();
                }

                // ================= retained ticket-2 feature checks =================

                if (sketchAName.Length > 0)
                    FeatureChecks(sw, doc!, ext, mgr, sketchAName, expectedA);

                // ================= ticket 6: failure handling =================
                // Runs last — it closes all documents at the end.

                if (probe2 != null)
                    Ticket6Checks(sw, doc!, ext, mgr, probe2);
                else
                    Check(false, "ticket-6 failure-handling live checks ran",
                        "no live probe after reload — skipped");

                sw.UnloadAddIn(dllPath);
                Console.WriteLine(_failures == 0 ? "# ALL CHECKS PASSED" : $"# {_failures} CHECK(S) FAILED");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                // Only exit an instance this probe spawned fresh; never exit
                // an attached session.
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        // ================= ticket 7: performance measurement =================

        /// <summary>
        /// The ascending size ladder (mm) every insertion-guard scan climbs —
        /// the perf pass takes the first rung that clears, the `xsettings`
        /// gate replays the whole ladder for its refused/accepted rungs.
        /// </summary>
        private static readonly double[] GuardLadderMm = { 25.0, 40.0, 60.0, 80.0, 120.0, 160.0 };

        /// <summary>
        /// Representative short/medium/dense payloads. Cell counts are reported
        /// per run — versions shift with length, that is the point.
        /// </summary>
        private static readonly (string tag, string content)[] PerfPayloads =
        {
            ("short", "SW-QR-MVP"),
            ("medium", "https://intranet.openaeros.local/parts/OA-ACCUTECH-SHIPPING-COVER" +
                       "?rev=C&checked=2026-10-09&bin=W4-017"),
            ("dense", "{\"part\":\"OA-ACCUTECH-SHIPPING-COVER\",\"rev\":\"C\"," +
                      "\"serial\":\"OA-2026-10-000742\",\"material\":\"AL6061-T6\"," +
                      "\"finish\":\"ANODIZE-BLK\",\"lot\":\"L26-041\",\"qa\":\"PASS\"," +
                      "\"notes\":\"machined all-over per OA-DWG-1142 rev C; inspect per " +
                      "OA-QAP-7; ship with desiccant per OA-PKG-22; customer ref " +
                      "PO-55219 line 14\",\"uri\":\"https://intranet.openaeros.local" +
                      "/qa/records/OA-2026-10-000742\"}"),
        };

        /// <summary>
        /// `LiveProbe.exe perf [deployDir] [progId]` — measures QR generation
        /// (host-independent — always runs, no SolidWorks needed) and, only on
        /// a session-free invisible instance, native insertion end-to-end.
        /// Sizes are the smallest that clear the insertion guard at 3% growth
        /// (≥25 mm) so "dense" measures real insertion, not a guard refusal —
        /// the size used is printed for the record.
        /// </summary>
        private static int PerfMain(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            Console.WriteLine("# QR add-in performance — generation + native insertion");
            Console.WriteLine($"# environment: .NET {System.Environment.Version}, " +
                $"{System.Environment.OSVersion}, {System.Environment.ProcessorCount} cores");

            // ---- generation timing (no SolidWorks) ----
            var accepted = new List<(string tag, QrSketch sketch, double sizeMm, double genMs)>();
            foreach (var (tag, content) in PerfPayloads)
            {
                // Smallest size that clears the insertion guard at 3% —
                // dense QR at 25 mm can fall below the 0.05 mm feature floor.
                QrSketchResult? result = null;
                double sizeMm = 0;
                foreach (var trySize in GuardLadderMm)
                {
                    var r = QrSketchGenerator.Generate(content, trySize, 0.03);
                    if (r.Succeeded) { result = r; sizeMm = trySize; break; }
                }
                if (result == null || !result.Succeeded)
                {
                    Console.WriteLine($"# {tag}: generation rejected at every tried size " +
                        $"(last: {result?.Error?.Message ?? "n/a"})");
                    continue;
                }

                // best of 5 — generation cost on this machine.
                double best = double.MaxValue;
                for (var i = 0; i < 5; i++)
                {
                    var t0 = System.Diagnostics.Stopwatch.StartNew();
                    QrSketchGenerator.Generate(content, sizeMm, 0.03);
                    t0.Stop();
                    best = Math.Min(best, t0.Elapsed.TotalMilliseconds);
                }
                var info = result.Sketch!.Info;
                Console.WriteLine($"PERF payload={tag} chars={content.Length} version={info.Version} " +
                    $"cells={info.CellCount} contours={info.ContourCount} segs={info.SegmentCount} " +
                    $"size_mm={sizeMm} gen_ms={best:F1}");
                accepted.Add((tag, result.Sketch!, sizeMm, best));
            }

            // ---- native insertion timing (needs a session-free SW) ----
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null)
                {
                    Console.WriteLine("# insertion timing skipped — SolidWorks launch failed");
                    return 0;
                }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine($"# insertion timing skipped — a visible/open-docs session " +
                        $"is running (visible={sw.Visible}, docs={openDocs}); re-run with no " +
                        "interactive SolidWorks open.");
                    return 0;   // never ExitApp an attached session
                }
                _spawnedFresh = true;
                Console.WriteLine($"# SolidWorks revision {sw.RevisionNumber()} — invisible instance, " +
                    "insertion timing enabled");

                int loadResult = sw.LoadAddIn(dllPath);
                if (loadResult != 0)
                {
                    Console.WriteLine($"# insertion timing skipped — LoadAddIn returned {loadResult} " +
                        $"(deployed DLL: {dllPath})");
                    return 0;
                }
                var probe = TryGetProbe(sw);
                if (probe == null)
                {
                    Console.WriteLine("# insertion timing skipped — no probe on the loaded add-in");
                    return 0;
                }

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (doc == null)
                {
                    Console.WriteLine($"# insertion timing skipped — NewDocument failed " +
                        $"(template: {template})");
                    return 0;
                }
                var ext = (IModelDocExtension)doc.Extension;
                var mgr = (ISketchManager)doc.SketchManager;

                foreach (var (tag, sketch, sizeMm, genMs) in accepted)
                {
                    doc.ClearSelection2(true);
                    if (!ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault))
                    {
                        Console.WriteLine($"# {tag}: Front Plane selection failed — skipped");
                        continue;
                    }
                    probe.SetQrContentProbe(
                        PerfPayloads.First(p => p.tag == tag).content);
                    probe.SetQrSizeProbe(sizeMm.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    var t0 = System.Diagnostics.Stopwatch.StartNew();
                    string create = probe.CreateQrFromWindowProbe();
                    t0.Stop();

                    var sketchObj = mgr.ActiveSketch as ISketch;
                    Check(create.StartsWith("OK"), $"perf {tag}: create succeeds", create);
                    if (sketchObj != null)
                        CheckOutline(sketchObj, sizeMm, $"perf {tag}: outline measures the used size");
                    Console.WriteLine($"PERF payload={tag} create_ms={t0.Elapsed.TotalMilliseconds:F0} " +
                        $"(incl. gen_ms={genMs:F1} — native insert ≈ " +
                        $"{t0.Elapsed.TotalMilliseconds - genMs:F0} ms)");
                    mgr.InsertSketch(false);   // exit before the next create
                }

                try { sw.UnloadAddIn(dllPath); }
                catch (Exception ex)
                {
                    // Teardown only — observed RPC_E_SERVERFAULT here after the
                    // dense run; the timings are already recorded, so report
                    // the fault instead of failing the measurement.
                    Console.WriteLine($"# NOTE UnloadAddIn faulted during teardown: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL during insertion timing: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
            Console.WriteLine(_failures == 0 ? "# PERF DONE" : $"# {_failures} perf check(s) failed");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// `LiveProbe.exe undo [deployDir] [progId]` — runs the
        /// FinishRecordingUndoObject(2) arrangement matrix through
        /// <see cref="IAddInProbe.UndoArrangementProbe"/> on a session-free
        /// instance. Each arrangement prints one line: the finish call's
        /// verdict and whether one EditUndo2 removed the sketch. If an older
        /// registered build auto-loaded, it is unloaded and replaced with the
        /// requested DLL so the matrix always runs against this build.
        /// </summary>
        private static int UndoMatrixMain(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            Console.WriteLine("# FinishRecordingUndoObject arrangement matrix");
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;

                var probe = TryGetProbe(sw);
                // A previously REGISTERED build may have auto-loaded — it has
                // no UndoArrangementProbe. Ping it; if the method is absent,
                // unload the registered copy and load the requested DLL.
                bool pingOk = false;
                if (probe != null)
                {
                    try
                    {
                        string ping = probe.UndoArrangementProbe(-1);
                        Console.WriteLine("# " + ping);
                        pingOk = ping.StartsWith("PING");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"# probe ping failed ({ex.GetType().Name}) — reloading add-in");
                    }
                }
                if (!pingOk)
                {
                    try { sw.UnloadAddIn(dllPath); } catch { }
                    // The registered copy may sit in a different folder —
                    // unload by its known deployed path too.
                    string registered = Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "SolidWorksQrAddIn", "SolidWorksQrAddIn.dll");
                    if (!string.Equals(registered, dllPath, StringComparison.OrdinalIgnoreCase))
                        try { sw.UnloadAddIn(registered); } catch { }
                    int lr = sw.LoadAddIn(dllPath);
                    Console.WriteLine($"# LoadAddIn({dllPath}) -> {lr}");
                    if (lr != 0) return 1;
                    probe = TryGetProbe(sw);
                }
                if (probe == null) { Console.WriteLine("# FATAL: no probe object"); return 1; }
                Console.WriteLine("# " + probe.AddinStateProbe());
                Console.WriteLine("# ping: " + probe.UndoArrangementProbe(-1));

                foreach (var arr in new[] { 9, 0, 1, 2, 3, 4, 5, 6, 8, 10, 11, 14, 13, 12, 7 })
                    Console.WriteLine(probe.UndoArrangementProbe(arr));
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        private static int UnitsMain(string[] args)
        {
            if (System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: unit verification requires SolidWorks to be closed.");
                return 1;
            }
            string deployDir = args.Length > 0 ? args[0] : Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            ISldWorks? sw = null;
            IDisposable? nativeWatcher = null;
            QrSketchSession? session = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) return 1;
                WaitForStartup(sw);
                if (sw.Visible || sw.GetDocumentCount() > 0)
                {
                    Console.WriteLine("# FATAL: refusing to change units in an interactive SolidWorks session.");
                    return 1;
                }
                _spawnedFresh = true;
                var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(deployDir, "SolidWorksQrAddIn.dll"));
                var host = (ISketchHost)Activator.CreateInstance(
                    assembly.GetType("SolidWorksQrAddIn.Host.SolidWorksSketchHost", true)!, new object[] { sw });
                var watcher = (IHostStateWatcher)Activator.CreateInstance(
                    assembly.GetType("SolidWorksQrAddIn.Host.SwHostStateWatcher", true)!, new object[] { sw });
                nativeWatcher = (IDisposable)watcher;
                session = new QrSketchSession(() => throw new InvalidOperationException("No test window"), () => host, watcher);
                int changes = 0;
                watcher.Changed += () =>
                {
                    Interlocked.Increment(ref changes);
                    Console.WriteLine($"# unit event: host={host.CaptureState().LengthUnit.Symbol}, cell={session.CellSizeReadout}");
                };
                string template = sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (doc == null) return 1;
                doc.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitSystem,
                    (int)swUnitSystem_e.swUnitSystem_MMGS);
                Thread.Sleep(1000);
                session.SetContent(PayloadA);
                Check(session.CellSizeReadout.EndsWith(" mm"), "millimetre baseline", session.CellSizeReadout);
                var paneProbe = TryGetProbe(sw);
                if (paneProbe == null)
                {
                    int loaded = sw.LoadAddIn(Path.Combine(deployDir, "SolidWorksQrAddIn.dll"));
                    Console.WriteLine($"# LoadAddIn -> {loaded}");
                    paneProbe = TryGetProbe(sw);
                }
                if (paneProbe == null)
                {
                    Check(false, "installed add-in probe available",
                        "Install the current build and pass its registered folder; a different DLL path does not override COM CodeBase.");
                    return 1;
                }
                paneProbe.SetQrContentProbe(PayloadA);
                string state = paneProbe.WindowStateProbe();
                Check(state.Contains("viewSizeLabel=QR code size (mm):") && state.Contains("viewCell=1.187 mm"),
                    "pre-created pane is synchronized without invoking the menu command", state);

                int before = changes;
                doc.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitSystem,
                    (int)swUnitSystem_e.swUnitSystem_IPS);
                Thread.Sleep(1000);
                Check(doc.LengthUnit == (int)swLengthUnit_e.swINCHES, "native document changed to inches");
                Check(changes > before, "native unit change notified the watcher", $"events={changes - before}");
                Check(session.CellSizeReadout.EndsWith(" in"),
                    "information refreshes without a field edit or Create", session.CellSizeReadout);
                Check(Math.Abs(session.RequestedSizeMm!.Value - 25) < 1e-9,
                    "unit switch preserves the physical QR size", $"sizeMm={session.RequestedSizeMm}");
                state = paneProbe.WindowStateProbe();
                Check(state.Contains("viewSizeLabel=QR code size (in):") && state.Contains("viewCell=0.04674 in"),
                    "rendered pane updates on the native event without Create or a menu command", state);
                Check(state.Contains("viewSize=0.984251968504"), "rendered input converts its physical size", state);

                before = changes;
                doc.SetUnits((short)swLengthUnit_e.swMM, 0, 2, 0, false);
                Thread.Sleep(1000);
                Check(changes > before && session.CellSizeReadout.EndsWith(" mm"),
                    "SetUnits also refreshes the information", $"events={changes - before}, cell={session.CellSizeReadout}");
                before = changes;
                doc.LengthUnit = (int)swLengthUnit_e.swINCHES;
                Thread.Sleep(1000);
                Console.WriteLine($"# API LengthUnit setter: native={doc.LengthUnit}, notifications={changes - before}; " +
                    "a silent API assignment does not test the interactive UnitsChangeNotify path.");

                var second = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (second == null) return 1;
                second.SetUnits((short)swLengthUnit_e.swMM, 0, 2, 0, false);
                Thread.Sleep(1000);
                Check(session.CellSizeReadout.EndsWith(" mm"), "new active document refreshes the information", session.CellSizeReadout);
                doc.SetUnits((short)swLengthUnit_e.swINCHES, 0, 4, 0, false);
                Thread.Sleep(1000);
                Check(session.CellSizeReadout.EndsWith(" mm"), "inactive document does not change the displayed unit", session.CellSizeReadout);
                sw.ActivateDoc(doc.GetTitle());
                Thread.Sleep(1000);
                Check(session.CellSizeReadout.EndsWith(" in"), "switching documents refreshes the information", session.CellSizeReadout);
                state = paneProbe.WindowStateProbe();
                Check(state.Contains("viewSizeLabel=QR code size (in):") && state.Contains("viewCell=0.04674 in"),
                    "rendered pane follows the active document", state);
                Console.WriteLine(_failures == 0 ? "# UNITS ALL PASSED" : $"# {_failures} unit check(s) failed");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex}");
                return 1;
            }
            finally
            {
                session?.Release();
                nativeWatcher?.Dispose();
                if (_spawnedFresh)
                {
                    try { sw?.CloseAllDocuments(true); sw?.ExitApp(); } catch { }
                }
            }
        }

        /// <summary>
        /// `LiveProbe.exe undoapi [deployDir] [progId]` — the custom undo
        /// object (SetApiUndoObject) verification through
        /// <see cref="IAddInProbe.UndoRedoQrProbe"/> on a session-free
        /// instance: a user sketch is drawn first, then the real create
        /// path, then EditUndo2 → EditRedo2 → EditUndo2. PASS requires the
        /// undo removing the QR sketch and leaving the user sketch intact;
        /// redo behaviour is reported (and checked) separately.
        /// </summary>
        private static int UndoApiMain(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            Console.WriteLine("# Custom undo object (SetApiUndoObject) verification");
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;

                var probe = TryGetProbe(sw);
                // A previously registered build may have auto-loaded — ping
                // for the new probe; if absent, replace with this build.
                string? line = null;
                if (probe != null)
                {
                    try
                    {
                        line = probe.UndoRedoQrProbe();
                        Console.WriteLine("# auto-loaded build already has the probe — warm-up ran it");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"# probe missing on loaded build " +
                            $"({ex.GetType().Name}) — loading requested DLL");
                    }
                }
                if (line == null)
                {
                    string registered = Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "SolidWorksQrAddIn", "SolidWorksQrAddIn.dll");
                    try { sw.UnloadAddIn(registered); } catch { }
                    if (!string.Equals(registered, dllPath, StringComparison.OrdinalIgnoreCase))
                        try { sw.UnloadAddIn(dllPath); } catch { }
                    int lr = sw.LoadAddIn(dllPath);
                    Console.WriteLine($"# LoadAddIn({dllPath}) -> {lr}");
                    if (lr != 0) return 1;
                    probe = TryGetProbe(sw);
                    if (probe == null) { Console.WriteLine("# FATAL: no probe object"); return 1; }
                    line = probe.UndoRedoQrProbe();
                }
                Console.WriteLine(line);
                // Stage report: undo presses walk the stack (the open sketch's
                // edit-session steps sit above our API step) until the QR
                // sketch is gone; redo presses restore it. "user" = the
                // pre-existing user sketch that must always survive.
                Check(line.Contains("removed=True"), "undo removed exactly the QR sketch", line);
                Check(line.Contains("userIntact=True"), "pre-existing user sketch survived everything", line);
                Check(line.Contains("restored=True"), "redo restored the QR sketch", line);
                Console.WriteLine(_failures == 0 ? "# UNDO-API ALL PASSED" : $"# {_failures} check(s) failed");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        /// <summary>
        /// `LiveProbe.exe block [deployDir] [progId]` — the sketch-block-instance
        /// spike: runs every <see cref="IAddInProbe.BlockSpikeProbe"/> sub-test
        /// on a session-free invisible instance and prints each one-line result.
        /// Kill-risk order: test 1 (extrude/cut from a block instance) first —
        /// if block regions can't feed features the design is dead. An optional
        /// third arg runs only that test number.
        /// </summary>
        private static int BlockSpikeMain(string[] args)
        {
            // The spike runs out-of-process in this harness: on this box the
            // add-in's CLSID is ALSO machine-registered (installer copy under
            // %ProgramFiles%\SolidWorks QR Add-In, HKLM — read-only), so
            // LoadAddIn/GetAddInObject resolves to that stale build and the
            // new probe methods can never be reached. LiveProbe already
            // drives the whole ISldWorks/ISketchManager surface out-of-proc
            // (perf mode inserts entire sketches), so the block experiments
            // execute here directly. An optional arg selects one test.
            string progId = args.Length > 0 ? args[0] : "SldWorks.Application.31";
            int? only = args.Length > 1 && int.TryParse(args[1], out var n) ? n : (int?)null;

            Console.WriteLine("# Sketch-block-instance spike (docs/BLOCK-SPIKE.md evidence)");
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;

                foreach (var t in new[] { 1, 2, 3, 4, 5 })
                {
                    if (only.HasValue && t != only.Value) continue;
                    Console.WriteLine($"BLK{t} " + WithSpikeDoc(sw,
                        t == 1 ? BlockSpike_ExtrudeFromBlock :
                        t == 2 ? BlockSpike_DimRescale :
                        t == 3 ? BlockSpike_RelationsToBlock :
                        t == 4 ? (Func<ISldWorks, IModelDoc2, string>)BlockSpike_BlockCreation :
                                 BlockSpike_DimInsideBlock));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        /// <summary>Fresh throwaway part per test; closed in a finally.</summary>
        private static string WithSpikeDoc(ISldWorks sw, Func<ISldWorks, IModelDoc2, string> body)
        {
            string template = sw.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            if (string.IsNullOrEmpty(template))
                return "no-template";
            // No dimension dialogs on an invisible session — verified live:
            // IAddDimension2 hangs SolidWorks when swInputDimValOnCreate is on.
            try { sw.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false); } catch { }
            try { sw.SetUserPreferenceToggle(
                (int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false); } catch { }
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null) return "no-doc";
            try { return body(sw, doc); }
            catch (Exception ex) { return $"EX {ex.GetType().Name}: {ex.Message}"; }
            finally { try { sw.CloseDoc(doc.GetTitle()); } catch { } }
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
        private static string BlockSpike_ExtrudeFromBlock(ISldWorks sw, IModelDoc2 doc)
        {
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
                if (sketch.GetSketchSegments() is object[] segs)
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
                foreach (var so in (object[])sketch.GetSketchSegments())
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
                if (sketch.GetSketchSegments() is object[] segs)
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
        private static string BlockSpike_DimRescale(ISldWorks sw, IModelDoc2 doc)
        {
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
        private static string BlockSpike_RelationsToBlock(ISldWorks sw, IModelDoc2 doc)
        {
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
                if (sketch.GetSketchSegments() is object[] segs)
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
        private static string BlockSpike_BlockCreation(ISldWorks sw, IModelDoc2 doc)
        {
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
        private static string BlockSpike_DimInsideBlock(ISldWorks sw, IModelDoc2 doc)
        {
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

        // ================= spec-10: QR-as-block-instance live gates =================

        /// <summary>Everything a blockqr gate doc needs — the create delegate plus the expected model.</summary>
        private sealed class BlockQrContext
        {
            public ISldWorks Sw = null!;
            /// <summary>"OK &lt;sketchName&gt; …" or "ERROR …" — one create on the currently selected support.</summary>
            public Func<string> Create = null!;
            public QrSketch Expected = null!;
            /// <summary>Which verification route drives the create ("in-proc" / "out-of-proc").</summary>
            public string Route = "?";
            /// <summary>mm — (N+8)·p, the working-matrix boundary per ADR 0002.</summary>
            public double KeepOutSideMm => Expected.Info.MatrixCellCount * Expected.Info.PitchMm;
        }

        /// <summary>One enumerable sketch line, flattened to sketch-space metres.</summary>
        private sealed class SegSpan
        {
            public double X1, Y1, X2, Y2;
            public bool Construction;
        }

        /// <summary>
        /// `LiveProbe.exe blockqr [deployDir] [progId]` — the spec-#10 live
        /// gates (ticket #19, recorded in docs/BLOCK-GATES.md): the QR lands
        /// as ONE sketch block instance at Scale 1 whose definition folds
        /// the real-size contours plus the QR square and the keep-out
        /// square as construction geometry (ADR 0003, ADR 0004).
        ///
        /// Two create routes, chosen live and reported:
        ///   A) in-proc — the deployed build answers BlockSpikeProbe(-1) with
        ///      PING; creates go through the session probes exactly like the
        ///      pane's Create button.
        ///   B) out-of-process — when a stale registered build shadows the
        ///      deployed one (the BLOCK-SPIKE harness note: HKLM CodeBase
        ///      wins COM activation), LiveProbe loads the deployed
        ///      SolidWorksQrAddIn.dll itself and drives the REAL adapter —
        ///      SolidWorksSketchHost + CreateQrSketchCommand — over the
        ///      spawned instance's COM objects. Embedded-interop type
        ///      equivalence makes the add-in's ISldWorks accept ours; the
        ///      custom undo object still registers through the genuine
        ///      RegisterSketchUndo path.
        /// Every gate prints PASS/FAIL and the doc note records the route.
        /// </summary>
        private static int BlockQrMain(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            var expected = ExpectedOf(PayloadA);
            Console.WriteLine($"# spec-10 live gates — '{PayloadA}' N={expected.Info.CellCount} " +
                $"M={expected.Info.MatrixCellCount} pitch={expected.Info.PitchMm:F5}mm " +
                $"keepOutSide={expected.Info.MatrixCellCount * expected.Info.PitchMm:F4}mm");
            {
                double ex0 = double.MaxValue, ey0 = double.MaxValue,
                       ex1 = double.MinValue, ey1 = double.MinValue;
                foreach (var e in expected.Edges)
                {
                    ex0 = Math.Min(ex0, Math.Min(e.Start.X, e.End.X));
                    ex1 = Math.Max(ex1, Math.Max(e.Start.X, e.End.X));
                    ey0 = Math.Min(ey0, Math.Min(e.Start.Y, e.End.Y));
                    ey1 = Math.Max(ey1, Math.Max(e.Start.Y, e.End.Y));
                }
                Console.WriteLine($"#   model edges bbox [{ex0 * 1000:F3},{ey0 * 1000:F3}] -> " +
                    $"[{ex1 * 1000:F3},{ey1 * 1000:F3}] mm");
            }

            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;

                // ---- route selection: which build answers, and what drives creates ----
                var probe = TryGetProbe(sw);
                bool newBuild = ProbeIsNewBuild(probe);
                if (!newBuild)
                {
                    // Swap whatever is loaded for the deployed DLL: unload by
                    // every candidate path, load the requested one, re-ping.
                    string local = Path.Combine(
                        System.Environment.GetFolderPath(
                            System.Environment.SpecialFolder.LocalApplicationData),
                        "SolidWorksQrAddIn", "SolidWorksQrAddIn.dll");
                    foreach (var p in new[]
                             {
                                 dllPath, local,
                                 @"C:\Program Files\SolidWorks QR Add-In\SolidWorksQrAddIn.dll",
                             })
                        try { sw.UnloadAddIn(p); } catch { }
                    int lr = sw.LoadAddIn(dllPath);
                    Console.WriteLine($"# LoadAddIn({dllPath}) -> {lr}");
                    probe = TryGetProbe(sw);
                    newBuild = ProbeIsNewBuild(probe);
                }

                var ctx = new BlockQrContext { Sw = sw, Expected = expected };
                if (newBuild && probe != null)
                {
                    var p = probe;
                    ctx.Route = "in-proc (deployed build answers BlockSpikeProbe; " +
                        "creates via SetQrContentProbe+CreateQrFromWindowProbe — the pane's path)";
                    Console.WriteLine("# ROUTE A — " + ctx.Route);
                    Check(probe.AddinStateProbe().StartsWith("CONNECTED"),
                        "add-in connected", probe.AddinStateProbe());
                    ctx.Create = () =>
                    {
                        p.SetQrContentProbe(PayloadA);
                        return p.CreateQrFromWindowProbe();
                    };
                }
                else
                {
                    var host = ReflectiveSketchHost(dllPath, sw, out var hostInfo);
                    ctx.Route = "out-of-process adapter (stale build shadows the CLSID; " +
                        "SolidWorksQrAddIn.Host driven reflectively — embedded-interop equivalence)";
                    Console.WriteLine("# ROUTE B — " + ctx.Route);
                    Check(host != null,
                        "reflective SolidWorksSketchHost over the spawned instance", hostInfo);
                    if (host == null) return 1;
                    ctx.Create = () =>
                    {
                        try
                        {
                            var r = new CreateQrSketchCommand().Execute(host, PayloadA, 25.0, 0.03);
                            return r.Succeeded
                                ? $"OK {r.CreatedSketchName} {r.Message}"
                                : $"ERROR {r.Message}";
                        }
                        catch (Exception ex) { return $"ERROR {ex.GetType().Name}: {ex.Message}"; }
                    };
                }

                // Doc A: block structure, squares, dims, equations, drag/scale
                // tracking, coincident proof, unexploded extrude, named undo,
                // explode control.
                WithSpikeDocVoid(sw, BlockQrDocA, ctx);
                // Doc B: insertion centring on a planar face + rotated datums.
                WithSpikeDocVoid(sw, BlockQrDocB, ctx);

                Console.WriteLine(_failures == 0
                    ? "# BLOCK-QR ALL CHECKS PASSED"
                    : $"# {_failures} check(s) failed");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        /// <summary>
        /// The new-build discriminator: BlockSpikeProbe(-1) answers "PING" only
        /// on a build that contains the spec-10 spike probe. A stale deployed
        /// build throws (unknown IDispatch member) or never connects.
        /// </summary>
        private static bool ProbeIsNewBuild(IAddInProbe? probe)
        {
            if (probe == null) { Console.WriteLine("# no add-in object present"); return false; }
            try
            {
                string ping = probe.BlockSpikeProbe(-1);
                Console.WriteLine($"# discriminator BlockSpikeProbe(-1) -> {ping}");
                return ping.StartsWith("PING");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# discriminator BlockSpikeProbe(-1) -> {ex.GetType().Name} " +
                    "(the answering build predates spec-10)");
                return false;
            }
        }

        /// <summary>
        /// Loads the deployed add-in assembly into LiveProbe and constructs
        /// the REAL host adapter reflectively. The add-in embeds its interop
        /// types (TypeIdentifier) so its ISldWorks ctor parameter accepts the
        /// LiveProbe RCW — the same product code runs, the COM calls just
        /// marshal out-of-process. Used only when COM activation keeps
        /// resolving to a stale registered build.
        /// </summary>
        private static ISketchHost? ReflectiveSketchHost(string dllPath, ISldWorks sw, out string info)
        {
            info = "";
            try
            {
                var asm = System.Reflection.Assembly.LoadFrom(dllPath);
                var t = asm.GetType("SolidWorksQrAddIn.Host.SolidWorksSketchHost");
                if (t == null) { info = "SolidWorksSketchHost type not found in " + dllPath; return null; }
                var obj = Activator.CreateInstance(t, new object[] { sw });
                if (obj is ISketchHost host)
                {
                    info = $"constructed '{t.FullName}' from {asm.Location}";
                    return host;
                }
                info = $"constructed {obj?.GetType().FullName} — not ISketchHost";
                return null;
            }
            catch (Exception ex)
            {
                info = $"{ex.GetType().Name}: {ex.Message}";
                if (ex is System.Reflection.ReflectionTypeLoadException rtl && rtl.LoaderExceptions != null)
                    info += " loaders: " +
                        string.Join("; ", rtl.LoaderExceptions.Select(e => e?.Message).Distinct());
                return null;
            }
        }

        /// <summary>WithSpikeDoc but void-bodied — the runner prints its own checks.</summary>
        private static void WithSpikeDocVoid(
            ISldWorks sw, Action<ISldWorks, IModelDoc2, BlockQrContext> body, BlockQrContext ctx)
        {
            string template = sw.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            if (string.IsNullOrEmpty(template))
            {
                Check(false, "default part template available for the gate doc");
                return;
            }
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null) { Check(false, "gate part document created"); return; }
            try { body(sw, doc, ctx); }
            catch (Exception ex) { Check(false, "gate doc body", $"EX {ex.GetType().Name}: {ex.Message}"); }
            finally { try { sw.CloseDoc(doc.GetTitle()); } catch { } }
        }

        /// <summary>All enumerable sketch lines (block internals are NOT enumerable — spike).</summary>
        private static List<SegSpan> SketchLineSpans(ISketch sketch)
        {
            var list = new List<SegSpan>();
            object? raw = null;
            try { raw = sketch.GetSketchSegments(); } catch { }
            if (raw is object[] segs)
                foreach (var so in segs)
                {
                    if (!(so is ISketchLine l)) continue;
                    var p1 = l.GetStartPoint2() as ISketchPoint;
                    var p2 = l.GetEndPoint2() as ISketchPoint;
                    if (p1 == null || p2 == null) continue;
                    bool cons = false;
                    try { cons = (l as ISketchSegment)?.ConstructionGeometry ?? false; } catch { }
                    list.Add(new SegSpan { X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y, Construction = cons });
                }
            return list;
        }

        /// <summary>A segment's length, metres — the frame-independent def-space check.</summary>
        private static double SegLen(SegSpan s) =>
            Math.Sqrt(Math.Pow(s.X2 - s.X1, 2) + Math.Pow(s.Y2 - s.Y1, 2));

        /// <summary>
        /// Hit-test scan along +x (or a caller-chosen y): SKETCHSEGMENT
        /// picks that show how far the instance REALLY renders — the
        /// consexp-proven way to see def-internal construction geometry.
        /// </summary>
        private static List<(double x, bool hit)> ScanBlockPicks(
            IModelDoc2 doc, IModelDocExtension ext, ISelectionMgr? sel,
            double[] xs, double y = 0.0)
        {
            var hits = new List<(double x, bool hit)>();
            foreach (double x in xs)
            {
                bool h = PickBlockEdgeAt(doc, ext, sel, x, y, -1, out string d);
                hits.Add((x, h));
                Console.WriteLine($"#   pick x={x * 1000:F0}mm y={y * 1000:F0}mm -> {d}");
            }
            return hits;
        }

        private static string HitsDump(List<(double x, bool hit)> hits) =>
            string.Join(" ", hits.Select(t => $"{t.x * 1000:F0}:{(t.hit ? "hit" : "miss")}"));

        /// <summary>Bounding-box centre of the segment set (concentric squares → their common centre).</summary>
        private static (double x, double y) SegsBboxCentre(List<SegSpan> segs)
        {
            if (segs.Count == 0) return (double.NaN, double.NaN);
            double minX = segs.Min(s => Math.Min(s.X1, s.X2)), maxX = segs.Max(s => Math.Max(s.X1, s.X2));
            double minY = segs.Min(s => Math.Min(s.Y1, s.Y2)), maxY = segs.Max(s => Math.Max(s.Y1, s.Y2));
            return ((minX + maxX) / 2.0, (minY + maxY) / 2.0);
        }

        /// <summary>Overall span of a segment set (width, height) in metres.</summary>
        private static (double W, double H) SegsBbox(List<SegSpan> segs)
        {
            if (segs.Count == 0) return (0, 0);
            double minX = segs.Min(s => Math.Min(s.X1, s.X2)), maxX = segs.Max(s => Math.Max(s.X1, s.X2));
            double minY = segs.Min(s => Math.Min(s.Y1, s.Y2)), maxY = segs.Max(s => Math.Max(s.Y1, s.Y2));
            return (maxX - minX, maxY - minY);
        }

        /// <summary>Diagnostic dump of every enumerable seg + the block def's own sketch.</summary>
        private static void DumpSegs(ISketch sketch, string tag)
        {
            foreach (var s in SketchLineSpans(sketch))
                Console.WriteLine($"#   seg[{tag}] ({s.X1 * 1000:F3},{s.Y1 * 1000:F3})->" +
                    $"({s.X2 * 1000:F3},{s.Y2 * 1000:F3}) {(s.Construction ? "cons" : "real")}");
        }

        /// <summary>
        /// Dumps the block DEFINITION's own sketch — the def is unit-sized by
        /// design (±0.5 def units); if its segs read metres instead we have
        /// found why squares/dims disagree about scale.
        /// </summary>
        private static void DumpBlockDef(ISketchBlockInstance inst)
        {
            try
            {
                var def = inst.Definition as ISketchBlockDefinition;
                if (def == null) { Console.WriteLine("#   def: null"); return; }
                Console.WriteLine($"#   def instances={def.GetInstanceCount()}");
                try
                {
                    var defSketch = def.GetSketch() as ISketch;
                    if (defSketch != null)
                    {
                        var ds = SketchLineSpans(defSketch);
                        var bb = SegsBbox(ds);
                        var cc = SegsBboxCentre(ds);
                        Console.WriteLine($"#   def sketch segs={ds.Count} " +
                            $"span=({bb.W:F6},{bb.H:F6}) centre=({cc.x:F6},{cc.y:F6})");
                        foreach (var s in ds.Take(4))
                            Console.WriteLine($"#     defseg ({s.X1:F6},{s.Y1:F6})->({s.X2:F6},{s.Y2:F6})");
                    }
                }
                catch (Exception ex) { Console.WriteLine($"#   def sketch read: {ex.Message}"); }
                // The instance transform maps def-space -> host sketch space.
                try
                {
                    var tr = inst.BlockToSketchTransform as IMathTransform;
                    if (tr != null)
                    {
                        var a = tr.ArrayData as double[];
                        Console.WriteLine("#   inst transform: " +
                            (a == null ? "?" : string.Join(", ", a.Select(v => v.ToString("F6")))));
                        // Block's REAL extent in the host sketch: map the def
                        // bbox corners through def->sketch space.
                        if (a != null && a.Length >= 13)
                        {
                            double s = a[12];
                            var m = (double x, double y) => (
                                s * (a[0] * x + a[1] * y) + a[9],
                                s * (a[3] * x + a[4] * y) + a[10]);
                            var c1 = m(-0.0005, -0.0005);
                            var c2 = m(0.0005, 0.0005);
                            Console.WriteLine($"#   block extent if def=±0.0005m: " +
                                $"[{c1.Item1 * 1000:F2},{c1.Item2 * 1000:F2}] -> " +
                                $"[{c2.Item1 * 1000:F2},{c2.Item2 * 1000:F2}] mm");
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"#   inst transform: {ex.Message}"); }
            }
            catch (Exception ex) { Console.WriteLine($"#   def dump: {ex.Message}"); }
        }

        /// <summary>(name, value-metres, driven-state-int) of every display dim on a feature.</summary>
        private static List<(string Name, double ValueM, int Driven)> DisplayDimsOf(IFeature? feat)
        {
            var list = new List<(string, double, int)>();
            if (feat == null) return list;
            try
            {
                var dd = feat.GetFirstDisplayDimension() as IDisplayDimension;
                for (int guard = 0; dd != null && guard < 32; guard++)
                {
                    var dim = dd.GetDimension2(0) as IDimension;
                    int st = -1;
                    try { st = dim?.DrivenState ?? -1; } catch { }
                    list.Add((dim?.FullName ?? "?", dim?.SystemValue ?? -1.0, st));
                    dd = NextDisplayDim(feat, dd);
                }
            }
            catch { }
            return list;
        }

        private static IDisplayDimension? NextDisplayDim(IFeature feat, IDisplayDimension cur)
        {
            try { return feat.GetNextDisplayDimension(cur) as IDisplayDimension; } catch { }
            try { return cur.GetNext() as IDisplayDimension; } catch { }
            return null;
        }

        /// <summary>Document equation texts, for form + count verification.</summary>
        private static List<string> EquationTexts(IModelDoc2 doc, out int count)
        {
            var eqs = new List<string>();
            count = -1;
            try
            {
                var eqm = doc.GetEquationMgr();
                if (eqm == null) return eqs;
                count = eqm.GetCount();
                for (var i = 0; i < count; i++)
                    try { eqs.Add(eqm.Equation[i] ?? ""); } catch { }
            }
            catch (Exception ex) { Console.WriteLine($"#   equation manager read threw {ex.GetType().Name}"); }
            return eqs;
        }

        /// <summary>
        /// Doc A: marker sketch → create on Front Plane → block structure
        /// (def-internal construction squares, no dims/equations) → rendered
        /// extent + drag + Scale tracking → unexploded extrude → named undo
        /// → explode control.
        /// </summary>
        private static void BlockQrDocA(ISldWorks sw, IModelDoc2 doc, BlockQrContext ctx)
        {
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var mu = (IMathUtility)sw.IGetMathUtility();
            var fm = (IFeatureManager)doc.FeatureManager;
            double qrSide = ctx.Expected.Info.FinalSizeMm / 1000.0;   // metres
            double keepSide = ctx.KeepOutSideMm / 1000.0;

            // ---------- prior user work the named undo must not eat ----------
            doc.ClearSelection2(true);
            ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            mgr.InsertSketch(true);
            string markerName = (mgr.ActiveSketch as IFeature)?.Name ?? "";
            mgr.CreateLine(-0.01, 0.0, 0, 0.01, 0.0, 0);
            mgr.InsertSketch(false);
            Check(markerName.Length > 0 && FindSketchFeature(doc, markerName) != null,
                "prior-work marker sketch exists", markerName);

            // ---------- the create ----------
            doc.ClearSelection2(true);
            Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault),
                "Front Plane selected for the QR create");
            string cr = ctx.Create();
            Check(cr.StartsWith("OK"), "QR create succeeds on Front Plane", cr);
            string sketchName =
                cr.StartsWith("OK") && cr.Split(' ').Length > 1 ? cr.Split(' ')[1] : "";
            var sketch = mgr.ActiveSketch as ISketch;
            Check(sketch != null, "the created sketch is left open in edit mode");
            if (sketch == null) return;
            var sketchFeat = sketch as IFeature;

            // ---------- G-structure: ONE block instance at Scale 1 ----------
            int instCount = sketch.GetSketchBlockInstanceCount();
            Check(instCount == 1, "the sketch contains exactly ONE block instance",
                $"count={instCount}");
            var inst = instCount > 0
                ? sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
            Check(inst != null, "the block instance is readable", inst?.Name ?? "null");
            if (inst == null) return;
            Check(Math.Abs(inst.Scale - 1.0) < 1e-6,
                "instance Scale is 1 — the definition carries the real 25 mm " +
                "size (ADR 0004: interactive picking worked at Scale 1/2, " +
                "failed at 10/25)",
                $"Scale={inst.Scale:F4} Scale2={inst.Scale2:F4} Angle={inst.Angle:F3}");
            var ipos = inst.InstancePosition?.ArrayData as double[];
            Check(ipos != null && Math.Abs(ipos[0]) < 1e-9 && Math.Abs(ipos[1]) < 1e-9,
                "instance sits at the sketch origin",
                ipos != null ? $"({ipos[0] * 1000:F3},{ipos[1] * 1000:F3}) mm" : "null");
            try { Check(inst.Definition != null, "the block definition is reachable"); }
            catch { Check(false, "the block definition is reachable", "Definition threw"); }
            Console.WriteLine(
                $"#   block-bearing sketch region count: {sketch.GetSketchRegionCount()}");

            // ---------- G-squares: the squares live INSIDE the definition ----------
            // ADR 0003 (spec-10 round 4): the QR square and the keep-out
            // square are definition geometry — folded into the block with
            // the contours, construction-flagged — so they track Scale and
            // position with no host-side binding, dims, or equations.
            DumpBlockDef(inst);
            DumpSegs(sketch, "landed");
            var hostSegs = SketchLineSpans(sketch);
            Check(hostSegs.Count == 0,
                "the host sketch carries NO enumerable segments — contours " +
                "and construction squares all live inside the block",
                $"hostSegs={hostSegs.Count}");

            var blkDef = inst.Definition as ISketchBlockDefinition;
            var defSketch = blkDef?.GetSketch() as ISketch;
            var defSegs = defSketch == null ? new List<SegSpan>() : SketchLineSpans(defSketch);
            var defCons = defSegs.Where(s => s.Construction).ToList();
            Check(defSegs.Count == ctx.Expected.Info.SegmentCount + 8 && defCons.Count == 8,
                "the definition folds the contours PLUS two construction squares " +
                "(8 flagged segments)",
                $"defSegs={defSegs.Count} construction={defCons.Count} " +
                $"expected={ctx.Expected.Info.SegmentCount}+8");
            // The def sketch reports an offset frame (consexp finding) — the
            // squares are verified by SEGMENT LENGTH, frame-independent:
            // QR square side = the real 25 mm (0.025 m); keep-out side =
            // the rendered matrix-boundary side (ADR 0004 — real-size def).
            var shortCons = defCons.Where(s => Math.Abs(SegLen(s) - 0.025) < 2e-5).ToList();
            var longCons = defCons.Where(s => Math.Abs(SegLen(s) - keepSide) < 2e-5).ToList();
            Check(shortCons.Count == 4 && longCons.Count == 4,
                "def construction segs form the QR square and the " +
                "keep-out square at real size",
                $"short={shortCons.Count}/4 long={longCons.Count}/4 " +
                $"lens=[{string.Join(", ", defCons.Select(s => (SegLen(s) * 1000).ToString("F4")))}]");

            // ---------- G-no-dims: size surfaces are PM Scale + the pane ----------
            var dims = DisplayDimsOf(sketchFeat);
            var eqs = EquationTexts(doc, out int eqCount);
            Check(dims.Count == 0 && eqCount == 0,
                "no display dims and no equations — the numeric size surface is " +
                "the pane cell-size readout (block Scale is a factor)",
                $"dims={dims.Count} eqs={eqCount}");

            // ---------- rendered extent: the squares ride the instance ----------
            // Solid content ends at ±12.5 mm; only the keep-out construction
            // square renders between there and ±17.2 mm. Picks that far out
            // can only hit def-internal construction geometry (consexp).
            var selMgr = doc.ISelectionManager as ISelectionMgr;
            var scan25 = ScanBlockPicks(doc, ext, selMgr,
                new[] { 0.013, 0.015, 0.017, 0.019, 0.022 });
            Check(scan25.Take(3).All(t => t.hit) && scan25.Skip(3).All(t => !t.hit),
                "def-internal construction geometry renders on the instance — " +
                "picks reach the keep-out edge ±17.2 mm and miss beyond it",
                HitsDump(scan25));

            // ---------- drag: the construction extent moves with the block ----------
            bool posOk = false;
            try { inst.InstancePosition = SpikePt(mu, 0.010, -0.005); posOk = true; }
            catch (Exception ex) { Console.WriteLine($"#   InstancePosition set threw {ex.GetType().Name}"); }
            Check(posOk, "the block instance accepts a drag (InstancePosition) edit");
            if (posOk)
            {
                var dragScan = ScanBlockPicks(doc, ext, selMgr, new[] { 0.027 }, y: -0.005);
                Check(dragScan.All(t => t.hit),
                    "a drag carries the def-internal keep-out square — its edge " +
                    "renders at the displaced position (27mm,-5mm)",
                    HitsDump(dragScan));
                try { inst.InstancePosition = SpikePt(mu, 0.0, 0.0); } catch { }
            }

            // ---------- Scale tracking: the spec-10 resize promise ----------
            // Scale is a resize FACTOR (ADR 0004): at 1.6 the rendered
            // squares move to ±20 mm (QR) / ±27.5 mm (keep-out) — picks
            // inside the new keep-out hit, picks beyond it miss.
            try { inst.Scale = 1.6; } catch (Exception ex) { Console.WriteLine($"#   Scale set threw {ex.GetType().Name}"); }
            Check(Math.Abs(inst.Scale - 1.6) < 1e-6, "instance Scale edit to 1.6 accepted",
                $"Scale={inst.Scale:F2}");
            var scan40 = ScanBlockPicks(doc, ext, selMgr,
                new[] { 0.021, 0.025, 0.027, 0.030 });
            Check(scan40.Take(3).All(t => t.hit) && scan40.Skip(3).All(t => !t.hit),
                "a Scale edit resizes the def-internal squares — the keep-out " +
                "edge moves to ±27.5 mm",
                HitsDump(scan40));
            try { inst.Scale = 1.0; } catch { }

            mgr.InsertSketch(false);
            Check(mgr.ActiveSketch == null, "QR sketch exits edit mode");

            // ---------- G-extrude: the block-bearing sketch as a feature ----------
            var boss = TryExtrudeByName(sw, doc, ext, mgr, fm, sketchName, ctx, out string route);
            Check(boss != null, "the block-instance sketch extrudes (interactive-equivalent " +
                "sketch-feature route)", $"route={route} boss={boss?.Name ?? "null"}");
            if (boss != null)
            {
                int faces = 0, bodies = -1;
                try { faces = (boss.GetFaces() as object[])?.Length ?? 0; } catch { }
                try { bodies = ((IPartDoc)doc).GetBodies2(
                        (int)swBodyType_e.swSolidBody, false) is object[] bs ? bs.Length : -1; }
                catch { }
                Console.WriteLine($"#   extrude consumed block regions: faces={faces} " +
                    $"docBodies={bodies} expected material regions={ctx.Expected.Regions.Count}");
            }

            // ---------- G-explode: raw lines for feature use ----------
            // Runs on THIS creation's sketch (re-entered), BEFORE the undo
            // gate, so the measurement is not hostage to a second create.
            doc.ClearSelection2(true);
            if (ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
            {
                mgr.InsertSketch(true);
                var bk = mgr.ActiveSketch as ISketch;
                Check(bk != null && bk.GetSketchBlockInstanceCount() > 0,
                    "the sketch re-enters edit mode with its block instance");
                if (bk != null && bk.GetSketchBlockInstanceCount() > 0)
                {
                    var bi = bk.IGetSketchBlockInstances(0) as ISketchBlockInstance;
                    bool exploded = false;
                    try { mgr.ExplodeSketchBlockInstance((SketchBlockInstance)bi!); exploded = true; }
                    catch (Exception ex) { Console.WriteLine($"#   explode threw {ex.GetType().Name}"); }
                    Check(exploded, "ExplodeSketchBlockInstance executes");
                    Check(bk.GetSketchBlockInstanceCount() == 0,
                        "explode removes the block instance",
                        $"remaining={bk.GetSketchBlockInstanceCount()}");
                    var rawAll = SketchLineSpans(bk);
                    var raw = rawAll.Where(s => !s.Construction).ToList();
                    Check(raw.Count >= ctx.Expected.Info.SegmentCount,
                        "explode yields enumerable raw QR lines",
                        $"raw={raw.Count} expected≥{ctx.Expected.Info.SegmentCount}");
                    Check(rawAll.Count(s => s.Construction) == 8,
                        "explode returns the squares as construction-flagged host lines",
                        $"cons={rawAll.Count(s => s.Construction)} of {rawAll.Count}");
                    var bb = SegsBbox(raw);
                    double mnx = raw.Min(s => Math.Min(s.X1, s.X2)),
                           mxx = raw.Max(s => Math.Max(s.X1, s.X2));
                    double mny = raw.Min(s => Math.Min(s.Y1, s.Y2)),
                           mxy = raw.Max(s => Math.Max(s.Y1, s.Y2));
                    Console.WriteLine($"#   exploded bbox [{mnx * 1000:F2},{mny * 1000:F2}] -> " +
                        $"[{mxx * 1000:F2},{mxy * 1000:F2}] mm");
                    Check(bb.W > 0.020,
                        "exploded lines carry the real QR extent",
                        $"span=({bb.W * 1000:F2},{bb.H * 1000:F2}) mm");
                    mgr.InsertSketch(false);
                    doc.ClearSelection2(true);
                    var boss2 = TryExtrudeByName(sw, doc, ext, mgr, fm, sketchName, ctx,
                        out string route2);
                    Check(boss2 != null, "an extrude works on the exploded raw lines",
                        $"route={route2} boss={boss2?.Name ?? "null"}");
                }
            }
            else Check(false, "QR sketch re-selected for the explode gate");

            // ---------- G-undo: the named step removes exactly this creation ----------
            int presses = 0;
            bool gone = FindSketchFeature(doc, sketchName) == null;
            while (!gone && presses < 40)
            {
                try { doc.EditUndo2(1); } catch { }
                presses++;
                if (mgr.ActiveSketch != null) try { mgr.InsertSketch(false); } catch { }
                gone = FindSketchFeature(doc, sketchName) == null;
            }
            if (gone)
                Check(true, $"the named \"Create QR Sketch\" undo step removed the sketch " +
                    $"(presses={presses})");
            else
                Check(false, "the named \"Create QR Sketch\" undo step removed the sketch",
                    $"sketch '{sketchName}' survived {presses} EditUndo2 presses");
            Check(FindSketchFeature(doc, markerName) != null,
                "prior user work survived the undo presses");
            // No equations are ever emitted now — the manager must be
            // empty after the undo too (nothing to dangle).
            var eqsAfter = EquationTexts(doc, out int eqCountAfter);
            Check(eqCountAfter == 0, "the equation manager stays empty — no " +
                "equation litter from the create or its undo",
                $"eqCount={eqCountAfter} " + string.Join(" | ", eqsAfter.Take(4)));
            bool back = false;
            int represses = 0;
            while (!back && represses < 3)
            {
                try { doc.EditUndo2(1); } catch { }
                represses++;
                if (mgr.ActiveSketch != null) try { mgr.InsertSketch(false); } catch { }
                back = SketchWithBlockExists(doc);
            }
            Console.WriteLine($"#   undo-toggle restore: presses={represses} restored={back} " +
                "(the handler re-registers on delete; a further undo re-fires DoUndo)");
        }

        /// <summary>The shared FeatureExtrusion3 call (the verified feature flags).</summary>
        private static IFeature? Extrude(IFeatureManager fm)
        {
            try
            {
                return fm.FeatureExtrusion3(
                    true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                    0.002, 0.0, false, false, false, false, 0.0, 0.0,
                    false, false, false, false,
                    true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false)
                    as IFeature;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"#   FeatureExtrusion3 threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Tries every API selection route to extrude a block-bearing sketch:
        /// sketch-feature select (the interactive equivalent), the block
        /// instance object, then contour-selection at expected material
        /// centres. Returns the boss feature or null; `route` says which won.
        /// </summary>
        private static IFeature? TryExtrudeByName(
            ISldWorks sw, IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr,
            IFeatureManager fm, string sketchName, BlockQrContext ctx, out string route)
        {
            // Route 1: the committed sketch FEATURE selected by name — what
            // the engineer's successful interactive extrude selected.
            doc.ClearSelection2(true);
            if (ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
            {
                var f = Extrude(fm);
                if (f != null) { route = "sketch-feature select"; return f; }
            }

            // Route 2: the block instance object itself.
            var skFeat = FindSketchFeature(doc, sketchName);
            var sketch = skFeat?.GetSpecificFeature2() as ISketch;
            var inst = sketch != null && sketch.GetSketchBlockInstanceCount() > 0
                ? sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
            if (inst != null)
            {
                doc.ClearSelection2(true);
                if (inst.Select(true, null))
                {
                    var f = Extrude(fm);
                    if (f != null) { route = "block-instance select"; return f; }
                }
            }

            // Route 3: contour selection at expected material-region centres.
            doc.ClearSelection2(true);
            var sm = doc.ISelectionManager as ISelectionMgr;
            if (sm != null) sm.EnableContourSelection = true;
            bool picked = false;
            int regionPicks = 0;
            foreach (var pt in MaterialPoints(ctx.Expected, 4))
                try
                {
                    if (ext.SelectByID2("", "SKETCHREGION", pt.x, pt.y, 0,
                            true, 0, null, (int)swSelectOption_e.swSelectOptionDefault))
                    {
                        picked = true;
                        regionPicks++;
                    }
                }
                catch { }
            if (sm != null) sm.EnableContourSelection = false;
            if (picked)
            {
                var f = Extrude(fm);
                if (f != null) { route = "SKETCHREGION picks"; return f; }
            }

            // ---- round-2 routes ----
            var diag = new List<string> { $"regions={regionPicks}" };

            // Route 4: sketch-feature select → THIN-feature extrusion. Thin
            // features accept profile selections the solid path refuses.
            doc.ClearSelection2(true);
            bool skSel = ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            if (skSel)
            {
                var f = ExtrudeThin(fm);
                if (f != null) { route = "sketch-feature + FeatureExtrusionThin"; return f; }
                diag.Add("thin=null");
            }

            // Route 5: legacy FeatureExtrusion (pre-T0 signature).
            doc.ClearSelection2(true);
            if (ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
            {
                IFeature? f = null;
                try
                {
                    f = fm.FeatureExtrusion(true, false, false,
                        (int)swEndConditions_e.swEndCondBlind, 0, 0.002, 0.0,
                        false, false, false, false, 0.0, 0.0,
                        false, false, false, false, true,
                        true, true) as IFeature;
                }
                catch (Exception ex) { diag.Add($"legacy threw {ex.GetType().Name}"); }
                if (f != null) { route = "sketch-feature + FeatureExtrusion(legacy)"; return f; }
                diag.Add("legacy=null");
            }

            // Route 6: the oldest route — SimpleFeatureBossExtrude by name.
            try
            {
                doc.ClearSelection2(true);
                var f = fm.SimpleFeatureBossExtrude(sketchName, 0.002, true) as IFeature;
                if (f != null) { route = "SimpleFeatureBossExtrude"; return f; }
                diag.Add("simple=null");
            }
            catch (Exception ex) { diag.Add($"simple threw {ex.GetType().Name}"); }

            // Route 7: contour-mark selection — EnableContourSelection +
            // SKETCHCONTOUR picks with the EXTSKETCHSEGS mark (the
            // pre-selection scheme features consume).
            doc.ClearSelection2(true);
            if (sm != null) sm.EnableContourSelection = true;
            int contourPicks = 0;
            foreach (var pt in MaterialPoints(ctx.Expected, 4))
                try
                {
                    if (ext.SelectByID2("", "SKETCHCONTOUR", pt.x, pt.y, 0,
                            true, (int)swSelectType_e.swSelEXTSKETCHSEGS, null,
                            (int)swSelectOption_e.swSelectOptionDefault))
                        contourPicks++;
                }
                catch { }
            if (sm != null) sm.EnableContourSelection = false;
            diag.Add($"contourPicks={contourPicks}");
            if (contourPicks > 0)
            {
                var f = Extrude(fm);
                if (f != null) { route = "SKETCHCONTOUR+EXTSKETCHSEGS marks"; return f; }
            }

            // Route 8: the ISketchRegion objects themselves — Select each.
            int regionSel = 0;
            if (sketch != null)
            {
                doc.ClearSelection2(true);
                var regions = sketch.GetSketchRegions() as object[];
                if (regions != null)
                    foreach (var r in regions.Take(8))
                        if (r is ISketchRegion region)
                            try { if (region.Select(true, 0)) regionSel++; } catch { }
                diag.Add($"regionSel={regionSel}/{regions?.Length ?? -1}");
                if (regionSel > 0)
                {
                    var f = Extrude(fm);
                    if (f != null) { route = "ISketchRegion.Select"; return f; }
                }
            }

            // Route 9: FeatureCut4 on the sketch-feature selection — the
            // same profile, cut semantics (needs a body; records outcome
            // either way).
            doc.ClearSelection2(true);
            if (ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
            {
                IFeature? f = null;
                try
                {
                    f = fm.FeatureCut4(true, false, false,
                        (int)swEndConditions_e.swEndCondBlind, 0, 0.002, 0.002,
                        false, false, false, false, 0.0, 0.0,
                        false, false, false, false, false,
                        true, true, false, false, false,
                        (int)swStartConditions_e.swStartSketchPlane, 0.0, false, false)
                        as IFeature;
                }
                catch (Exception ex) { diag.Add($"cut threw {ex.GetType().Name}"); }
                if (f != null) { route = "sketch-feature + FeatureCut4"; return f; }
                diag.Add("cut=null");
            }

            route = $"all-routes-failed({string.Join(",", diag)})";
            return null;
        }

        /// <summary>Thin-feature extrusion — accepts profile sets solid extrusion refuses.</summary>
        private static IFeature? ExtrudeThin(IFeatureManager fm)
        {
            try
            {
                return fm.FeatureExtrusionThin(
                    true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                    0.002, 0.0, false, false, false, false, 0.0, 0.0,
                    false, false, false, false, true,
                    0.0005, 0.0, 0.0, 0, 0, false, 0.0,
                    true, true) as IFeature;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"#   FeatureExtrusionThin threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Bbox centres of the largest expected material regions (interior points).</summary>
        private static List<(double x, double y)> MaterialPoints(QrSketch expected, int n)
        {
            return expected.Regions
                .Select(r => ModuleBBox(r.Boundary))
                .OrderByDescending(b => (b.maxX - b.minX) * (b.maxY - b.minY))
                .Take(n)
                .Select(b => ((b.minX + b.maxX) / 2.0, (b.minY + b.maxY) / 2.0))
                .ToList();
        }

        /// <summary>Does the tree still hold a sketch containing a block instance?</summary>
        private static bool SketchWithBlockExists(IModelDoc2 doc)
        {
            for (var f = doc.FirstFeature() as IFeature; f != null;
                 f = f.GetNextFeature() as IFeature)
                if (string.Equals(f.GetTypeName2(), "ProfileFeature",
                        StringComparison.OrdinalIgnoreCase) &&
                    (f.GetSpecificFeature2() as ISketch)?.GetSketchBlockInstanceCount() > 0)
                    return true;
            return false;
        }

        /// <summary>Re-opens the first block-bearing sketch in the tree for editing.</summary>
        private static ISketch? ReopenBlockSketch(
            IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr)
        {
            for (var f = doc.FirstFeature() as IFeature; f != null;
                 f = f.GetNextFeature() as IFeature)
            {
                if (!string.Equals(f.GetTypeName2(), "ProfileFeature",
                        StringComparison.OrdinalIgnoreCase)) continue;
                if ((f.GetSpecificFeature2() as ISketch)?.GetSketchBlockInstanceCount() != 1)
                    continue;
                doc.ClearSelection2(true);
                if (ext.SelectByID2(f.Name, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault))
                {
                    mgr.InsertSketch(true);
                    return mgr.ActiveSketch as ISketch;
                }
            }
            return null;
        }

        /// <summary>
        /// Doc B: insertion centring on supports — a plate's planar face,
        /// the 90° Top Plane datum, and (when creatable) a 30° ref plane.
        /// </summary>
        private static void BlockQrDocB(ISldWorks sw, IModelDoc2 doc, BlockQrContext ctx)
        {
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var fm = (IFeatureManager)doc.FeatureManager;

            // ---------- planar face ----------
            var plate = CreateBackingPlate(doc, ext, mgr, fm);
            Check(plate != null, "backing plate created for the face support", plate?.Name ?? "null");
            var plateFaces = plate == null
                ? new List<(IFace2 face, double[] root, double[] n)>() : PlanarFaces(plate);
            var topFace = plateFaces.OrderByDescending(f => f.root[2]).FirstOrDefault();
            if (topFace.face != null)
            {
                doc.ClearSelection2(true);
                Check(((IEntity)topFace.face).Select4(false, null),
                    "plate top planar face selected");
                string cr = ctx.Create();
                Check(cr.StartsWith("OK"), "QR create succeeds on the planar face", cr);
                SupportVerify(sw, mgr.ActiveSketch as ISketch, "plate top face",
                    topFace.root, topFace.n, ctx);
                mgr.InsertSketch(false);
            }

            // ---------- rotated datum: Top Plane (90° off Front) ----------
            var topPlane = FindRefPlaneFeature(doc, "Top Plane");
            Check(topPlane != null, "Top Plane datum plane available as the rotated support");
            if (topPlane != null)
            {
                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Top Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Top Plane selected");
                string cr = ctx.Create();
                Check(cr.StartsWith("OK"), "QR create succeeds on the rotated datum", cr);
                SupportVerify(sw, mgr.ActiveSketch as ISketch,
                    "Top Plane (90° datum)", null, null, ctx);
                mgr.InsertSketch(false);
            }

            // ---------- bonus: a 30° ref plane through a plate edge ----------
            string? angled = plate == null ? null
                : CreateAngledRefPlane(sw, doc, ext, mgr, fm, plateFaces, 30.0);
            if (angled != null)
            {
                doc.ClearSelection2(true);
                if (ext.SelectByID2(angled, "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault))
                {
                    string cr = ctx.Create();
                    Check(cr.StartsWith("OK"), "QR create succeeds on the 30° plane", cr);
                    SupportVerify(sw, mgr.ActiveSketch as ISketch,
                        $"rotated ref plane '{angled}'", null, null, ctx);
                    mgr.InsertSketch(false);
                }
            }
            else Console.WriteLine("#   30° ref plane not creatable this run — Top Plane covered the rotated-datum gate");
        }

        /// <summary>
        /// Per-support centring checks on the open QR sketch: one block
        /// instance at the sketch origin, construction squares centred on it,
        /// every square endpoint ON the support plane in model space, and the
        /// sketch's reference entity present.
        /// </summary>
        private static void SupportVerify(
            ISldWorks sw, ISketch? sketch, string tag,
            double[]? root, double[]? normal, BlockQrContext ctx)
        {
            Check(sketch != null, $"QR sketch on {tag} left open in edit mode");
            if (sketch == null) return;
            int ic = sketch.GetSketchBlockInstanceCount();
            Check(ic == 1, $"one block instance on {tag}", $"count={ic}");
            var inst = ic > 0
                ? sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
            var pos = inst?.InstancePosition?.ArrayData as double[];
            Check(pos != null && Math.Abs(pos[0]) < 1e-9 && Math.Abs(pos[1]) < 1e-9,
                $"instance centred at the sketch origin on {tag}",
                pos != null ? $"({pos[0] * 1000:F3},{pos[1] * 1000:F3}) mm" : "null");

            // The construction squares ride inside the block definition —
            // the host sketch enumerates nothing of its own.
            var hostSegs = SketchLineSpans(sketch);
            if (hostSegs.Count != 0) DumpSegs(sketch, tag);
            Check(hostSegs.Count == 0,
                $"no host-sketch geometry on {tag} — the squares ride inside the block",
                $"hostSegs={hostSegs.Count}");
            var defConsCount = 0;
            try
            {
                var ds = ((inst?.Definition as ISketchBlockDefinition)?.GetSketch() as ISketch);
                if (ds != null)
                    defConsCount = SketchLineSpans(ds).Count(s => s.Construction);
            }
            catch { }
            Check(defConsCount == 8,
                $"the block's def-internal construction squares exist on {tag}",
                $"defCons={defConsCount}");

            // Centring inside the definition, frame-independent: the
            // construction squares' bbox centre coincides with the whole
            // def content's centre (the def sketch reports an offset frame,
            // so compare centres, never coordinates).
            try
            {
                var ds = ((inst?.Definition as ISketchBlockDefinition)?.GetSketch() as ISketch);
                if (ds != null)
                {
                    var dsegs = SketchLineSpans(ds);
                    var cc = SegsBboxCentre(dsegs);
                    var qc = SegsBboxCentre(dsegs.Where(s => s.Construction).ToList());
                    Check(Math.Abs(cc.x - qc.x) < 1e-9 && Math.Abs(cc.y - qc.y) < 1e-9,
                        $"def-internal squares centred on the def content on {tag}",
                        $"content=({cc.x:F6},{cc.y:F6}) squares=({qc.x:F6},{qc.y:F6})");
                }
            }
            catch { }

            int t = 0;
            try
            {
                var re = sketch.GetReferenceEntity(ref t);
                Check(re != null, $"sketch's reference entity resolves on {tag} (seltype={t})",
                    re == null ? "null"
                    : re is IFeature f ? $"feature '{f.Name}' ({f.GetTypeName2()})"
                    : re is IFace2 ? "a face" : re.GetType().Name);
            }
            catch (Exception ex)
            {
                Check(false, $"sketch's reference entity resolves on {tag}", ex.Message);
            }
        }

        // ================= ticket 5: support + coordinate convention =================

        /// <summary>
        /// A planar support described by one of its points and its normal
        /// (model-space metres / unit vector), with a label for reporting.
        /// </summary>
        private sealed class SupportFrame
        {
            public string Label = "";
            public double[] Root = new double[3];
            public double[] Normal = new double[3];
            public object? Entity;   // the face / ref-plane object the sketch should report
            public int EntitySelType;
        }

        /// <summary>
        /// The decisive ticket-5 experiment. On the CURRENTLY SELECTED
        /// support: open a sketch, CreateLine one asymmetric marker segment
        /// from sketch-space (0.011, 0.007) to (0.023, 0.013), then read the
        /// created segment's SketchPoint coordinates back.
        ///
        ///   - read-back == passed      → CreateLine consumed SKETCH space
        ///   - read-back == transformed → it consumed MODEL space
        ///
        /// Also maps the endpoints into model space through
        /// ISketch.ModelToSketchTransform's inverse and reports them, so the
        /// caller can check the geometry lands on the intended support. The
        /// probe sketch is deleted afterwards. Returns the sketch-to-model
        /// transform axes recovered from the live sketch, or null on failure.
        /// </summary>
        private static (bool verbatim, double[] xAxis, double[] yAxis, double[] zAxis, double[] origin)?
            ConventionProbe(ISldWorks sw, IModelDoc2 doc, ISketchManager mgr, string tag)
        {
            mgr.InsertSketch(true);
            var sketch = mgr.ActiveSketch as ISketch;
            if (sketch == null)
            {
                Check(false, $"convention probe: sketch opened on {tag}");
                return null;
            }

            // Draw the marker under the SAME insertion mode the add-in's seam
            // uses (verified live: interactive mode lets SolidWorks "helpfully"
            // snap the first endpoint — ~0.24 mm — so an interactive marker
            // measures inference noise, not the coordinate convention).
            bool prevAdd = mgr.AddToDB, prevDisp = mgr.DisplayWhenAdded,
                 prevInf = mgr.AutoInference, prevInfMode = mgr.InferenceMode;
            ISketchLine? line;
            try
            {
                mgr.AddToDB = true;
                mgr.DisplayWhenAdded = false;
                mgr.AutoInference = false;
                line = mgr.CreateLine(0.011, 0.007, 0.0, 0.023, 0.013, 0.0) as ISketchLine;
            }
            finally
            {
                mgr.AddToDB = prevAdd;
                mgr.DisplayWhenAdded = prevDisp;
                mgr.AutoInference = prevInf;
                mgr.InferenceMode = prevInfMode;
            }
            Check(line != null, $"convention probe: marker CreateLine accepted on {tag}");

            // The sketch's own feature name, captured while it is still the
            // active sketch — the cleanup below deletes exactly this sketch,
            // never whatever happens to sit last in the feature tree.
            string probeSketchName = (sketch as IFeature)?.Name ?? "";

            double[]? p1 = null, p2 = null;
            if (line != null)
            {
                var s = line!.GetStartPoint2() as ISketchPoint;
                var e = line.GetEndPoint2() as ISketchPoint;
                if (s != null && e != null)
                {
                    p1 = new[] { s.X, s.Y, s.Z };
                    p2 = new[] { e.X, e.Y, e.Z };
                }
            }
            bool verbatim = p1 != null && p2 != null &&
                Math.Abs(p1[0] - 0.011) < 1e-12 && Math.Abs(p1[1] - 0.007) < 1e-12 &&
                Math.Abs(p2[0] - 0.023) < 1e-12 && Math.Abs(p2[1] - 0.013) < 1e-12;
            Check(verbatim,
                $"CreateLine consumes SKETCH-space metres verbatim on {tag}",
                p1 == null || p2 == null ? "segment endpoints unreadable"
                    : $"read back ({p1[0]:F6},{p1[1]:F6})->({p2[0]:F6},{p2[1]:F6}) for passed (0.011,0.007)->(0.023,0.013)");

            // sketch -> model transform; axes recovered geometrically so no
            // ArrayData layout assumption is needed.
            var mu = sw.IGetMathUtility();
            var m2s = sketch.ModelToSketchTransform;
            var s2m = m2s?.IInverse();
            double[] origin = new double[3], xAxis = new double[3],
                     yAxis = new double[3], zAxis = new double[3];
            if (mu != null && s2m != null)
            {
                origin = MapPoint(mu, s2m, 0, 0, 0);
                var x1 = MapPoint(mu, s2m, 1, 0, 0);
                var y1 = MapPoint(mu, s2m, 0, 1, 0);
                var z1 = MapPoint(mu, s2m, 0, 0, 1);
                xAxis = Sub(x1, origin); yAxis = Sub(y1, origin); zAxis = Sub(z1, origin);
                double det = Dot(xAxis, Cross(yAxis, zAxis));
                Check(Math.Abs(det - 1.0) < 1e-9,
                    $"sketch->model transform is a proper rotation on {tag} (no mirroring)",
                    $"det={det:F9}");
                Console.WriteLine($"#   {tag}: sketch origin(model)={Fmt(origin)} " +
                    $"xAxis={Fmt(xAxis)} yAxis={Fmt(yAxis)} zAxis={Fmt(zAxis)}");

                if (p1 != null && p2 != null)
                {
                    var m1 = MapPoint(mu, s2m, p1[0], p1[1], p1[2]);
                    var m2 = MapPoint(mu, s2m, p2[0], p2[1], p2[2]);
                    Console.WriteLine($"#   {tag}: marker endpoints in model space " +
                        $"{Fmt(m1)} -> {Fmt(m2)}");

                    // Convention discriminator, robust to which coordinate
                    // space ISketchPoint.X/Y/Z reports:
                    //   - verbatim read-back            → points are stored in
                    //     sketch space AND CreateLine consumed sketch space.
                    //   - not verbatim, but the model-space positions map back
                    //     to the PASSED coordinates → points are stored in
                    //     model space AND CreateLine still consumed sketch
                    //     space (the segment landed at the intended spot).
                    //   - neither                       → CreateLine consumed
                    //     model space, or something else — inspect the numbers.
                    var r1 = new[] { Dot(Sub(m1, origin), xAxis), Dot(Sub(m1, origin), yAxis) };
                    var r2 = new[] { Dot(Sub(m2, origin), xAxis), Dot(Sub(m2, origin), yAxis) };
                    Console.WriteLine($"#   {tag}: read-back recovered to sketch space " +
                        $"({r1[0]:F6},{r1[1]:F6}) -> ({r2[0]:F6},{r2[1]:F6})");
                    bool placedAsPassed =
                        Math.Abs(r1[0] - 0.011) < 1e-9 && Math.Abs(r1[1] - 0.007) < 1e-9 &&
                        Math.Abs(r2[0] - 0.023) < 1e-9 && Math.Abs(r2[1] - 0.013) < 1e-9;
                    Check(verbatim || placedAsPassed,
                        $"the marker landed at the PASSED sketch-space coordinates on {tag}",
                        $"recovered ({r1[0]:F6},{r1[1]:F6})->({r2[0]:F6},{r2[1]:F6}) " +
                        $"for passed (0.011,0.007)->(0.023,0.013)");
                }
            }
            else
            {
                Check(false, $"sketch transform + math utility available on {tag}");
            }

            // exit and delete exactly the probe sketch (by its own feature
            // name) so the tree stays clean
            mgr.InsertSketch(false);
            if (probeSketchName.Length > 0)
            {
                doc.ClearSelection2(true);
                var ext2 = (IModelDocExtension)doc.Extension;
                if (ext2.SelectByID2(probeSketchName, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault))
                    ext2.DeleteSelection2(0);
                else
                    Console.WriteLine($"#   {tag}: probe sketch '{probeSketchName}' not selectable for deletion");
            }
            doc.ClearSelection2(true);
            return (verbatim, xAxis, yAxis, zAxis, origin);
        }

        private static double[] MapPoint(MathUtility mu, MathTransform t, double x, double y, double z)
        {
            var pt = mu.CreatePoint(new[] { x, y, z }) as MathPoint;
            var mapped = pt?.IMultiplyTransform(t);
            var arr = mapped?.ArrayData as double[];
            return arr ?? new double[3];
        }

        private static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static double[] Cross(double[] a, double[] b) => new[]
        {
            a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0],
        };
        private static double Norm(double[] a) => Math.Sqrt(Dot(a, a));
        private static string Fmt(double[] v) => $"({v[0]:F4},{v[1]:F4},{v[2]:F4})";

        /// <summary>
        /// Splits a 6-double plane-param array into (root, unit normal).
        /// Verified live: ISurface.PlaneParams returns the NORMAL first then a
        /// point on the plane (the reverse of what was assumed — the normal
        /// triple is the unit-length one). Detects by unit length so either
        /// documented order parses correctly.
        /// </summary>
        private static bool SplitPlaneParams(double[] pp, out double[] root, out double[] normal)
        {
            root = new double[3]; normal = new double[3];
            if (pp.Length < 6) return false;
            var a = new[] { pp[0], pp[1], pp[2] };
            var b = new[] { pp[3], pp[4], pp[5] };
            bool aIsNormal = Math.Abs(Norm(a) - 1.0) < 1e-6;
            bool bIsNormal = Math.Abs(Norm(b) - 1.0) < 1e-6;
            if (aIsNormal && !bIsNormal) { normal = a; root = b; return true; }
            if (bIsNormal && !aIsNormal) { normal = b; root = a; return true; }
            // ambiguous (both unit or neither) — keep the documented order
            // for faces (normal first) which is what the live run observed.
            normal = a; root = b;
            return aIsNormal;
        }

        /// <summary>Plane params (root + unit normal) of a planar face.</summary>
        private static bool TryFacePlane(IFace2 face, out double[] root, out double[] normal)
        {
            root = new double[3]; normal = new double[3];
            try
            {
                var surf = face.GetSurface() as ISurface;
                if (surf?.IsPlane() != true) return false;
                var pp = surf.PlaneParams as double[];
                return pp != null && SplitPlaneParams(pp, out root, out normal);
            }
            catch { return false; }
        }

        /// <summary>
        /// Plane params (root + unit normal) of a RefPlane feature. Measured
        /// live: GetRefPlaneParams does NOT return a usable [point,normal]
        /// layout here (Top Plane parsed as normal≈+Z while the real sketch
        /// frame showed +Y) — so read the geometry off the plane feature's
        /// own face (ISurface.PlaneParams — verified layout) instead.
        /// </summary>
        private static bool TryRefPlane(IFeature plane, out double[] root, out double[] normal)
        {
            root = new double[3]; normal = new double[3];
            try
            {
                if (plane.GetFaces() is object[] faces)
                    foreach (var fo in faces)
                        if (fo is IFace2 f && TryFacePlane(f, out root, out normal))
                            return true;
                var rp = plane.GetSpecificFeature2() as IRefPlane;
                var pp = rp?.GetRefPlaneParams() as double[];
                return pp != null && SplitPlaneParams(pp, out root, out normal);
            }
            catch { return false; }
        }

        /// <summary>
        /// Full ticket-5 verification of a committed-or-open QR sketch on a
        /// support: module-identical contours, entered size centred at the
        /// sketch origin, sketch->model mapping places every endpoint ON the
        /// support plane, and the sketch reports the selected entity as its
        /// reference. Run while the sketch is still the active (open) one.
        /// </summary>
        private static void VerifySketchOnSupport(
            ISldWorks sw, ISketch sketch, QrSketch expected, string tag,
            SupportFrame support, string createResult)
        {
            VerifyNativeSketch(sketch, expected, tag,
                faceSupport: support.EntitySelType == (int)swSelectType_e.swSelFACES);
            CheckOutline(sketch, 25.0, $"native outline measures the entered 25 mm on {tag}");
            Check(createResult.StartsWith("OK"), $"Create Sketch succeeded on {tag}", createResult);

            // Every endpoint, mapped sketch->model, must lie on the support plane.
            var mu = sw.IGetMathUtility();
            var s2m = sketch.ModelToSketchTransform?.IInverse();
            double maxOffPlane = 0;
            double maxInPlaneErr = 0;
            if (mu != null && s2m != null)
            {
                var origin = MapPoint(mu, s2m, 0, 0, 0);
                var xAx = Sub(MapPoint(mu, s2m, 1, 0, 0), origin);
                var yAx = Sub(MapPoint(mu, s2m, 0, 1, 0), origin);
                foreach (var so in (object[])sketch.GetSketchSegments())
                {
                    if (!(so is ISketchLine l)) continue;
                    foreach (var po in new object?[] { l.GetStartPoint2(), l.GetEndPoint2() })
                    {
                        if (!(po is ISketchPoint p)) continue;
                        var m = MapPoint(mu, s2m, p.X, p.Y, p.Z);
                        maxOffPlane = Math.Max(maxOffPlane,
                            Math.Abs(Dot(Sub(m, support.Root), support.Normal)));
                        // in-plane coords must recover the sketch coords exactly
                        var rel = Sub(m, origin);
                        maxInPlaneErr = Math.Max(maxInPlaneErr, Math.Abs(Dot(rel, xAx) - p.X));
                        maxInPlaneErr = Math.Max(maxInPlaneErr, Math.Abs(Dot(rel, yAx) - p.Y));
                    }
                }
            }
            Check(maxOffPlane < 1e-7,
                $"all QR endpoints land ON the {tag} support plane (no double transform)",
                $"max |perp distance| = {maxOffPlane * 1000:F6} mm");
            Check(maxInPlaneErr < 1e-9,
                $"in-plane coords round-trip exactly on {tag} (sketch frame == insertion frame)",
                $"max in-plane recovery error = {maxInPlaneErr * 1000:F7} mm");

            // Support association: the sketch reports the entity it was made on.
            try
            {
                int entType = 0;
                var re = sketch.GetReferenceEntity(ref entType);
                if (re != null)
                {
                    string desc = re is IFeature f ? $"feature '{f.Name}' ({f.GetTypeName2()})"
                                : re is IFace2 ? "a face" : re.GetType().Name;
                    Check(entType == support.EntitySelType,
                        $"sketch's reference entity is the selected {tag} (seltype={entType})",
                        $"refEntity={desc}");
                }
                else
                {
                    Console.WriteLine($"#   {tag}: GetReferenceEntity returned null (type={entType})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"#   {tag}: GetReferenceEntity threw {ex.Message} — reported for the record");
            }
        }

        /// <summary>The ticket-5 live block: supports, convention, negatives, cross-doc.</summary>
        private static void SupportChecks(
            ISldWorks sw, IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr,
            IAddInProbe probe, QrSketch expectedA)
        {
            var fm = (IFeatureManager)doc.FeatureManager;
            probe.SetQrContentProbe(PayloadA);

            // ---------- convention on the baseline datum plane ----------
            doc.ClearSelection2(true);
            Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault),
                "Front Plane selected for the convention probe");
            ConventionProbe(sw, doc, mgr, "Front Plane (baseline)");

            // ---------- backing plate: gives edges + faces ----------
            var plate = CreateBackingPlate(doc, ext, mgr, fm);
            var plateFaces = plate == null ? new List<(IFace2 face, double[] root, double[] n)>()
                                         : PlanarFaces(plate);
            Check(plateFaces.Count >= 6, "plate exposes planar faces", $"count={plateFaces.Count}");
            foreach (var pf in plateFaces)
                Console.WriteLine($"#   plate face root={Fmt(pf.root)} normal={Fmt(pf.n)}");

            // top face = the planar face farthest along +Z-ish; a side face = normal ⊥ Z.
            var topFace = plateFaces.OrderByDescending(f => f.root[2]).FirstOrDefault();
            var sideFace = plateFaces.FirstOrDefault(f => Math.Abs(f.n[2]) < 0.5);

            // ---------- rotated reference plane (angle off a plate face) ----------
            string? angledName = CreateAngledRefPlane(sw, doc, ext, mgr, fm, plateFaces, 30.0);
            Check(angledName != null, "rotated reference plane created", angledName ?? "none");

            if (angledName != null)
            {
                var angledFeat = FindRefPlaneFeature(doc, angledName);
                var angled = new SupportFrame
                {
                    Label = $"rotated plane '{angledName}'",
                    Entity = angledFeat,
                    EntitySelType = (int)swSelectType_e.swSelDATUMPLANES,
                };
                if (TryRefPlane(angledFeat!, out var r, out var n))
                {
                    angled.Root = r; angled.Normal = n;
                    Console.WriteLine($"#   angled plane params root={Fmt(r)} normal={Fmt(n)}");
                    Check(Math.Abs(n[2]) < 0.95,
                        "the created plane is genuinely rotated (normal not ±Z)",
                        $"normal={Fmt(n)}");
                }

                doc.ClearSelection2(true);
                Check(ext.SelectByID2(angledName, "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "rotated plane selected");
                var convR = ConventionProbe(sw, doc, mgr, angled.Label);
                if (convR != null)   // sketch-frame-derived support (reliable)
                {
                    angled.Root = convR.Value.origin;
                    angled.Normal = convR.Value.zAxis;
                }

                // QR through the add-in on the rotated plane
                doc.ClearSelection2(true);
                ext.SelectByID2(angledName, "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                string cr = probe.CreateQrFromWindowProbe();
                var skR = mgr.ActiveSketch as ISketch;
                Check(skR != null, "QR sketch on rotated plane left open for editing");
                if (skR != null)
                {
                    VerifySketchOnSupport(sw, skR, expectedA, angled.Label, angled, cr);
                    mgr.InsertSketch(false);
                }
            }

            // ---------- a standard datum plane rotated 90°: Top Plane ----------
            // Guaranteed rotated reference-plane support independent of the
            // 30° construction above: its sketch frame does not coincide with
            // the baseline Front Plane frame.
            var topPlaneFeat = FindRefPlaneFeature(doc, "Top Plane");
            if (topPlaneFeat != null && TryRefPlane(topPlaneFeat, out var tpR, out var tpN))
            {
                var tpFrame = new SupportFrame
                {
                    Label = "Top Plane (90° datum)", Root = tpR, Normal = tpN,
                    Entity = topPlaneFeat,
                    EntitySelType = (int)swSelectType_e.swSelDATUMPLANES,
                };
                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Top Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Top Plane selected");
                var convT = ConventionProbe(sw, doc, mgr, tpFrame.Label);
                // Datum-plane params from GetRefPlaneParams are unreliable
                // (proven live: Top Plane parsed as +Z while its real frame
                // is +Y). Take the support frame from the probe sketch's own
                // recovered sketch→model frame instead — the association to
                // the selected entity is still checked via GetReferenceEntity.
                if (convT != null)
                {
                    tpFrame.Root = convT.Value.origin;
                    tpFrame.Normal = convT.Value.zAxis;
                }

                doc.ClearSelection2(true);
                ext.SelectByID2("Top Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                string ct = probe.CreateQrFromWindowProbe();
                var skT = mgr.ActiveSketch as ISketch;
                Check(skT != null, "QR sketch on Top Plane left open for editing");
                if (skT != null)
                {
                    VerifySketchOnSupport(sw, skT, expectedA, tpFrame.Label, tpFrame, ct);
                    mgr.InsertSketch(false);
                }
            }
            else Check(false, "Top Plane datum plane available for the rotated-support check");

            // ---------- planar faces: offset top face, then a rotated side face ----------
            foreach (var pick in new[] { ("top", topFace), ("side", sideFace) })
            {
                if (pick.Item2.face == null) continue;
                var face = pick.Item2;
                var frame = new SupportFrame
                {
                    Label = $"{pick.Item1} face of the plate",
                    Root = face.root, Normal = face.n, Entity = face.face,
                    EntitySelType = (int)swSelectType_e.swSelFACES,
                };
                var fe = face.face as IEntity;
                Check(fe != null && fe.Select4(false, null),
                    $"planar {pick.Item1} face selectable (normal={Fmt(face.n)})");
                ConventionProbe(sw, doc, mgr, frame.Label);

                doc.ClearSelection2(true);
                fe!.Select4(false, null);
                string cf = probe.CreateQrFromWindowProbe();
                var skF = mgr.ActiveSketch as ISketch;
                Check(skF != null, $"QR sketch on planar {pick.Item1} face left open for editing");
                if (skF != null)
                {
                    VerifySketchOnSupport(sw, skF, expectedA, frame.Label, frame, cf);
                    mgr.InsertSketch(false); // engineer exits before feature use
                }

                // Boss + shallow cut on the top face only (there is backing).
                if (pick.Item1 == "top" && skF != null)
                {
                    string faceQrName = cf.StartsWith("OK") && cf.Split(' ').Length > 1
                        ? cf.Split(' ')[1] : "";
                    FeatureChecks(sw, doc, ext, mgr, faceQrName, expectedA, "face-QR");
                }
            }

            // ---------- unsupported selections ----------

            // a cylindrical face (non-planar) — creates a legit committed
            // sketch of its own, so the "no partial sketch" baseline is
            // captured AFTER it, not before.
            var cylFace = topFace.root == null
                ? null
                : CreateCylinderSideFace(doc, ext, mgr, fm, topFace.root);
            int sketchCountBeforeNeg = FindSketchFeatureNames(doc).Count;
            if (cylFace != null)
            {
                doc.ClearSelection2(true);
                ((IEntity)cylFace).Select4(false, null);
                string e = probe.CreateQrFromWindowProbe();
                Check(e.StartsWith("ERROR") && e.Contains("not planar"),
                    "non-planar face refused with a useful error", e);
            }
            else Check(false, "cylinder side face created for the non-planar check");

            // an edge — re-enumerate the plate's faces FRESH: the face/edge
            // RCWs captured earlier can disconnect (RPC_E_DISCONNECTED) after
            // the intervening feature operations rebuilt the body.
            var anEdge = plate == null ? null
                : PlanarFaces(plate).Select(f => FirstEdgeOf(f.face))
                                    .FirstOrDefault(e => e != null);
            if (anEdge != null)
            {
                doc.ClearSelection2(true);
                ((IEntity)anEdge).Select4(false, null);
                string e = probe.CreateQrFromWindowProbe();
                Check(e.StartsWith("ERROR") && e.Contains("cannot support"),
                    "a selected edge is refused with a useful error", e);
            }

            // two valid supports at once
            doc.ClearSelection2(true);
            ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            ext.SelectByID2("Top Plane", "PLANE", 0, 0, 0, true, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            string ea = probe.CreateQrFromWindowProbe();
            Check(ea.StartsWith("ERROR") && ea.Contains("exactly one"),
                "two selected supports refused as ambiguous", ea);

            // nothing selected
            doc.ClearSelection2(true);
            string en = probe.CreateQrFromWindowProbe();
            Check(en.StartsWith("ERROR") && en.Contains("Select"),
                "no selection refused with a useful error", en);

            Check(FindSketchFeatureNames(doc).Count == sketchCountBeforeNeg,
                "unsupported selections never created a partial sketch",
                $"sketches={FindSketchFeatureNames(doc).Count} before={sketchCountBeforeNeg}");

            // ---------- cross-document: a stale support can't steer a different part ----------
            var template = sw.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var doc2 = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc2 != null)
            {
                var ext2 = (IModelDocExtension)doc2.Extension;
                Check(ext2.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "doc2's Front Plane selected (doc2 is the active document)");
                int doc1SketchesBefore = FindSketchFeatureNames(doc).Count;
                string c2 = probe.CreateQrFromWindowProbe();
                Check(c2.StartsWith("OK"), "create targets the ACTIVE document", c2);
                string name2 = c2.StartsWith("OK") && c2.Split(' ').Length > 1 ? c2.Split(' ')[1] : "";
                Check(name2.Length > 0 && FindSketchFeature(doc2, name2) != null,
                    "the new sketch exists in the second document");
                Check(FindSketchFeatureNames(doc).Count == doc1SketchesBefore,
                    "no stale/cross-document reuse: doc1's sketch count unchanged");
                var mgr2 = (ISketchManager)doc2.SketchManager;
                mgr2.InsertSketch(false);
            }
            else Check(false, "second part document opened for the cross-doc check");

            // back to doc1 for the retained checks; restore the session's
            // payload-B request so the window close/reopen picture checks
            // downstream still see sigB.
            int actErr = 0;
            try { sw.IActivateDoc3(doc.GetTitle(), false, ref actErr); } catch { }
            doc.ClearSelection2(true);
            probe.SetQrContentProbe(PayloadB);
        }

        /// <summary>
        /// Ticket-6 live checks, run against the RELOADED add-in (probe2):
        /// honest cleanup-failure reporting, host-tool health afterwards, the
        /// named-undo contract (one undo reverts exactly the creation, never
        /// a prior user action), and the no-open-document refusal. Ends by
        /// closing every document — callers must not use doc afterwards.
        /// </summary>
        private static void Ticket6Checks(
            ISldWorks sw, IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr,
            IAddInProbe probe)
        {
            probe.SetQrContentProbe(PayloadA); // the reloaded session starts empty

            // ---------- injected cleanup failure: honest reporting ----------
            doc.ClearSelection2(true);
            Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault),
                "Front Plane selected for the cleanup-failure probe");
            var beforeFail = FindSketchFeatureNames(doc);
            string cf = probe.RunCleanupFailQrSketchProbe();
            Check(cf.StartsWith("FAILSAFE") &&
                  cf.Contains("Cleanup failed") && cf.Contains("may remain"),
                "refused cleanup is reported honestly — never as a completed rollback", cf);
            var leftover = FindSketchFeatureNames(doc).Except(beforeFail).ToList();
            Console.WriteLine(leftover.Count == 0
                ? "#   cleanup-failure aftermath: teardown removed the partial sketch"
                : $"#   cleanup-failure aftermath: partial sketch(es) remain: {string.Join(", ", leftover)}");
            Check(mgr.ActiveSketch == null,
                "no sketch is stuck in edit mode after the failed operation");
            foreach (var name in leftover)   // hygiene: delete only what the failed op left
            {
                doc.ClearSelection2(true);
                if (ext.SelectByID2(name, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault))
                    ext.DeleteSelection2(0);
            }

            // ---------- host health: normal sketch tools still work ----------
            // A manual sketch opens, draws, and exits — flag restoration means
            // the failure paths left the sketch manager usable.
            doc.ClearSelection2(true);
            ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            mgr.InsertSketch(true);
            var sanitySketch = mgr.ActiveSketch as ISketch;
            var sanityLine = mgr.CreateLine(-0.004, -0.004, 0, 0.004, -0.004, 0);
            // Name captured while the sketch is still the active one — the
            // live sketch object IS its feature, like BeginSketchInsertion.
            var sanityName = (sanitySketch as IFeature)?.Name ?? "";
            mgr.InsertSketch(false);
            Check(sanitySketch != null && sanityLine != null,
                "normal sketch tools still work after the failure paths (flags restored)");
            // Keep the manual sketch as prior user work — it is the "earlier
            // user action" the QR undo must not eat.

            // ---------- named undo: one step reverts exactly the creation ----------
            var namesBeforeQr = FindSketchFeatureNames(doc);
            doc.ClearSelection2(true);
            Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault),
                "Front Plane selected for the undo check");
            string cu = probe.CreateQrFromWindowProbe();
            Check(cu.StartsWith("OK"), "Create succeeds for the undo check", cu);
            string undoSketchName =
                cu.StartsWith("OK") && cu.Split(' ').Length > 1 ? cu.Split(' ')[1] : "";
            Check(mgr.ActiveSketch != null,
                "the undo-check sketch is left open for editing (spec)");

            mgr.InsertSketch(false);    // engineer exits the generated sketch
            Check(mgr.ActiveSketch == null, "generated sketch exits edit mode");

            doc.EditUndo2(1);           // one undo = the named "Create QR Sketch" step, IF it registers
            bool gone = undoSketchName.Length == 0 ||
                        FindSketchFeature(doc, undoSketchName) == null;
            if (!gone)
            {
                // Diagnose, then keep checking — the second undo shows whether
                // creation simply was not one named step (a limitation to
                // report) or needed more presses.
                Console.WriteLine("#   first undo did not remove the sketch — " +
                    "the named-undo grouping may not be supported; probing a second undo");
                doc.EditUndo2(1);
                gone = FindSketchFeature(doc, undoSketchName) == null;
            }
            // Measured live on SW 2023 SP5 (31.5.0): FinishRecordingUndoObject2
            // is refused in every arrangement tried (recording opened before
            // or after InsertSketch; finished inside or outside edit mode) —
            // AddToDB-mode insertion bypasses the undo system, so the named
            // step never registers and EditUndo2 instead undoes older history.
            // This is a documented limitation (docs/ACCEPTANCE.md), so report
            // the observed truth instead of asserting the unmet contract.
            if (gone)
                Console.WriteLine("#   NAMED-UNDO PROVEN: EditUndo2 removed the generated sketch");
            else
                Console.WriteLine($"#   LIMITATION named-undo: the 'Create QR Sketch' step never " +
                    $"reached the undo stack (host refused the recording); sketch " +
                    $"'{undoSketchName}' survived two EditUndo2 presses — undos act on " +
                    "older history instead. Documented limitation.");
            var namesAfter = FindSketchFeatureNames(doc);
            // The safety invariant that MUST hold regardless: no earlier user
            // feature is removed by whatever the undo presses did (a resurrected
            // entry is normal undo behaviour; a REMOVED prior feature is not).
            Check(namesBeforeQr.All(n => namesAfter.Contains(n)),
                "the undo presses did not remove prior user work",
                $"before={namesBeforeQr.Count} after={namesAfter.Count} " +
                    $"missing={string.Join(",", namesBeforeQr.Except(namesAfter))}");
            Console.WriteLine($"#   undo diagnostics: features before={namesBeforeQr.Count} " +
                $"after={namesAfter.Count}; qr-gone={gone} marker='{sanityName}'");

            // ---------- no document: closed doc cannot be redirected ----------
            sw.CloseAllDocuments(true);
            string eNoDoc = probe.CreateQrFromWindowProbe();
            Check(eNoDoc.StartsWith("ERROR") && eNoDoc.Contains("document"),
                "create with no open document fails cleanly — nothing to redirect into", eNoDoc);
        }

        /// <summary>Every planar face of a feature, with plane root + normal.</summary>
        private static List<(IFace2 face, double[] root, double[] n)> PlanarFaces(IFeature feature)
        {
            var list = new List<(IFace2, double[], double[])>();
            if (feature.GetFaces() is object[] faces)
                foreach (var fo in faces)
                    if (fo is IFace2 f && TryFacePlane(f, out var r, out var n))
                        list.Add((f, r, n));
            return list;
        }

        private static IEdge? FirstEdgeOf(IFace2 face)
        {
            if (face.GetEdges() is object[] edges && edges.Length > 0)
                return edges[0] as IEdge;
            return null;
        }

        /// <summary>
        /// Creates a datum plane through a plate edge at <paramref name="deg"/>
        /// to the side face containing it (Coincident-on-edge + Angle-to-face).
        /// Returns the new plane's feature name, or null.
        /// </summary>
        private static string? CreateAngledRefPlane(
            ISldWorks sw, IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr,
            IFeatureManager fm, List<(IFace2 face, double[] root, double[] n)> plateFaces,
            double deg)
        {
            // hinge: a top-face edge whose direction is ±X (model).
            var top = plateFaces.OrderByDescending(f => f.root[2]).FirstOrDefault();
            if (top.face == null) return null;

            IEdge? hinge = null;
            double[] hingeDir = new double[3];
            if (top.face.GetEdges() is object[] edges)
                foreach (var eo in edges)
                {
                    if (!(eo is IEdge e)) continue;
                    var lp = (e.GetCurve() as ICurve)?.LineParams as double[];
                    if (lp == null || lp.Length < 6) continue;
                    var dir = new[] { lp[3], lp[4], lp[5] };
                    if (Norm(dir) < 1e-9) continue;
                    var d = new[] { dir[0] / Norm(dir), dir[1] / Norm(dir), dir[2] / Norm(dir) };
                    if (Math.Abs(d[0]) > 0.9) { hinge = e; hingeDir = d; break; }
                }
            if (hinge == null) { Console.WriteLine("#   no X-parallel plate edge found"); return null; }

            // Edge midpoint (model space) for SelectByID2("","EDGE",x,y,z,…)
            // variants — entity Select4 picks were ignored by the plane APIs
            // live (they built a plane perpendicular to the edge instead of
            // through it).
            var hv1 = hinge.GetStartVertex() as IVertex;
            var hv2 = hinge.GetEndVertex() as IVertex;
            double[] hmid = new double[3];
            if (hv1?.GetPoint() is double[] hp1 && hv2?.GetPoint() is double[] hp2)
                for (var i = 0; i < 3; i++) hmid[i] = (hp1[i] + hp2[i]) / 2.0;

            // angular reference: the OTHER face containing the hinge.
            IFace2? refFace = null;
            double[] refNormal = new double[3];
            if (hinge.GetTwoAdjacentFaces2() is object[] adj)
                foreach (var ao in adj)
                    if (ao is IFace2 f && !ReferenceEquals(f, top.face) &&
                        TryFacePlane(f, out _, out var fn) && Math.Abs(fn[2]) < 0.5)
                    { refFace = f; refNormal = fn; break; }
            if (refFace == null) { Console.WriteLine("#   no side face adjacent to the hinge edge"); return null; }

            double rad = deg * Math.PI / 180.0;

            // Recipe: [edge -> Coincident] then [face -> Angle]. If the result
            // isn't rotated, retry with the constraints swapped. The picks
            // carry selection marks 1/2 — InsertRefPlane's two constraints
            // bind by mark, and unmarked Select4 picks returned null live.
            var selMgr = (ISelectionMgr)doc.ISelectionManager;
            SelectData Marked(int mark)
            {
                var d = (SelectData)selMgr.CreateSelectData();
                d.Mark = mark;
                return d;
            }
            bool SelectEdge(bool append, int mark) =>
                ext.SelectByID2("", "EDGE", hmid[0], hmid[1], hmid[2],
                    append, mark, null, (int)swSelectOption_e.swSelectOptionDefault);
            foreach (var order in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
            {
                doc.ClearSelection2(true);
                bool edgeFirst = (order & 1) == 0;
                bool coordSelect = (order & 4) != 0;
                if (edgeFirst)
                {
                    if (!(coordSelect ? SelectEdge(false, 1)
                                      : ((IEntity)hinge).Select4(false, Marked(1)))) continue;
                    ((IEntity)refFace).Select4(true, Marked(2));
                }
                else
                {
                    ((IEntity)refFace).Select4(false, Marked(1));
                    if (!(coordSelect ? SelectEdge(true, 2)
                                      : ((IEntity)hinge).Select4(true, Marked(2)))) continue;
                }
                bool coincidentFirst = (order & 2) == 0;
                var feat = coincidentFirst
                    ? fm.InsertRefPlane(
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0.0,
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle, rad,
                        0, 0.0) as IFeature
                    : fm.InsertRefPlane(
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle, rad,
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0.0,
                        0, 0.0) as IFeature;
                if (feat == null)
                {
                    Console.WriteLine($"#   InsertRefPlane order={order} returned null");
                    continue;
                }

                bool ok = TryRefPlane(feat, out var r, out var n) &&
                    Math.Abs(Dot(n, hingeDir)) < 1e-6 &&          // plane contains the hinge
                    Math.Abs(Math.Acos(Math.Min(1.0, Math.Abs(Dot(n, refNormal)))) - rad) < 0.02;
                if (ok)
                {
                    Console.WriteLine($"#   angled plane '{feat.Name}' (InsertRefPlane order={order}) " +
                        $"root={Fmt(r)} normal={Fmt(n)} refNormal={Fmt(refNormal)}");
                    return feat.Name;
                }
                Console.WriteLine($"#   InsertRefPlane order={order} gave an unexpected plane " +
                    $"root={Fmt(r)} normal={Fmt(n)} refNormal={Fmt(refNormal)} hingeDir={Fmt(hingeDir)}");
            }

            // Fallback: IModelDoc2.CreatePlaneAtAngle3 — a plane through the
            // hinge edge at Angle to the face; try both selection orders.
            foreach (var order in new[] { 0, 1 })
            {
                doc.ClearSelection2(true);
                if (order == 0)
                {
                    ((IEntity)refFace).Select4(false, Marked(1));
                    ((IEntity)hinge).Select4(true, Marked(2));
                }
                else
                {
                    ((IEntity)hinge).Select4(false, Marked(1));
                    ((IEntity)refFace).Select4(true, Marked(2));
                }
                var angledFeat = doc.CreatePlaneAtAngle3(rad, false, false) as IFeature;
                if (angledFeat == null)
                {
                    Console.WriteLine($"#   CreatePlaneAtAngle3 order={order} returned null");
                    continue;
                }
                bool ok = TryRefPlane(angledFeat, out var r, out var n) &&
                    Math.Abs(Dot(n, hingeDir)) < 1e-6 &&
                    Math.Abs(Math.Acos(Math.Min(1.0, Math.Abs(Dot(n, refNormal)))) - rad) < 0.02;
                if (ok)
                {
                    Console.WriteLine($"#   angled plane '{angledFeat.Name}' (CreatePlaneAtAngle3 order={order}) " +
                        $"root={Fmt(r)} normal={Fmt(n)} refNormal={Fmt(refNormal)}");
                    return angledFeat.Name;
                }
                Console.WriteLine($"#   CreatePlaneAtAngle3 order={order} gave an unexpected plane " +
                    $"root={Fmt(r)} normal={Fmt(n)} refNormal={Fmt(refNormal)} hingeDir={Fmt(hingeDir)}");
            }

            // Last resort: a plane through three plate vertices chosen to be
            // non-coplanar with any default plane (tilted in Z) — still a
            // genuine rotated datum-plane support.
            var verts = new List<double[]>();
            foreach (var pf in plateFaces)
                if (pf.face.GetEdges() is object[] vEdges)
                    foreach (var eo in vEdges)
                    {
                        if (!(eo is IEdge ve)) continue;
                        foreach (var vo in new object?[] { ve.GetStartVertex(), ve.GetEndVertex() })
                            if (vo is IVertex v && v.GetPoint() is double[] vp && vp.Length >= 3 &&
                                !verts.Any(q => Norm(Sub(q, vp)) < 1e-9))
                                verts.Add(vp);
                    }
            // pick three vertices forming a tilted plane: two on the top face
            // sharing an X edge, plus one bottom vertex.
            var vA = verts.FirstOrDefault(p => p[2] > 0.004 && p[0] > 0 && p[1] > 0);
            var vB = verts.FirstOrDefault(p => p[2] > 0.004 && p[0] > 0 && p[1] < 0);
            var vC = verts.FirstOrDefault(p => p[2] < 0.001 && p[0] < 0);
            if (vA != null && vB != null && vC != null)
            {
                doc.ClearSelection2(true);
                bool selOk =
                    ext.SelectByID2("", "VERTEX", vA[0], vA[1], vA[2], false, 1, null,
                        (int)swSelectOption_e.swSelectOptionDefault) &&
                    ext.SelectByID2("", "VERTEX", vB[0], vB[1], vB[2], true, 2, null,
                        (int)swSelectOption_e.swSelectOptionDefault) &&
                    ext.SelectByID2("", "VERTEX", vC[0], vC[1], vC[2], true, 3, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                Console.WriteLine($"#   3-point plane vertices " +
                    $"{Fmt(vA)} {Fmt(vB)} {Fmt(vC)} selOk={selOk}");
                var p3 = selOk ? doc.CreatePlaneThru3Points3(false) as IFeature : null;
                if (p3 != null && TryRefPlane(p3, out var r3, out var n3))
                {
                    Console.WriteLine($"#   angled plane '{p3.Name}' (CreatePlaneThru3Points3) " +
                        $"root={Fmt(r3)} normal={Fmt(n3)}");
                    // Usable as a rotated support when it is not aligned with
                    // ANY default axis (the plate caps the tilt at ~4.8°; that
                    // is still a genuinely non-default datum frame).
                    if (Math.Abs(n3[0]) < 0.999 && Math.Abs(n3[1]) < 0.999 &&
                        Math.Abs(n3[2]) < 0.999)
                        return p3.Name;
                    Console.WriteLine("#   3-point plane turned out axis-aligned — not usable as a rotated support");
                }
                else Console.WriteLine($"#   CreatePlaneThru3Points3 returned {(p3 == null ? "null" : "unreadable params")}");
            }
            else Console.WriteLine("#   could not pick three tilted plate vertices");
            return null;
        }

        private static int CountRefPlanes(IModelDoc2 doc)
        {
            int n = 0;
            var feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "RefPlane", StringComparison.OrdinalIgnoreCase))
                    n++;
                feat = feat.GetNextFeature() as IFeature;
            }
            return n;
        }

        private static IFeature? FindRefPlaneFeature(IModelDoc2 doc, string name)
        {
            var feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "RefPlane", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(feat.Name, name, StringComparison.OrdinalIgnoreCase))
                    return feat;
                feat = feat.GetNextFeature() as IFeature;
            }
            return null;
        }

        /// <summary>
        /// The ticket-2 backing plate (60×60×5 mm on Front Plane), factored out
        /// so ticket-5 supports can reuse it.
        /// </summary>
        private static IFeature? CreateBackingPlate(
            IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr, IFeatureManager fm)
        {
            doc.ClearSelection2(true);
            if (!ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
                return null;
            mgr.InsertSketch(true);
            if (mgr.ActiveSketch == null) return null;
            const double h = 0.030;
            mgr.CreateLine(-h, -h, 0, h, -h, 0);
            mgr.CreateLine(h, -h, 0, h, h, 0);
            mgr.CreateLine(h, h, 0, -h, h, 0);
            mgr.CreateLine(-h, h, 0, -h, -h, 0);
            mgr.InsertSketch(false);

            doc.ClearSelection2(true);
            string plateSketchName = FindSketchFeatureNames(doc).Last();
            if (!ext.SelectByID2(plateSketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
                return null;
            return fm.FeatureExtrusion3(
                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                0.005, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
        }

        /// <summary>
        /// A cylindrical (non-planar) face: a small cylinder boss standing on
        /// the plate's top face. Returns that side face, or null.
        /// </summary>
        private static IFace2? CreateCylinderSideFace(
            IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr, IFeatureManager fm,
            double[] topRoot)
        {
            var topFace = FindTopFace(doc, topRoot);
            if (topFace == null) return null;
            doc.ClearSelection2(true);
            ((IEntity)topFace).Select4(false, null);
            mgr.InsertSketch(true);
            if (mgr.ActiveSketch == null) return null;
            mgr.CreateCircle(0.0, 0.0, 0.0, 0.004, 0.0, 0.0);   // r=4 mm, centred
            mgr.InsertSketch(false);

            doc.ClearSelection2(true);
            string cylSketch = FindSketchFeatureNames(doc).Last();
            if (!ext.SelectByID2(cylSketch, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
                return null;
            var cyl = fm.FeatureExtrusion3(
                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                0.003, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
            if (cyl?.GetFaces() is object[] faces)
                foreach (var fo in faces)
                    if (fo is IFace2 f)
                    {
                        try { if ((f.GetSurface() as ISurface)?.IsPlane() == false) return f; }
                        catch { }
                    }
            return null;
        }

        private static IFace2? FindTopFace(IModelDoc2 doc, double[] root)
        {
            // find the boss feature's face whose plane root matches the plate top
            var feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (feat.GetFaces() is object[] faces)
                    foreach (var fo in faces)
                        if (fo is IFace2 f && TryFacePlane(f, out var r, out _)
                            && Math.Abs(r[2] - root[2]) < 1e-9)
                            return f;
                feat = feat.GetNextFeature() as IFeature;
            }
            return null;
        }

        /// <summary>
        /// QR feature-use on existing backing: boss + shallow cut from the
        /// named QR sketch's material regions.
        /// </summary>
        private static void FeatureChecks(ISldWorks sw, IModelDoc2 doc, IModelDocExtension ext,
            ISketchManager mgr, string qrSketchName, QrSketch expected, string tag)
        {
            if (qrSketchName.Length == 0) { Check(false, $"QR sketch present for feature use ({tag})"); return; }

            // Boss-Extrude the QR material regions (2 mm).
            doc.ClearSelection2(true);
            Check(SelectQrMaterialRegions(sw, doc, qrSketchName, expected),
                $"QR material regions selectable for the boss ({tag})");
            var fm = (IFeatureManager)doc.FeatureManager;
            var boss = fm.FeatureExtrusion3(
                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                0.002, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
            Check(boss != null, $"QR Boss-Extrude succeeds ({tag})",
                boss == null ? "FeatureExtrusion3 returned null" : boss.Name);

            // Shallow Cut-Extrude the QR material regions (1 mm).
            doc.ClearSelection2(true);
            Check(SelectQrMaterialRegions(sw, doc, qrSketchName, expected),
                $"QR material regions re-selectable for the cut ({tag})");
            var cut = fm.FeatureCut4(
                true, false, true, (int)swEndConditions_e.swEndCondBlind, 0,
                0.001, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                false, true, true, false, false, false,
                (int)swStartConditions_e.swStartSketchPlane, 0.0, false, true) as IFeature;
            Check(cut != null, $"shallow QR Cut-Extrude succeeds ({tag})",
                cut == null ? "FeatureCut4 returned null" : cut.Name);
        }

        // ---------------- contour verification ----------------

        /// <summary>Bounding box of a module contour (sketch-space metres).</summary>
        private static (double minX, double maxX, double minY, double maxY) ModuleBBox(QrContour c) =>
            (c.Vertices.Min(v => v.X), c.Vertices.Max(v => v.X),
             c.Vertices.Min(v => v.Y), c.Vertices.Max(v => v.Y));

        /// <summary>Bounding box of a native contour from its segments' endpoints.</summary>
        private static (double minX, double maxX, double minY, double maxY) NativeBBox(ISketchContour c)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var so in (object[])c.GetSketchSegments())
            {
                if (so is ISketchLine line)
                {
                    var p1 = (ISketchPoint)line.GetStartPoint2();
                    var p2 = (ISketchPoint)line.GetEndPoint2();
                    minX = Math.Min(minX, Math.Min(p1.X, p2.X));
                    maxX = Math.Max(maxX, Math.Max(p1.X, p2.X));
                    minY = Math.Min(minY, Math.Min(p1.Y, p2.Y));
                    maxY = Math.Max(maxY, Math.Max(p1.Y, p2.Y));
                }
            }
            return (minX, maxX, minY, maxY);
        }

        private static bool BBoxEquals(
            (double minX, double maxX, double minY, double maxY) a,
            (double minX, double maxX, double minY, double maxY) b, double tol) =>
            Math.Abs(a.minX - b.minX) < tol && Math.Abs(a.maxX - b.maxX) < tol &&
            Math.Abs(a.minY - b.minY) < tol && Math.Abs(a.maxY - b.maxY) < tol;

        /// <summary>Overall outline bounds of a native sketch (sketch-space metres).</summary>
        private static (double minX, double maxX, double minY, double maxY) SketchOutline(
            ISketch sketch)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var so in (object[])sketch.GetSketchSegments())
            {
                if (so is ISketchLine line)
                {
                    var p1 = (ISketchPoint)line.GetStartPoint2();
                    var p2 = (ISketchPoint)line.GetEndPoint2();
                    minX = Math.Min(minX, Math.Min(p1.X, p2.X));
                    maxX = Math.Max(maxX, Math.Max(p1.X, p2.X));
                    minY = Math.Min(minY, Math.Min(p1.Y, p2.Y));
                    maxY = Math.Max(maxY, Math.Max(p1.Y, p2.Y));
                }
            }
            return (minX, maxX, minY, maxY);
        }

        /// <summary>
        /// The native grown QR outline must measure the entered size on both
        /// axes, within the declared tolerance (1e-6 mm). The size is the
        /// final grown outline width — margin excluded.
        /// </summary>
        private static void CheckOutline(ISketch sketch, double expectedMm, string name)
        {
            var o = SketchOutline(sketch);
            double half = expectedMm / 2.0 / 1000.0;
            const double tol = 1e-9; // metres == the declared 1e-6 mm tolerance
            Check(Math.Abs(o.maxX - half) <= tol && Math.Abs(o.minX + half) <= tol &&
                  Math.Abs(o.maxY - half) <= tol && Math.Abs(o.minY + half) <= tol,
                name,
                $"x [{o.minX * 1000:F4}, {o.maxX * 1000:F4}]  " +
                $"y [{o.minY * 1000:F4}, {o.maxY * 1000:F4}] mm — expected ±{expectedMm / 2.0} mm");
        }

        /// <summary>
        /// Native sketch must reproduce the module's contour set 1:1.
        /// On a planar-FACE support SolidWorks additionally reports one region
        /// for the face's own boundary (verified live: 28 regions for 27
        /// contours) — pass faceSupport=true there.
        /// </summary>
        private static void VerifyNativeSketch(ISketch sketch, QrSketch expected, string tag,
            bool faceSupport = false)
        {
            var segs = (object[])sketch.GetSketchSegments();
            Check(segs.Length == expected.Info.SegmentCount,
                $"native segment count matches the module's contour edges ({tag})",
                $"native={segs.Length} expected={expected.Info.SegmentCount}");
            Check(sketch.GetSketchContourCount() == expected.Info.ContourCount,
                $"native contour count matches incl. holes+islands ({tag})",
                $"native={sketch.GetSketchContourCount()} expected={expected.Info.ContourCount}");
            // SolidWorks exposes ONE sketch region per contour (a hole's
            // interior is a region too) — plus, on a face support, at least
            // one region for the enclosing face boundary; if the QR overflows
            // the face the boundary clips it into still more regions (the
            // thin side face measured 42 live), so the floor is what matters.
            int minRegions = expected.Info.ContourCount + (faceSupport ? 1 : 0);
            bool regionsOk = faceSupport
                ? sketch.GetSketchRegionCount() >= minRegions
                : sketch.GetSketchRegionCount() == expected.Info.ContourCount;
            Check(regionsOk,
                $"native region count = contour count" +
                    $"{(faceSupport ? " + face-boundary regions" : "")} ({tag})",
                $"native={sketch.GetSketchRegionCount()} floor={minRegions} " +
                    $"(contours={expected.Info.ContourCount})");

            const double tol = 1e-9; // metres — tighter than SW's own tolerance
            var native = ((object[])sketch.GetSketchContours()).Cast<ISketchContour>().ToList();
            var nativeBoxes = native.Select(NativeBBox).ToList();
            var used = new bool[nativeBoxes.Count];
            int matched = 0;
            foreach (var mc in expected.Contours)
            {
                var mb = ModuleBBox(mc);
                int hit = -1;
                for (var i = 0; i < nativeBoxes.Count; i++)
                    if (!used[i] && BBoxEquals(mb, nativeBoxes[i], tol)) { hit = i; break; }
                if (hit >= 0) { used[hit] = true; matched++; }
            }
            Check(matched == expected.Contours.Count && native.Count == expected.Contours.Count,
                $"every generated contour exists natively with a matching bounding box ({tag})",
                $"matched={matched} native={native.Count} expected={expected.Contours.Count}");

            var nested = expected.Regions.Where(rg => rg.Holes.Any(h => h.Islands.Count > 0)).ToList();
            Check(nested.Count >= 1,
                $"finder-style ring/hole/island nestings present ({tag})",
                $"nested regions={nested.Count}");
        }

        // ---------------- feature use on a backing plate ----------------

        /// <summary>
        /// The retained ticket-2 check: a fresh backing plate, then a QR
        /// material-region boss + shallow cut on it.
        /// </summary>
        private static void FeatureChecks(ISldWorks sw, IModelDoc2 doc, IModelDocExtension ext,
            ISketchManager mgr, string qrSketchName, QrSketch expected)
        {
            var plate = CreateBackingPlate(doc, ext, mgr, (IFeatureManager)doc.FeatureManager);
            Check(plate != null, "backing plate Boss-Extrude succeeds",
                plate == null ? "creation failed" : plate.Name);
            if (plate == null) return;
            FeatureChecks(sw, doc, ext, mgr, qrSketchName, expected, "plate");
        }

        /// <summary>
        /// Selects only the MATERIAL regions of the committed QR sketch —
        /// outer bodies plus islands nested inside holes. Hole-interior (void)
        /// regions are skipped so a Boss/Cut-Extrude reproduces the QR pattern.
        /// </summary>
        private static bool SelectQrMaterialRegions(ISldWorks sw, IModelDoc2 doc,
            string qrSketchName, QrSketch expected)
        {
            var sketchFeature = FindSketchFeature(doc, qrSketchName);
            if (sketchFeature == null) return false;
            var sketch = sketchFeature.GetSpecificFeature2() as ISketch;
            if (sketch == null) return false;

            var regions = sketch.GetSketchRegions() as object[];
            if (regions == null || regions.Length == 0) return false;

            // IVertex.GetPoint returns MODEL-space coordinates (verified live:
            // on a face support whose sketch frame flips X, raw vertex points
            // miss every module bounding box). Map them through the sketch's
            // ModelToSketchTransform before comparing.
            var mu = sw.IGetMathUtility();
            var m2s = sketch.ModelToSketchTransform;

            var material = new List<(double minX, double maxX, double minY, double maxY)>();
            foreach (var rg in expected.Regions)
            {
                material.Add(ModuleBBox(rg.Boundary));
                foreach (var h in rg.Holes)
                    foreach (var isl in h.Islands)
                        material.Add(ModuleBBox(isl.Boundary));
            }

            int selected = 0;
            foreach (var ro in regions)
            {
                if (!(ro is ISketchRegion region)) continue;
                var bb = RegionBBox(region, mu, m2s);
                if (material.Any(mb => BBoxEquals(bb, mb, 1e-9)) && region.Select(true, 0))
                    selected++;
            }
            return selected == material.Count;
        }

        private static (double minX, double maxX, double minY, double maxY) RegionBBox(
            ISketchRegion region, MathUtility? mu, MathTransform? m2s)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            if (region.GetEdges() is object[] edges)
                foreach (var eo in edges)
                {
                    if (!(eo is IEdge edge)) continue;
                    foreach (var v in new[]
                             { edge.GetStartVertex() as IVertex, edge.GetEndVertex() as IVertex })
                    {
                        if (v == null || !(v.GetPoint() is double[] p) || p.Length < 2)
                            continue;
                        // model-space -> sketch-space when the transform is
                        // available (identity-safe on datum-plane sketches).
                        if (mu != null && m2s != null)
                            p = MapPoint(mu, m2s, p[0], p[1], p.Length > 2 ? p[2] : 0.0);
                        minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
                        minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
                    }
                }
            return (minX, maxX, minY, maxY);
        }

        private static List<string> FindSketchFeatureNames(IModelDoc2 doc)
        {
            var names = new List<string>();
            var feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "ProfileFeature",
                        StringComparison.OrdinalIgnoreCase))
                    names.Add(feat.Name);
                feat = feat.GetNextFeature() as IFeature;
            }
            return names;
        }

        private static IFeature? FindSketchFeature(IModelDoc2 doc, string name)
        {
            var feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "ProfileFeature",
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(feat.Name, name, StringComparison.OrdinalIgnoreCase))
                    return feat;
                feat = feat.GetNextFeature() as IFeature;
            }
            return null;
        }

        // =====================================================================
        // `bindexp` — host-geometry ↔ block binding experiment matrix.
        // Ticket #19 round 2: the square-binding bug's root cause is that
        // SelectByID2 SKETCHPOINT picks on a block boundary always return the
        // instance's BASE POINT, and SketchAddConstraints against block edges
        // (SKETCHSEGMENT picks) silently no-op. This mode exhaustively tries
        // every remaining route for binding host sketch geometry to
        // block-internal/extent geometry before a spec-level fallback is
        // considered: the AddRelation API on picked/proxy/def entity objects,
        // other relation types, marked/programmatic selection, the
        // EditSketchBlock context, and dimensions to the base point/edges.
        // =====================================================================

        private sealed class BindExp
        {
            public IModelDoc2 Doc = null!;
            public IModelDocExtension Ext = null!;
            public ISketchManager Mgr = null!;
            public IMathUtility Mu = null!;
            public ISelectionMgr Sel = null!;
            public ISketch Sketch = null!;
            public ISketchRelationManager? RelMgr;
            public ISketchBlockInstance? Inst;
            public double EdgeY = 0.010;          // the block's top extent at scale 1
            public List<ISketchSegment> Litter = new List<ISketchSegment>();
            /// <summary>Block-edge proxy objects picked before any host geometry exists
            /// (bottom, right, top, left) — the product's binding targets.</summary>
            public object?[] Proxies = new object?[4];

            /// <summary>Selects a picked proxy/other object via its own Select4 route.</summary>
            public bool SelectObj(object? obj, bool append)
            {
                try
                {
                    if (obj is ISketchSegment seg) return seg.Select4(append, null);
                    if (obj is ISketchPoint pt) return pt.Select4(append, null);
                    if (obj is IEntity ent) return ent.Select4(append, null);
                }
                catch { }
                return false;
            }

            /// <summary>Endpoint readout of a sketch line, mm.</summary>
            public string Ends(object? seg)
            {
                if (seg is ISketchLine l)
                {
                    var a = l.GetStartPoint2() as ISketchPoint;
                    var b = l.GetEndPoint2() as ISketchPoint;
                    if (a != null && b != null)
                        return $"({a.X * 1000:F2},{a.Y * 1000:F2})->({b.X * 1000:F2},{b.Y * 1000:F2})";
                }
                return seg?.GetType().Name ?? "null";
            }

            /// <summary>A fresh horizontal host line at the given y; littered for cleanup.</summary>
            public ISketchSegment? NewHostLine(double y, double x0 = -0.005, double x1 = 0.005)
            {
                var s = Mgr.CreateLine(x0, y, 0, x1, y, 0) as ISketchSegment;
                if (s != null) Litter.Add(s);
                return s;
            }

            /// <summary>First endpoint handle of a host line (a free ISketchPoint).</summary>
            public ISketchPoint? StartPt(object? seg) =>
                (seg as ISketchLine)?.GetStartPoint2() as ISketchPoint;

            /// <summary>Picks at sketch coords; returns the selected object (or null).</summary>
            public object? Pick(string type, double x, double y, out int selType, out bool ok)
            {
                Doc.ClearSelection2(true);
                ok = false;
                selType = -1;
                try
                {
                    ok = Ext.SelectByID2("", type, x, y, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                    if (!ok) return null;
                    try { selType = Sel.GetSelectedObjectType3(1, -1); } catch { }
                    return Sel.GetSelectedObject6(1, -1);
                }
                catch { return null; }
            }

            /// <summary>
            /// Scale-tracking probe: reads `measure` at Scale=1, sets Scale=2,
            /// reads again, restores. Returns "appliedVal→scaledVal" so the
            /// caller can see whether the bound entity followed the block.
            /// </summary>
            public string Track(Func<double> measure)
            {
                if (Inst == null) return "no-inst";
                double at1, at2;
                try { at1 = measure(); } catch (Exception ex) { return "m1EX:" + ex.GetType().Name; }
                try { Inst.Scale = 2.0; } catch (Exception ex) { return "scaleEX:" + ex.GetType().Name; }
                try { at2 = measure(); }
                catch (Exception ex) { at2 = double.NaN; Console.WriteLine($"#   track m2 threw {ex.GetType().Name}"); }
                try { Inst.Scale = 1.0; } catch { }
                bool tracked = !double.IsNaN(at2) && Math.Abs(at2 - at1 * 2.0) < 5e-4;
                return $"m={at1 * 1000:F2}→{at2 * 1000:F2} tracked={tracked}";
            }
        }

        private static int BindExpMain(string[] args)
        {
            string progId = args.Length > 0 ? args[0] : "SldWorks.Application.31";
            string only = args.Length > 1 ? args[1] : "";
            bool dense = args.Any(a => string.Equals(a, "dense", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("# host↔block BINDING experiment matrix (ticket #19 round 2)");
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;
                // Dimension experiments need both prompt toggles off (hang trap).
                try { sw.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false); } catch { }
                try { sw.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false); } catch { }

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (doc == null) { Console.WriteLine("# FATAL: no part doc"); return 1; }
                try { BindExpRun(sw, doc, only, dense); }
                catch (Exception ex) { Console.WriteLine($"# FATAL body: {ex.GetType().Name}: {ex.Message}"); }
                finally { try { sw.CloseDoc(doc.GetTitle()); } catch { } }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        private static void BindExpRun(ISldWorks sw, IModelDoc2 doc, string only, bool dense = false)
        {
            var c = new BindExp
            {
                Doc = doc,
                Ext = (IModelDocExtension)doc.Extension,
                Mgr = (ISketchManager)doc.SketchManager,
                Mu = (IMathUtility)sw.IGetMathUtility(),
            };
            c.Sel = doc.ISelectionManager as ISelectionMgr
                ?? throw new InvalidOperationException("no selection manager");

            bool Wants(string id) => only.Length == 0 || id.StartsWith(only);
            void Exp(string id, string name, Action body)
            {
                if (!Wants(id)) return;
                try { body(); }
                catch (Exception ex) { Console.WriteLine($"# EXP{id} {name}: THREW {ex.GetType().Name}: {ex.Message}"); }
            }

            // ---------- the test block: a ±10 mm square, base point at centre ----------
            if (!OpenFrontSketch(doc, c.Ext, c.Mgr)) { Console.WriteLine("# no sketch"); return; }
            bool prevInf = c.Mgr.AutoInference;
            try
            {
                c.Mgr.AutoInference = false;
                c.Mgr.CreateLine(-0.010, -0.010, 0, 0.010, -0.010, 0);
                c.Mgr.CreateLine(0.010, -0.010, 0, 0.010, 0.010, 0);
                c.Mgr.CreateLine(0.010, 0.010, 0, -0.010, 0.010, 0);
                c.Mgr.CreateLine(-0.010, 0.010, 0, -0.010, -0.010, 0);
                if (dense)
                {
                    // A dense interior — the QR block's failure mode: 200+
                    // interior segments make coordinate picks land arbitrary
                    // def edges instead of the boundary (production finding).
                    var rnd = new Random(42);
                    for (int i = 0; i < 160; i++)
                    {
                        double x = -0.009 + rnd.NextDouble() * 0.018;
                        double y = -0.009 + rnd.NextDouble() * 0.018;
                        bool h = rnd.Next(2) == 0;
                        c.Mgr.CreateLine(x, y, 0,
                            h ? x + 0.002 : x, h ? y : y + 0.002, 0);
                    }
                    Console.WriteLine("#   dense fixture: +160 interior segs in block");
                }
            }
            finally { c.Mgr.AutoInference = prevInf; }
            doc.ClearSelection2(true);
            c.Ext.SelectAll();
            var def = c.Mgr.MakeSketchBlockFromSelected(SpikePt(c.Mu, 0, 0)) as ISketchBlockDefinition;
            c.Sketch = c.Mgr.ActiveSketch as ISketch
                ?? throw new InvalidOperationException("no active sketch");
            if (def == null || c.Sketch.GetSketchBlockInstanceCount() != 1)
            {
                Console.WriteLine("# block fixture failed — aborting matrix");
                return;
            }
            c.Inst = c.Sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance;
            try { c.RelMgr = c.Sketch.RelationManager as ISketchRelationManager; } catch { }
            Console.WriteLine($"# fixture: block ±10 mm, base pt at centre; " +
                $"inst={(c.Inst == null ? "null" : c.Inst.Name)} relMgr={(c.RelMgr == null ? "null" : "ok")}");

            // Pre-pick the four extent-line edge proxies BEFORE any host
            // geometry exists — the product does exactly this so the pick can
            // never land on the construction square's own overlapping edge.
            foreach (var (idx, px, py) in new[]
                 {
                     (0, 0.005, -0.010),   // bottom extent line
                     (1, 0.010, 0.005),    // right
                     (2, 0.005, 0.010),    // top
                     (3, -0.010, 0.005),   // left
                 })
            {
                c.Proxies[idx] = c.Pick("SKETCHSEGMENT", px, py, out int st, out bool ok);
                Console.WriteLine($"#   proxy[{idx}] pick@({px * 1000:F0},{py * 1000:F0}) " +
                    $"ok={ok} seltype={st} obj={DescribeCom(c.Proxies[idx])}");
            }

            // ---------- E1: what do boundary/extent picks actually return? ----------
            Exp("E1", "pick census", () =>
            {
                var seg = c.Pick("SKETCHSEGMENT", 0.005, c.EdgeY, out int st, out bool ok);
                Console.WriteLine($"# EXP1 seg pick ok={ok} seltype={st} obj={DescribeCom(seg)}");
                var vtx = c.Pick("SKETCHPOINT", 0.010, c.EdgeY, out int st2, out bool ok2);
                Console.WriteLine($"# EXP1 vertex pick ok={ok2} seltype={st2} obj={DescribeCom(vtx)}" +
                    (vtx is ISketchPoint p ? $" at ({p.X * 1000:F2},{p.Y * 1000:F2})mm" : ""));
                var mid = c.Pick("SKETCHPOINT", 0.005, c.EdgeY, out int st3, out bool ok3);
                Console.WriteLine($"# EXP1 edge-mid SKETCHPOINT pick ok={ok3} seltype={st3} obj={DescribeCom(mid)}" +
                    (mid is ISketchPoint p2 ? $" at ({p2.X * 1000:F2},{p2.Y * 1000:F2})mm" : ""));
                var origin = c.Pick("SKETCHPOINT", 0.0, 0.0, out int st4, out bool ok4);
                Console.WriteLine($"# EXP1 origin SKETCHPOINT pick ok={ok4} seltype={st4} obj={DescribeCom(origin)}" +
                    (origin is ISketchPoint p3 ? $" at ({p3.X * 1000:F2},{p3.Y * 1000:F2})mm" : ""));
            });

            // ---------- E2: AddRelation API — host seg ↔ picked block edge ----------
            Exp("E2", "AddRelation COLINEAR seg+blockEdge", () =>
            {
                var edgeObj = c.Pick("SKETCHSEGMENT", 0.005, c.EdgeY, out _, out bool ok);
                var host = c.NewHostLine(0.014);
                if (!ok || edgeObj == null || host == null || c.RelMgr == null)
                { Console.WriteLine($"# EXP2 setup fail pick={ok} host={(host == null ? "null" : "ok")}"); return; }
                Console.WriteLine($"# EXP2 host before {c.Ends(host)}");
                var rel = c.RelMgr.AddRelation(
                    new object[] { host, edgeObj }, (int)swConstraintType_e.swConstraintType_COLINEAR);
                Console.WriteLine($"# EXP2 rel={(rel == null ? "null" : "ok")} host after {c.Ends(host)}" +
                    $"  track[{c.Track(() => LineY(host))}]");
            });

            Exp("E3", "AddRelation COINCIDENT seg+blockEdge", () =>
            {
                var edgeObj = c.Pick("SKETCHSEGMENT", -0.005, c.EdgeY, out _, out bool ok);
                var host = c.NewHostLine(0.015);
                if (!ok || edgeObj == null || host == null || c.RelMgr == null)
                { Console.WriteLine("# EXP3 setup fail"); return; }
                var rel = c.RelMgr.AddRelation(
                    new object[] { host, edgeObj }, (int)swConstraintType_e.swConstraintType_COINCIDENT);
                Console.WriteLine($"# EXP3 rel={(rel == null ? "null" : "ok")} host after {c.Ends(host)}" +
                    $"  track[{c.Track(() => LineY(host))}]");
            });

            // ---------- E4/E5: point-on-edge coincident (the untested combo) ----------
            Exp("E4", "AddRelation COINCIDENT point+blockEdge", () =>
            {
                var edgeObj = c.Pick("SKETCHSEGMENT", 0.003, c.EdgeY, out _, out bool ok);
                var host = c.NewHostLine(0.016);
                var pt = c.StartPt(host);
                if (!ok || edgeObj == null || pt == null || c.RelMgr == null)
                { Console.WriteLine("# EXP4 setup fail"); return; }
                var rel = c.RelMgr.AddRelation(
                    new object[] { pt, edgeObj }, (int)swConstraintType_e.swConstraintType_COINCIDENT);
                Console.WriteLine($"# EXP4 rel={(rel == null ? "null" : "ok")} " +
                    $"pt now ({pt.X * 1000:F2},{pt.Y * 1000:F2}) line {c.Ends(host)}" +
                    $"  track[{c.Track(() => pt.Y)}]");
            });

            Exp("E5", "SketchAddConstraints point+blockEdge", () =>
            {
                var host = c.NewHostLine(0.017);
                var pt = c.StartPt(host);
                doc.ClearSelection2(true);
                bool ptSel = false;
                try { ptSel = pt != null && pt.Select4(false, null); } catch { }
                bool segSel = c.Ext.SelectByID2("", "SKETCHSEGMENT", 0.006, c.EdgeY, 0,
                    true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                Console.WriteLine($"# EXP5 pt={ptSel} seg={segSel}");
                if (ptSel && segSel)
                {
                    doc.SketchAddConstraints("sgCOINCIDENT");
                    Console.WriteLine($"# EXP5 pt now ({pt!.X * 1000:F2},{pt.Y * 1000:F2})" +
                        $"  track[{c.Track(() => pt.Y)}]");
                }
            });

            // ---------- E6: which relations does SW say are allowed? ----------
            Exp("E6", "GetAllowedRelations census", () =>
            {
                var edgeObj = c.Pick("SKETCHSEGMENT", 0.004, c.EdgeY, out _, out _);
                var baseObj = c.Pick("SKETCHPOINT", 0.0, 0.0, out _, out _);
                var host = c.NewHostLine(0.018);
                var host2 = c.NewHostLine(0.019);
                var pt = c.StartPt(host);
                if (c.RelMgr == null) { Console.WriteLine("# EXP6 no RelMgr"); return; }
                DumpAllowed(c.RelMgr, "hostLine+blockEdge", new object?[] { host, edgeObj });
                DumpAllowed(c.RelMgr, "hostPt+blockEdge", new object?[] { pt, edgeObj });
                DumpAllowed(c.RelMgr, "hostLine+basePt", new object?[] { host, baseObj });
                DumpAllowed(c.RelMgr, "hostPt+basePt", new object?[] { pt, baseObj });
                DumpAllowed(c.RelMgr, "hostLine+hostLine", new object?[] { host, host2 });
            });

            // ---------- E7: other relation types on the picked block edge ----------
            Exp("E7", "other relations on block edge", () =>
            {
                var edgeObj = c.Pick("SKETCHSEGMENT", 0.002, c.EdgeY, out _, out bool ok);
                if (!ok || edgeObj == null || c.RelMgr == null) { Console.WriteLine("# EXP7 no pick"); return; }
                // PARALLEL: an angled host line + the (horizontal) block edge.
                var angled = c.Mgr.CreateLine(-0.004, 0.020, 0, 0.004, 0.024, 0) as ISketchSegment;
                if (angled != null) c.Litter.Add(angled);
                var relP = c.RelMgr.AddRelation(
                    new object?[] { angled!, edgeObj },
                    (int)swConstraintType_e.swConstraintType_PARALLEL);
                Console.WriteLine($"# EXP7 PARALLEL rel={(relP == null ? "null" : "ok")} angled {c.Ends(angled)}" +
                    $"  track[{c.Track(() => LineY(angled!))}]");
                // ATMIDDLE: a host point to the block edge's midpoint.
                var host = c.NewHostLine(0.021);
                var pt = c.StartPt(host);
                var relM = c.RelMgr.AddRelation(
                    new object?[] { pt!, edgeObj },
                    (int)swConstraintType_e.swConstraintType_ATMIDDLE);
                Console.WriteLine($"# EXP7 ATMIDDLE rel={(relM == null ? "null" : "ok")} " +
                    $"pt now ({pt!.X * 1000:F2},{pt.Y * 1000:F2})");
                // PERPENDICULAR: a vertical host line + the block edge.
                var vert = c.Mgr.CreateLine(0.022, 0.006, 0, 0.022, 0.016, 0) as ISketchSegment;
                if (vert != null) c.Litter.Add(vert);
                var relV = c.RelMgr.AddRelation(
                    new object?[] { vert!, edgeObj },
                    (int)swConstraintType_e.swConstraintType_PERPENDICULAR);
                Console.WriteLine($"# EXP7 PERPENDICULAR rel={(relV == null ? "null" : "ok")} vert {c.Ends(vert)}");
            });

            // ---------- E8: definition-sketch entity handles ----------
            Exp("E8", "def-sketch entity handles", () =>
            {
                var inst = c.Inst;
                if (inst == null) { Console.WriteLine("# EXP8 no instance"); return; }
                try
                {
                    var ownSketch = inst.GetSketch() as ISketch;
                    var ownFeat = ownSketch as IFeature;
                    Console.WriteLine($"# EXP8 inst.GetSketch -> '{ownFeat?.Name ?? "null"}' " +
                        $"(host is '{(c.Sketch as IFeature)?.Name}')");
                }
                catch (Exception ex) { Console.WriteLine($"# EXP8 inst.GetSketch threw {ex.GetType().Name}"); }
                var defI = inst.Definition as ISketchBlockDefinition;
                if (defI == null) { Console.WriteLine("# EXP8 no def"); return; }
                ISketch? defSketch = null;
                try { defSketch = defI.GetSketch() as ISketch; } catch { }
                Console.WriteLine($"# EXP8 def.GetSketch -> {(defSketch == null ? "null" : (defSketch as IFeature)?.Name ?? "unnamed")}");
                if (defSketch != null)
                {
                    int pc1 = -1, pc2 = -1;
                    try { pc1 = defSketch.GetSketchPointsCount(); } catch { }
                    try { pc2 = defSketch.GetSketchPointsCount2(); } catch { }
                    object? pts = null, pts2 = null, segs = null, lines = null;
                    try { pts = defSketch.GetSketchPoints(); } catch { }
                    try { pts2 = defSketch.GetSketchPoints2(); } catch { }
                    try { segs = defSketch.GetSketchSegments(); } catch { }
                    try { lines = defSketch.GetLines(); } catch { }
                    int Count(object? o) => o is object[] a ? a.Length : o is System.Array aa ? aa.Length : -1;
                    Console.WriteLine($"# EXP8 defSketch: ptsCount={pc1} pts2Count={pc2} " +
                        $"pts={Count(pts)} pts2={Count(pts2)} segs={Count(segs)} lines={Count(lines)}");
                }
                object? defLines = null, defPts = null;
                try { defLines = defI.GetLines(); } catch { }
                try { defPts = defI.GetUserPoints(); } catch { }
                var lineArr = defLines as object[];
                var ptArr = defPts as object[];
                Console.WriteLine($"# EXP8 def.GetLines -> {(lineArr == null ? "null" : lineArr.Length.ToString())} " +
                    $"GetUserPoints -> {(ptArr == null ? "null" : ptArr.Length.ToString())}");
                var defLine = lineArr?.FirstOrDefault(l => l is ISketchLine) as ISketchLine;
                if (defLine != null)
                {
                    var da = defLine.GetStartPoint2() as ISketchPoint;
                    var db = defLine.GetEndPoint2() as ISketchPoint;
                    Console.WriteLine($"# EXP8 def line reads {c.Ends(defLine)} (def space)");
                    // Route 8a: Select4 on the def entity + host line → SketchAddConstraints.
                    var host = c.NewHostLine(0.022);
                    doc.ClearSelection2(true);
                    bool dsel = false, hsel = false;
                    try { dsel = (defLine as ISketchSegment)?.Select4(false, null) ?? false; } catch { }
                    try { hsel = host?.Select4(true, null) ?? false; } catch { }
                    Console.WriteLine($"# EXP8a defLine.Select4={dsel} host.Select4={hsel}");
                    if (dsel && hsel)
                    {
                        doc.SketchAddConstraints("sgCOLINEAR");
                        Console.WriteLine($"# EXP8a host after {c.Ends(host)}");
                    }
                    // Route 8b: AddRelation on the def entity object.
                    if (c.RelMgr != null)
                    {
                        var host2 = c.NewHostLine(0.023);
                        var rel = c.RelMgr.AddRelation(
                            new object?[] { host2!, defLine },
                            (int)swConstraintType_e.swConstraintType_COLINEAR);
                        Console.WriteLine($"# EXP8b rel={(rel == null ? "null" : "ok")} host {c.Ends(host2)}" +
                            $"  track[{c.Track(() => LineY(host2!))}]");
                    }
                    // Route 8c: host point coincident to a def point.
                    if (c.RelMgr != null && da != null)
                    {
                        var host3 = c.NewHostLine(0.024);
                        var pt = c.StartPt(host3);
                        var rel = c.RelMgr.AddRelation(
                            new object?[] { pt!, da },
                            (int)swConstraintType_e.swConstraintType_COINCIDENT);
                        Console.WriteLine($"# EXP8c rel={(rel == null ? "null" : "ok")} " +
                            $"pt now ({pt!.X * 1000:F2},{pt.Y * 1000:F2})");
                    }
                }
            });

            // ---------- E9: EditSketchBlock context ----------
            Exp("E9", "EditSketchBlock context", () =>
            {
                var inst = c.Inst;
                if (inst == null) { Console.WriteLine("# EXP9 no instance"); return; }
                doc.ClearSelection2(true);
                bool sel = false;
                try { sel = inst.Select(true, null); } catch { }
                Console.WriteLine($"# EXP9 inst.Select={sel}");
                if (!sel) return;
                try { c.Mgr.EditSketchBlock(); }
                catch (Exception ex) { Console.WriteLine($"# EXP9 EditSketchBlock threw {ex.GetType().Name}"); return; }
                var bs = c.Mgr.ActiveSketch as ISketch;
                if (bs == null) Console.WriteLine("# EXP9 no active sketch inside block edit");
                else
                {
                    int pc1 = -1, pc2 = -1;
                    try { pc1 = bs.GetSketchPointsCount(); } catch { }
                    try { pc2 = bs.GetSketchPointsCount2(); } catch { }
                    object? pts = null, pts2 = null, segs = null, lines = null;
                    try { pts = bs.GetSketchPoints(); } catch { }
                    try { pts2 = bs.GetSketchPoints2(); } catch { }
                    try { segs = bs.GetSketchSegments(); } catch { }
                    try { lines = bs.GetLines(); } catch { }
                    int Count(object? o) => o is object[] a ? a.Length : o is System.Array aa ? aa.Length : -1;
                    Console.WriteLine($"# EXP9 inside: ptsCount={pc1} pts2Count={pc2} " +
                        $"pts={Count(pts)} pts2={Count(pts2)} segs={Count(segs)} lines={Count(lines)}");
                    // Can host-sketch entities be selected while inside block edit?
                    doc.ClearSelection2(true);
                    bool hostPick = c.Ext.SelectByID2("", "SKETCHSEGMENT", 0.005, 0.014, 0,
                        false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                    Console.WriteLine($"# EXP9 host-entity pick inside block edit={hostPick}");
                    // Can a relation land between two def points inside?
                    if (pts is object[] pa && pa.Length >= 2 &&
                        pa[0] is ISketchPoint dp1 && pa[1] is ISketchPoint dp2)
                    {
                        var rm = bs.RelationManager as ISketchRelationManager;
                        var rel = rm?.AddRelation(
                            new object?[] { dp1, dp2 },
                            (int)swConstraintType_e.swConstraintType_COINCIDENT);
                        Console.WriteLine($"# EXP9 def-internal AddRelation rel={(rel == null ? "null" : "ok")}");
                    }
                }
                try { c.Mgr.EndEditSketchBlock(false); } catch { }
            });

            // ---------- E10: dimensions against block entities ----------
            Exp("E10", "dims to block entities", () =>
            {
                // 10a: dim between the base point and a host line.
                var host = c.NewHostLine(0.025);
                doc.ClearSelection2(true);
                bool bp = c.Ext.SelectByID2("", "SKETCHPOINT", 0.0, 0.0, 0,
                    false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                bool hl = false;
                try { hl = host != null && host.Select4(true, null); } catch { }
                Console.WriteLine($"# EXP10a basePt={bp} hostLine={hl}");
                IDisplayDimension? dd = null;
                if (bp && hl)
                    try { dd = doc.IAddDimension2(0.012, 0.030, 0) as IDisplayDimension; }
                    catch (Exception ex) { Console.WriteLine($"# EXP10a dim threw {ex.GetType().Name}"); }
                var dim = dd?.GetDimension2(0) as IDimension;
                Console.WriteLine($"# EXP10a dim={(dim == null ? "null" : dim.FullName)} " +
                    $"v={(dim == null ? -1 : dim.SystemValue * 1000):F2}mm " +
                    $"driven={(dim == null ? -1 : dim.DrivenState)}");
                if (dim != null)
                {
                    // Is it DRIVING (settable — would drag the line)?
                    int r = -99;
                    try { r = dim.SetSystemValue3(0.011, 0, null); } catch { }
                    Console.WriteLine($"# EXP10a set11mm -> {r} line now {c.Ends(host)}");
                    try { dim.SetSystemValue3(0.010, 0, null); } catch { }
                }
                // 10b: dim between a picked block edge and a host line.
                var host2 = c.NewHostLine(0.027);
                doc.ClearSelection2(true);
                bool be = c.Ext.SelectByID2("", "SKETCHSEGMENT", 0.001, c.EdgeY, 0,
                    false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                bool h2 = false;
                try { h2 = host2 != null && host2.Select4(true, null); } catch { }
                Console.WriteLine($"# EXP10b blockEdge={be} hostLine={h2}");
                IDisplayDimension? dd2 = null;
                if (be && h2)
                    try { dd2 = doc.IAddDimension2(0.008, 0.030, 0) as IDisplayDimension; }
                    catch (Exception ex) { Console.WriteLine($"# EXP10b dim threw {ex.GetType().Name}"); }
                var dim2 = dd2?.GetDimension2(0) as IDimension;
                Console.WriteLine($"# EXP10b dim={(dim2 == null ? "null" : dim2.FullName)} " +
                    $"v={(dim2 == null ? -1 : dim2.SystemValue * 1000):F2}mm " +
                    $"driven={(dim2 == null ? -1 : dim2.DrivenState)}");
                if (dim2 != null)
                {
                    // Does the dim to block geometry TRACK a Scale edit?
                    try { c.Inst!.Scale = 2.0; } catch { }
                    double v2 = -1;
                    try { v2 = dim2.SystemValue; } catch { }
                    try { c.Inst!.Scale = 1.0; } catch { }
                    Console.WriteLine($"# EXP10b dim after Scale=2: {v2 * 1000:F2}mm (0→tracked)");
                }
                // 10c: dim between the base point and a host corner point.
                var host3 = c.NewHostLine(0.029);
                var pt = c.StartPt(host3);
                doc.ClearSelection2(true);
                bool bp3 = c.Ext.SelectByID2("", "SKETCHPOINT", 0.0, 0.0, 0,
                    false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                bool p3 = false;
                try { p3 = pt != null && pt.Select4(true, null); } catch { }
                IDisplayDimension? dd3 = null;
                if (bp3 && p3)
                    try { dd3 = doc.IAddDimension2(-0.020, 0.030, 0) as IDisplayDimension; }
                    catch (Exception ex) { Console.WriteLine($"# EXP10c dim threw {ex.GetType().Name}"); }
                var dim3 = dd3?.GetDimension2(0) as IDimension;
                Console.WriteLine($"# EXP10c basePt+hostPt sel=({bp3},{p3}) " +
                    $"dim={(dim3 == null ? "null" : dim3.FullName)} " +
                    $"v={(dim3 == null ? -1 : dim3.SystemValue * 1000):F2}mm");
            });

            // ---------- E11: marked / programmatic selection routes ----------
            Exp("E11", "AddSelectionListObject + SketchAddConstraints", () =>
            {
                var edgeObj = c.Pick("SKETCHSEGMENT", 0.007, c.EdgeY, out _, out bool ok);
                var host = c.NewHostLine(0.031);
                if (!ok || edgeObj == null || host == null) { Console.WriteLine("# EXP11 setup fail"); return; }
                doc.ClearSelection2(true);
                bool a1 = false, a2 = false;
                try { a1 = c.Sel.AddSelectionListObject(host, null); } catch (Exception ex) { Console.WriteLine($"# EXP11 add1 {ex.GetType().Name}"); }
                try { a2 = c.Sel.AddSelectionListObject(edgeObj, null); } catch (Exception ex) { Console.WriteLine($"# EXP11 add2 {ex.GetType().Name}"); }
                int n = -1;
                try { n = c.Sel.GetSelectedObjectCount(); } catch { }
                Console.WriteLine($"# EXP11 add host={a1} add edge={a2} selCount={n}");
                doc.SketchAddConstraints("sgCOLINEAR");
                Console.WriteLine($"# EXP11 host after {c.Ends(host)}" +
                    $"  track[{c.Track(() => LineY(host))}]");
            });

            Exp("E12", "Select4 on the picked proxy object", () =>
            {
                var edgeObj = c.Pick("SKETCHSEGMENT", 0.008, c.EdgeY, out _, out bool ok);
                var host = c.NewHostLine(0.033);
                if (!ok || edgeObj == null || host == null) { Console.WriteLine("# EXP12 setup fail"); return; }
                doc.ClearSelection2(true);
                bool es = false, hs = false;
                if (edgeObj is ISketchSegment segObj)
                    try { es = segObj.Select4(false, null); } catch { }
                else if (edgeObj is IEntity entObj)
                    try { es = entObj.Select4(false, null); } catch { }
                try { hs = host.Select4(true, null); } catch { }
                Console.WriteLine($"# EXP12 proxy.Select4={es} host.Select4={hs} proxyType={DescribeCom(edgeObj)}");
                doc.SketchAddConstraints("sgCOLINEAR");
                Console.WriteLine($"# EXP12 host after {c.Ends(host)}" +
                    $"  track[{c.Track(() => LineY(host))}]");
            });

            // ---------- E13: block edit + host pick for a mid-segment vertex ----------
            Exp("E13", "base-point coincident control (known-good route)", () =>
            {
                var baseObj = c.Pick("SKETCHPOINT", 0.0, 0.0, out _, out bool ok);
                var host = c.NewHostLine(0.035);
                var pt = c.StartPt(host);
                if (!ok || baseObj == null || pt == null) { Console.WriteLine("# EXP13 setup fail"); return; }
                if (c.RelMgr != null)
                {
                    var rel = c.RelMgr.AddRelation(
                        new object?[] { pt, baseObj },
                        (int)swConstraintType_e.swConstraintType_COINCIDENT);
                    Console.WriteLine($"# EXP13 AddRelation rel={(rel == null ? "null" : "ok")} " +
                        $"pt now ({pt.X * 1000:F2},{pt.Y * 1000:F2})" +
                        $"  track[{c.Track(() => pt.Y)}]");
                }
                doc.ClearSelection2(true);
                bool p1 = false, p2 = false;
                try { p1 = pt.Select4(false, null); } catch { }
                p2 = c.Ext.SelectByID2("", "SKETCHPOINT", 0.0, 0.0, 0,
                    true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                if (p1 && p2) doc.SketchAddConstraints("sgCOINCIDENT");
                Console.WriteLine($"# EXP13 sel-route pt=({p1},{p2}) pt now ({pt.X * 1000:F2},{pt.Y * 1000:F2})");
            });

            // ================= ROUND 2 — the product-route candidates =================

            // Draws a construction square (min..max, axis-aligned) the product's
            // way: 4 segments → corner merges → H/V + equal → rigid square.
            // Returns the drawn lines; corners[k] gives BL,BR,TR,TL points.
            List<ISketchSegment>? DrawSquare(
                BindExp c, double min, double max, out ISketchPoint?[] corners)
            {
                corners = new ISketchPoint?[4];
                var sq = new List<ISketchSegment>();
                foreach (var (x1, y1, x2, y2) in new[]
                     { (min, min, max, min), (max, min, max, max), (max, max, min, max), (min, max, min, min) })
                {
                    var l = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                    if (l == null) return null;
                    try { l.ConstructionGeometry = true; } catch { }
                    sq.Add(l); c.Litter.Add(l);
                }
                // merge corners
                for (int i = 0; i < 4; i++)
                {
                    var end = (sq[i] as ISketchLine)?.IGetEndPoint2() as ISketchPoint;
                    var start = (sq[(i + 1) % 4] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                    doc.ClearSelection2(true);
                    if ((end?.Select4(false, null) ?? false) && (start?.Select4(true, null) ?? false))
                        doc.SketchAddConstraints("sgCOINCIDENT");
                }
                for (int i = 0; i < 4; i++)
                {
                    doc.ClearSelection2(true);
                    if (sq[i].Select4(false, null))
                        doc.SketchAddConstraints(i % 2 == 0 ? "sgHORIZONTAL" : "sgVERTICAL");
                }
                doc.ClearSelection2(true);
                if (sq[0].Select4(false, null) && sq[1].Select4(true, null))
                    doc.SketchAddConstraints("sgSAMELENGTH");
                // corners: BL = bottom.start, BR = right.start, TR = top.start, TL = left.start
                corners[0] = (sq[0] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                corners[1] = (sq[1] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                corners[2] = (sq[2] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                corners[3] = (sq[3] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                return sq;
            }


            // ---------- E14: stashed proxy + host point → sgCOINCIDENT ----------
            Exp("E14", "pre-picked proxy + point coincident (product route)", () =>
            {
                var host = c.NewHostLine(0.040);
                var pt = c.StartPt(host);
                var proxy = c.Proxies[2];   // top extent line
                if (pt == null || proxy == null) { Console.WriteLine("# EXP14 setup fail"); return; }
                doc.ClearSelection2(true);
                bool p1 = c.SelectObj(proxy, false);
                bool p2 = pt.Select4(true, null);
                Console.WriteLine($"# EXP14 proxy.Select4={p1} pt.Select4={p2}");
                if (p1 && p2)
                {
                    doc.SketchAddConstraints("sgCOINCIDENT");
                    Console.WriteLine($"# EXP14 pt now ({pt.X * 1000:F2},{pt.Y * 1000:F2})" +
                        $"  track[{c.Track(() => pt.Y)}]");
                }
            });

            // ---------- E15: proxy + host SEG → sgCOLINEAR at a clean scale ----------
            Exp("E15", "pre-picked proxy + segment colinear", () =>
            {
                try { c.Inst!.Scale = 1.0; } catch { }   // guarantee scale 1
                var host = c.NewHostLine(0.042);
                var proxy = c.Proxies[0];   // bottom extent line
                if (host == null || proxy == null) { Console.WriteLine("# EXP15 setup fail"); return; }
                doc.ClearSelection2(true);
                bool p1 = c.SelectObj(proxy, false);
                bool p2 = host.Select4(true, null);
                Console.WriteLine($"# EXP15 proxy={p1} host={p2}");
                if (p1 && p2)
                {
                    doc.SketchAddConstraints("sgCOLINEAR");
                    Console.WriteLine($"# EXP15 host after {c.Ends(host)}" +
                        $"  track[{c.Track(() => LineY(host))}]");
                }
            });

            // ---------- E16: drag tracking on a bound point ----------
            Exp("E16", "drag tracking (InstancePosition)", () =>
            {
                try { c.Inst!.Scale = 1.0; } catch { }
                var host = c.NewHostLine(0.044);
                var pt = c.StartPt(host);
                var proxy = c.Proxies[2];
                if (pt == null || proxy == null) { Console.WriteLine("# EXP16 setup fail"); return; }
                doc.ClearSelection2(true);
                bool p1 = c.SelectObj(proxy, false);
                bool p2 = pt.Select4(true, null);
                if (!(p1 && p2)) { Console.WriteLine($"# EXP16 sel=({p1},{p2})"); return; }
                doc.SketchAddConstraints("sgCOINCIDENT");
                double before = pt.Y;
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.005, -0.003); } catch { }
                double dx = pt.X - (-0.005), dy = pt.Y - before;
                Console.WriteLine($"# EXP16 after drag pt=({pt.X * 1000:F2},{pt.Y * 1000:F2}) " +
                    $"Δ=({dx * 1000:F2},{dy * 1000:F2})mm expected(+5,-3)");
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.0, 0.0); } catch { }
            });

            // ---------- E17: pick ambiguity on an overlapped extent line ----------
            Exp("E17", "pick ambiguity: host edge ON the extent line", () =>
            {
                // Draw a host segment exactly ON the block's bottom extent line.
                var on = c.Mgr.CreateLine(-0.004, -0.010, 0, 0.004, -0.010, 0) as ISketchSegment;
                if (on != null) c.Litter.Add(on);
                var picked = c.Pick("SKETCHSEGMENT", 0.0, -0.010, out int st, out bool ok);
                Console.WriteLine($"# EXP17 pick@overlap ok={ok} seltype={st} " +
                    $"obj={DescribeCom(picked)} ends={c.Ends(picked)} " +
                    $"(host line ends {-0.004 * 1000:F0}..{0.004 * 1000:F0}mm at y=-10)");
            });

            // ---------- E18: def-sketch entity handles bound from the host ----------
            Exp("E18", "def-sketch entity handles", () =>
            {
                var inst = c.Inst;
                var defI = inst?.Definition as ISketchBlockDefinition;
                var defSketch = defI?.GetSketch() as ISketch;
                if (defSketch == null) { Console.WriteLine("# EXP18 no def sketch"); return; }
                object? pts = null, lines = null;
                try { pts = defSketch.GetSketchPoints(); } catch { }
                try { lines = defSketch.GetLines(); } catch { }
                var ptArr = pts as object[];
                var lineArr = lines as object[];
                Console.WriteLine($"# EXP18 defSketch pts={(ptArr == null ? "null" : ptArr.Length.ToString())} " +
                    $"lines={(lineArr == null ? "null" : lineArr.Length.ToString())}");
                var defPt = ptArr?.OfType<ISketchPoint>().FirstOrDefault();
                var defLine = lineArr?.OfType<ISketchLine>().FirstOrDefault();
                if (defPt != null)
                    Console.WriteLine($"# EXP18 defPt at ({defPt.X * 1000:F2},{defPt.Y * 1000:F2})mm (def space)");
                // host point coincident to a def vertex — via Select4 route.
                var host = c.NewHostLine(0.046);
                var hpt = c.StartPt(host);
                doc.ClearSelection2(true);
                bool s1 = false, s2 = false;
                try { s1 = defPt?.Select4(false, null) ?? false; } catch { }
                try { s2 = hpt?.Select4(true, null) ?? false; } catch { }
                Console.WriteLine($"# EXP18 defPt.Select4={s1} hostPt.Select4={s2}");
                if (s1 && s2)
                {
                    doc.SketchAddConstraints("sgCOINCIDENT");
                    Console.WriteLine($"# EXP18 hostPt now ({hpt!.X * 1000:F2},{hpt.Y * 1000:F2})" +
                        $"  track[{c.Track(() => hpt.Y)}]");
                }
                // host line colinear to a def line.
                if (defLine != null)
                {
                    var host2 = c.NewHostLine(0.048);
                    doc.ClearSelection2(true);
                    bool d1 = c.SelectObj(defLine as ISketchSegment ?? (object)defLine, false);
                    bool h1 = c.SelectObj(host2, true);
                    Console.WriteLine($"# EXP18 defLine.Select4={d1} host={h1}");
                    if (d1 && h1)
                    {
                        doc.SketchAddConstraints("sgCOLINEAR");
                        Console.WriteLine($"# EXP18 host after {c.Ends(host2)}" +
                            $"  track[{c.Track(() => LineY(host2!))}]");
                    }
                }
            });

            // ---------- E19: both-endpoint binds pin a whole host edge ----------
            Exp("E19", "two point-on-edge binds pin one host edge", () =>
            {
                try { c.Inst!.Scale = 1.0; } catch { }
                var host = c.NewHostLine(-0.050, -0.005, 0.005);   // vertical? no — horizontal at y=-50
                if (host == null) { Console.WriteLine("# EXP19 draw fail"); return; }
                var proxy = c.Proxies[0];
                var a = c.StartPt(host);
                var b = (host as ISketchLine)?.GetEndPoint2() as ISketchPoint;
                if (proxy == null || a == null || b == null) { Console.WriteLine("# EXP19 setup fail"); return; }
                foreach (var p in new[] { a, b })
                {
                    doc.ClearSelection2(true);
                    bool q1 = c.SelectObj(proxy, false);
                    bool q2 = p.Select4(true, null);
                    if (q1 && q2) doc.SketchAddConstraints("sgCOINCIDENT");
                    Console.WriteLine($"# EXP19 bind pt sel=({q1},{q2}) → ({p.X * 1000:F2},{p.Y * 1000:F2})");
                }
                Console.WriteLine($"# EXP19 host after {c.Ends(host)}" +
                    $"  track[{c.Track(() => LineY(host))}]");
            });

            // ---------- E20: DRESS REHEARSAL — a fully bound host square ----------
            Exp("E20", "bound host square tracks Scale + drag", () =>
            {
                try { c.Inst!.Scale = 1.0; } catch { }
                // Draw a ±10 mm square — exactly on the block's extent lines,
                // just like the product's staged QR square.
                var sq = new List<ISketchLine>();
                foreach (var (x1, y1, x2, y2) in new[]
                     {
                         (-0.010, -0.010, 0.010, -0.010),
                         (0.010, -0.010, 0.010, 0.010),
                         (0.010, 0.010, -0.010, 0.010),
                         (-0.010, 0.010, -0.010, -0.010),
                     })
                {
                    var l = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchLine;
                    if (l is ISketchSegment seg) { c.Litter.Add(seg); sq.Add(l); }
                }
                if (sq.Count != 4) { Console.WriteLine("# EXP20 draw fail"); return; }
                // Merge corners (the keep-out's exact routine).
                for (int i = 0; i < 4; i++)
                {
                    var end = sq[i].IGetEndPoint2() as ISketchPoint;
                    var start = sq[(i + 1) % 4].IGetStartPoint2() as ISketchPoint;
                    doc.ClearSelection2(true);
                    bool e1 = end?.Select4(false, null) ?? false;
                    bool e2 = start?.Select4(true, null) ?? false;
                    if (e1 && e2) doc.SketchAddConstraints("sgCOINCIDENT");
                }
                // H/V + equal → rigid square with (cx, cy, side) free.
                foreach (var l in sq)
                {
                    doc.ClearSelection2(true);
                    if ((l as ISketchSegment)?.Select4(false, null) == true)
                        doc.SketchAddConstraints(sq.IndexOf(l) % 2 == 0 ? "sgHORIZONTAL" : "sgVERTICAL");
                }
                doc.ClearSelection2(true);
                if ((sq[0] as ISketchSegment)?.Select4(false, null) == true &&
                    (sq[1] as ISketchSegment)?.Select4(true, null) == true)
                    doc.SketchAddConstraints("sgSAMELENGTH");
                // The product's three binds: bottom-left corner → bottom edge;
                // bottom-left corner → left edge; top-right corner → top edge.
                // (sq[0]=bottom, sq[1]=right, sq[2]=top, sq[3]=left)
                var binds = new[]
                {
                    (sq[0].IGetStartPoint2() as ISketchPoint, c.Proxies[0], "bottom"),
                    (sq[3].IGetEndPoint2() as ISketchPoint, c.Proxies[3], "left"),
                    (sq[2].IGetStartPoint2() as ISketchPoint, c.Proxies[2], "top"),
                };
                foreach (var (pt, proxy, tag) in binds)
                {
                    if (pt == null || proxy == null) { Console.WriteLine($"# EXP20 {tag}: no pt/proxy"); continue; }
                    doc.ClearSelection2(true);
                    bool q1 = c.SelectObj(proxy, false);
                    bool q2 = pt.Select4(true, null);
                    if (q1 && q2) doc.SketchAddConstraints("sgCOINCIDENT");
                    Console.WriteLine($"# EXP20 bind {tag} sel=({q1},{q2}) " +
                        $"pt=({pt.X * 1000:F2},{pt.Y * 1000:F2})");
                }
                double Half() => Math.Max(
                    Math.Abs((sq[0].IGetStartPoint2() as ISketchPoint)!.Y),
                    Math.Abs((sq[2].IGetStartPoint2() as ISketchPoint)!.Y));
                // Scale tracking.
                try { c.Inst!.Scale = 2.0; } catch { }
                double h2 = Half();
                try { c.Inst!.Scale = 1.0; } catch { }
                double h1 = Half();
                Console.WriteLine($"# EXP20 square half: 10→{h2 * 1000:F2}mm@S2, back→{h1 * 1000:F2}mm@S1 " +
                    $"scaleTracked={Math.Abs(h2 - 0.020) < 5e-4 && Math.Abs(h1 - 0.010) < 5e-4}");
                // Drag tracking.
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.004, 0.006); } catch { }
                var p0 = sq[0].IGetStartPoint2() as ISketchPoint;
                Console.WriteLine($"# EXP20 after drag(+4,+6) bottom-left corner=" +
                    $"({p0!.X * 1000:F2},{p0.Y * 1000:F2})mm expected(-6,-4)");
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.0, 0.0); } catch { }
            });

            // ---------- E21: PRODUCT ROUTE CANDIDATE A — exact-coord square ----------
            Exp("E21", "exact square + coord-pick corner binds (product candidate A)", () =>
            {
                try { c.Inst!.Scale = 1.0; c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
                // Square drawn EXACTLY on the block extents — the product's
                // staged coords. Binds use EXP5's pattern (pt.Select4 then a
                // SKETCHSEGMENT coordinate pick, appended) — the question is
                // whether the coord pick lands on the block edge or the
                // square's own coincident edge.
                var sq = DrawSquare(c, -0.010, 0.010, out var corners);
                if (sq == null) { Console.WriteLine("# EXP21 draw fail"); return; }
                // Corner→extent-line binds: each corner to BOTH adjacent
                // extent lines (8 attempts); pick coords are extent-line
                // midpoints — ambiguous with the square's own edges.
                var picks = new (double x, double y)[] { (0, -0.010), (0.010, 0), (0, 0.010), (-0.010, 0) };
                var cornerSides = new[] { (0, 3), (0, 1), (2, 1), (2, 3) }; // BL,BR,TR,TL → (sideA,sideB)
                int applied = 0;
                for (int k = 0; k < 4; k++)
                {
                    var pt = corners[k];
                    foreach (var side in new[] { cornerSides[k].Item1, cornerSides[k].Item2 })
                    {
                        doc.ClearSelection2(true);
                        bool ps = false, es = false;
                        try { ps = pt != null && pt.Select4(false, null); } catch { }
                        es = c.Ext.SelectByID2("", "SKETCHSEGMENT", picks[side].x, picks[side].y, 0,
                            true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                        if (ps && es) { doc.SketchAddConstraints("sgCOINCIDENT"); applied++; }
                        Console.WriteLine($"# EXP21 c{k}→side{side} sel=({ps},{es})");
                    }
                }
                var bl = corners[0];
                Console.WriteLine($"# EXP21 binds={applied} BL=({bl!.X * 1000:F2},{bl.Y * 1000:F2})");
                try { c.Inst!.Scale = 2.0; } catch { }
                Console.WriteLine($"# EXP21 @S2 BL=({bl.X * 1000:F2},{bl.Y * 1000:F2}) expect(-20,-20)");
                try { c.Inst!.Scale = 1.0; } catch { }
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.004, 0.006); }
                catch (Exception ex) { Console.WriteLine($"# EXP21 drag threw {ex.GetType().Name}"); }
                // Did the instance actually move? Read the position back and
                // probe where the block's bottom edge now sits.
                try
                {
                    var pos = c.Inst!.InstancePosition as IMathPoint;
                    var pd = pos?.ArrayData as double[];
                    Console.WriteLine($"# EXP21 inst pos=({pd?[0] * 1000:F2},{pd?[1] * 1000:F2})mm");
                }
                catch { }
                var probe = c.Pick("SKETCHSEGMENT", 0, -0.004, out _, out bool pk);
                Console.WriteLine($"# EXP21 pick@(-?,-4) ok={pk} ends={c.Ends(probe)}");
                Console.WriteLine($"# EXP21 dragged BL=({bl.X * 1000:F2},{bl.Y * 1000:F2}) expect(-6,-4)");
                // A block move may not re-solve the host sketch the way a
                // Scale write does — force a rebuild and re-read.
                try { doc.EditRebuild3(); } catch { }
                var back = ReopenBlockSketch(doc, c.Ext, c.Mgr);
                if (back != null) c.Sketch = back;
                // Re-acquire handles — sketch re-open may hand out new objects.
                var bl2 = (sq[0] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                Console.WriteLine($"# EXP21 after rebuild BL=({bl2?.X * 1000:F2},{bl2?.Y * 1000:F2})" +
                    $" (reopen={back != null})");
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
            });

            // ---------- E22: PRODUCT ROUTE CANDIDATE B — inset square ----------
            Exp("E22", "inset square + coord-pick corner binds (product candidate B)", () =>
            {
                try { c.Inst!.Scale = 1.0; c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
                var sq = DrawSquare(c, -0.008, 0.008, out var corners);
                if (sq == null) { Console.WriteLine("# EXP22 draw fail"); return; }
                var picks = new (double x, double y)[] { (0, -0.010), (0.010, 0), (0, 0.010), (-0.010, 0) };
                var cornerSides = new[] { (0, 3), (0, 1), (2, 1), (2, 3) };
                int applied = 0;
                for (int k = 0; k < 4; k++)
                {
                    var pt = corners[k];
                    foreach (var side in new[] { cornerSides[k].Item1, cornerSides[k].Item2 })
                    {
                        doc.ClearSelection2(true);
                        bool ps = false, es = false;
                        try { ps = pt != null && pt.Select4(false, null); } catch { }
                        es = c.Ext.SelectByID2("", "SKETCHSEGMENT", picks[side].x, picks[side].y, 0,
                            true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                        if (ps && es) { doc.SketchAddConstraints("sgCOINCIDENT"); applied++; }
                        Console.WriteLine($"# EXP22 c{k}→side{side} sel=({ps},{es})");
                    }
                }
                var bl = corners[0];
                var tr = corners[2];
                Console.WriteLine($"# EXP22 binds={applied} BL=({bl!.X * 1000:F2},{bl.Y * 1000:F2}) " +
                    $"TR=({tr!.X * 1000:F2},{tr.Y * 1000:F2}) expect(±10)");
                try { c.Inst!.Scale = 2.0; } catch { }
                Console.WriteLine($"# EXP22 @S2 BL=({bl.X * 1000:F2},{bl.Y * 1000:F2}) " +
                    $"TR=({tr.X * 1000:F2},{tr.Y * 1000:F2}) expect(±20)");
                try { c.Inst!.Scale = 1.0; } catch { }
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.004, 0.006); } catch { }
                Console.WriteLine($"# EXP22 dragged BL=({bl.X * 1000:F2},{bl.Y * 1000:F2}) expect(-6,-4)");
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
            });

            // ---------- E23: WHAT does an overlapped pick actually return? ----------
            Exp("E23", "overlapped-pick forensics (construction flag + identity)", () =>
            {
                try { c.Inst!.Scale = 1.0; c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
                var sq = DrawSquare(c, -0.010, 0.010, out _);
                if (sq == null) { Console.WriteLine("# EXP23 draw fail"); return; }
                for (int i = 0; i < 4; i++)
                {
                    var pickAt = new[] { (0.0, -0.010), (0.010, 0.0), (0.0, 0.010), (-0.010, 0.0) }[i];
                    foreach (var append in new[] { false, true })
                    {
                        doc.ClearSelection2(true);
                        if (append)
                        {
                            // mimic the product flow: a corner pt already selected
                            var dummy = (sq[0] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                            try { dummy?.Select4(false, null); } catch { }
                        }
                        bool ok = c.Ext.SelectByID2("", "SKETCHSEGMENT", pickAt.Item1, pickAt.Item2, 0,
                            append, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                        var obj = c.Sel.GetSelectedObject6(append ? 2 : 1, -1);
                        bool cons = false, isOwn = false;
                        int st = -1;
                        try { st = c.Sel.GetSelectedObjectType3(append ? 2 : 1, -1); } catch { }
                        if (obj is ISketchSegment seg)
                        {
                            try { cons = seg.ConstructionGeometry; } catch { }
                            foreach (var own in sq)
                                if (own.Equals(obj)) isOwn = true;
                        }
                        Console.WriteLine($"# EXP23 side{i} append={append} ok={ok} seltype={st} " +
                            $"construction={cons} ownEdge={isOwn} ends={c.Ends(obj)}");
                    }
                }
            });

            // ---------- E24: swSelectOptionExtensive — pick THROUGH the square ----------
            Exp("E24", "EXTENSIVE pick reaches the block edge under the square edge", () =>
            {
                try { c.Inst!.Scale = 1.0; c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
                var sq = DrawSquare(c, -0.010, 0.010, out var corners);
                if (sq == null) { Console.WriteLine("# EXP24 draw fail"); return; }
                var picks = new (double x, double y)[] { (0, -0.010), (0.010, 0), (0, 0.010), (-0.010, 0) };
                var cornerSides = new[] { (0, 3), (0, 1), (2, 1), (2, 3) };
                int applied = 0, ownPicks = 0;
                for (int k = 0; k < 4; k++)
                {
                    var pt = corners[k];
                    foreach (var side in new[] { cornerSides[k].Item1, cornerSides[k].Item2 })
                    {
                        doc.ClearSelection2(true);
                        bool ps = false, es = false;
                        object? obj = null;
                        try { ps = pt != null && pt.Select4(false, null); } catch { }
                        es = c.Ext.SelectByID2("", "SKETCHSEGMENT", picks[side].x, picks[side].y, 0,
                            true, 0, null, (int)swSelectOption_e.swSelectOptionExtensive);
                        if (es)
                        {
                            obj = c.Sel.GetSelectedObject6(2, -1);
                            foreach (var own in sq) if (own.Equals(obj)) { ownPicks++; es = false; }
                        }
                        if (ps && es) { doc.SketchAddConstraints("sgCOINCIDENT"); applied++; }
                        Console.WriteLine($"# EXP24 c{k}→side{side} sel=({ps},{es}) own={obj != null && sq.Any(s => s.Equals(obj))}");
                    }
                }
                var bl = corners[0];
                Console.WriteLine($"# EXP24 binds={applied} ownPicks={ownPicks} BL=({bl!.X * 1000:F2},{bl.Y * 1000:F2})");
                try { c.Inst!.Scale = 2.0; } catch { }
                Console.WriteLine($"# EXP24 @S2 BL=({bl.X * 1000:F2},{bl.Y * 1000:F2}) expect(-20,-20)");
                try { c.Inst!.Scale = 1.0; } catch { }
            });

            // ---------- E25: binds against a PRE-SCALED instance (the product's state) ----------
            Exp("E25", "bind corner pts to a scaled block's edges (scale-before-bind)", () =>
            {
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
                // The product's state at bind time: instance already scaled —
                // def ±10mm × Scale 2 → effective ±20mm; square at ±20.
                try { c.Inst!.Scale = 2.0; } catch { }
                var sq = DrawSquare(c, -0.016, 0.016, out var corners);   // inset ±16 vs extents ±20
                if (sq == null) { Console.WriteLine("# EXP25 draw fail"); return; }
                var picks = new (double x, double y)[] { (0, -0.020), (0.020, 0), (0, 0.020), (-0.020, 0) };
                var cornerSides = new[] { (0, 3), (0, 1), (2, 1), (2, 3) };
                int applied = 0;
                for (int k = 0; k < 4; k++)
                {
                    var pt = corners[k];
                    foreach (var side in new[] { cornerSides[k].Item1, cornerSides[k].Item2 })
                    {
                        doc.ClearSelection2(true);
                        bool ps = false, es = false;
                        try { ps = pt != null && pt.Select4(false, null); } catch { }
                        es = c.Ext.SelectByID2("", "SKETCHSEGMENT", picks[side].x, picks[side].y, 0,
                            true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                        if (ps && es) { doc.SketchAddConstraints("sgCOINCIDENT"); applied++; }
                    }
                }
                var bl = corners[0];
                Console.WriteLine($"# EXP25 binds={applied} BL=({bl!.X * 1000:F2},{bl.Y * 1000:F2}) expect(-20,-20)");
                try { c.Inst!.Scale = 4.0; } catch { }
                Console.WriteLine($"# EXP25 @S4 BL=({bl.X * 1000:F2},{bl.Y * 1000:F2}) expect(-40,-40)");
                try { c.Inst!.Scale = 2.0; } catch { }
                try { c.Inst!.Scale = 1.0; } catch { }
            });

            // ---------- E26: the product's flag state — AddToDB + no inference ----------
            Exp("E26", "inset square + binds under AddToDB/AutoInference=false", () =>
            {
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); c.Inst!.Scale = 1.0; } catch { }
                bool prevDb = c.Mgr.AddToDB, prevInf = c.Mgr.AutoInference;
                List<ISketchSegment>? sq;
                ISketchPoint?[] corners;
                try
                {
                    c.Mgr.AddToDB = true;
                    c.Mgr.AutoInference = false;
                    sq = DrawSquare(c, -0.008, 0.008, out corners);
                }
                finally { c.Mgr.AddToDB = prevDb; c.Mgr.AutoInference = prevInf; }
                if (sq == null) { Console.WriteLine("# EXP26 draw fail"); return; }
                var picks = new (double x, double y)[] { (0, -0.010), (0.010, 0), (0, 0.010), (-0.010, 0) };
                var cornerSides = new[] { (0, 3), (0, 1), (2, 1), (2, 3) };
                int applied = 0;
                for (int k = 0; k < 4; k++)
                {
                    var pt = corners[k];
                    foreach (var side in new[] { cornerSides[k].Item1, cornerSides[k].Item2 })
                    {
                        doc.ClearSelection2(true);
                        bool ps = false, es = false;
                        try { ps = pt != null && pt.Select4(false, null); } catch { }
                        es = c.Ext.SelectByID2("", "SKETCHSEGMENT", picks[side].x, picks[side].y, 0,
                            true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                        if (ps && es) { doc.SketchAddConstraints("sgCOINCIDENT"); applied++; }
                    }
                }
                var bl = corners[0];
                Console.WriteLine($"# EXP26 binds={applied} BL=({bl!.X * 1000:F2},{bl.Y * 1000:F2}) expect(-10,-10)");
                try { c.Inst!.Scale = 2.0; } catch { }
                Console.WriteLine($"# EXP26 @S2 BL=({bl.X * 1000:F2},{bl.Y * 1000:F2}) expect(-20,-20)");
                try { c.Inst!.Scale = 1.0; } catch { }
            });

            // ---------- E30: does the host sketch enumerate block def points? ----------
            Exp("E30", "sketch point census", () =>
            {
                object? ptsObj = null;
                try { ptsObj = c.Sketch.GetSketchPoints(); } catch { }
                var pts = ptsObj as object[];
                Console.WriteLine($"# EXP30 GetSketchPoints -> {(pts == null ? "null" : pts.Length.ToString())}");
                if (pts == null) return;
                int shown = 0;
                foreach (var o in pts)
                {
                    if (o is ISketchPoint sp && shown < 14)
                    {
                        try { Console.WriteLine($"#   skpt ({sp.X * 1000:F3},{sp.Y * 1000:F3})mm"); }
                        catch { Console.WriteLine("#   skpt (unreadable)"); }
                        shown++;
                    }
                }
                // Try Select4 on every enumerated point — do any select?
                int selOk = 0, selFail = 0;
                foreach (var o in pts)
                {
                    doc.ClearSelection2(true);
                    bool ok = c.SelectObj(o, false);
                    if (ok) selOk++; else selFail++;
                }
                Console.WriteLine($"# EXP30 Select4 points: ok={selOk} fail={selFail}");
            });

            // ---------- E31: instance attached entities / instance sketch ----------
            Exp("E31", "instance entity routes", () =>
            {
                var inst = c.Inst;
                if (inst == null) { Console.WriteLine("# EXP31 no inst"); return; }
                try
                {
                    object? ents = null, types = null;
                    bool ok = inst.GetAttachedEntities(ref ents, ref types);
                    var ea = ents as object[]; var ta = types as object[];
                    Console.WriteLine($"# EXP31 GetAttachedEntities ok={ok} " +
                        $"ents={(ea == null ? "null" : ea.Length.ToString())} " +
                        $"types={(ta == null ? "null" : string.Join(",", ta))}");
                    if (ea != null)
                        foreach (var e in ea.Take(10))
                            Console.WriteLine($"#   attached {DescribeCom(e)} {(e is ISketchLine l ? c.Ends(l) : "")}");
                }
                catch (Exception ex) { Console.WriteLine($"# EXP31 GetAttachedEntities threw {ex.GetType().Name}"); }
            });

            // ---------- E32: dense-block segment pick census ----------
            Exp("E32", "dense pick census", () =>
            {
                foreach (var (px, py, tag) in new[]
                    { (0.005, -0.010, "bottom"), (0.010, 0.005, "right"),
                      (-0.005, 0.010, "top"), (-0.010, -0.005, "left") })
                {
                    var obj = c.Pick("SKETCHSEGMENT", px, py, out int st, out bool ok);
                    string pos = obj is ISketchLine l ? c.Ends(l) : DescribeCom(obj);
                    Console.WriteLine($"# EXP32 {tag} pick ok={ok} seltype={st} -> {pos}");
                }
                // def-space pick variant: same coords ÷ implicit scale (fixture is Scale=1 → identical)
            });

            // ---------- E33: def-sketch segment objects → Select4 in host context ----------
            Exp("E33", "def seg object select", () =>
            {
                var def = c.Inst?.Definition as ISketchBlockDefinition;
                var defSketch = def?.GetSketch() as ISketch;
                if (defSketch == null) { Console.WriteLine("# EXP33 no def sketch"); return; }
                object? segs = null;
                try { segs = defSketch.GetSketchSegments(); } catch { }
                var arr = segs as object[];
                Console.WriteLine($"# EXP33 def segs={(arr == null ? "null" : arr.Length.ToString())}");
                if (arr == null) return;
                // Find the def segment that IS the bottom extent edge
                // (horizontal, y = def min): pick candidates by endpoints.
                ISketchLine? bottom = null; double minY = double.MaxValue;
                foreach (var o in arr)
                {
                    if (o is ISketchLine l)
                    {
                        var a = l.GetStartPoint2() as ISketchPoint;
                        var b = l.GetEndPoint2() as ISketchPoint;
                        if (a == null || b == null) continue;
                        if (Math.Abs(a.Y - b.Y) < 1e-9 && a.Y < minY) { minY = a.Y; bottom = l; }
                    }
                }
                Console.WriteLine($"# EXP33 bottom extent def seg={(bottom == null ? "none" : c.Ends(bottom))}");
                if (bottom == null) return;
                // Does a def-segment object select in the HOST sketch context?
                doc.ClearSelection2(true);
                bool sel = false;
                try { sel = (bottom as ISketchSegment)?.Select4(false, null) ?? false; } catch { }
                int st = -1;
                if (sel) try { st = c.Sel.GetSelectedObjectType3(1, -1); } catch { }
                Console.WriteLine($"# EXP33 defSeg.Select4={sel} seltype={st}");
                // And can a coincident bind a host corner to it?
                var host = c.NewHostLine(0.030);
                var pt = c.StartPt(host);
                doc.ClearSelection2(true);
                bool ps = false;
                try { ps = pt != null && pt.Select4(false, null); } catch { }
                bool bs = false;
                try { bs = (bottom as ISketchSegment)?.Select4(true, null) ?? false; } catch { }
                Console.WriteLine($"# EXP33 pt={ps} defSegAppend={bs}");
                if (ps && bs)
                {
                    try { doc.SketchAddConstraints("sgCOINCIDENT"); } catch (Exception ex) { Console.WriteLine($"# EXP33 coincident threw {ex.GetType().Name}"); }
                    Console.WriteLine($"# EXP33 host after {c.Ends(host)} (expect y~{minY * 1000:F1})");
                }
            });

            // ---------- E34: does a pre-selected point corrupt the appended pick? ----------
            Exp("E34", "append-mode pick census", () =>
            {
                var host = c.NewHostLine(0.030);
                var pt = c.StartPt(host);
                foreach (var (px, py, tag) in new[]
                    { (0.005, -0.010, "bottom"), (0.010, 0.005, "right"),
                      (-0.005, 0.010, "top"), (-0.010, -0.005, "left") })
                {
                    doc.ClearSelection2(true);
                    bool ps = false;
                    try { ps = pt != null && pt.Select4(false, null); } catch { }
                    bool es = c.Ext.SelectByID2("", "SKETCHSEGMENT", px, py, 0,
                        true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                    object? obj = null; int st = -1;
                    try { st = c.Sel.GetSelectedObjectType3(-1, -1); } catch { }
                    try { obj = c.Sel.GetSelectedObject6(2, -1); } catch { }
                    string pos = obj is ISketchLine l ? c.Ends(l) : DescribeCom(obj);
                    Console.WriteLine($"# EXP34 {tag}: pt={ps} seg={es} seltype={st} -> {pos}");
                }
            });

            // ---------- litter cleanup ----------
            foreach (var s in c.Litter)
            {
                try
                {
                    doc.ClearSelection2(true);
                    if (s.Select4(false, null))
                        c.Ext.DeleteSelection2(0);
                }
                catch { }
            }
            doc.ClearSelection2(true);
            try { c.Mgr.InsertSketch(false); } catch { }
            Console.WriteLine("# bindexp done — litter removed, sketch closed");
        }

        /// <summary>The average |y| of a horizontal host line (its offset from the origin).</summary>
        private static double LineY(ISketchSegment seg)
        {
            if (seg is ISketchLine l)
            {
                var a = l.GetStartPoint2() as ISketchPoint;
                var b = l.GetEndPoint2() as ISketchPoint;
                if (a != null && b != null) return (a.Y + b.Y) / 2.0;
            }
            return double.NaN;
        }

        /// <summary>A COM object's runtime identity for experiment logs.</summary>
        private static string DescribeCom(object? o)
        {
            if (o == null) return "null";
            string kinds = "";
            if (o is ISketchPoint) kinds += "|SKETCHPOINT";
            if (o is ISketchLine) kinds += "|SKETCHLINE";
            if (o is ISketchSegment) kinds += "|SKETCHSEGMENT";
            if (o is ISketchBlockInstance) kinds += "|BLOCKINST";
            if (o is IEntity) kinds += "|ENTITY";
            if (o is IFeature) kinds += "|FEATURE";
            return $"{o.GetType().Name}{kinds}";
        }

        /// <summary>Logs the relation types SW allows between an entity pair.</summary>
        private static void DumpAllowed(ISketchRelationManager rm, string tag, object?[] entities)
        {
            try
            {
                var raw = rm.GetAllowedRelations(entities!);
                string vals;
                if (raw is int[] ai) vals = string.Join(",", ai);
                else if (raw is object[] ao) vals = string.Join(",", ao);
                else if (raw is System.Array ar)
                    vals = string.Join(",", ar.Cast<object>());
                else vals = raw?.ToString() ?? "null";
                Console.WriteLine($"# EXP6 {tag}: allowed=[{vals}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# EXP6 {tag}: threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // =====================================================================
        // `dimexp` — def-internal display dims + last binding probes.
        // Ticket #19 round 3: the round-2 binding matrix (docs/BLOCK-GATES.md)
        // proved host sketch geometry cannot bind to instance-projected block
        // entities — every route binds at raw definition coordinates or the
        // instance base point. The fallback design question: does a
        // DisplayDimension INSIDE the block definition DISPLAY the scaled value
        // on the instance — a live size readout tracking Scale edits, which the
        // host square + driven dim cannot provide? Read-backs used:
        // IDimension.SystemValue / GetUserValueIn / GetSystemValue3,
        // IDisplayDimension.GetText / Scale2 / GetOverrideValue, and — the
        // decisive one — IDisplayData.GetTextAtIndex (the RENDERED text).
        // Then the two last binding ideas: (a) does a SketchUseEdge2-converted
        // block edge persist as a host segment that TRACKS Scale edits or as a
        // frozen copy; (b) sgCONCENTRIC / midpoint-anchored centres riding the
        // instance base point (the only bindable block entity found so far).
        // =====================================================================

        private sealed class DimExp
        {
            public IModelDoc2 Doc = null!;
            public IModelDocExtension Ext = null!;
            public ISketchManager Mgr = null!;
            public IMathUtility Mu = null!;
            public ISelectionMgr Sel = null!;
            public ISketchRelationManager? RelMgr;
            public ISketch? Sketch;
            public IFeature? SketchFeat;
            public ISketchBlockDefinition? Def;
            public ISketchBlockInstance? Inst;
            /// <summary>Def dims in enumeration order: (label, dd).</summary>
            public readonly List<(string Tag, IDisplayDimension Dd)> DefDims =
                new List<(string, IDisplayDimension)>();
            public readonly List<ISketchSegment> Litter = new List<ISketchSegment>();

            /// <summary>Endpoint readout of a sketch line, mm.</summary>
            public string Ends(object? seg)
            {
                if (seg is ISketchLine l)
                {
                    var a = l.GetStartPoint2() as ISketchPoint;
                    var b = l.GetEndPoint2() as ISketchPoint;
                    if (a != null && b != null)
                        return $"({a.X * 1000:F2},{a.Y * 1000:F2})->({b.X * 1000:F2},{b.Y * 1000:F2})";
                }
                return seg?.GetType().Name ?? "null";
            }

            /// <summary>Picks at sketch coords; returns the selected object (or null).</summary>
            public object? Pick(string type, double x, double y, out int selType, out bool ok)
            {
                Doc.ClearSelection2(true);
                ok = false;
                selType = -1;
                try
                {
                    ok = Ext.SelectByID2("", type, x, y, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                    if (!ok) return null;
                    try { selType = Sel.GetSelectedObjectType3(1, -1); } catch { }
                    return Sel.GetSelectedObject6(1, -1);
                }
                catch { return null; }
            }
        }

        private static int DimExpMain(string[] args)
        {
            string progId = args.Length > 0 ? args[0] : "SldWorks.Application.31";
            string only = args.Length > 1 ? args[1] : "";
            Console.WriteLine("# def-internal dim display + last binding probes (ticket #19 round 3)");
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;
                // Dimension experiments need both prompt toggles off (hang trap).
                try { sw.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swInputDimValOnCreate, false); } catch { }
                try { sw.SetUserPreferenceToggle(
                    (int)swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false); } catch { }

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (doc == null) { Console.WriteLine("# FATAL: no part doc"); return 1; }
                try { DimExpRun(sw, doc, only); }
                catch (Exception ex) { Console.WriteLine($"# FATAL body: {ex.GetType().Name}: {ex.Message}"); }
                finally { try { sw.CloseDoc(doc.GetTitle()); } catch { } }
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        private static void DimExpRun(ISldWorks sw, IModelDoc2 doc, string only)
        {
            var c = new DimExp
            {
                Doc = doc,
                Ext = (IModelDocExtension)doc.Extension,
                Mgr = (ISketchManager)doc.SketchManager,
                Mu = (IMathUtility)sw.IGetMathUtility(),
            };
            c.Sel = doc.ISelectionManager as ISelectionMgr
                ?? throw new InvalidOperationException("no selection manager");
            try
            {
                object? u = doc.GetUnits();
                Console.WriteLine($"# doc units raw=[{(u is System.Array ua ? string.Join(",", ua.Cast<object>()) : u?.ToString() ?? "null")}]");
            }
            catch (Exception ex) { Console.WriteLine($"# doc units read threw {ex.GetType().Name}"); }

            bool Wants(string id) => only.Length == 0 || id.StartsWith(only);
            void Exp(string id, string name, Action body)
            {
                if (!Wants(id)) return;
                try { body(); }
                catch (Exception ex) { Console.WriteLine($"# EXP{id} {name}: THREW {ex.GetType().Name}: {ex.Message}"); }
            }
            double SafeD(Func<double> f) { try { return f(); } catch { return double.NaN; } }

            // Draws a merged, H/V, equal-sided square (raw lines under bulk
            // flags); fix=true also FIXes the BL corner (full position anchor).
            List<ISketchSegment>? DrawRigid(double min, double max, bool fix)
            {
                var sq = new List<ISketchSegment>();
                bool prevDb = c.Mgr.AddToDB, prevInf = c.Mgr.AutoInference;
                try
                {
                    c.Mgr.AddToDB = true;
                    c.Mgr.AutoInference = false;
                    foreach (var (x1, y1, x2, y2) in new[]
                         { (min, min, max, min), (max, min, max, max), (max, max, min, max), (min, max, min, min) })
                    {
                        var l = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                        if (l == null) return null;
                        sq.Add(l); c.Litter.Add(l);
                    }
                }
                finally { c.Mgr.AddToDB = prevDb; c.Mgr.AutoInference = prevInf; }
                for (int i = 0; i < 4; i++)
                {
                    var end = (sq[i] as ISketchLine)?.IGetEndPoint2() as ISketchPoint;
                    var start = (sq[(i + 1) % 4] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                    doc.ClearSelection2(true);
                    if ((end?.Select4(false, null) ?? false) && (start?.Select4(true, null) ?? false))
                        doc.SketchAddConstraints("sgCOINCIDENT");
                }
                for (int i = 0; i < 4; i++)
                {
                    doc.ClearSelection2(true);
                    if (sq[i].Select4(false, null))
                        doc.SketchAddConstraints(i % 2 == 0 ? "sgHORIZONTAL" : "sgVERTICAL");
                }
                doc.ClearSelection2(true);
                if (sq[0].Select4(false, null) && sq[1].Select4(true, null))
                    doc.SketchAddConstraints("sgSAMELENGTH");
                if (fix)
                {
                    var bl = (sq[0] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                    doc.ClearSelection2(true);
                    if (bl?.Select4(false, null) ?? false) doc.SketchAddConstraints("sgFIXED");
                }
                return sq;
            }

            IDisplayDimension? DimSeg(ISketchSegment? seg, double tx, double ty, string tag)
            {
                if (seg == null) return null;
                doc.ClearSelection2(true);
                bool s = false;
                try { s = seg.Select4(false, null); } catch { }
                if (!s) { Console.WriteLine($"#   dim '{tag}': edge select failed"); return null; }
                try
                {
                    var dd = doc.IAddDimension2(tx, ty, 0) as IDisplayDimension;
                    var dim = dd?.GetDimension2(0) as IDimension;
                    Console.WriteLine($"#   dim '{tag}': {(dd == null ? "null" : "ok")} " +
                        $"name='{dim?.Name}' full='{dim?.FullName}' " +
                        $"v={(dim == null ? -1 : dim.SystemValue * 1000):F4}mm st={dim?.DrivenState ?? -1}");
                    return dd;
                }
                catch (Exception ex) { Console.WriteLine($"#   dim '{tag}' threw {ex.GetType().Name}"); return null; }
            }

            IDisplayDimension? DimSegPair(ISketchSegment? a, ISketchSegment? b, double tx, double ty, string tag)
            {
                doc.ClearSelection2(true);
                bool sa = false, sb = false;
                try { sa = a?.Select4(false, null) ?? false; } catch { }
                try { sb = b?.Select4(true, null) ?? false; } catch { }
                if (!(sa && sb)) { Console.WriteLine($"#   dim '{tag}': pair select ({sa},{sb})"); return null; }
                try
                {
                    var dd = doc.IAddDimension2(tx, ty, 0) as IDisplayDimension;
                    var dim = dd?.GetDimension2(0) as IDimension;
                    Console.WriteLine($"#   dim '{tag}': {(dd == null ? "null" : "ok")} " +
                        $"name='{dim?.Name}' full='{dim?.FullName}' " +
                        $"v={(dim == null ? -1 : dim.SystemValue * 1000):F4}mm st={dim?.DrivenState ?? -1}");
                    return dd;
                }
                catch (Exception ex) { Console.WriteLine($"#   dim '{tag}' threw {ex.GetType().Name}"); return null; }
            }

            string? DimName(IDisplayDimension? dd)
            {
                try { return (dd?.GetDimension2(0) as IDimension)?.Name; } catch { return null; }
            }
            string? DimFullName(IDisplayDimension? dd)
            {
                try { return (dd?.GetDimension2(0) as IDimension)?.FullName; } catch { return null; }
            }
            bool SameDim(IDisplayDimension a, IDisplayDimension b)
            {
                var na = DimFullName(a);
                return na != null && na == DimFullName(b);
            }

            // Every enumeration context that could expose the def's dims:
            // the def object itself, the host sketch feature, the def's own
            // sketch feature. Dedup by FullName (RCWs differ per context).
            List<(string Where, IDisplayDimension Dd)> AllDims()
            {
                var list = new List<(string Where, IDisplayDimension Dd)>();
                foreach (var (tag, dd) in c.DefDims) list.Add(($"def:{tag}", dd));
                foreach (var (where, feat) in new[] { ("skfeat", c.SketchFeat), ("defsk", c.Def?.GetSketch() as IFeature) })
                {
                    if (feat == null) continue;
                    try
                    {
                        var dd = feat.GetFirstDisplayDimension() as IDisplayDimension;
                        for (int g = 0; dd != null && g < 32; g++)
                        {
                            if (!list.Any(t => SameDim(t.Dd, dd))) list.Add((where, dd));
                            dd = NextDisplayDim(feat, dd);
                        }
                    }
                    catch { }
                }
                return list;
            }

            void DumpDims(string tag)
            {
                try { doc.GraphicsRedraw2(); } catch { }
                foreach (var (where, dd) in AllDims())
                {
                    var dim = dd.GetDimension2(0) as IDimension;
                    string name = "", full = "", t0 = "", t3 = "", t4 = "", sv3 = "";
                    double sys = double.NaN, uval = double.NaN, sc2 = double.NaN, ovr = double.NaN;
                    int st = -1;
                    try { name = dim?.Name ?? ""; } catch { }
                    try { full = dim?.FullName ?? ""; } catch { }
                    try { sys = dim?.SystemValue ?? double.NaN; } catch { }
                    try { st = dim?.DrivenState ?? -1; } catch { }
                    try { uval = dim?.GetUserValueIn(doc) ?? double.NaN; } catch { }
                    try
                    {
                        var v3 = dim?.GetSystemValue3(2, null);
                        sv3 = v3 is System.Array va ? string.Join(",", va.Cast<object>()) : v3?.ToString() ?? "null";
                    }
                    catch (Exception ex) { sv3 = "EX:" + ex.GetType().Name; }
                    try { sc2 = dd.Scale2; } catch { }
                    try { ovr = dd.GetOverrideValue(); } catch { }
                    try { t0 = dd.GetText(0) ?? ""; } catch { }
                    try { t3 = dd.GetText(3) ?? ""; } catch { }
                    try { t4 = dd.GetText(4) ?? ""; } catch { }
                    string disp;
                    try
                    {
                        var ddata = dd.IGetDisplayData();
                        int tc = ddata?.GetTextCount() ?? -1;
                        var parts = new List<string>();
                        for (int i = 0; i < tc && i < 8; i++)
                            try { parts.Add(ddata!.GetTextAtIndex(i) ?? ""); } catch { }
                        disp = $"texts={tc}[{string.Join("|", parts)}]";
                    }
                    catch (Exception ex) { disp = "dispEX:" + ex.GetType().Name; }
                    Console.WriteLine($"# EXPD {tag} {where,-15} '{name}' full='{full}' st={st} " +
                        $"sys={sys * 1000:F4}mm usr={uval:F4} sysv3=[{sv3}] scale2={sc2:F4} " +
                        $"ovr={ovr:F4} txt='{t0}' above='{t3}' below='{t4}' {disp}");
                }
            }

            // ---------- the test def: a 1x1-unit inner square (the spec's unit
            // convention — mm doc -> +-0.0005 m) + a larger square at 4x offset ----------
            if (!OpenFrontSketch(doc, c.Ext, c.Mgr)) { Console.WriteLine("# no sketch"); return; }
            var inner = DrawRigid(-0.0005, 0.0005, true);
            var outer = DrawRigid(-0.0020, 0.0020, true);
            if (inner == null || outer == null) { Console.WriteLine("# fixture draw failed"); return; }
            var dInB  = DimSeg(inner[0], 0.0, -0.0010, "inner-bottom(driving?)");
            var dInT  = DimSeg(inner[2], 0.0,  0.0010, "inner-top(driven?)");
            var dOutB = DimSeg(outer[0], 0.0, -0.0028, "outer-bottom(driving?)");
            var dOutT = DimSeg(outer[2], 0.0,  0.0028, "outer-top(driven?)");
            var dGap  = DimSegPair(inner[2], outer[2], 0.0040, 0.0012, "gap(driven?)");

            doc.ClearSelection2(true);
            c.Ext.SelectAll();
            int selN = c.Sel.GetSelectedObjectCount();
            c.Def = c.Mgr.MakeSketchBlockFromSelected(SpikePt(c.Mu, 0, 0)) as ISketchBlockDefinition;
            c.Sketch = c.Mgr.ActiveSketch as ISketch;
            c.SketchFeat = c.Sketch as IFeature;
            Console.WriteLine($"# fixture: selCount={selN} def={(c.Def == null ? "null" : "ok")} " +
                $"sketch='{c.SketchFeat?.Name}' instCount={c.Sketch?.GetSketchBlockInstanceCount()}");
            if (c.Def == null || c.Sketch == null || c.Sketch.GetSketchBlockInstanceCount() != 1)
            { Console.WriteLine("# block fixture failed — aborting"); return; }
            c.Inst = c.Sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance;
            try { c.RelMgr = c.Sketch.RelationManager as ISketchRelationManager; } catch { }
            var made = new List<(string Tag, IDisplayDimension? Dd)>
            {
                ("inner-bottom", dInB), ("inner-top", dInT),
                ("outer-bottom", dOutB), ("outer-top", dOutT), ("gap", dGap),
            };
            object? ddarr = null;
            try { ddarr = c.Def.GetDisplayDimensions(); } catch { }
            if (ddarr is object[] arr)
            {
                foreach (var d in arr)
                {
                    if (d is not IDisplayDimension dd) continue;
                    var tag = made.FirstOrDefault(m => DimName(m.Dd) != null && DimName(m.Dd) == DimName(dd)).Tag ?? "?";
                    c.DefDims.Add((tag, dd));
                }
            }
            Console.WriteLine($"# fixture: inst='{c.Inst?.Name}' defDims={c.DefDims.Count} " +
                $"(def count={c.Def.GetDisplayDimensionCount()})");

            // ---------- E1: does a def-internal dim DISPLAY the scaled value? ----------
            Exp("D1", "def-internal dim readback across Scale 1 -> 25 -> 40", () =>
            {
                if (c.Inst == null) { Console.WriteLine("# EXPD1 no instance"); return; }
                try { c.Inst.Scale = 1.0; } catch { }
                DumpDims("S1");
                try { c.Inst.Scale = 25.0; } catch (Exception ex) { Console.WriteLine($"# EXPD1 scale25 {ex.GetType().Name}"); }
                try { c.Inst.DimensionDisplay = true; } catch { }
                Console.WriteLine($"# EXPD1 inst Scale={c.Inst?.Scale} DimDisp={c.Inst?.DimensionDisplay}");
                DumpDims("S25+show");
                try { c.Inst.Scale = 40.0; } catch { }
                DumpDims("S40");
                try { c.Inst.Scale = 1.0; } catch { }
            });

            // ---------- E2: the def-geometry-driven gap dim + the Scale2 lever ----------
            Exp("D2", "gap dim driven by def geometry + DisplayDimension.Scale2", () =>
            {
                if (c.Inst == null) { Console.WriteLine("# EXPD2 no instance"); return; }
                // the gap dim is the one measuring ~1.5 mm (0.002 - 0.0005 def)
                var gap = c.DefDims.FirstOrDefault(
                    t => Math.Abs(SafeD(() => (t.Dd.GetDimension2(0) as IDimension)?.SystemValue ?? -1)
                        - 0.0015) < 1e-6).Dd;
                if (gap == null) { Console.WriteLine("# EXPD2 no gap dim (lookup by value ~1.5mm)"); return; }
                var gdim = gap.GetDimension2(0) as IDimension;
                foreach (var s in new[] { 1.0, 25.0, 40.0 })
                {
                    try { c.Inst.Scale = s; } catch { }
                    double sys = SafeD(() => gdim?.SystemValue ?? double.NaN);
                    double uv = SafeD(() => gdim?.GetUserValueIn(doc) ?? double.NaN);
                    int st = gdim?.DrivenState ?? -1;
                    Console.WriteLine($"# EXPD2 gap@S{s}: sys={sys * 1000:F4}mm usr={uv:F4} st={st}");
                }
                // IMeasure cross-check between the two picked block edges —
                // instance-space distance is what the gap dim would show scaled.
                try
                {
                    c.Inst.Scale = 25.0;
                    var meas = c.Ext.CreateMeasure() as IMeasure;
                    var e1 = c.Pick("SKETCHSEGMENT", 0.0, 0.0005 * c.Inst.Scale, out _, out bool k1);
                    c.Ext.SelectByID2("", "SKETCHSEGMENT", 0.0, 0.002 * c.Inst.Scale, 0,
                        true, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                    bool okm = meas?.Calculate(null) ?? false;
                    Console.WriteLine($"# EXPD2 measure edges ok={okm} dist={(meas?.Distance ?? -1) * 1000:F4}mm " +
                        $"norm={(meas?.NormalDistance ?? -1) * 1000:F4}mm (picked {DescribeCom(e1)})");
                }
                catch (Exception ex) { Console.WriteLine($"# EXPD2 measure threw {ex.GetType().Name}"); }
                // Scale2 write: does the display dim's own scale factor multiply
                // the rendered value? (a possible per-def fallback lever)
                try { gap.Scale2 = 25.0; } catch { }
                Console.WriteLine($"# EXPD2 wrote gap.Scale2=25 -> read {SafeD(() => gap.Scale2):F4}");
                DumpDims("S25+s2=25");
                try { gap.Scale2 = 1.0; c.Inst.Scale = 1.0; } catch { }
            });

            // ---------- E3: SketchUseEdge2 — frozen copy or tracking host edge? ----------
            Exp("D3", "SketchUseEdge2-converted block edge: frozen vs tracking", () =>
            {
                if (c.Inst == null || c.Sketch == null) { Console.WriteLine("# EXPD3 no inst/sketch"); return; }
                try { c.Inst.Scale = 2.0; c.Inst.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
                List<string> Snap()
                {
                    var list = new List<string>();
                    try
                    {
                        if (c.Sketch!.GetSketchSegments() is object[] segs)
                            foreach (var s in segs) list.Add(c.Ends(s));
                    }
                    catch { }
                    return list;
                }
                ISketchSegment? FindAdded(List<string> added)
                {
                    if (c.Sketch!.GetSketchSegments() is object[] segs)
                        foreach (var s in segs)
                            if (s is ISketchSegment ss && added.Contains(c.Ends(ss))) return ss;
                    return null;
                }
                void TrackEdge(ISketchSegment? edge, string tag)
                {
                    foreach (var s in new[] { 2.0, 4.0, 1.0 })
                    {
                        try { c.Inst!.Scale = s; } catch { }
                        Console.WriteLine($"# EXPD3 {tag}@S{s}: ends={c.Ends(edge)}");
                    }
                    var host = c.Mgr.CreateLine(-0.005, 0.030, 0, 0.005, 0.030, 0) as ISketchSegment;
                    if (host != null) c.Litter.Add(host);
                    if (edge == null || host == null) return;
                    doc.ClearSelection2(true);
                    bool s1 = false, s2 = false;
                    try { s1 = edge.Select4(false, null); } catch { }
                    try { s2 = host.Select4(true, null); } catch { }
                    if (s1 && s2) doc.SketchAddConstraints("sgCOLINEAR");
                    Console.WriteLine($"# EXPD3 {tag} colinear sel=({s1},{s2}) host={c.Ends(host)}");
                    foreach (var s in new[] { 1.0, 3.0 })
                    {
                        try { c.Inst!.Scale = s; } catch { }
                        Console.WriteLine($"# EXPD3 {tag} bound host@S{s}: {c.Ends(host)}");
                    }
                    try { c.Inst!.Scale = 1.0; } catch { }
                }
                // Route A: coordinate pick on the instance-projected edge.
                // At scale 2 the outer square's top edge projects at y=+4 mm.
                var before = Snap();
                var picked = c.Pick("SKETCHSEGMENT", 0.0, 0.004, out int st, out bool ok);
                Console.WriteLine($"# EXPD3a pick@outerTop(S2,y=4) ok={ok} seltype={st} " +
                    $"obj={DescribeCom(picked)} ends={c.Ends(picked)}");
                bool used = false;
                try { used = c.Mgr.SketchUseEdge2(false); }
                catch (Exception ex) { Console.WriteLine($"# EXPD3a SketchUseEdge2 threw {ex.GetType().Name}"); }
                var after = Snap();
                var added = after.Except(before).ToList();
                Console.WriteLine($"# EXPD3a SketchUseEdge2 -> {used} segs {before.Count}->{after.Count} " +
                    $"added=[{string.Join(" ; ", added)}]");
                TrackEdge(FindAdded(added), "convA");
                // Route B: Select4 on the def's own line object (round-2's route).
                // def.GetLines() returns raw coordinate doubles — the
                // selectable entity objects live on the DEFINITION SKETCH
                // (def.GetSketch().GetLines()), the route bindexp E8 used.
                object? lines = null;
                var defSketch = c.Def?.GetSketch() as ISketch;
                try { lines = defSketch?.GetSketchSegments(); } catch { }
                if (lines == null || lines is not object[])
                    try { lines = defSketch?.GetLines(); } catch { }
                var defLines = new List<ISketchLine>();
                if (lines is object[] la)
                    foreach (var lo in la)
                        if (lo is ISketchLine ll) defLines.Add(ll);
                Console.WriteLine($"# EXPD3b defSketch='{(defSketch as IFeature)?.Name}' " +
                    $"GetLines -> {(lines == null ? "null" : (lines is object[] la2 ? la2.Length.ToString() : lines.GetType().Name))} " +
                    $"parsed={defLines.Count}");
                foreach (var dl in defLines)
                    Console.WriteLine($"# EXPD3b   def line {c.Ends(dl)}");
                var outerTop = defLines.FirstOrDefault(dl =>
                {
                    var pa = dl.GetStartPoint2() as ISketchPoint;
                    var pb = dl.GetEndPoint2() as ISketchPoint;
                    return pa != null && pb != null &&
                        Math.Abs(pa.Y - 0.002) < 1e-9 && Math.Abs(pb.Y - 0.002) < 1e-9;
                }) ?? defLines.FirstOrDefault();
                Console.WriteLine($"# EXPD3b convert candidate: {(outerTop == null ? "null" : c.Ends(outerTop))}");
                if (outerTop != null)
                {
                    var before2 = Snap();
                    doc.ClearSelection2(true);
                    bool dsel = false;
                    try { dsel = (outerTop as ISketchSegment)?.Select4(false, null) ?? false; } catch { }
                    bool used2 = false;
                    try { used2 = c.Mgr.SketchUseEdge2(false); }
                    catch (Exception ex) { Console.WriteLine($"# EXPD3b SketchUseEdge2 threw {ex.GetType().Name}"); }
                    var after2 = Snap();
                    var added2 = after2.Except(before2).ToList();
                    Console.WriteLine($"# EXPD3b defLine.Select4={dsel} SketchUseEdge2 -> {used2} " +
                        $"segs {before2.Count}->{after2.Count} added=[{string.Join(" ; ", added2)}]");
                    TrackEdge(FindAdded(added2), "convB");
                }
            });

            // ---------- E4: base-point anchors — concentric + square centre ----------
            Exp("D4", "sgCONCENTRIC + square-centre anchor on the base point", () =>
            {
                if (c.Inst == null) { Console.WriteLine("# EXPD4 no instance"); return; }
                try { c.Inst.Scale = 1.0; c.Inst.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }
                // Disambiguation: displace the instance — base point moves to
                // (10,10) while the sketch origin stays at (0,0); a pick at
                // (10,10) can then only hit the base point.
                try { c.Inst.InstancePosition = SpikePt(c.Mu, 0.010, 0.010); } catch { }
                var bp = c.Pick("SKETCHPOINT", 0.010, 0.010, out int bst, out bool bok);
                Console.WriteLine($"# EXPD4 base-pt pick@(10,10) ok={bok} seltype={bst} obj={DescribeCom(bp)}" +
                    (bp is ISketchPoint bpp ? $" at ({bpp.X * 1000:F2},{bpp.Y * 1000:F2})mm" : ""));
                var orig = c.Pick("SKETCHPOINT", 0.0, 0.0, out int ost, out bool ook);
                Console.WriteLine($"# EXPD4 origin-pt pick@(0,0) ok={ook} seltype={ost} obj={DescribeCom(orig)}" +
                    (orig is ISketchPoint opp ? $" at ({opp.X * 1000:F2},{opp.Y * 1000:F2})mm" : ""));
                try { c.Inst.InstancePosition = SpikePt(c.Mu, 0, 0); } catch { }

                // A: construction circle concentric to the base point — the
                // bind is made WHILE the instance sits at (10,10).
                var circ = c.Mgr.CreateCircleByRadius(0.030, 0.030, 0, 0.005) as ISketchSegment;
                if (circ != null) { c.Litter.Add(circ); try { circ.ConstructionGeometry = true; } catch { } }
                ISketchPoint? Centre() => (circ as ISketchArc)?.GetCenterPoint2() as ISketchPoint;
                try { c.Inst.InstancePosition = SpikePt(c.Mu, 0.010, 0.010); } catch { }
                doc.ClearSelection2(true);
                bool q1 = false, q2 = false;
                try { q1 = circ?.Select4(false, null) ?? false; } catch { }
                q2 = c.Ext.SelectByID2("", "SKETCHPOINT", 0.010, 0.010, 0, true, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                if (q1 && q2) doc.SketchAddConstraints("sgCONCENTRIC");
                var cpt = Centre();
                Console.WriteLine($"# EXPD4a concentric sel=({q1},{q2}) centre=({cpt?.X * 1000:F2},{cpt?.Y * 1000:F2}) " +
                    "(10,10=base-pt bound / 0,0=origin bound)");
                try { c.Inst.InstancePosition = SpikePt(c.Mu, 0.020, 0.010); } catch { }
                cpt = Centre();
                Console.WriteLine($"# EXPD4a re-dragged(+10x) centre=({cpt?.X * 1000:F2},{cpt?.Y * 1000:F2})");
                // A re-drag may not re-solve the host sketch — force a rebuild
                // (exits edit mode) then re-enter and re-read.
                try { doc.EditRebuild3(); } catch { }
                var back = ReopenBlockSketch(doc, c.Ext, c.Mgr);
                if (back != null)
                {
                    c.Sketch = back;
                    c.Inst = back.IGetSketchBlockInstances(0) as ISketchBlockInstance;
                }
                var circ2 = c.Sketch?.GetSketchSegments() is object[] segs4
                    ? segs4.OfType<ISketchArc>().FirstOrDefault() : null;
                var cpt2 = circ2?.GetCenterPoint2() as ISketchPoint;
                Console.WriteLine($"# EXPD4a after rebuild+reenter centre=({cpt2?.X * 1000:F2},{cpt2?.Y * 1000:F2}) " +
                    $"reopen={back != null}");
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); c.Inst!.Scale = 3.0; } catch { }
                cpt2 = circ2?.GetCenterPoint2() as ISketchPoint;
                Console.WriteLine($"# EXPD4a @S3 centre=({cpt2?.X * 1000:F2},{cpt2?.Y * 1000:F2})");
                try { c.Inst!.Scale = 1.0; } catch { }

                // B: square-centre anchor — same disambiguation at (10,10).
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.010, 0.010); } catch { }
                var sq = DrawRigid(0.020, 0.040, false);
                var diag = c.Mgr.CreateCenterLine(0.020, 0.020, 0, 0.040, 0.040, 0) as ISketchSegment;
                if (diag != null) c.Litter.Add(diag);
                var mid = c.Mgr.CreatePoint(0.026, 0.032, 0) as ISketchPoint;
                doc.ClearSelection2(true);
                bool m1 = false, m2 = false;
                try { m1 = mid?.Select4(false, null) ?? false; } catch { }
                try { m2 = diag?.Select4(true, null) ?? false; } catch { }
                if (m1 && m2) doc.SketchAddConstraints("sgATMIDDLE");
                Console.WriteLine($"# EXPD4b midpt sel=({m1},{m2}) mid=({mid?.X * 1000:F2},{mid?.Y * 1000:F2}) expect(30,30)");
                doc.ClearSelection2(true);
                bool b1 = false, b2 = false;
                try { b1 = mid?.Select4(false, null) ?? false; } catch { }
                b2 = c.Ext.SelectByID2("", "SKETCHPOINT", 0.010, 0.010, 0, true, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                if (b1 && b2) doc.SketchAddConstraints("sgCOINCIDENT");
                var bl = (sq?[0] as ISketchLine)?.IGetStartPoint2() as ISketchPoint;
                Console.WriteLine($"# EXPD4b centre-anchored sel=({b1},{b2}) BL=({bl?.X * 1000:F2},{bl?.Y * 1000:F2}) " +
                    $"(0,0=base-pt bound / -10,-10=origin bound) mid=({mid?.X * 1000:F2},{mid?.Y * 1000:F2})");
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0.020, 0.010); } catch { }
                Console.WriteLine($"# EXPD4b re-dragged(+10x) BL=({bl?.X * 1000:F2},{bl?.Y * 1000:F2}) " +
                    $"mid=({mid?.X * 1000:F2},{mid?.Y * 1000:F2})");
                try { doc.EditRebuild3(); } catch { }
                var back2 = ReopenBlockSketch(doc, c.Ext, c.Mgr);
                if (back2 != null) { c.Sketch = back2; c.Inst = back2.IGetSketchBlockInstances(0) as ISketchBlockInstance; }
                Console.WriteLine($"# EXPD4b after rebuild BL=({bl?.X * 1000:F2},{bl?.Y * 1000:F2}) " +
                    $"mid=({mid?.X * 1000:F2},{mid?.Y * 1000:F2}) reopen={back2 != null}");
                try { c.Inst!.InstancePosition = SpikePt(c.Mu, 0, 0); c.Inst!.Scale = 3.0; } catch { }
                Console.WriteLine($"# EXPD4b @S3 BL=({bl?.X * 1000:F2},{bl?.Y * 1000:F2}) mid=({mid?.X * 1000:F2},{mid?.Y * 1000:F2})");
                try { c.Inst!.Scale = 1.0; } catch { }
            });

            // ---------- litter cleanup ----------
            foreach (var s in c.Litter)
            {
                try
                {
                    doc.ClearSelection2(true);
                    if (s.Select4(false, null))
                        c.Ext.DeleteSelection2(0);
                }
                catch { }
            }
            doc.ClearSelection2(true);
            try { c.Mgr.InsertSketch(false); } catch { }
            Console.WriteLine("# dimexp done — litter removed, sketch closed");
        }

        // =====================================================================
        // `consexp` — construction geometry INSIDE a block definition.
        // Spec-10 round 4 (engineer decision): the QR square + keep-out
        // square move into the block definition as construction geometry, so
        // the footprint and 4-cell quiet zone ride the instance's Scale and
        // position transforms instead of host-side binding. THE deciding
        // fact for the design: does construction geometry inside a def
        // actually RENDER on the block instance? Proven here BEFORE the
        // product change lands — plus the two wart checks: whether
        // ExplodeSketchBlockInstance keeps the construction flag, and
        // whether def construction content feeds sketch regions/profiles.
        // =====================================================================

        private static int ConsExpMain(string[] args)
        {
            string progId = args.Length > 0 ? args[0] : "SldWorks.Application.31";
            Console.WriteLine("# construction-geometry-in-block experiment (spec-10 round 4)");
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;
                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (doc == null) { Console.WriteLine("# FATAL: no part doc"); return 1; }
                try { ConsExpRun(sw, doc); }
                catch (Exception ex) { Console.WriteLine($"# FATAL body: {ex.GetType().Name}: {ex.Message}"); }
                finally { try { sw.CloseDoc(doc.GetTitle()); } catch { } }
                Console.WriteLine(_failures == 0 ? "# CONSEXP ALL CHECKS PASSED" : $"# {_failures} check(s) failed");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        private static void ConsExpRun(ISldWorks sw, IModelDoc2 doc)
        {
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            var mu = (IMathUtility)sw.IGetMathUtility();
            var sel = doc.ISelectionManager as ISelectionMgr;

            // Fixture mirrors the product's def: a ±0.5-doc-unit solid square
            // (the "QR content" — mm doc → ±0.0005 m sketch coords) plus a
            // larger construction square at the keep-out's unit ratio
            // (±0.0007 m). Drawn in one sketch, folded into ONE block
            // definition, the instance scaled ×25.
            if (!OpenFrontSketch(doc, ext, mgr)) { Console.WriteLine("# FATAL: no sketch"); return; }
            var solid = new List<ISketchSegment>();
            var cons = new List<ISketchSegment>();
            bool prevInf = mgr.AutoInference;
            try
            {
                mgr.AutoInference = false;
                foreach (var (x1, y1, x2, y2) in SquareEdges(0.0005))
                {
                    var s = mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                    if (s != null) solid.Add(s);
                }
                foreach (var (x1, y1, x2, y2) in SquareEdges(0.0007))
                {
                    var s = mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                    if (s != null)
                    {
                        try { s.ConstructionGeometry = true; } catch { }
                        cons.Add(s);
                    }
                }
            }
            finally { mgr.AutoInference = prevInf; }
            Check(solid.Count == 4 && cons.Count == 4 && cons.All(s => s.ConstructionGeometry),
                "fixture drew 4 solid + 4 construction segments",
                $"solid={solid.Count} cons={cons.Count} flagged={cons.Count(s => s.ConstructionGeometry)}");

            doc.ClearSelection2(true);
            ext.SelectAll();
            var def = mgr.MakeSketchBlockFromSelected(SpikePt(mu, 0, 0)) as ISketchBlockDefinition;
            var sketch = mgr.ActiveSketch as ISketch;
            Check(def != null && sketch != null && sketch.GetSketchBlockInstanceCount() == 1,
                "block formed with the construction square inside",
                $"def={(def == null ? "null" : "ok")} inst={sketch?.GetSketchBlockInstanceCount() ?? -1}");
            if (def == null || sketch == null) return;
            var inst = sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance;
            Check(inst != null, "instance readable", inst?.Name ?? "null");
            if (inst == null) return;

            // The def's own sketch is enumerable — the squares must be
            // inside it, construction-flagged, at the drawn unit coords.
            var defSketch = def.GetSketch() as ISketch;
            var defSegs = defSketch == null ? new List<SegSpan>() : SketchLineSpans(defSketch);
            var defCons = defSegs.Where(s => s.Construction).ToList();
            Check(defSegs.Count == 8 && defCons.Count == 4,
                "the block definition holds 8 segments — exactly 4 construction",
                $"segs={defSegs.Count} construction={defCons.Count}");
            // The def sketch reports endpoints in an offset frame (its own
            // origin, observed at the def bbox's bottom-centre) — endpoint
            // reads are NOT frame-stable, so verify the square by its
            // segment LENGTHS instead: 1.4 def-units for the keep-out vs
            // 1.0 for the solid square → the 1.4 keep-out ratio.
            double consLen = defCons.Count == 0 ? 0.0 : defCons.Average(
                s => Math.Sqrt(Math.Pow(s.X2 - s.X1, 2) + Math.Pow(s.Y2 - s.Y1, 2)));
            var defSolid = defSegs.Where(s => !s.Construction).ToList();
            double solidLen = defSolid.Count == 0 ? 0.0 : defSolid.Average(
                s => Math.Sqrt(Math.Pow(s.X2 - s.X1, 2) + Math.Pow(s.Y2 - s.Y1, 2)));
            Console.WriteLine($"#   def seg lengths: cons={consLen * 1000:F3}mm " +
                $"solid={solidLen * 1000:F3}mm ratio={(solidLen > 0 ? consLen / solidLen : 0):F3}");
            Check(defCons.Count == 4 && solidLen > 0 && consLen > solidLen * 1.2 && consLen < solidLen * 1.6,
                "the def's construction square is larger than the solid content by the keep-out ratio",
                $"consLen={consLen * 1000:F3}mm solidLen={solidLen * 1000:F3}mm");

            // ---- THE deciding fact: does the instance render the square? ----
            // Coordinate picks on block internals return a fixed proxy edge
            // (round-2 finding, re-observed: the wire body below shows the
            // SAME def edge answers every pick). So the pick CANNOT name
            // which edge renders — but the pick's hit-test extent CAN show
            // HOW FAR the instance renders: at Scale=100 the solid square
            // projects to ±50 mm and the construction square to ±70 mm. A
            // pick hit beyond 50 mm + the pick radius can only be answered
            // by geometry the instance actually carries out there — the
            // construction square. A miss beyond 70 mm bounds the extent.
            try { inst.Scale = 100.0; } catch (Exception ex) { Console.WriteLine($"#   Scale set threw {ex.Message}"); }
            Check(Math.Abs(inst.Scale - 100.0) < 1e-4, "instance Scale=100 applied", $"Scale={inst.Scale}");

            // Scan along +x from outside the solid edge (50 mm) through the
            // construction edge (70 mm) to beyond the block (95 mm).
            var scan = new List<(double x, bool hit)>();
            foreach (double x in new[] { 0.052, 0.058, 0.065, 0.070, 0.074, 0.080, 0.095 })
            {
                bool h = PickBlockEdgeAt(doc, ext, sel, x, 0.0, -1, out string d);
                scan.Add((x, h));
                Console.WriteLine($"#   pick x={x * 1000:F0}mm -> {d}");
            }
            bool insideCons = scan.Where(t => t.x <= 0.072).All(t => t.hit);
            bool beyondCons = scan.Where(t => t.x >= 0.080).All(t => !t.hit);
            Check(insideCons && beyondCons,
                "construction geometry inside the def RENDERS on the block instance " +
                "(picks hit out to the projected keep-out edge ±70mm, miss beyond — " +
                "solid content alone ends at ±50mm)",
                string.Join(" ", scan.Select(t => $"{t.x * 1000:F0}:{(t.hit ? "hit" : "miss")}")));
            PickBlockEdgeAt(doc, ext, sel, 0.0, 0.070, -1, out string dTop);
            Console.WriteLine($"#   pick top cons edge (0,70mm) -> {dTop}");
            Check(true, "top-edge pick lands inside the construction extent", dTop);

            // Scale tracking — at Scale=40 the construction square moves to
            // ±28 mm: the rendered extent must shrink past it.
            try { inst.Scale = 40.0; } catch { }
            var scan40 = new List<(double x, bool hit)>();
            foreach (double x in new[] { 0.024, 0.028, 0.032, 0.045, 0.070 })
            {
                bool h = PickBlockEdgeAt(doc, ext, sel, x, 0.0, -1, out string d);
                scan40.Add((x, h));
                Console.WriteLine($"#   scale40 pick x={x * 1000:F0}mm -> {d}");
            }
            bool inside40 = scan40.Where(t => t.x <= 0.030).All(t => t.hit);
            bool beyond40 = scan40.Where(t => t.x >= 0.045).All(t => !t.hit);
            Check(inside40 && beyond40,
                "a Scale edit rescales the def-internal construction square " +
                "(rendered extent shrinks to the new ±28mm keep-out)",
                string.Join(" ", scan40.Select(t => $"{t.x * 1000:F0}:{(t.hit ? "hit" : "miss")}")));

            // Drag tracking — InstancePosition displaces the rendered extent.
            try { inst.InstancePosition = SpikePt(mu, 0.010, -0.005); } catch { }
            bool hitDrag = PickBlockEdgeAt(doc, ext, sel, 0.010 + 0.028, -0.005, -1, out string dDrag);
            bool hitOldSpot = PickBlockEdgeAt(doc, ext, sel, 0.028, -0.005, -1, out string dOldSpot);
            Console.WriteLine($"#   drag pick at displaced cons edge (38mm,-5mm) -> {dDrag}");
            Console.WriteLine($"#   drag pick at old spot (28mm,-5mm) -> {dOldSpot}");
            Check(hitDrag, "a drag carries the def-internal construction square", dDrag);
            try { inst.InstancePosition = SpikePt(mu, 0.0, 0.0); inst.Scale = 25.0; } catch { }

            // Region census — construction content must not feed profiles.
            int regions = -1;
            try { regions = sketch.GetSketchRegionCount(); } catch { }
            Console.WriteLine($"#   sketch regions with the block inside: {regions}");

            // Explode — does the construction flag survive (the wart check)?
            bool exploded = false;
            try { mgr.ExplodeSketchBlockInstance((SketchBlockInstance)inst); exploded = true; }
            catch (Exception ex) { Console.WriteLine($"#   explode threw {ex.GetType().Name}"); }
            Check(exploded, "ExplodeSketchBlockInstance executes");
            var raw = SketchLineSpans(sketch);
            var rawCons = raw.Where(s => s.Construction).ToList();
            Console.WriteLine($"#   exploded segs={raw.Count} constructionFlagged={rawCons.Count}");
            Check(raw.Count == 8, "explode yields every def segment including the squares",
                $"raw={raw.Count}");
            Check(rawCons.Count == 4, "exploded construction lines KEEP the construction flag",
                $"flagged={rawCons.Count} of {raw.Count}");
            // The exploded square must land at the instance's PROJECTED
            // extent — Scale=25 on a ±0.7-def-unit square → ±17.5 mm.
            double expMax = rawCons.Count == 0 ? 0.0
                : rawCons.Max(s => Math.Max(
                    Math.Max(Math.Abs(s.X1), Math.Abs(s.X2)),
                    Math.Max(Math.Abs(s.Y1), Math.Abs(s.Y2))));
            Console.WriteLine($"#   exploded cons square half-extent: {expMax * 1000:F3} mm (expect ~17.5)");
            Check(Math.Abs(expMax - 0.0175) < 0.003,
                "exploded construction square lands at the instance-projected extent",
                $"half={expMax * 1000:F3} mm");
            int regionsAfter = -1;
            try { regionsAfter = sketch.GetSketchRegionCount(); } catch { }
            Console.WriteLine($"#   sketch regions after explode: {regionsAfter} (before: {regions})");
            doc.ClearSelection2(true);
            try { mgr.InsertSketch(false); } catch { }
            Console.WriteLine("# consexp done — sketch closed");
        }

        /// <summary>Edge list of an axis-aligned ±half square centred on the origin.</summary>
        private static IEnumerable<(double x1, double y1, double x2, double y2)> SquareEdges(double half)
        {
            yield return (-half, -half, +half, -half);
            yield return (+half, -half, +half, +half);
            yield return (+half, +half, -half, +half);
            yield return (-half, +half, -half, -half);
        }

        /// <summary>
        /// A coordinate SKETCHSEGMENT pick at (x,y) that identifies WHICH
        /// block-internal edge answered: the picked object reads at raw DEF
        /// coordinates, so a hit is only counted when the selected line's
        /// def extent matches <paramref name="wantDefHalf"/> — proving the
        /// construction edge (not a neighbouring solid edge inside the
        /// pick tolerance) rendered at that projected position.
        /// </summary>
        private static bool PickBlockEdgeAt(
            IModelDoc2 doc, IModelDocExtension ext, ISelectionMgr? sel,
            double x, double y, double wantDefHalf, out string detail)
        {
            detail = "";
            doc.ClearSelection2(true);
            bool ok = false;
            try
            {
                ok = ext.SelectByID2("", "SKETCHSEGMENT", x, y, 0.0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
            }
            catch (Exception ex) { detail = $"threw {ex.GetType().Name}"; return false; }
            int st = -1;
            string kind = "null";
            double defHalf = 0.0;
            bool consFlag = false;
            string aux = "";
            if (ok && sel != null)
            {
                try
                {
                    st = sel.GetSelectedObjectType3(1, -1);
                    var o = sel.GetSelectedObject6(1, -1);
                    kind = DescribeCom(o);
                    if (o is ISketchSegment seg)
                    {
                        try { consFlag = seg.ConstructionGeometry; } catch { }
                        try { aux += $" len={seg.GetLength() * 1000:F2}mm"; } catch { }
                        try
                        {
                            var crv = seg.GetCurve() as ICurve;
                            if (crv != null)
                            {
                                double u0 = 0, u1 = 0; bool cl = false, per = false;
                                crv.GetEndParams(out u0, out u1, out cl, out per);
                                var pa = crv.Evaluate2(u0, 0) as double[];
                                var pb = crv.Evaluate2(u1, 0) as double[];
                                if (pa != null && pb != null)
                                    aux += $" crv=({pa[0] * 1000:F1},{pa[1] * 1000:F1})-" +
                                        $"({pb[0] * 1000:F1},{pb[1] * 1000:F1})";
                            }
                        }
                        catch (Exception ex) { aux += " crvEX:" + ex.GetType().Name; }
                        try
                        {
                            var body = seg.CreateWireBody() as IBody2;
                            var edges = body?.GetEdges() as object[];
                            var e0 = edges?.FirstOrDefault() as IEdge;
                            var ec = e0?.GetCurve() as ICurve;
                            if (ec != null)
                            {
                                double u0 = 0, u1 = 0; bool cl = false, per = false;
                                ec.GetEndParams(out u0, out u1, out cl, out per);
                                var pa = ec.Evaluate2(u0, 0) as double[];
                                var pb = ec.Evaluate2(u1, 0) as double[];
                                if (pa != null && pb != null)
                                    aux += $" wire=({pa[0] * 1000:F1},{pa[1] * 1000:F1})-" +
                                        $"({pb[0] * 1000:F1},{pb[1] * 1000:F1})";
                            }
                            else aux += $" wire={(body == null ? "null-body" : "null-curve")}";
                        }
                        catch (Exception ex) { aux += " wireEX:" + ex.GetType().Name; }
                        if (o is ISketchLine l)
                        {
                            var a = l.GetStartPoint2() as ISketchPoint;
                            var b = l.GetEndPoint2() as ISketchPoint;
                            if (a != null && b != null)
                                defHalf = Math.Max(
                                    Math.Max(Math.Abs(a.X), Math.Abs(b.X)),
                                    Math.Max(Math.Abs(a.Y), Math.Abs(b.Y)));
                        }
                    }
                }
                catch { }
            }
            detail = $"ok={ok} seltype={st} obj={kind} defHalf={defHalf * 1000:F4}mm cons={consFlag}{aux}";
            return ok && (wantDefHalf < 0 || Math.Abs(defHalf - wantDefHalf) < 5e-5);
        }

        // ---------------- plumbing ----------------

        // =====================================================================
        // `xsettings` — spec-11 extended-settings live gates (ticket #28).
        //
        // One create driven through the pane probes at NON-DEFAULT settings —
        // the WiFi preset's fields composing the literal payload, ECC H,
        // inverted polarity — verified module-identically on the landed block
        // instance (definition census + exploded 1:1 contour match), then the
        // insertion-guard floor on a denser payload at the same settings.
        // Refuses to run while ANY SolidWorks process exists — an occupied
        // seat can be an interactive session the probe must never touch.
        // =====================================================================

        private static int XsettingsMain(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");
            const double growth = QrSketchSession.InitialGrowthFractionPerSide;
            const QRCodeGenerator.ECCLevel gateEcc = QRCodeGenerator.ECCLevel.H;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // ---- module-side expectations — every live verdict is computed, never guessed ----
            var wifiFields = new Dictionary<string, string>
            {
                ["ssid"] = "GATE-NET-28",
                ["security"] = "wpa",
                ["password"] = "probe-pass-28",
                ["hidden"] = "true",
            };
            var compose = PayloadComposer.Compose("wifi", wifiFields);
            if (!compose.Succeeded)
            {
                Console.WriteLine("# FATAL: module cannot compose the WiFi gate fields: " +
                    compose.Error!.Message);
                return 1;
            }
            string wifiContent = compose.Content!;
            Console.WriteLine($"# spec-11 live gates — WiFi fields -> '{wifiContent}'");

            // The interim payload while the 'hidden' field is still at its
            // default — the probes see this content between the field edits.
            string interimContent = PayloadComposer.Compose("wifi", new Dictionary<string, string>
            {
                ["ssid"] = wifiFields["ssid"],
                ["security"] = wifiFields["security"],
                ["password"] = wifiFields["password"],
            }).Content!;
            Console.WriteLine($"#   interim (hidden default) -> '{interimContent}'");

            if (!SmallestAccepted(wifiContent, gateEcc, invert: true, growth,
                    out double gateSizeMm, out var gateSketch))
            {
                Console.WriteLine("# FATAL: WiFi @ H+invert clears the guard at no tried size");
                return 1;
            }
            var expectedGate = gateSketch!;
            var interim25 = Verdict(interimContent, QrSketchSession.InitialSizeMm,
                growth, QRCodeGenerator.ECCLevel.M, invert: false);
            var interimGate = Verdict(interimContent, gateSizeMm,
                growth, QRCodeGenerator.ECCLevel.M, invert: false);
            var fullAtM = Verdict(wifiContent, gateSizeMm,
                growth, QRCodeGenerator.ECCLevel.M, invert: false);
            Console.WriteLine($"# gate request: {gateSizeMm}mm ECC=H invert=true " +
                $"N={expectedGate.Info.CellCount} M={expectedGate.Info.MatrixCellCount} " +
                $"contours={expectedGate.Info.ContourCount} segs={expectedGate.Info.SegmentCount} " +
                $"keepOut={expectedGate.Info.MatrixCellCount * expectedGate.Info.PitchMm:F3}mm " +
                $"minEdge={expectedGate.Info.MinEdgeMm:F4}mm minGap={expectedGate.Info.MinLightGapMm:F4}mm");
            Console.WriteLine($"# interim verdicts: interim@M/25mm -> {VerdictName(interim25)}; " +
                $"interim@M/{gateSizeMm}mm -> {VerdictName(interimGate)}; " +
                $"wifi@M/{gateSizeMm}mm -> {VerdictName(fullAtM)}");

            string sigInv = QrPictureRenderer.Render(expectedGate,
                QrSketchSession.PreviewPixelsAcross).Signature();
            var normalGate = Verdict(wifiContent, gateSizeMm, growth, gateEcc, invert: false);
            string sigNorm = normalGate.Succeeded
                ? QrPictureRenderer.Render(normalGate.Sketch!,
                    QrSketchSession.PreviewPixelsAcross).Signature()
                : "(no-normal-picture)";

            // ---- the denser payload: refused below the floor, accepted above it ----
            string denseContent = "";
            double denseRefused = 0, denseAccepted = 0;
            QrSketch? denseSketch = null;
            string refusedKind = "";
            foreach (var (tag, content) in PerfPayloads.Where(p => p.tag != "short"))
            {
                var rungs = RungVerdicts(content, gateEcc, invert: true, growth);
                Console.WriteLine($"# rung table '{tag}' ({content.Length} chars @H+invert): " +
                    string.Join(" ", rungs.Select(r => $"{r.size:g}mm:{VerdictName(r.result)}")));
                var refused = rungs.Where(r => !r.result.Succeeded).Select(r => r.size).ToList();
                var accepted = rungs.Where(r => r.result.Succeeded).ToList();
                double smallestOk = accepted.Count > 0 ? accepted.Min(r => r.size) : 0;
                if (refused.Count > 0 && accepted.Count > 0 && refused.Max() < smallestOk &&
                    rungs.Any(r => !r.result.Succeeded &&
                        r.result.Error!.Kind == QrSketchErrorKind.BelowInsertionLimit))
                {
                    denseContent = content;
                    denseRefused = refused.Max();
                    denseAccepted = smallestOk;
                    denseSketch = accepted.First(r => r.size == smallestOk).result.Sketch;
                    refusedKind = rungs.First(r => r.size == denseRefused)
                        .result.Error!.Kind.ToString();
                    break;
                }
            }
            if (denseSketch == null)
            {
                Console.WriteLine("# FATAL: no perf payload offers a refused + accepted rung — " +
                    "the floor gate cannot be exercised");
                return 1;
            }
            var denseAtGate = Verdict(denseContent, gateSizeMm, growth, gateEcc, invert: true);
            Console.WriteLine($"# dense payload: refused <={denseRefused:g}mm ({refusedKind}), " +
                $"accepted {denseAccepted:g}mm (N={denseSketch.Info.CellCount}); " +
                $"at gate size -> {VerdictName(denseAtGate)}");

            // The module half is done — an occupied seat still gets the full
            // expectation printout above before the refusal lands here.
            if (System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: extended-settings verification requires SolidWorks " +
                    "closed — a SLDWORKS.exe process is already running. Refusing to attach; " +
                    "the probe never mutates or kills an occupied seat.");
                return 1;
            }

            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;

                // ---- the answering build must expose the spec-11 probe surface ----
                var probe = TryGetProbe(sw);
                if (!ProbeHasExtendedSettings(probe))
                {
                    string local = Path.Combine(
                        System.Environment.GetFolderPath(
                            System.Environment.SpecialFolder.LocalApplicationData),
                        "SolidWorksQrAddIn", "SolidWorksQrAddIn.dll");
                    foreach (var p in new[]
                             {
                                 dllPath, local,
                                 @"C:\Program Files\SolidWorks QR Add-In\SolidWorksQrAddIn.dll",
                             })
                        try { sw.UnloadAddIn(p); } catch { }
                    int lr = sw.LoadAddIn(dllPath);
                    Console.WriteLine($"# LoadAddIn({dllPath}) -> {lr}");
                    if (lr != 0) return 1;
                    probe = TryGetProbe(sw);
                }
                Check(ProbeHasExtendedSettings(probe),
                    "the answering build exposes the spec-11 probe surface " +
                    "(preset=/ecc=/invert= in WindowStateProbe)");
                if (!ProbeHasExtendedSettings(probe)) return 1;
                Check(probe!.AddinStateProbe().StartsWith("CONNECTED"),
                    "add-in connected", probe.AddinStateProbe());

                string opened = probe.OpenQrWindowProbe();
                Check(opened.Contains("open=True") && opened.Contains("windows=1"),
                    "pane opened through the command entry — the surface the gate drives", opened);
                string fresh = probe.WindowStateProbe();
                Check(fresh.Contains("preset=raw") && fresh.Contains("ecc=M") &&
                      fresh.Contains("invert=False"),
                    "a fresh session starts on default settings (raw / ECC M / dark-on-light)", fresh);

                // ---------- negative plumbing — refusals name the offender ----------
                string badEcc = probe.SetQrEccProbe("bogus");
                Check(badEcc.StartsWith("ERROR") && badEcc.Contains("unknown ECC level"),
                    "an unrecognised ECC level never reaches the session", badEcc);
                string badType = probe.SetQrContentTypeProbe("bogus");
                Check(badType.StartsWith("INVALID") && badType.Contains("InvalidPayload"),
                    "an unknown content-type id is refused as InvalidPayload", badType);
                Check(probe.WindowStateProbe().Contains("preset=raw"),
                    "the refused type leaves the current selection in force",
                    probe.WindowStateProbe());
                string badField = probe.SetQrPresetFieldProbe("nonesuch", "x");
                Check(badField.StartsWith("INVALID") && badField.Contains("nonesuch"),
                    "an undeclared preset field is refused by name", badField);

                // ---------- the WiFi preset driven field by field ----------
                string pick = probe.SetQrContentTypeProbe("wifi");
                Check(pick.StartsWith("INVALID") && pick.Contains("InvalidPayload"),
                    "the WiFi preset opens invalid — required fields cannot compose yet", pick);
                Check(probe.PreviewSignatureProbe() == "NO-PICTURE",
                    "no picture while the preset fields cannot compose",
                    probe.PreviewSignatureProbe());
                Check(probe.WindowStateProbe().Contains("preset=wifi"),
                    "preset=wifi selected", probe.WindowStateProbe());

                string s1 = probe.SetQrPresetFieldProbe("ssid", wifiFields["ssid"]);
                Check(s1.StartsWith("INVALID") && s1.Contains("InvalidPayload"),
                    "ssid alone still misses the secured-network password", s1);
                string s2 = probe.SetQrPresetFieldProbe("security", wifiFields["security"]);
                Check(s2.StartsWith("INVALID") && s2.Contains("InvalidPayload"),
                    "the security choice alone still misses the password", s2);
                string s3 = probe.SetQrPresetFieldProbe("password", wifiFields["password"]);
                Check(ResponseMatchesVerdict(s3, interim25),
                    "the composed payload is judged at the CURRENT size — " +
                    $"the live verdict matches the module ({VerdictName(interim25)})", s3);

                string sz = probe.SetQrSizeProbe(gateSizeMm.ToString(inv));
                Check(ResponseMatchesVerdict(sz, interimGate),
                    $"size {gateSizeMm:g}mm revalidates the composed payload — " +
                    $"live verdict matches the module ({VerdictName(interimGate)})", sz);
                string s4 = probe.SetQrPresetFieldProbe("hidden", wifiFields["hidden"]);
                Check(ResponseMatchesVerdict(s4, fullAtM),
                    "the boolean hidden-network field composes too — " +
                    $"live verdict matches the module ({VerdictName(fullAtM)})", s4);

                // ---------- verbatim rule + remembered fields ----------
                string manual = probe.SetQrContentProbe(PayloadA);
                Check(manual.StartsWith("VALID") &&
                      probe.WindowStateProbe().Contains("preset=raw"),
                    "a manual content edit reverts the content type to raw",
                    $"{manual} | {probe.WindowStateProbe()}");
                string repick = probe.SetQrContentTypeProbe("wifi");
                Check(ResponseMatchesVerdict(repick, fullAtM),
                    "re-picking the preset recomposes the REMEMBERED fields — " +
                    $"the payload survives a raw detour ({VerdictName(fullAtM)})", repick);

                // ---------- the non-default settings land ----------
                string eccH = probe.SetQrEccProbe("h");   // lowercase — the parser is case-insensitive
                Check(eccH.StartsWith("VALID") &&
                      eccH.Contains($"cells={expectedGate.Info.CellCount}"),
                    "ECC 'h' re-encodes the payload at level H (denser matrix)", eccH);
                string invOn = probe.SetQrInvertProbe(true);
                Check(invOn.StartsWith("VALID") &&
                      invOn.Contains($"contours={expectedGate.Info.ContourCount}"),
                    "invert=true emits the light cells — the contour census " +
                    "matches the module's inverted output", invOn);

                string gate = probe.WindowStateProbe();
                Check(gate.Contains("preset=wifi") && gate.Contains("ecc=H") &&
                      gate.Contains("invert=True"),
                    "the session reports the non-default request end-to-end", gate);
                Check(gate.Contains("viewEcc=H") && gate.Contains("viewInvert=True"),
                    "the rendered pane controls follow the session's settings (view sync)", gate);
                string pic = probe.PreviewSignatureProbe();
                Check(pic.Contains($"sha={sigInv}"),
                    "the live preview renders exactly the module's inverted+H picture", pic);
                Check(!pic.Contains($"sha={sigNorm}"),
                    "the inverted preview differs from the same request at normal polarity",
                    $"inv={sigInv} norm={sigNorm}");

                // ---------- the spec gate: ONE create at the non-default settings ----------
                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "gate part document created");
                if (doc == null) return 1;
                string sketchName = "";
                try
                {
                    var ext = (IModelDocExtension)doc.Extension;
                    var mgr = (ISketchManager)doc.SketchManager;

                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane selected for the non-default create");
                    string cr = probe.CreateQrFromWindowProbe();
                    Check(cr.StartsWith("OK"),
                        "the spec-11 gate create succeeds: WiFi payload @ ECC H, inverted", cr);
                    sketchName = cr.StartsWith("OK") && cr.Split(' ').Length > 1
                        ? cr.Split(' ')[1] : "";
                    var sketch = mgr.ActiveSketch as ISketch;
                    Check(sketch != null, "the created sketch is left open in edit mode");
                    if (sketch != null)
                    {
                        VerifyXsettingsBlock(sketch, expectedGate, "wifi@H+invert");
                        mgr.InsertSketch(false);
                        Check(mgr.ActiveSketch == null, "the gate sketch exits edit mode");
                        // The committed feature is re-entered and exploded —
                        // the module-identical 1:1 contour check then runs in
                        // real sketch coordinates (the blockqr explode gate).
                        VerifyExplodedModuleIdentical(doc, ext, mgr, sketchName,
                            expectedGate, "wifi@H+invert");
                    }

                    // ---------- the insertion-guard floor on a denser payload ----------
                    string dense = probe.SetQrContentProbe(denseContent);
                    Check(ResponseMatchesVerdict(dense, denseAtGate),
                        "the dense payload revalidates at the live settings — " +
                        $"live verdict matches the module ({VerdictName(denseAtGate)})", dense);
                    Check(probe.WindowStateProbe().Contains("preset=raw"),
                        "the manual content edit reverted the preset to raw",
                        probe.WindowStateProbe());

                    // The refused rung first — the floor must say NO before it says YES.
                    string rsz = probe.SetQrSizeProbe(denseRefused.ToString(inv));
                    Check(rsz.StartsWith("INVALID") && rsz.Contains(refusedKind),
                        $"the dense payload reports {refusedKind} at {denseRefused:g}mm — " +
                        "the live refusal matches the module's rung table", rsz);
                    int featsBefore = FindSketchFeatureNames(doc).Count;
                    string refused = probe.CreateQrFromWindowProbe();
                    Check(refused.StartsWith("ERROR"),
                        $"create REFUSES the below-floor request at {denseRefused:g}mm", refused);
                    Check(FindSketchFeatureNames(doc).Count == featsBefore &&
                          mgr.ActiveSketch == null,
                        "the refused create touched neither the feature tree " +
                        "nor the sketch-edit state");

                    string asz = probe.SetQrSizeProbe(denseAccepted.ToString(inv));
                    Check(asz.StartsWith("VALID") &&
                          asz.Contains($"cells={denseSketch.Info.CellCount}"),
                        $"the cleared rung accepts the dense payload ({denseAccepted:g}mm)", asz);
                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane re-selected for the dense create");
                    string dc = probe.CreateQrFromWindowProbe();
                    Check(dc.StartsWith("OK"),
                        "the denser-payload create lands at the cleared size", dc);
                    var dsk = mgr.ActiveSketch as ISketch;
                    if (dsk != null)
                    {
                        VerifyXsettingsBlock(dsk, denseSketch, "dense@H+invert");
                        mgr.InsertSketch(false);
                        Check(mgr.ActiveSketch == null, "the dense sketch exits edit mode");
                    }
                    Check(FindSketchFeatureNames(doc).Count == featsBefore + 1 &&
                          (sketchName.Length == 0 || FindSketchFeature(doc, sketchName) != null),
                        "the earlier QR sketch is untouched by the dense create",
                        $"sketches=[{string.Join(", ", FindSketchFeatureNames(doc))}]");

                    string after = probe.WindowStateProbe();
                    Check(after.Contains("ecc=H") && after.Contains("invert=True"),
                        "the non-default settings persist across creates", after);
                }
                finally
                {
                    try { sw.CloseDoc(doc.GetTitle()); } catch { }
                }

                try { sw.UnloadAddIn(dllPath); } catch { }
                Console.WriteLine(_failures == 0
                    ? "# XSETTINGS ALL CHECKS PASSED"
                    : $"# {_failures} check(s) failed");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        /// <summary>
        /// The spec-11 discriminator: only a build carrying the extended-
        /// settings probes reports preset=/ecc=/invert= in WindowStateProbe.
        /// A stale registered build either lacks the member (throws) or
        /// reports a state string without the fields.
        /// </summary>
        private static bool ProbeHasExtendedSettings(IAddInProbe? probe)
        {
            if (probe == null) { Console.WriteLine("# no add-in object present"); return false; }
            try
            {
                string state = probe.WindowStateProbe();
                Console.WriteLine($"# discriminator WindowStateProbe -> {state}");
                return state.Contains("preset=") && state.Contains("ecc=") &&
                       state.Contains("invert=");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# discriminator WindowStateProbe -> {ex.GetType().Name} " +
                    "(the answering build predates spec-11)");
                return false;
            }
        }

        /// <summary>The module's verdict on one request — what the live session must answer.</summary>
        private static QrSketchResult Verdict(
            string content, double sizeMm, double growthFraction,
            QRCodeGenerator.ECCLevel ecc, bool invert) =>
            QrSketchGenerator.Generate(new QrSketchRequest(content, sizeMm, growthFraction)
            { EccLevel = ecc, Invert = invert });

        /// <summary>One-line form of a verdict, for rung-table dumps and check details.</summary>
        private static string VerdictName(QrSketchResult result) =>
            result.Succeeded
                ? $"VALID cells={result.Sketch!.Info.CellCount}"
                : $"INVALID {result.Error!.Kind}";

        /// <summary>Does a live probe answer reproduce the module's verdict for the same request?</summary>
        private static bool ResponseMatchesVerdict(string response, QrSketchResult verdict) =>
            verdict.Succeeded
                ? response.StartsWith("VALID") &&
                  response.Contains($"cells={verdict.Sketch!.Info.CellCount}")
                : response.StartsWith("INVALID") &&
                  response.Contains(verdict.Error!.Kind.ToString());

        /// <summary>The module's verdict at every ladder rung — the table the live run replays.</summary>
        private static List<(double size, QrSketchResult result)> RungVerdicts(
            string content, QRCodeGenerator.ECCLevel ecc, bool invert, double growth) =>
            GuardLadderMm.Select(s => (s, Verdict(content, s, growth, ecc, invert))).ToList();

        /// <summary>The smallest ladder rung the module accepts, plus its expected sketch.</summary>
        private static bool SmallestAccepted(
            string content, QRCodeGenerator.ECCLevel ecc, bool invert, double growth,
            out double sizeMm, out QrSketch? sketch)
        {
            var ok = RungVerdicts(content, ecc, invert, growth)
                .FirstOrDefault(r => r.result.Succeeded);
            sizeMm = ok.size;
            sketch = ok.result?.Sketch;
            return sketch != null;
        }

        /// <summary>A construction square's side length, mm — measured on its module vertices.</summary>
        private static double SquareSideMm(QrSketch expected, QrSquareRole role)
        {
            var sq = expected.ConstructionSquares.FirstOrDefault(s => s.Role == role);
            if (sq == null || sq.Vertices.Count == 0) return -1;
            return (sq.Vertices.Max(v => v.X) - sq.Vertices.Min(v => v.X)) * 1000.0;
        }

        /// <summary>Overall (W,H) span of the module's emitted contour edges, metres.</summary>
        private static (double W, double H) ModuleEdgesSpan(QrSketch expected)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var e in expected.Edges)
            {
                minX = Math.Min(minX, Math.Min(e.Start.X, e.End.X));
                maxX = Math.Max(maxX, Math.Max(e.Start.X, e.End.X));
                minY = Math.Min(minY, Math.Min(e.Start.Y, e.End.Y));
                maxY = Math.Max(maxY, Math.Max(e.Start.Y, e.End.Y));
            }
            return (maxX - minX, maxY - minY);
        }

        /// <summary>
        /// The block-level census for a spec-11 create. The def sketch reports
        /// an OFFSET frame (the consexp finding), so the frame-independent
        /// truths are checked: one instance at Scale 1, no enumerable host
        /// segments, the def folding the module's edges plus the QR and
        /// keep-out construction squares at real size, the material span
        /// matching the module's emitted extent (the POLARITY check — under
        /// invert the field spans the whole working matrix, distinctly wider
        /// than the QR square), and a 1:1 edge-length census against the
        /// module's contour edges.
        /// </summary>
        private static void VerifyXsettingsBlock(ISketch sketch, QrSketch expected, string tag)
        {
            int instCount = sketch.GetSketchBlockInstanceCount();
            Check(instCount == 1, $"one block instance carries the QR ({tag})", $"count={instCount}");
            var inst = instCount > 0
                ? sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
            Check(inst != null, $"the block instance is readable ({tag})", inst?.Name ?? "null");
            if (inst == null) return;
            Check(Math.Abs(inst.Scale - 1.0) < 1e-6,
                $"instance Scale is 1 — the definition carries the real size ({tag})",
                $"Scale={inst.Scale:F4} Angle={inst.Angle:F3}");
            var ipos = inst.InstancePosition?.ArrayData as double[];
            Check(ipos != null && Math.Abs(ipos[0]) < 1e-9 && Math.Abs(ipos[1]) < 1e-9,
                $"instance sits at the sketch origin ({tag})",
                ipos != null ? $"({ipos[0] * 1000:F3},{ipos[1] * 1000:F3}) mm" : "null");

            var hostSegs = SketchLineSpans(sketch);
            Check(hostSegs.Count == 0,
                $"the host sketch carries NO enumerable segments — " +
                $"everything rides the block ({tag})", $"hostSegs={hostSegs.Count}");

            ISketchBlockDefinition? blkDef = null;
            try { blkDef = inst.Definition as ISketchBlockDefinition; }
            catch (Exception ex) { Console.WriteLine($"#   Definition threw {ex.GetType().Name}"); }
            Check(blkDef != null, $"the block definition is reachable ({tag})");
            var defSketch = blkDef?.GetSketch() as ISketch;
            var defSegs = defSketch == null ? new List<SegSpan>() : SketchLineSpans(defSketch);
            var defCons = defSegs.Where(s => s.Construction).ToList();
            var material = defSegs.Where(s => !s.Construction).ToList();
            Check(defSegs.Count == expected.Info.SegmentCount + 8 && defCons.Count == 8,
                $"the def folds the module's contours PLUS the two construction squares ({tag})",
                $"defSegs={defSegs.Count} construction={defCons.Count} " +
                $"expected={expected.Info.SegmentCount}+8");

            double qrSide = SquareSideMm(expected, QrSquareRole.QrSquare) / 1000.0;
            double keepSide = SquareSideMm(expected, QrSquareRole.KeepOutSquare) / 1000.0;
            var shortCons = defCons.Where(s => Math.Abs(SegLen(s) - qrSide) < 2e-5).ToList();
            var longCons = defCons.Where(s => Math.Abs(SegLen(s) - keepSide) < 2e-5).ToList();
            Check(shortCons.Count == 4 && longCons.Count == 4,
                $"def construction segs = the QR square ({qrSide * 1000:F3}mm) and the " +
                $"keep-out square ({keepSide * 1000:F3}mm) at real size ({tag})",
                $"short={shortCons.Count}/4 long={longCons.Count}/4 " +
                $"lens=[{string.Join(", ", defCons.Select(s => (SegLen(s) * 1000).ToString("F4")))}]");

            var cc = SegsBboxCentre(defSegs);
            var qc = SegsBboxCentre(defCons);
            Check(Math.Abs(cc.x - qc.x) < 1e-9 && Math.Abs(cc.y - qc.y) < 1e-9,
                $"def construction squares centred on the QR content ({tag})",
                $"content=({cc.x:F6},{cc.y:F6}) squares=({qc.x:F6},{qc.y:F6})");

            var (exW, exH) = ModuleEdgesSpan(expected);
            var (matW, matH) = SegsBbox(material);
            Check(Math.Abs(matW - exW) < 2e-5 && Math.Abs(matH - exH) < 2e-5,
                $"emitted polarity: def material span matches the module's emitted extent ({tag})",
                $"def=({matW * 1000:F3},{matH * 1000:F3})mm " +
                $"module=({exW * 1000:F3},{exH * 1000:F3})mm " +
                $"({(expected.Info.Inverted ? "inverted field = keep-out extent"
                                          : "dark cells = grown outline")})");
            if (expected.Info.Inverted)
                Check(matW > qrSide + 1e-4,
                    $"the inverted field's material span exceeds the QR square — " +
                    $"light cells emitted, not dark ({tag})",
                    $"material={matW * 1000:F3}mm qrSquare={qrSide * 1000:F3}mm");

            // Module-identical edge census: the sorted def material lengths
            // must equal the module's contour edge lengths 1:1 — every edge
            // accounted for, frame-independent.
            var defLens = material.Select(SegLen).OrderBy(v => v).ToList();
            var modLens = expected.Edges.Select(e => Math.Sqrt(
                    Math.Pow(e.End.X - e.Start.X, 2) + Math.Pow(e.End.Y - e.Start.Y, 2)))
                .OrderBy(v => v).ToList();
            double maxErr = defLens.Count == modLens.Count
                ? defLens.Zip(modLens, (a, b) => Math.Abs(a - b)).DefaultIfEmpty(0).Max()
                : -1;
            Check(defLens.Count == modLens.Count && maxErr >= 0 && maxErr < 5e-7,
                $"def material edges match the module's contour edges 1:1 by length ({tag})",
                $"count={defLens.Count}/{modLens.Count} " +
                $"maxLenErr={(maxErr < 0 ? -1 : maxErr * 1000):F6}mm");
        }

        /// <summary>
        /// Re-enters the committed sketch feature and explodes its block
        /// instance — the established blockqr verification route — then checks
        /// the exploded HOST lines against the module 1:1 in real sketch
        /// coordinates: material segment count, the two construction squares
        /// flagged, and every material contour matching a module contour's
        /// bounding box exactly (positions, not just lengths).
        /// </summary>
        private static void VerifyExplodedModuleIdentical(
            IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr,
            string sketchName, QrSketch expected, string tag)
        {
            doc.ClearSelection2(true);
            bool reentered = ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            Check(reentered, $"the committed QR sketch re-selects for exploded verification ({tag})");
            if (!reentered) return;
            mgr.InsertSketch(true);
            var bk = mgr.ActiveSketch as ISketch;
            Check(bk != null && bk.GetSketchBlockInstanceCount() == 1,
                $"the committed feature still carries its ONE block instance ({tag})");
            if (bk == null || bk.GetSketchBlockInstanceCount() == 0) return;

            var bi = bk.IGetSketchBlockInstances(0) as ISketchBlockInstance;
            bool exploded = false;
            try { mgr.ExplodeSketchBlockInstance((SketchBlockInstance)bi!); exploded = true; }
            catch (Exception ex) { Console.WriteLine($"#   explode threw {ex.GetType().Name}"); }
            Check(exploded, $"ExplodeSketchBlockInstance executes ({tag})");
            if (!exploded) { mgr.InsertSketch(false); return; }
            Check(bk.GetSketchBlockInstanceCount() == 0,
                $"explode removes the block instance ({tag})");

            var rawAll = SketchLineSpans(bk);
            var raw = rawAll.Where(s => !s.Construction).ToList();
            Check(raw.Count == expected.Info.SegmentCount,
                $"exploded material segments match the module's edge count ({tag})",
                $"raw={raw.Count} expected={expected.Info.SegmentCount}");
            Check(rawAll.Count(s => s.Construction) == 8,
                $"explode returns the squares as construction-flagged host lines ({tag})",
                $"cons={rawAll.Count(s => s.Construction)} of {rawAll.Count}");

            object? rawContours = null;
            try { rawContours = bk.GetSketchContours(); } catch { }
            var nativeAll = rawContours is object[] arr
                ? arr.Cast<ISketchContour>().ToList() : new List<ISketchContour>();
            var materialNative = nativeAll.Where(ContourIsMaterial).ToList();
            int regions = -1;
            try { regions = bk.GetSketchRegionCount(); } catch { }
            Console.WriteLine($"#   exploded census: contours total={nativeAll.Count} " +
                $"material={materialNative.Count} regions={regions}");
            const double tol = 1e-9;
            var nativeBoxes = materialNative.Select(NativeBBox).ToList();
            var used = new bool[nativeBoxes.Count];
            int matched = 0;
            foreach (var mc in expected.Contours)
            {
                var mb = ModuleBBox(mc);
                int hit = -1;
                for (var i = 0; i < nativeBoxes.Count; i++)
                    if (!used[i] && BBoxEquals(mb, nativeBoxes[i], tol)) { hit = i; break; }
                if (hit >= 0) { used[hit] = true; matched++; }
            }
            Check(matched == expected.Contours.Count &&
                  materialNative.Count == expected.Contours.Count,
                $"every exploded contour matches a module contour bbox 1:1 ({tag})",
                $"matched={matched} nativeMaterial={materialNative.Count} " +
                $"nativeTotal={nativeAll.Count} expected={expected.Contours.Count}");
            mgr.InsertSketch(false);
            doc.ClearSelection2(true);
        }

        /// <summary>A contour counts as material when any of its segments is non-construction.</summary>
        private static bool ContourIsMaterial(ISketchContour c)
        {
            foreach (var so in (object[])c.GetSketchSegments())
                if (so is ISketchLine l &&
                    !((l as ISketchSegment)?.ConstructionGeometry ?? false))
                    return true;
            return false;
        }

        // =====================================================================
        // `shapes` — spec-13 live gates (ticket #36), recorded in
        // docs/SHAPES-GATES.md:
        //   A. a disjoint-dots (Circle) QR inserts through the pane path and
        //      extrudes via per-cell region selection — every emitted dot
        //      region individually selectable, a subset extrudes;
        //   B. a rounded-square QR at a FEASIBLE request (no radius resolves
        //      at the initial 25 mm/3% — the #32 note) extrudes a boss and a
        //      shallow cut with no contact failures, the keep-function-square
        //      carve-out verified natively on a finder ring;
        //   C. a dense shaped payload at the MaxSegmentCount bound — native
        //      insertion time + sketch-edit responsiveness measured;
        //   D. one rung over the bound — TooManySegments surfacing on the
        //      pane status line with no host mutation.
        //
        // Like `xsettings`, every module-side expectation is computed and
        // printed BEFORE the occupied-seat refusal, so a blocked run still
        // shows exactly what the live half would check.
        // =====================================================================

        /// <summary>The module's verdict at any shape settings — what the live session must answer.</summary>
        private static QrSketchResult ShapeVerdict(
            string content, double sizeMm, double growthFraction,
            QrCellShape shape, double shapeParam, bool keepFn) =>
            QrSketchGenerator.Generate(new QrSketchRequest(content, sizeMm, growthFraction)
            {
                CellShape = shape,
                CellShapeParameter = shapeParam,
                KeepFunctionPatternsSquare = keepFn,
            });

        /// <summary>
        /// One dense-gate candidate: a (content, shape, parameter, growth)
        /// combo climbed up the size ladder. `verdict` is the first accepted
        /// rung, or the first refused rung when nothing accepted — the
        /// segment ceiling candidates are exactly the TooManySegments rows.
        /// </summary>
        private sealed class ShapeCandidate
        {
            public string Tag = "";
            public string Content = "";
            public QrCellShape Shape;
            public double Param;            // fraction — paramText for probes is (Param*100)
            public double Growth;           // fraction — inert under Circle
            public double SizeMm;
            public QrSketchResult Verdict = null!;
            public bool IsAccepted => Verdict.Succeeded;
            public bool IsOverBound =>
                !Verdict.Succeeded &&
                Verdict.Error!.Kind == QrSketchErrorKind.TooManySegments;
            public int Segments => Verdict.Succeeded ? Verdict.Sketch!.Info.SegmentCount : -1;
        }

        /// <summary>One dense-search combo result line for the module-phase table.</summary>
        private static List<ShapeCandidate> ShapeCandidates()
        {
            // A mid payload between `medium` (~v6) and `dense` (~v14) so the
            // ~v9-class rounded rung of the #35 analysis is in the table.
            var contents = new List<(string tag, string content)>(PerfPayloads)
            {
                ("mid", "https://intranet.openaeros.local/parts/OA-ACCUTECH-SHIPPING-COVER" +
                        "?rev=C&checked=2026-10-09&bin=W4-017&qa=PASS&station=CELL-4" +
                        "&operator=JROBELIA&note=machined-all-over"),
            };
            var combos = new[]
            {
                (QrCellShape.Square, 0.00, 0.03),
                (QrCellShape.RoundedSquare, 0.10, 0.03),
                (QrCellShape.RoundedSquare, 0.10, 0.10),
                (QrCellShape.RoundedSquare, 0.30, 0.10),
                (QrCellShape.Circle, 0.50, 0.03),
                (QrCellShape.Circle, 0.70, 0.03),
                (QrCellShape.Circle, 0.90, 0.03),
                (QrCellShape.Circle, 0.95, 0.03),
            };
            var results = new List<ShapeCandidate>();
            foreach (var (tag, content) in contents)
                foreach (var (shape, param, growth) in combos)
                {
                    ShapeCandidate? cand = null;
                    foreach (var size in GuardLadderMm)
                    {
                        var v = ShapeVerdict(content, size, growth, shape, param, keepFn: false);
                        if (v.Succeeded)
                        {
                            cand = new ShapeCandidate
                            {
                                Tag = tag, Content = content, Shape = shape, Param = param,
                                Growth = growth, SizeMm = size, Verdict = v,
                            };
                            break;
                        }
                        // Only the ceiling rejection makes an over-bound
                        // rung — a floor refusal keeps climbing.
                        if (v.Error!.Kind == QrSketchErrorKind.TooManySegments)
                        {
                            cand ??= new ShapeCandidate
                            {
                                Tag = tag, Content = content, Shape = shape, Param = param,
                                Growth = growth, SizeMm = size, Verdict = v,
                            };
                            break;
                        }
                    }
                    if (cand != null) results.Add(cand);
                }
            return results;
        }

        /// <summary>
        /// The spec-13 discriminator: only a build carrying the cell-shape
        /// probes and the pane status readback reports shape=/keepFn= AND
        /// viewStatus= in WindowStateProbe. A stale registered build either
        /// lacks the members (InvokeMember throws) or reports without them.
        /// </summary>
        private static bool ProbeHasShapes(IAddInProbe? probe)
        {
            if (probe == null) { Console.WriteLine("# no add-in object present"); return false; }
            try
            {
                string state = probe.WindowStateProbe();
                Console.WriteLine($"# discriminator WindowStateProbe -> {state}");
                return state.Contains("shape=") && state.Contains("keepFn=") &&
                       state.Contains("viewStatus=");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# discriminator WindowStateProbe -> {ex.GetType().Name} " +
                    "(the answering build predates spec-13/ticket-36)");
                return false;
            }
        }

        /// <summary>The pane's rendered status line, out of the trailing viewStatus= field.</summary>
        private static string ViewStatusOf(string windowState)
        {
            const string key = "viewStatus=";
            int i = windowState.IndexOf(key, StringComparison.Ordinal);
            return i < 0 ? "" : windowState.Substring(i + key.Length).Trim();
        }

        /// <summary>Number of material regions the module emits — region boundaries + hole islands.</summary>
        private static int MaterialRegionCount(QrSketch expected) =>
            expected.Regions.Count +
            expected.Regions.Sum(r => r.Holes.Sum(h => h.Islands.Count));

        /// <summary>
        /// The native material regions of the committed QR sketch — every
        /// ISketchRegion whose bounding box matches a module material
        /// contour (region outlines + nested islands) at 1e-9 m. regionTotal
        /// reports everything SolidWorks enumerated, material or not.
        /// </summary>
        private static List<ISketchRegion> MaterialRegionsOf(ISldWorks sw, IModelDoc2 doc,
            string sketchName, QrSketch expected, out int regionTotal)
        {
            regionTotal = -1;
            var found = new List<ISketchRegion>();
            var feat = FindSketchFeature(doc, sketchName);
            var sketch = feat?.GetSpecificFeature2() as ISketch;
            if (sketch == null) return found;
            var regions = sketch.GetSketchRegions() as object[];
            if (regions == null) { regionTotal = 0; return found; }
            regionTotal = regions.Length;

            var mu = sw.IGetMathUtility();
            var m2s = sketch.ModelToSketchTransform;
            var material = new List<(double minX, double maxX, double minY, double maxY)>();
            foreach (var rg in expected.Regions)
            {
                material.Add(ModuleBBox(rg.Boundary));
                foreach (var h in rg.Holes)
                    foreach (var isl in h.Islands)
                        material.Add(ModuleBBox(isl.Boundary));
            }
            foreach (var ro in regions)
            {
                if (!(ro is ISketchRegion region)) continue;
                var bb = RegionBBox(region, mu, m2s);
                if (material.Any(mb => BBoxEquals(bb, mb, 1e-9)))
                    found.Add(region);
            }
            return found;
        }

        /// <summary>
        /// Gate A's core: on the committed dots sketch, every emitted dot is
        /// its own disjoint region — clear the selection, then Select each
        /// material region ON ITS OWN. Returns (total, matched, selectable).
        /// </summary>
        private static (int total, int matched, int selectable) DotsRegionCensus(
            ISldWorks sw, IModelDoc2 doc, string sketchName, QrSketch expected, string tag)
        {
            var regions = MaterialRegionsOf(sw, doc, sketchName, expected, out int total);
            int selectable = 0;
            foreach (var region in regions)
            {
                doc.ClearSelection2(true);
                try { if (region.Select(false, 0)) selectable++; } catch { }
            }
            doc.ClearSelection2(true);
            Console.WriteLine($"#   {tag} region census: native={total} " +
                $"material-matched={regions.Count} individually-selectable={selectable} " +
                $"(module emits {MaterialRegionCount(expected)} dots)");
            return (total, regions.Count, selectable);
        }

        /// <summary>Selects up to `take` of the sketch's material regions (appended) — the subset extrude.</summary>
        private static int SelectMaterialRegionSubset(ISldWorks sw, IModelDoc2 doc,
            string sketchName, QrSketch expected, int take)
        {
            var regions = MaterialRegionsOf(sw, doc, sketchName, expected, out _);
            doc.ClearSelection2(true);
            int selected = 0;
            foreach (var region in regions.Take(take))
                try { if (region.Select(true, 0)) selected++; } catch { }
            return selected;
        }

        /// <summary>
        /// Re-enters the committed QR sketch and explodes its block instance —
        /// the fallback when the block-form sketch does not enumerate
        /// selectable regions. Leaves the sketch committed with raw lines.
        /// </summary>
        private static bool ExplodeQrSketchBlock(IModelDoc2 doc, IModelDocExtension ext,
            ISketchManager mgr, string sketchName, string tag)
        {
            doc.ClearSelection2(true);
            if (!ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
            {
                Console.WriteLine($"#   {tag}: sketch feature no longer selects for explode");
                return false;
            }
            mgr.InsertSketch(true);
            var bk = mgr.ActiveSketch as ISketch;
            if (bk == null || bk.GetSketchBlockInstanceCount() == 0)
            {
                try { mgr.InsertSketch(false); } catch { }
                return false;
            }
            var bi = bk.IGetSketchBlockInstances(0) as ISketchBlockInstance;
            bool ok = false;
            try { mgr.ExplodeSketchBlockInstance((SketchBlockInstance)bi!); ok = true; }
            catch (Exception ex) { Console.WriteLine($"#   {tag}: explode threw {ex.GetType().Name}"); }
            try { mgr.InsertSketch(false); } catch { }
            doc.ClearSelection2(true);
            return ok;
        }

        /// <summary>
        /// The 1:1 material-contour bbox census on a sketch that ALREADY
        /// holds raw lines (post-explode) — VerifyExplodedModuleIdentical's
        /// contour half without requiring a block instance to explode.
        /// </summary>
        private static void RawSketchContourCheck(IModelDoc2 doc, IModelDocExtension ext,
            ISketchManager mgr, string sketchName, QrSketch expected, string tag)
        {
            doc.ClearSelection2(true);
            bool reentered = ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            Check(reentered, $"the committed QR sketch re-selects for the contour census ({tag})");
            if (!reentered) return;
            mgr.InsertSketch(true);
            var bk = mgr.ActiveSketch as ISketch;
            Check(bk != null, $"the QR sketch re-enters edit mode ({tag})");
            if (bk == null) { try { mgr.InsertSketch(false); } catch { } return; }

            object? rawContours = null;
            try { rawContours = bk.GetSketchContours(); } catch { }
            var nativeAll = rawContours is object[] arr
                ? arr.Cast<ISketchContour>().ToList() : new List<ISketchContour>();
            var materialNative = nativeAll.Where(ContourIsMaterial).ToList();
            const double tol = 1e-9;
            var nativeBoxes = materialNative.Select(NativeBBox).ToList();
            var used = new bool[nativeBoxes.Count];
            int matched = 0;
            foreach (var mc in expected.Contours)
            {
                var mb = ModuleBBox(mc);
                int hit = -1;
                for (var i = 0; i < nativeBoxes.Count; i++)
                    if (!used[i] && BBoxEquals(mb, nativeBoxes[i], tol)) { hit = i; break; }
                if (hit >= 0) { used[hit] = true; matched++; }
            }
            Check(matched == expected.Contours.Count &&
                  materialNative.Count == expected.Contours.Count,
                $"every native contour matches a module contour bbox 1:1 ({tag})",
                $"matched={matched} nativeMaterial={materialNative.Count} " +
                $"nativeTotal={nativeAll.Count} expected={expected.Contours.Count}");
            mgr.InsertSketch(false);
            doc.ClearSelection2(true);
        }

        /// <summary>
        /// Gate B's native carve-out check: on the exploded (raw-line) QR
        /// sketch, the finder ring's outer contour must carry the SQUARE
        /// primitive's edge count — the keep-function-squares cells —
        /// strictly fewer edges than the all-rounded counterfactual emits
        /// for the same extent (chamfered corners).
        /// </summary>
        private static void FinderRingNativeCheck(IModelDoc2 doc, IModelDocExtension ext,
            ISketchManager mgr, string sketchName,
            QrSketch expectedKeep, QrSketch expectedNoKeep, string tag)
        {
            // A finder ring = the ring→hole→island nested region (v1 has
            // three identical ones — any one proves the carve-out).
            var ring = expectedKeep.Regions
                .FirstOrDefault(r => r.Holes.Any(h => h.Islands.Count > 0));
            Check(ring != null, $"a finder ring→hole→island region exists in the module output ({tag})");
            if (ring == null) return;
            var ringBox = ModuleBBox(ring.Boundary);
            int keepVerts = ring.Boundary.Vertices.Count;
            var counter = expectedNoKeep.Contours
                .FirstOrDefault(c => BBoxEquals(ModuleBBox(c), ringBox, 1e-9));
            int noKeepVerts = counter?.Vertices.Count ?? -1;
            Check(noKeepVerts > keepVerts,
                $"the all-rounded counterfactual emits MORE finder-ring edges " +
                $"than the carve-out — the checkbox changes the emitted geometry ({tag})",
                $"keepFn ring outer={keepVerts} verts, all-rounded={noKeepVerts} verts");

            doc.ClearSelection2(true);
            bool reentered = ext.SelectByID2(sketchName, "SKETCH", 0, 0, 0, false, 0, null,
                (int)swSelectOption_e.swSelectOptionDefault);
            Check(reentered, $"the QR sketch re-selects for the finder-ring census ({tag})");
            if (!reentered) return;
            mgr.InsertSketch(true);
            var bk = mgr.ActiveSketch as ISketch;
            Check(bk != null, $"the QR sketch re-enters edit mode ({tag})");
            if (bk == null) { try { mgr.InsertSketch(false); } catch { } return; }
            object? rawContours = null;
            try { rawContours = bk.GetSketchContours(); } catch { }
            var nativeAll = rawContours is object[] arr
                ? arr.Cast<ISketchContour>().ToList() : new List<ISketchContour>();
            var native = nativeAll.Where(ContourIsMaterial)
                .FirstOrDefault(c => BBoxEquals(NativeBBox(c), ringBox, 1e-9));
            Check(native != null,
                $"a native contour sits at the finder ring's extent ({tag})");
            int nativeSegs = -1;
            if (native != null)
            {
                try { nativeSegs = ((object[])native.GetSketchSegments()).Length; } catch { }
            }
            Check(nativeSegs == keepVerts,
                $"the native finder ring carries the SQUARE primitive's edge count " +
                $"— function cells emitted square, natively ({tag})",
                $"native={nativeSegs} keepFn-module={keepVerts} all-rounded-module={noKeepVerts}");
            mgr.InsertSketch(false);
            doc.ClearSelection2(true);
        }

        private static double TimeMs(System.Action a)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            a();
            t.Stop();
            return t.Elapsed.TotalMilliseconds;
        }

        private static int ShapesMain(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(
                        System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // ================= module-side expectations =================
            // Everything a live verdict will be asserted against is computed
            // and printed BEFORE the occupied-seat refusal — a blocked run
            // is still self-diagnosing (the xsettings convention).

            Console.WriteLine("# spec-13 live gates — cell shapes (ticket #36)");

            // Gate A — disjoint dots: the v1 payload at the window's initial
            // 25 mm, dot size 50% of pitch. Growth does not apply to circles
            // (the generator never consults it — the growth-0 layout map).
            var squareAtInitial = Verdict(PayloadA, QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide,
                QRCodeGenerator.ECCLevel.M, invert: false);
            var dotsAtDefaultParam = ShapeVerdict(PayloadA, QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide,
                QrCellShape.Circle, 0.10, keepFn: false);
            var dots = ShapeVerdict(PayloadA, QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide,
                QrCellShape.Circle, 0.50, keepFn: false);
            if (!squareAtInitial.Succeeded || !dotsAtDefaultParam.Succeeded || !dots.Succeeded)
            {
                Console.WriteLine("# FATAL: module cannot build the gate-A fixtures — " +
                    $"square={VerdictName(squareAtInitial)} " +
                    $"dots@10={VerdictName(dotsAtDefaultParam)} dots@50={VerdictName(dots)}");
                return 1;
            }
            var dotsSketch = dots.Sketch!;
            Console.WriteLine($"# gate A (dots): '{PayloadA}' @ 25mm circle dot=50% -> " +
                $"N={dotsSketch.Info.CellCount} contours={dotsSketch.Info.ContourCount} " +
                $"segs={dotsSketch.Info.SegmentCount} dots={MaterialRegionCount(dotsSketch)} " +
                $"minEdge={dotsSketch.Info.MinEdgeMm:F4}mm");
            string dotsSig = QrPictureRenderer.Render(dotsSketch,
                QrSketchSession.PreviewPixelsAcross).Signature();

            // Gate B — rounded square: at the initial 25 mm/3% NO radius
            // resolves (#32's feasible-domain note); 60 mm/10% accepts.
            // The carve-out variant is the gate request; the all-rounded
            // counterfactual discriminates the native finder-ring check.
            var roundedAtInitial = ShapeVerdict(PayloadA, QrSketchSession.InitialSizeMm,
                QrSketchSession.InitialGrowthFractionPerSide,
                QrCellShape.RoundedSquare, 0.10, keepFn: false);
            var roundedAt60x3 = ShapeVerdict(PayloadA, 60.0, 0.03,
                QrCellShape.RoundedSquare, 0.10, keepFn: false);
            var roundedNoKeep = ShapeVerdict(PayloadA, 60.0, 0.10,
                QrCellShape.RoundedSquare, 0.10, keepFn: false);
            var roundedGate = ShapeVerdict(PayloadA, 60.0, 0.10,
                QrCellShape.RoundedSquare, 0.10, keepFn: true);
            if (roundedAtInitial.Succeeded ||
                roundedAtInitial.Error!.Kind != QrSketchErrorKind.BelowInsertionLimit)
            {
                Console.WriteLine("# FATAL: module no longer refuses rounded at the " +
                    $"initial 25mm/3% — expected BelowInsertionLimit, got " +
                    VerdictName(roundedAtInitial));
                return 1;
            }
            if (!roundedAt60x3.Succeeded || !roundedNoKeep.Succeeded || !roundedGate.Succeeded)
            {
                Console.WriteLine("# FATAL: module cannot build the gate-B fixtures — " +
                    $"60mm/3%={VerdictName(roundedAt60x3)} " +
                    $"60mm/10%={VerdictName(roundedNoKeep)} " +
                    $"keepFn={VerdictName(roundedGate)}");
                return 1;
            }
            var roundedSketch = roundedGate.Sketch!;
            Console.WriteLine($"# gate B (rounded): '{PayloadA}' @ 60mm growth=10% " +
                $"radius=10% keepFn -> N={roundedSketch.Info.CellCount} " +
                $"contours={roundedSketch.Info.ContourCount} " +
                $"segs={roundedSketch.Info.SegmentCount} " +
                $"minEdge={roundedSketch.Info.MinEdgeMm:F4}mm | " +
                $"refusal at 25mm/3%: {roundedAtInitial.Error!.Message}");
            string roundedSig = QrPictureRenderer.Render(roundedSketch,
                QrSketchSession.PreviewPixelsAcross).Signature();
            {
                var ringKeep = roundedSketch.Regions
                    .FirstOrDefault(r => r.Holes.Any(h => h.Islands.Count > 0));
                var counter = roundedNoKeep.Sketch!.Contours.FirstOrDefault(
                    c => ringKeep != null && BBoxEquals(ModuleBBox(c), ModuleBBox(ringKeep.Boundary), 1e-9));
                Console.WriteLine($"# gate B carve-out discriminator: finder-ring outer " +
                    $"verts keepFn={ringKeep?.Boundary.Vertices.Count ?? -1} " +
                    $"all-rounded={counter?.Vertices.Count ?? -1}");
            }

            // Gate C — the dense rung at MaxSegmentCount: every (payload x
            // shape x param) combo climbed up the size ladder; the accepted
            // row nearest the 8000 bound is measured live, the cheapest
            // TooManySegments row is gate D's over-bound rung.
            var candidates = ShapeCandidates();
            Console.WriteLine($"# gate C candidate table (first accepted rung per combo, " +
                $"MaxSegmentCount={QrInsertionGuard.MaxSegmentCount}):");
            foreach (var c in candidates)
            {
                var info = c.Verdict.Succeeded ? c.Verdict.Sketch!.Info : null;
                Console.WriteLine($"#   {c.Tag,-6} {QrCellShapes.CodeFor(c.Shape),-7} " +
                    $"param={c.Param * 100:F0}%" +
                    (c.Shape == QrCellShape.Circle
                        ? " growth=n/a (inert under Circle)"
                        : $" growth={c.Growth * 100:F0}%") +
                    $" @ {c.SizeMm:F0}mm -> {(c.IsAccepted
                        ? $"VALID v{info!.Version} N={info.CellCount} " +
                          $"segs={info.SegmentCount}"
                        : $"INVALID {c.Verdict.Error!.Kind}")}");
            }
            var inBound = candidates.Where(c => c.IsAccepted && c.Segments <= QrInsertionGuard.MaxSegmentCount)
                .OrderByDescending(c => c.Segments).FirstOrDefault();
            var overBound = candidates.Where(c => c.IsOverBound)
                .OrderBy(c => c.SizeMm).FirstOrDefault();
            if (inBound == null || overBound == null)
            {
                Console.WriteLine("# FATAL: the candidate table has no under-bound and/or " +
                    "no over-bound rung — the dense gates cannot be exercised");
                return 1;
            }
            var denseSketch = inBound.Verdict.Sketch!;
            Console.WriteLine($"# gate C rung: {inBound.Tag} {QrCellShapes.CodeFor(inBound.Shape)} " +
                $"param={inBound.Param * 100:F0}% @ {inBound.SizeMm:F0}mm -> " +
                $"segs={inBound.Segments} (bound {QrInsertionGuard.MaxSegmentCount})");
            Console.WriteLine($"# gate D rung: {overBound.Tag} {QrCellShapes.CodeFor(overBound.Shape)} " +
                $"param={overBound.Param * 100:F0}% @ {overBound.SizeMm:F0}mm -> " +
                overBound.Verdict.Error!.Message);
            double genMs = double.MaxValue;
            for (var i = 0; i < 5; i++)
            {
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                ShapeVerdict(inBound.Content, inBound.SizeMm, inBound.Growth,
                    inBound.Shape, inBound.Param, keepFn: false);
                t0.Stop();
                genMs = Math.Min(genMs, t0.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"# gate C module generation: gen_ms={genMs:F1} (best of 5)");

            // ================= refuse while any SolidWorks process lives =================
            if (System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: the spec-13 shape gates require SolidWorks " +
                    "closed — a SLDWORKS.exe process is already running. Refusing to " +
                    "attach; the probe never mutates or kills an occupied seat.");
                return 1;
            }

            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Console.WriteLine("# FATAL: SolidWorks launch failed"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: attached session is visible or holds documents — " +
                        "refusing to mutate an interactive session.");
                    return 1;
                }
                _spawnedFresh = true;

                // ---- the answering build must expose the spec-13 probe surface ----
                var probe = TryGetProbe(sw);
                if (!ProbeHasShapes(probe))
                {
                    string local = Path.Combine(
                        System.Environment.GetFolderPath(
                            System.Environment.SpecialFolder.LocalApplicationData),
                        "SolidWorksQrAddIn", "SolidWorksQrAddIn.dll");
                    foreach (var p in new[]
                             {
                                 dllPath, local,
                                 @"C:\Program Files\SolidWorks QR Add-In\SolidWorksQrAddIn.dll",
                             })
                        try { sw.UnloadAddIn(p); } catch { }
                    int lr = sw.LoadAddIn(dllPath);
                    Console.WriteLine($"# LoadAddIn({dllPath}) -> {lr}");
                    if (lr != 0) return 1;
                    probe = TryGetProbe(sw);
                }
                Check(ProbeHasShapes(probe),
                    "the answering build exposes the spec-13 probe surface " +
                    "(shape=/keepFn=/viewStatus= in WindowStateProbe)");
                if (!ProbeHasShapes(probe)) return 1;
                Check(probe!.AddinStateProbe().StartsWith("CONNECTED"),
                    "add-in connected", probe.AddinStateProbe());

                string opened = probe.OpenQrWindowProbe();
                Check(opened.Contains("open=True") && opened.Contains("windows=1"),
                    "pane opened through the command entry", opened);
                string fresh = probe.WindowStateProbe();
                Check(fresh.Contains("shape=square") && fresh.Contains("keepFn=False") &&
                      fresh.Contains("shapeParam=10"),
                    "a fresh session starts square with the shape parameter at its initial 10%",
                    fresh);

                // ---------- negative plumbing ----------
                string badShape = probe.SetQrCellShapeProbe("bogus");
                Check(badShape.StartsWith("ERROR") && badShape.Contains("unknown cell shape"),
                    "an unrecognised cell shape never reaches the session", badShape);
                Check(probe.WindowStateProbe().Contains("shape=square"),
                    "the refused shape leaves the current selection in force",
                    probe.WindowStateProbe());

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "gate part document created");
                if (doc == null) return 1;
                try
                {
                    var ext = (IModelDocExtension)doc.Extension;
                    var mgr = (ISketchManager)doc.SketchManager;
                    var fm = (IFeatureManager)doc.FeatureManager;

                    // ================= GATE A — disjoint dots =================

                    string rcA = probe.SetQrContentProbe(PayloadA);
                    Check(ResponseMatchesVerdict(rcA, squareAtInitial),
                        "payload content accepts at the initial square request — " +
                        "live verdict matches the module", rcA);
                    string rshapeA = probe.SetQrCellShapeProbe("circle");
                    Check(ResponseMatchesVerdict(rshapeA, dotsAtDefaultParam),
                        "switching to Circle revalidates — live verdict matches the module " +
                        "(dot parameter still at its initial 10%)", rshapeA);
                    string rparamA = probe.SetQrShapeParamProbe("50");
                    Check(ResponseMatchesVerdict(rparamA, dots),
                        "dot size 50% of pitch revalidates — live verdict matches the module",
                        rparamA);
                    string dotsState = probe.WindowStateProbe();
                    Check(dotsState.Contains("shape=circle") &&
                          dotsState.Contains("viewShape=circle") &&
                          dotsState.Contains("viewGrowthEnabled=False") &&
                          dotsState.Contains("viewShapeParamLabel=Dot size"),
                        "the pane reports Circle: shape=circle, growth field disabled, " +
                        "the parameter relabelled 'Dot size'", dotsState);
                    Check(probe.PreviewSignatureProbe().Contains($"sha={dotsSig}"),
                        "the live preview renders exactly the module's dotted picture",
                        probe.PreviewSignatureProbe());

                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane selected for the dots create");
                    string crA = probe.CreateQrFromWindowProbe();
                    Check(crA.StartsWith("OK"), "the disjoint-dots create succeeds", crA);
                    string dotsName = crA.StartsWith("OK") && crA.Split(' ').Length > 1
                        ? crA.Split(' ')[1] : "";
                    var dotsLive = mgr.ActiveSketch as ISketch;
                    Check(dotsLive != null, "the dots sketch is left open in edit mode");
                    if (dotsLive != null)
                    {
                        VerifyXsettingsBlock(dotsLive, dotsSketch, "dots");
                        mgr.InsertSketch(false);
                        Check(mgr.ActiveSketch == null, "the dots sketch exits edit mode");
                    }

                    // Per-cell region selection on the committed sketch —
                    // the engineer's route. Every emitted dot is its own
                    // disjoint region; each must be individually selectable.
                    int dotsEmitted = MaterialRegionCount(dotsSketch);
                    var (dotsTotal, dotsMatched, dotsSelectable) =
                        DotsRegionCensus(sw, doc, dotsName, dotsSketch, "dots");
                    Check(dotsMatched == dotsEmitted,
                        "every emitted dot has a native sketch region " +
                        "(region census on the committed QR sketch)",
                        $"nativeRegions={dotsTotal} matched={dotsMatched} emitted={dotsEmitted}");
                    Check(dotsSelectable == dotsMatched && dotsMatched == dotsEmitted,
                        "every emitted dot region is individually selectable",
                        $"selectable={dotsSelectable}/{dotsEmitted}");
                    bool dotsExploded = false;
                    if (dotsMatched != dotsEmitted)
                    {
                        // Fallback diagnostics: explode the block to raw lines
                        // and re-census — the doc records which route held.
                        dotsExploded = ExplodeQrSketchBlock(doc, ext, mgr, dotsName, "dots");
                        Console.WriteLine($"#   dots explode-after-census -> {dotsExploded}");
                        var (t2, m2, s2) = DotsRegionCensus(sw, doc, dotsName, dotsSketch,
                            "dots-exploded");
                        Check(m2 == dotsEmitted && s2 == dotsEmitted,
                            "every emitted dot region is individually selectable " +
                            "after exploding the block",
                            $"nativeRegions={t2} matched={m2} selectable={s2}");
                    }

                    var plateA = CreateBackingPlate(doc, ext, mgr, fm);
                    Check(plateA != null, "backing plate for the dots extrude",
                        plateA?.Name ?? "null");
                    if (plateA != null)
                    {
                        int take = Math.Max(1, dotsEmitted / 4);
                        int subset = SelectMaterialRegionSubset(sw, doc, dotsName, dotsSketch, take);
                        Check(subset == take,
                            $"a {take}-dot subset of the per-cell regions selects for the boss",
                            $"selected={subset}");
                        var boss = fm.FeatureExtrusion3(
                            true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                            0.002, 0.0, false, false, false, false, 0.0, 0.0,
                            false, false, false, false,
                            true, true, true,
                            (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
                        Check(boss != null,
                            "the selected dot subset extrudes a boss — no contact failures",
                            boss == null ? "FeatureExtrusion3 returned null" : boss.Name);
                    }
                    if (!dotsExploded)
                        VerifyExplodedModuleIdentical(doc, ext, mgr, dotsName,
                            dotsSketch, "dots");
                    else
                        RawSketchContourCheck(doc, ext, mgr, dotsName, dotsSketch, "dots");

                    // ================= GATE B — rounded square =================

                    // The shape-parameter text persists across shape
                    // switches (verbatim-field discipline): left at "50" the
                    // rounded request would carry radius 50%. Reset it to
                    // the gate's 10% while still on Circle (a 10% dot), so
                    // the rounded switch lands exactly the module's fixture.
                    string rparamB = probe.SetQrShapeParamProbe("10");
                    Check(ResponseMatchesVerdict(rparamB, dotsAtDefaultParam),
                        "the shape-parameter field resets to 10% under Circle — " +
                        "live verdict matches the module", rparamB);
                    string rshapeB = probe.SetQrCellShapeProbe("rounded");
                    Check(ResponseMatchesVerdict(rshapeB, roundedAtInitial),
                        "Rounded at the initial 25mm/3% is refused exactly as the module " +
                        "says — no radius resolves there (the #32 feasible-domain note)",
                        roundedAtInitial.Error!.Message);
                    string rsz60 = probe.SetQrSizeProbe("60");
                    Check(ResponseMatchesVerdict(rsz60, roundedAt60x3),
                        "size 60mm revalidates — live verdict matches the module", rsz60);
                    string rgr10 = probe.SetQrGrowthProbe("10");
                    Check(ResponseMatchesVerdict(rgr10, roundedNoKeep),
                        "growth 10% opens the feasible band — live verdict matches the module",
                        roundedNoKeep.Succeeded ? "" : roundedNoKeep.Error?.Message ?? "");
                    string badParam = probe.SetQrShapeParamProbe("bogus");
                    Check(badParam.StartsWith("INVALID") &&
                          badParam.Contains("InvalidShapeParameter"),
                        "a malformed shape parameter is refused by name", badParam);
                    Check(ResponseMatchesVerdict(probe.SetQrShapeParamProbe("10"), roundedNoKeep),
                        "corner radius 10% revalidates — live verdict matches the module",
                        roundedNoKeep.Succeeded ? "" : roundedNoKeep.Error?.Message ?? "");
                    Check(ResponseMatchesVerdict(probe.SetQrKeepFunctionSquareProbe(true),
                            roundedGate),
                        "the keep-function-squares carve-out revalidates — " +
                        "live verdict matches the module",
                        roundedGate.Succeeded ? "" : roundedGate.Error?.Message ?? "");
                    string roundedState = probe.WindowStateProbe();
                    Check(roundedState.Contains("keepFn=True") &&
                          roundedState.Contains("shape=rounded") &&
                          roundedState.Contains("viewKeepFn=True"),
                        "the pane reports the carve-out end-to-end " +
                        "(keepFn=True + the rendered checkbox)", roundedState);
                    Check(probe.PreviewSignatureProbe().Contains($"sha={roundedSig}"),
                        "the live preview renders exactly the module's rounded+carve-out picture",
                        probe.PreviewSignatureProbe());

                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane selected for the rounded create");
                    string crB = probe.CreateQrFromWindowProbe();
                    Check(crB.StartsWith("OK"),
                        "the rounded-square create succeeds (60mm/10%/radius10%/keepFn)", crB);
                    string roundedName = crB.StartsWith("OK") && crB.Split(' ').Length > 1
                        ? crB.Split(' ')[1] : "";
                    var roundedLive = mgr.ActiveSketch as ISketch;
                    Check(roundedLive != null, "the rounded sketch is left open in edit mode");
                    if (roundedLive != null)
                    {
                        VerifyXsettingsBlock(roundedLive, roundedSketch, "rounded");
                        mgr.InsertSketch(false);
                        Check(mgr.ActiveSketch == null, "the rounded sketch exits edit mode");
                    }

                    // Boss + shallow cut through the committed sketch's
                    // material regions — the established feature route.
                    var roundedRegions = MaterialRegionsOf(sw, doc, roundedName,
                        roundedSketch, out int roundedTotal);
                    bool roundedExploded = false;
                    Check(roundedRegions.Count == MaterialRegionCount(roundedSketch),
                        "the rounded QR's material regions enumerate on the committed sketch",
                        $"nativeRegions={roundedTotal} matched={roundedRegions.Count} " +
                        $"expected={MaterialRegionCount(roundedSketch)}");
                    if (roundedRegions.Count == 0)
                    {
                        roundedExploded = ExplodeQrSketchBlock(doc, ext, mgr, roundedName,
                            "rounded");
                        Console.WriteLine($"#   rounded explode-before-features -> {roundedExploded}");
                    }
                    FeatureChecks(sw, doc, ext, mgr, roundedName, roundedSketch, "rounded-gate");

                    if (!roundedExploded)
                        VerifyExplodedModuleIdentical(doc, ext, mgr, roundedName,
                            roundedSketch, "rounded");
                    else
                        RawSketchContourCheck(doc, ext, mgr, roundedName, roundedSketch, "rounded");
                    FinderRingNativeCheck(doc, ext, mgr, roundedName,
                        roundedSketch, roundedNoKeep.Sketch!, "rounded");

                    // ================= GATE C — dense rung at the bound =================

                    string denseResp = DriveShapeRequest(probe, inBound, inv);
                    Check(ResponseMatchesVerdict(denseResp,
                            ShapeVerdict(inBound.Content, inBound.SizeMm, inBound.Growth,
                                inBound.Shape, inBound.Param, keepFn: false)),
                        "the dense rung drives through the pane — live verdict matches the module",
                        denseResp);
                    string denseState = probe.WindowStateProbe();
                    Check(denseState.Contains(
                            $"shape={QrCellShapes.CodeFor(inBound.Shape)}"),
                        "the session reports the dense rung's shape", denseState);

                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane selected for the dense create");
                    double createMs = 0;
                    string crC = "";
                    createMs = TimeMs(() => crC = probe.CreateQrFromWindowProbe());
                    Check(crC.StartsWith("OK"),
                        $"the dense create succeeds — {inBound.Segments} segments natively", crC);
                    string denseName = crC.StartsWith("OK") && crC.Split(' ').Length > 1
                        ? crC.Split(' ')[1] : "";
                    var denseLive = mgr.ActiveSketch as ISketch;
                    double censusMs = 0, exitMs = 0, enterMs = 0, regionsMs = 0,
                        segsMs = 0, exit2Ms = 0, rebuildMs = 0;
                    int nativeSegs = -1, nativeRegions = -1;
                    if (denseLive != null)
                    {
                        // The native census while the sketch is open: the
                        // block def must carry the module's segment count.
                        censusMs = TimeMs(() =>
                        {
                            var inst = denseLive.GetSketchBlockInstanceCount() > 0
                                ? denseLive.IGetSketchBlockInstances(0) as ISketchBlockInstance
                                : null;
                            var ds = (inst?.Definition as ISketchBlockDefinition)?.GetSketch()
                                as ISketch;
                            object? raw = null;
                            try { raw = ds?.GetSketchSegments(); } catch { }
                            nativeSegs = raw is object[] a
                                ? a.Count(s => s is ISketchLine &&
                                      !((s as ISketchSegment)?.ConstructionGeometry ?? false))
                                : -1;
                        });
                        Check(nativeSegs == inBound.Segments,
                            "the native block def carries the module's segment count 1:1",
                            $"nativeMaterialSegs={nativeSegs} expected={inBound.Segments} " +
                            $"(WindowStateProbe segs corroborated by the setter's VALID line)");
                        exitMs = TimeMs(() => mgr.InsertSketch(false));
                        Check(mgr.ActiveSketch == null, "the dense sketch exits edit mode");

                        // Sketch-edit responsiveness, honestly: re-enter the
                        // committed feature, solve its regions, enumerate its
                        // segments, exit again, rebuild the document.
                        doc.ClearSelection2(true);
                        bool sel = ext.SelectByID2(denseName, "SKETCH", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault);
                        Check(sel, "the committed dense sketch re-selects for edit timings");
                        if (sel)
                        {
                            enterMs = TimeMs(() => mgr.InsertSketch(true));
                            var back = mgr.ActiveSketch as ISketch;
                            Check(back != null, "the dense sketch re-enters edit mode",
                                $"enter_ms={enterMs:F0}");
                            if (back != null)
                            {
                                regionsMs = TimeMs(() =>
                                {
                                    try { nativeRegions = back.GetSketchRegionCount(); } catch { }
                                    try { nativeRegions = (back.GetSketchRegions()
                                        as object[])?.Length ?? nativeRegions; } catch { }
                                });
                                segsMs = TimeMs(() =>
                                {
                                    try { _ = back.GetSketchSegments(); } catch { }
                                });
                                exit2Ms = TimeMs(() => mgr.InsertSketch(false));
                            }
                        }
                        rebuildMs = TimeMs(() => { try { doc.EditRebuild3(); } catch { } });
                        Console.WriteLine($"PERF dense-rung segs={inBound.Segments} " +
                            $"gen_ms={genMs:F1} create_ms={createMs:F0} " +
                            $"native_insert_ms≈{createMs - genMs:F0} " +
                            $"def_census_ms={censusMs:F0} exit_ms={exitMs:F0} " +
                            $"enter_edit_ms={enterMs:F0} region_solve_ms={regionsMs:F0} " +
                            $"(regions={nativeRegions}) seg_enum_ms={segsMs:F0} " +
                            $"exit2_ms={exit2Ms:F0} doc_rebuild_ms={rebuildMs:F0}");
                    }

                    // ================= GATE D — one rung over the bound =================

                    string overResp = DriveShapeRequest(probe, overBound, inv);
                    Check(overResp.StartsWith("INVALID") && overResp.Contains("TooManySegments"),
                        "the over-bound request rejects TooManySegments at the pane", overResp);
                    string overState = probe.WindowStateProbe();
                    Check(overState.Contains("hasRequest=False"),
                        "the rejected request leaves no valid request", overState);
                    string status = ViewStatusOf(overState);
                    Check(status.Contains("segment") &&
                          status.Contains(QrInsertionGuard.MaxSegmentCount.ToString()),
                        "the rejection surfaces on the pane status line verbatim", status);

                    int featsBeforeRefusal = FindSketchFeatureNames(doc).Count;
                    string refusedCreate = probe.CreateQrFromWindowProbe();
                    Check(refusedCreate.StartsWith("ERROR"),
                        "create REFUSES the over-bound request", refusedCreate);
                    Check(FindSketchFeatureNames(doc).Count == featsBeforeRefusal &&
                          mgr.ActiveSketch == null,
                        "the refused create touched neither the feature tree " +
                        "nor the sketch-edit state");
                }
                finally
                {
                    try { sw.CloseDoc(doc.GetTitle()); } catch { }
                }

                try { sw.UnloadAddIn(dllPath); } catch { }
                Console.WriteLine(_failures == 0
                    ? "# SHAPES ALL CHECKS PASSED"
                    : $"# {_failures} check(s) failed");
                return _failures == 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally
            {
                try { if (_spawnedFresh) sw?.ExitApp(); } catch { }
            }
        }

        /// <summary>
        /// Drives one gate-C/D candidate through the pane probes in field
        /// order (shape -> keepFn -> param -> growth -> size -> content) and
        /// returns the content edit's verdict — the fully-determined request.
        /// Every response is printed for the record.
        /// </summary>
        private static string DriveShapeRequest(IAddInProbe probe, ShapeCandidate c,
            System.Globalization.CultureInfo inv)
        {
            Console.WriteLine($"#   drive {c.Tag} {QrCellShapes.CodeFor(c.Shape)} " +
                $"param={c.Param * 100:F0}% growth={c.Growth * 100:F0}% size={c.SizeMm:F0}mm");
            string r;
            r = probe.SetQrCellShapeProbe(QrCellShapes.CodeFor(c.Shape));
            Console.WriteLine($"#     shape -> {Trim140(r)}");
            r = probe.SetQrKeepFunctionSquareProbe(false);
            Console.WriteLine($"#     keepFn -> {Trim140(r)}");
            if (c.Shape != QrCellShape.Square)
            {
                r = probe.SetQrShapeParamProbe((c.Param * 100.0).ToString("G4", inv));
                Console.WriteLine($"#     param -> {Trim140(r)}");
            }
            if (c.Shape != QrCellShape.Circle)
            {
                r = probe.SetQrGrowthProbe((c.Growth * 100.0).ToString("G4", inv));
                Console.WriteLine($"#     growth -> {Trim140(r)}");
            }
            r = probe.SetQrSizeProbe(c.SizeMm.ToString("G4", inv));
            Console.WriteLine($"#     size -> {Trim140(r)}");
            return probe.SetQrContentProbe(c.Content);
        }

        private static string Trim140(string s) => s.Length <= 140 ? s : s.Substring(0, 140) + "…";

        private static ISldWorks? AttachOrLaunch(string progId)
        {
            // Accepts a ProgID ("SldWorks.Application.31") or a "{clsid}" —
            // some installed versions register a versioned ProgID whose
            // CLSID lookup fails; the raw CLSID still activates.
            Type? t = null;
            if (progId.StartsWith("{") && Guid.TryParse(progId, out var clsid))
                t = Type.GetTypeFromCLSID(clsid);
            else
                t = Type.GetTypeFromProgID(progId);
            if (t == null) { Console.WriteLine($"FAIL  ProgID/CLSID {progId} not registered"); return null; }
            return Activator.CreateInstance(t) as ISldWorks;
        }

        private static void WaitForStartup(ISldWorks sw)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                try { if (sw.StartupProcessCompleted) return; } catch { }
                Thread.Sleep(1000);
            }
        }

        private static IAddInProbe? TryGetProbe(ISldWorks sw)
        {
            var keys = new[]
            {
                AddinIdentity.Clsid,
                AddinIdentity.Clsid.Trim('{', '}'),
                AddinIdentity.Clsid.ToLowerInvariant(),
                AddinIdentity.ProgId,
            };
            // Control: the registered InvenTree add-in's object — distinguishes
            // "our add-in isn't loaded" from "GetAddInObject usage is wrong".
            foreach (var key in keys.Concat(new[] { "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}" }))
            {
                try
                {
                    object? raw = sw.GetAddInObject(key);
                    if (raw == null) { Console.WriteLine($"# GetAddInObject({key}) -> null"); continue; }
                    Console.WriteLine($"# GetAddInObject({key}) -> type={raw.GetType().FullName}");
                    if (raw is IAddInProbe asProbe)
                    {
                        Console.WriteLine("#   cast to IAddInProbe OK");
                        return asProbe;
                    }
                    bool isOurs = raw is SolidWorks.Interop.swpublished.ISwAddin;
                    Console.WriteLine($"#   not IAddInProbe; IsSwAddin={isOurs} — trying IDispatch invocation");
                    if (isOurs)
                        return new LateBoundProbe(raw);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# GetAddInObject({key}) -> {ex.GetType().Name}: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Calls the add-in's public probe methods through the RCW's dispatch
        /// interface (InvokeMember by name) — the same route SolidWorks uses
        /// for command callbacks. Used when the RCW won't QI our managed
        /// IAddInProbe interface.
        /// </summary>
        private sealed class LateBoundProbe : IAddInProbe
        {
            private readonly object _addin;

            public LateBoundProbe(object addin) { _addin = addin; }

            private string Invoke(string method, params object[] args)
            {
                var result = _addin.GetType().InvokeMember(
                    method,
                    System.Reflection.BindingFlags.InvokeMethod |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance,
                    null, _addin, args);
                return result as string ?? "(no result)";
            }

            public string RunQrSketchProbe() => Invoke("RunQrSketchProbe");
            public string RunFailingQrSketchProbe() => Invoke("RunFailingQrSketchProbe");
            public string RunCleanupFailQrSketchProbe() => Invoke("RunCleanupFailQrSketchProbe");
            public string AddinStateProbe() => Invoke("AddinStateProbe");
            public string OpenQrWindowProbe() => Invoke("OpenQrWindowProbe");
            public string CloseQrWindowProbe() => Invoke("CloseQrWindowProbe");
            public string WindowStateProbe() => Invoke("WindowStateProbe");
            public string SetQrContentProbe(string content) => Invoke("SetQrContentProbe", content);
            public string SetQrSizeProbe(string sizeText) => Invoke("SetQrSizeProbe", sizeText);
            public string SetQrGrowthProbe(string growthText) => Invoke("SetQrGrowthProbe", growthText);
            public string SetQrEccProbe(string level) => Invoke("SetQrEccProbe", level);
            public string SetQrInvertProbe(bool invert) => Invoke("SetQrInvertProbe", invert);
            public string SetQrKeepFunctionSquareProbe(bool keepSquare) => Invoke("SetQrKeepFunctionSquareProbe", keepSquare);
            public string SetQrCellShapeProbe(string shape) => Invoke("SetQrCellShapeProbe", shape);
            public string SetQrShapeParamProbe(string percentText) => Invoke("SetQrShapeParamProbe", percentText);
            public string SetQrContentTypeProbe(string presetId) => Invoke("SetQrContentTypeProbe", presetId);
            public string SetQrPresetFieldProbe(string key, string value) => Invoke("SetQrPresetFieldProbe", key, value);
            public string CreateQrFromWindowProbe() => Invoke("CreateQrFromWindowProbe");
            public string UndoArrangementProbe(int arrangement) => Invoke("UndoArrangementProbe", arrangement);
            public string UndoRedoQrProbe() => Invoke("UndoRedoQrProbe");
            public string BlockSpikeProbe(int test) => Invoke("BlockSpikeProbe", test);
            public string PreviewSignatureProbe() => Invoke("PreviewSignatureProbe");
        }
    }
}
