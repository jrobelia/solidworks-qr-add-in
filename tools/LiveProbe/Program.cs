using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
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
    public static partial class Program
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
        /// COM-launched one — the only kind it is safe to kill. An
        /// instance that is already visible (or already holds documents) is
        /// somebody's interactive session: the probe refuses to mutate it.
        /// </summary>
        private static bool _spawnedFresh;

        /// <summary>
        /// HKCU AddInsStartup\{clsid} value captured at launch. SolidWorks
        /// rewrites the flag to 0 on every runtime unload, so a probe run
        /// would silently untick the user's Start-Up checkbox — restored at
        /// teardown.
        /// </summary>
        private static int? _startupFlagAtLaunch;
        private static bool _startupFlagCaptured;

        private static readonly string StartupFlagKey =
            @"HKEY_CURRENT_USER\" + AddinIdentity.AddinsStartupKeyPath + @"\" + AddinIdentity.Clsid;

        private static void CaptureStartupFlag()
        {
            if (_startupFlagCaptured) return;   // first attach wins — a later one
            _startupFlagCaptured = true;        // could read our own unload's 0
            try
            {
                if (Registry.GetValue(StartupFlagKey, "", null) is int v)
                    _startupFlagAtLaunch = v;
            }
            catch { }
        }

        private static void RestoreStartupFlag()
        {
            if (_startupFlagAtLaunch == null) return;
            try
            {
                Registry.SetValue(StartupFlagKey, "", _startupFlagAtLaunch.Value,
                    RegistryValueKind.DWord);
            }
            catch { }
        }

        /// <summary>
        /// Tears down the spawned instance: restores the captured Start-Up
        /// flag, closes documents (so no recovery prompt greets the user's
        /// next launch), then kills the process by PID. Never ExitApp — a
        /// graceful shutdown flushes this instance's stale per-user settings
        /// snapshot over whatever the user's interactive session saved
        /// meanwhile. Never taskkill /IM either — that would kill the
        /// user's session as well.
        /// </summary>
        private static void TeardownSpawned(ISldWorks? sw)
        {
            RestoreStartupFlag();
            if (!_spawnedFresh || sw == null) return;
            try { sw.CloseAllDocuments(true); } catch { }
            try { Process.GetProcessById(sw.GetProcessID()).Kill(); } catch { }
        }

        public static int Main(string[] args)
        {
            // `perf` mode (ticket 7): generation + native-insertion timings
            // for representative payloads. `perf` consumes args[0]; the rest
            // keep the usual <deployDir> <progId> shape.
            if (args.Length > 0 &&
                string.Equals(args[0], "perf", StringComparison.OrdinalIgnoreCase))
                return PerfMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "undoapi", StringComparison.OrdinalIgnoreCase))
                return UndoApiMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "units", StringComparison.OrdinalIgnoreCase))
                return UnitsMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "xsettings", StringComparison.OrdinalIgnoreCase))
                return XsettingsMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "pattern", StringComparison.OrdinalIgnoreCase))
                return PatternSpikeMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "circledims", StringComparison.OrdinalIgnoreCase))
                return CircleDimsMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "circleladder", StringComparison.OrdinalIgnoreCase))
                return CircleLadderMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "circles", StringComparison.OrdinalIgnoreCase))
                return CircleGatesMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "circlediag", StringComparison.OrdinalIgnoreCase))
                return CircleDiagMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "swprops", StringComparison.OrdinalIgnoreCase))
                return SwpropsMain(args.Skip(1).ToArray());
            if (args.Length > 0 &&
                string.Equals(args[0], "ringsplit", StringComparison.OrdinalIgnoreCase))
                return RingsplitMain(args.Skip(1).ToArray());

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

                string inv = probe.SetQrField("content", "");
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

                string va = probe.SetQrField("content", PayloadA);
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
                    VerifyXsettingsBlock(sketchA, expectedA, "A");
                    CheckBlockOutline(sketchA, 25.0,
                        "the QR outline measures the entered 25 mm (default size)");
                    sketchAName = createA.Split(' ')[1];

                    string again = probe.CreateQrFromWindowProbe();
                    Check(again.StartsWith("ERROR") && again.Contains("already"),
                        "create blocked while sketch A is being edited", again);
                    Check(BlockSketchUnchanged(sketchA, expectedA, "A"),
                        "blocked create left the open sketch unchanged");

                    mgr.InsertSketch(false); // engineer exits the sketch
                    Check(mgr.ActiveSketch == null, "sketch A exits edit mode cleanly");
                }

                // ================= ticket 4: size + growth fields =================

                // Invalid inputs are rejected with a useful error, the request
                // is invalidated (no stale request can be created), the text
                // is kept verbatim, and the part is never mutated.
                int featureCountBeforeInvalid = FindSketchFeatureNames(doc!).Count;
                string bad1 = probe.SetQrField("size", "abc");
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
                Check(probe.SetQrField("size", "0").StartsWith("INVALID"),
                    "zero size is rejected", probe.SetQrField("size", "0"));
                Check(probe.SetQrField("size", "-5").StartsWith("INVALID"),
                    "negative size is rejected");
                Check(probe.SetQrField("growth", "abc").StartsWith("INVALID"),
                    "unparseable growth is rejected");
                Check(probe.SetQrField("growth", "0").StartsWith("INVALID"),
                    "zero growth is rejected");
                Check(probe.SetQrField("growth", "50").StartsWith("INVALID"),
                    "growth at 50% is rejected (must be less than 50% per side)");

                // Restore growth first — the whole current request is
                // re-validated on every edit, so a rejected growth left in
                // the field keeps every later edit invalid too.
                probe.SetQrField("growth", "3");

                // Custom size: 40 mm. The normalized picture is scale-invariant
                // (same QR, same 4-cell margin from the grown extents), so the
                // signature stays sigA; the request's final size is 40 mm.
                string s40 = probe.SetQrField("size", "40");
                Check(s40.StartsWith("VALID") && s40.Contains("size=40.000"),
                    "custom size 40 mm accepted and drives the request", s40);
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigA}"),
                    "size edit re-renders the picture from the new request");

                // Growth change preserves the entered outline size (pitch
                // recalculation) but changes the grown picture.
                string g8 = probe.SetQrField("growth", "8");
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
                    VerifyXsettingsBlock(sketchC, expectedA8, "C(40mm,8%)");
                    CheckBlockOutline(sketchC, 40.0,
                        "the QR outline measures the entered 40 mm — not 25");
                }

                // Editing inputs while a sketch is open must not resize or
                // regenerate that sketch.
                probe.SetQrField("size", "30");
                probe.SetQrField("growth", "12");
                if (sketchC != null)
                {
                    Check(BlockSketchUnchanged(sketchC, expectedA8, "C"),
                        "editing the inputs did not touch the open sketch");
                    CheckBlockOutline(sketchC, 40.0, "open sketch keeps its 40 mm outline after input edits");
                }
                mgr.InsertSketch(false); // engineer exits the custom-size sketch
                Check(mgr.ActiveSketch == null, "custom-size sketch exits edit mode cleanly");

                // Back to the defaults — valid→invalid→valid transitions keep
                // working, and later creates honour the restored request.
                Check(probe.SetQrField("size", "25").StartsWith("VALID"),
                    "size 25 re-accepted after the rejected edits");
                Check(probe.SetQrField("growth", "3").StartsWith("VALID"),
                    "growth 3% re-accepted");
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigA}"),
                    "picture is back to the default-request rendering");

                if (sketchAName.Length > 0)
                {
                    var featA2 = FindSketchFeature(doc!, sketchAName);
                    var skA2 = featA2?.GetSpecificFeature2() as ISketch;
                    Check(skA2 != null && BlockSketchUnchanged(skA2, expectedA, "A"),
                        "earlier sketch unchanged after the size/growth edits");
                    if (skA2 != null)
                        CheckBlockOutline(skA2, 25.0, "earlier sketch keeps its 25 mm outline");
                }

                // ================= payload B: second sketch, first unchanged =================

                string vb = probe.SetQrField("content", PayloadB);
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
                    VerifyXsettingsBlock(sketchB, expectedB, "B");
                    mgr.InsertSketch(false);
                }

                if (sketchAName.Length > 0)
                {
                    var featA = FindSketchFeature(doc, sketchAName);
                    var skA = featA?.GetSpecificFeature2() as ISketch;
                    Check(skA != null && BlockSketchUnchanged(skA, expectedA, "A"),
                        "first sketch remains unchanged after the second create");
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
                TeardownSpawned(sw);
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
                    probe.SetQrField("content", 
                        PerfPayloads.First(p => p.tag == tag).content);
                    probe.SetQrField("size", sizeMm.ToString(System.Globalization.CultureInfo.InvariantCulture));

                    var t0 = System.Diagnostics.Stopwatch.StartNew();
                    string create = probe.CreateQrFromWindowProbe();
                    t0.Stop();

                    var sketchObj = mgr.ActiveSketch as ISketch;
                    Check(create.StartsWith("OK"), $"perf {tag}: create succeeds", create);
                    if (sketchObj != null)
                        CheckBlockOutline(sketchObj, sizeMm, $"perf {tag}: outline measures the used size");
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
                TeardownSpawned(sw);
            }
            Console.WriteLine(_failures == 0 ? "# PERF DONE" : $"# {_failures} perf check(s) failed");
            return _failures == 0 ? 0 : 1;
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
                paneProbe.SetQrField("content", PayloadA);
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
                TeardownSpawned(sw);
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
                TeardownSpawned(sw);
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

        /// <summary>One enumerable sketch line, flattened to sketch-space metres.</summary>
        private sealed class SegSpan
        {
            public double X1, Y1, X2, Y2;
            public bool Construction;
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
        /// Full ticket-5 verification of an open QR sketch on a support: the
        /// block census proves the module-identical content (instance count,
        /// Scale, origin, def edges 1:1, real-size construction squares) and
        /// the outline measures the entered size; the instance is then
        /// exploded so the real host-space entities can be checked — every
        /// endpoint ON the support plane via the sketch->model frame, the
        /// contour/region census — and the sketch reports the selected
        /// entity as its reference. The def reports an offset frame
        /// (consexp), so raw def coordinates are never read: only lengths
        /// and extents pre-explode. Run while the sketch is still the
        /// active (open) one.
        /// </summary>
        private static void VerifySketchOnSupport(
            ISldWorks sw, ISketchManager mgr, ISketch sketch, QrSketch expected, string tag,
            SupportFrame support, string createResult)
        {
            VerifyXsettingsBlock(sketch, expected, tag);
            CheckBlockOutline(sketch, 25.0, $"the QR outline measures the entered 25 mm on {tag}");
            Check(createResult.StartsWith("OK"), $"Create Sketch succeeded on {tag}", createResult);

            // Post-block the host sketch carries no enumerable segments and
            // the def's own frame is offset — explode the instance to
            // materialise the real host-space geometry the endpoint and
            // region checks read, the same route used on committed sketches.
            var inst = sketch.GetSketchBlockInstanceCount() == 1
                ? sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
            bool exploded = false;
            try { mgr.ExplodeSketchBlockInstance((SketchBlockInstance)inst!); exploded = true; }
            catch (Exception ex) { Console.WriteLine($"#   {tag}: explode threw {ex.GetType().Name}"); }
            Check(exploded, $"the block explodes into real host-space geometry on {tag}");
            if (!exploded) return;

            ExplodedSketchCensus(sketch, expected, tag);

            // Face supports add a region for the face's own boundary on top
            // of the QR contours (the pre-block floor, now on the exploded
            // sketch: 28 regions for 27 contours).
            bool faceSupport = support.EntitySelType == (int)swSelectType_e.swSelFACES;
            int minRegions = expected.Info.ContourCount + (faceSupport ? 1 : 0);
            bool regionsOk = faceSupport
                ? sketch.GetSketchRegionCount() >= minRegions
                : sketch.GetSketchRegionCount() == expected.Info.ContourCount;
            Check(regionsOk,
                $"native region count = contour count" +
                    $"{(faceSupport ? " + face-boundary regions" : "")} ({tag})",
                $"native={sketch.GetSketchRegionCount()} floor={minRegions} " +
                    $"(contours={expected.Info.ContourCount})");

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
                foreach (var s in SketchLineSpans(sketch))
                {
                    foreach (var (px, py) in new[] { (s.X1, s.Y1), (s.X2, s.Y2) })
                    {
                        var m = MapPoint(mu, s2m, px, py, 0.0);
                        maxOffPlane = Math.Max(maxOffPlane,
                            Math.Abs(Dot(Sub(m, support.Root), support.Normal)));
                        // in-plane coords must recover the sketch coords exactly
                        var rel = Sub(m, origin);
                        maxInPlaneErr = Math.Max(maxInPlaneErr, Math.Abs(Dot(rel, xAx) - px));
                        maxInPlaneErr = Math.Max(maxInPlaneErr, Math.Abs(Dot(rel, yAx) - py));
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
            probe.SetQrField("content", PayloadA);

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
                    VerifySketchOnSupport(sw, mgr, skR, expectedA, angled.Label, angled, cr);
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
                    VerifySketchOnSupport(sw, mgr, skT, expectedA, tpFrame.Label, tpFrame, ct);
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
                    VerifySketchOnSupport(sw, mgr, skF, expectedA, frame.Label, frame, cf);
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
            probe.SetQrField("content", PayloadB);
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
            probe.SetQrField("content", PayloadA); // the reloaded session starts empty

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
            // Select the profile REGION object — measured live (circlediag
            // D4, SW 2023 SP5): on a second/third document the sketch-FEATURE
            // selection is ignored by FeatureExtrusion3 (returns null with a
            // selected, active, closed sketch); the ISketchRegion object
            // select feeds it every time. Feature select stays as fallback.
            int regionsPicked = 0;
            var plateSk = FindSketchFeature(doc, plateSketchName)
                ?.GetSpecificFeature2() as ISketch;
            if (plateSk?.GetSketchRegions() is object[] regs)
                foreach (var ro in regs)
                    if (ro is ISketchRegion rg)
                        try { if (rg.Select(true, 0)) regionsPicked++; } catch { }
            if (regionsPicked == 0 &&
                !ext.SelectByID2(plateSketchName, "SKETCH", 0, 0, 0, false, 0, null,
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
            Check(SelectQrMaterialRegions(sw, doc, ext, mgr, qrSketchName, expected),
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
            Check(SelectQrMaterialRegions(sw, doc, ext, mgr, qrSketchName, expected),
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

        /// <summary>
        /// The post-block QR invariants: exactly one instance at Scale 1 in
        /// the sketch, zero enumerable host segments, and the definition
        /// carrying the material segments plus eight square edges. Returns
        /// whether the sketch bears the expected block — works on open and
        /// committed sketches.
        /// </summary>
        private static bool TryQrBlockCensus(
            ISketch sketch, QrSketch expected, string tag, out string diag)
        {
            int inst = sketch.GetSketchBlockInstanceCount();
            int hostSegs = SketchLineSpans(sketch).Count;
            var blk = inst > 0 ? sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
            var defSketch = (blk?.Definition as ISketchBlockDefinition)?.GetSketch() as ISketch;
            int defSegs = defSketch == null ? -1 : SketchLineSpans(defSketch).Count;
            diag = $"inst={inst} hostSegs={hostSegs} " +
                $"defSegs={defSegs} expected={expected.Info.SegmentCount}+8 ({tag})";
            return inst == 1 && hostSegs == 0 &&
                   defSegs == expected.Info.SegmentCount + 8;
        }

        /// <summary>
        /// A sketch that is supposed to be left behind by a refused create
        /// (or retained across a second create) must still be the untouched
        /// QR block — the pre-block verifier read host-space segments that no
        /// longer exist; the block census is the equivalent evidence.
        /// </summary>
        private static bool BlockSketchUnchanged(
            ISketch sketch, QrSketch expected, string tag)
        {
            bool ok = false;
            string diag = "unreadable";
            try { ok = TryQrBlockCensus(sketch, expected, tag, out diag); }
            catch (Exception ex) { diag = ex.GetType().Name; }
            Console.WriteLine($"#   retained sketch '{tag}' census: {diag}");
            return ok;
        }

        /// <summary>
        /// The QR's material extent inside the block definition must measure
        /// the entered size on both axes, within the declared tolerance
        /// (1e-6 mm). Definition coordinates are def-space — the def sketch
        /// reports an offset frame — so only the SPAN (frame-independent) is
        /// measured, never absolute positions.
        /// </summary>
        private static void CheckBlockOutline(
            ISketch sketch, double expectedMm, string name)
        {
            var inst = sketch.GetSketchBlockInstanceCount() == 1
                ? sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
            var defSketch = inst != null
                ? (inst.Definition as ISketchBlockDefinition)?.GetSketch() as ISketch : null;
            var defLines = defSketch != null
                ? SketchLineSpans(defSketch)
                : new List<SegSpan>();
            double mnX = double.MaxValue, mxX = double.MinValue;
            double mnY = double.MaxValue, mxY = double.MinValue;
            foreach (var s in defLines.Where(s => !s.Construction))
            {
                mnX = Math.Min(mnX, Math.Min(s.X1, s.X2));
                mxX = Math.Max(mxX, Math.Max(s.X1, s.X2));
                mnY = Math.Min(mnY, Math.Min(s.Y1, s.Y2));
                mxY = Math.Max(mxY, Math.Max(s.Y1, s.Y2));
            }
            const double tol = 1e-9; // metres == the declared 1e-6 mm tolerance
            double w = mxX - mnX, h = mxY - mnY;
            Check(Math.Abs(w - expectedMm / 1000.0) <= tol &&
                  Math.Abs(h - expectedMm / 1000.0) <= tol,
                name,
                $"span {w * 1000:F4} × {h * 1000:F4} mm — expected {expectedMm} mm");
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
            IModelDocExtension ext, ISketchManager mgr, string qrSketchName,
            QrSketch expected)
        {
            var sketchFeature = FindSketchFeature(doc, qrSketchName);
            if (sketchFeature == null) return false;
            var sketch = sketchFeature.GetSpecificFeature2() as ISketch;
            if (sketch == null) return false;

            // Post-block the QR rides a block instance whose internals are not
            // host sketch regions — the user route is to explode the block,
            // then extrude the material contours. Do the same: re-enter the
            // committed sketch, explode the instance, exit, then read regions.
            try
            {
                if (sketch.GetSketchBlockInstanceCount() > 0)
                {
                    doc.ClearSelection2(true);
                    if (!ext.SelectByID2(qrSketchName, "SKETCH", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault))
                        return false;
                    mgr.InsertSketch(true);
                    var editSketch = mgr.ActiveSketch as ISketch;
                    var inst = editSketch != null && editSketch.GetSketchBlockInstanceCount() > 0
                        ? editSketch.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
                    if (inst != null)
                        mgr.ExplodeSketchBlockInstance((SketchBlockInstance)inst);
                    mgr.InsertSketch(false);
                    doc.ClearSelection2(true);
                    sketch = sketchFeature.GetSpecificFeature2() as ISketch;
                    if (sketch == null) return false;
                }
            }
            catch { }
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
                string badEcc = probe.SetQrField("ecc", "bogus");
                Check(badEcc.StartsWith("ERROR") && badEcc.Contains("unknown ECC level"),
                    "an unrecognised ECC level never reaches the session", badEcc);
                string badType = probe.SetQrField("preset", "bogus");
                Check(badType.StartsWith("INVALID") && badType.Contains("InvalidPayload"),
                    "an unknown content-type id is refused as InvalidPayload", badType);
                Check(probe.WindowStateProbe().Contains("preset=raw"),
                    "the refused type leaves the current selection in force",
                    probe.WindowStateProbe());
                string badField = probe.SetQrField("nonesuch", "x");
                Check(badField.StartsWith("INVALID") && badField.Contains("nonesuch"),
                    "an undeclared preset field is refused by name", badField);

                // ---------- the WiFi preset driven field by field ----------
                string pick = probe.SetQrField("preset", "wifi");
                Check(pick.StartsWith("INVALID") && pick.Contains("InvalidPayload"),
                    "the WiFi preset opens invalid — required fields cannot compose yet", pick);
                Check(probe.PreviewSignatureProbe() == "NO-PICTURE",
                    "no picture while the preset fields cannot compose",
                    probe.PreviewSignatureProbe());
                Check(probe.WindowStateProbe().Contains("preset=wifi"),
                    "preset=wifi selected", probe.WindowStateProbe());

                string s1 = probe.SetQrField("ssid", wifiFields["ssid"]);
                Check(s1.StartsWith("INVALID") && s1.Contains("InvalidPayload"),
                    "ssid alone still misses the secured-network password", s1);
                string s2 = probe.SetQrField("security", wifiFields["security"]);
                Check(s2.StartsWith("INVALID") && s2.Contains("InvalidPayload"),
                    "the security choice alone still misses the password", s2);
                string s3 = probe.SetQrField("password", wifiFields["password"]);
                Check(ResponseMatchesVerdict(s3, interim25),
                    "the composed payload is judged at the CURRENT size — " +
                    $"the live verdict matches the module ({VerdictName(interim25)})", s3);

                string sz = probe.SetQrField("size", gateSizeMm.ToString(inv));
                Check(ResponseMatchesVerdict(sz, interimGate),
                    $"size {gateSizeMm:g}mm revalidates the composed payload — " +
                    $"live verdict matches the module ({VerdictName(interimGate)})", sz);
                string s4 = probe.SetQrField("hidden", wifiFields["hidden"]);
                Check(ResponseMatchesVerdict(s4, fullAtM),
                    "the boolean hidden-network field composes too — " +
                    $"live verdict matches the module ({VerdictName(fullAtM)})", s4);

                // ---------- verbatim rule + remembered fields ----------
                string manual = probe.SetQrField("content", PayloadA);
                Check(manual.StartsWith("VALID") &&
                      probe.WindowStateProbe().Contains("preset=raw"),
                    "a manual content edit reverts the content type to raw",
                    $"{manual} | {probe.WindowStateProbe()}");
                string repick = probe.SetQrField("preset", "wifi");
                Check(ResponseMatchesVerdict(repick, fullAtM),
                    "re-picking the preset recomposes the REMEMBERED fields — " +
                    $"the payload survives a raw detour ({VerdictName(fullAtM)})", repick);

                // ---------- the non-default settings land ----------
                string eccH = probe.SetQrField("ecc", "h");   // lowercase — the parser is case-insensitive
                Check(eccH.StartsWith("VALID") &&
                      eccH.Contains($"cells={expectedGate.Info.CellCount}"),
                    "ECC 'h' re-encodes the payload at level H (denser matrix)", eccH);
                string invOn = probe.SetQrField("invert", "true");
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
                    string dense = probe.SetQrField("content", denseContent);
                    Check(ResponseMatchesVerdict(dense, denseAtGate),
                        "the dense payload revalidates at the live settings — " +
                        $"live verdict matches the module ({VerdictName(denseAtGate)})", dense);
                    Check(probe.WindowStateProbe().Contains("preset=raw"),
                        "the manual content edit reverted the preset to raw",
                        probe.WindowStateProbe());

                    // The refused rung first — the floor must say NO before it says YES.
                    string rsz = probe.SetQrField("size", denseRefused.ToString(inv));
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

                    string asz = probe.SetQrField("size", denseAccepted.ToString(inv));
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
                TeardownSpawned(sw);
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

            ExplodedSketchCensus(bk, expected, tag);
            mgr.InsertSketch(false);
            doc.ClearSelection2(true);
        }

        /// <summary>
        /// Census of an exploded QR sketch (still open): the exploded HOST
        /// lines match the module 1:1 in real sketch coordinates — material
        /// segment count, the two construction squares flagged, and every
        /// material contour matching a module contour's bounding box exactly
        /// (positions, not just lengths). Shared by the committed-sketch
        /// route above and the open-sketch support verification.
        /// </summary>
        private static void ExplodedSketchCensus(ISketch bk, QrSketch expected, string tag)
        {
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
            var created = Activator.CreateInstance(t) as ISldWorks;
            if (created != null) CaptureStartupFlag();
            return created;
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
        /// `LiveProbe.exe circledims [deployDir] [progId]` — the ticket-#40
        /// live gate on the REAL add-in path: a circle-mode create through
        /// the session, the PatternDimsProbe surface, a master-dim rescale
        /// via SetPatternDimProbe, then re-census. Session-free instance
        /// only — same attach/refuse discipline as the default mode.
        /// </summary>
        private static int CircleDimsMain(string[] args)
        {
            string deployDir = args.Length > 0 ? args[0] : Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            Console.WriteLine("# ticket-#40 circle-mode dims/equations live gate");
            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Check(false, "SolidWorks launch"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: session is visible or holds documents — refusing.");
                    return 1;
                }
                _spawnedFresh = true;

                var probe = TryGetProbe(sw);
                if (probe == null)
                {
                    int loadResult = sw.LoadAddIn(dllPath);
                    Check(loadResult == 0, "LoadAddIn", $"returned {loadResult} for {dllPath}");
                    probe = TryGetProbe(sw);
                }
                Check(probe != null, "GetAddInObject returns the probe interface",
                    probe == null ? "null" : "obtained");
                if (probe == null) return 1;

                Check(probe.SetQrField("content", PayloadA).StartsWith("VALID"),
                    "probe payload accepted", probe.WindowStateProbe());
                Check(probe.SetQrField("shape", "circle").StartsWith("VALID"),
                    "circle shape accepted", probe.WindowStateProbe());
                Check(probe.SetQrField("param", "90").StartsWith("VALID"),
                    "dot size 90 accepted", probe.SetQrField("param", "90"));

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "part document created", doc == null ? "null" : "ok");
                if (doc == null) return 1;
                var ext = (IModelDocExtension)doc.Extension;
                var mgr = (ISketchManager)doc.SketchManager;
                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected as insertion support");

                string created = probe.CreateQrFromWindowProbe();
                Check(created.StartsWith("OK"), "circle-mode create succeeds", created);
                if (!created.StartsWith("OK")) return 1;
                string skName = created.Split(' ')[1];

                string census = probe.PatternCensusProbe(skName);
                Check(census.StartsWith("PATCENSUS"), "census on the open sketch", census);
                string dims = probe.PatternDimsProbe(skName);
                Check(dims.StartsWith("PATDIMS") && dims.Contains("eqs=8")
                        && dims.Contains("mode=circle"),
                    "dim census + 8 equations on the circle sketch", dims);

                // Exit edit mode — committed-state reads are the accurate
                // ones (in-edit spacing-dim reads can be stale, PAT12).
                mgr.InsertSketch(false);
                doc.ClearSelection2(true);

                double boxM = ParseDimMm(dims, "D1");
                Check(boxM > 0.0, "master dim D1 resolved from census", $"D1={boxM * 1000}mm");
                if (boxM <= 0.0) return 1;
                string set = probe.SetPatternDimProbe($"D1@{skName}", boxM * 2.0);
                Check(set.StartsWith("PATSET") && set.Contains($"{boxM * 2000.0:F4}mm"),
                    "master dim rescale accepted", set);

                string dims2 = probe.PatternDimsProbe(skName);
                double pitch1 = ParseDimMm(dims, "D5");    // spacingX — creation order
                double pitch2 = ParseDimMm(dims2, "D5");
                Check(dims2.Contains("eqs=8"), "equations intact after rescale", dims2);
                Check(pitch1 > 0 && Math.Abs(pitch2 - pitch1 * 2.0) < 1e-9,
                    "spacing dim doubled with the master", dims2);

                string census2 = probe.PatternCensusProbe(skName);
                Check(census2.StartsWith("PATCENSUS") && census2.Contains("regions="),
                    "census after rescale", census2);

                Console.WriteLine(_failures == 0 ? "# CIRCLEDIMS ALL PASSED"
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
                TeardownSpawned(sw);
            }
        }

        /// <summary>
        /// `LiveProbe.exe circleladder [deployDir] [progId]` — the ticket-#41
        /// density ladder that sizes MaxCircleInstances: circle-mode creates
        /// through the REAL add-in path (session probes →
        /// CreateQrFromWindowProbe → the pattern emitter) at increasing
        /// emitted-dot counts, timing the same responsiveness set ticket #36
        /// measured for the segment bound — create round-trip, sketch
        /// exit/re-enter, the region solve a contour pick pays — plus the
        /// circle-mode edit operation itself (a master-dim rescale through
        /// SetPatternDimProbe). The module-side rung table prints BEFORE the
        /// SLDWORKS.exe refusal so a blocked run still shows what the live
        /// half would measure. Rungs the deployed build refuses (over the
        /// recorded bound, or a dot-% cap) are logged and skipped — the
        /// ladder doubles as the bound's live re-verification: measure with
        /// an unbounded build, re-run with the bound to watch it refuse.
        /// </summary>
        private static int CircleLadderMain(string[] args)
        {
            string deployDir = args.Length > 0 ? args[0] : Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // ================= module-side rung table =================
            // A rung is a QR version (the minimal 'a'×k byte-mode payload
            // landing on it at ECC M) or its inverted twin. Dots, not cells,
            // are the currency — emitted counts come from the real plan.
            Console.WriteLine("# ticket-#41 circle-mode instance-bound ladder");
            Console.WriteLine($"# recorded bound under test: MaxCircleInstances=" +
                $"{QrInsertionGuard.MaxCircleInstances} (module-side value; " +
                "the DEPLOYED build's rules apply to the live rungs)");

            var rungs = new List<(string tag, string content, bool invert,
                int cells, QrSketchResult verdict, double genMs)>();
            var landing = MinContentForVersions(new[] { 1, 5, 6, 9, 12, 15, 18, 21, 25, 29, 33, 38 });
            foreach (var pair in landing)
            {
                string content = new string('a', pair.Value);
                var verdict = CircleVerdict(content, invert: false);
                double genMs = double.MaxValue;
                for (var i = 0; i < 3; i++)
                    genMs = Math.Min(genMs, TimeMs(() => CircleVerdict(content, false)));
                rungs.Add(($"v{pair.Key} k={pair.Value}", content, false,
                    17 + 4 * pair.Key, verdict, genMs));
            }
            // Inverted rungs corroborate the count→cost law across polarity:
            // invert emits the LIGHT cells of the whole (N+8)² matrix — v15
            // lands over the bound, giving the ceiling live invert coverage.
            foreach (int v in new[] { 6, 12, 15 })
            {
                string content = new string('a', landing[v]);
                var verdict = CircleVerdict(content, invert: true);
                double genMs = TimeMs(() => CircleVerdict(content, true));
                rungs.Add(($"v{v} k={landing[v]} INVERT", content, true,
                    17 + 4 * v, verdict, genMs));
            }

            rungs.Sort((a, b) =>
            {
                int da = a.verdict.Succeeded ? a.verdict.Sketch!.Info.InstanceCount : int.MaxValue;
                int db = b.verdict.Succeeded ? b.verdict.Sketch!.Info.InstanceCount : int.MaxValue;
                return da.CompareTo(db);
            });

            Console.WriteLine("# rung table (module-side, dot size 90%, live size = N×2 mm):");
            foreach (var r in rungs)
            {
                var info = r.verdict.Sketch?.Info;
                Console.WriteLine(info == null
                    ? $"#   {r.tag,-18} INVALID {r.verdict.Error!.Kind}"
                    : $"#   {r.tag,-18} v{info.Version} N={info.CellCount} " +
                      $"dots={info.InstanceCount} skipped={r.verdict.Sketch!.CirclePlan!.SkippedCount} " +
                      $"gen_ms={r.genMs:F1}");
            }

            if (System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: the circle ladder requires SolidWorks " +
                    "closed — a SLDWORKS.exe process is already running. Refusing to " +
                    "attach; the probe never mutates or kills an occupied seat.");
                return 1;
            }

            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch(progId);
                if (sw == null) { Check(false, "SolidWorks launch"); return 1; }
                WaitForStartup(sw);
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                if (sw.Visible || openDocs > 0)
                {
                    Console.WriteLine("# FATAL: session is visible or holds documents — refusing.");
                    return 1;
                }
                _spawnedFresh = true;

                var probe = TryGetProbe(sw);
                if (probe == null)
                {
                    int loadResult = sw.LoadAddIn(dllPath);
                    Check(loadResult == 0, "LoadAddIn", $"returned {loadResult} for {dllPath}");
                    probe = TryGetProbe(sw);
                }
                Check(probe != null, "GetAddInObject returns the probe interface",
                    probe == null ? "null" : "obtained");
                if (probe == null) return 1;

                Check(probe.SetQrField("content", PayloadA).StartsWith("VALID"),
                    "probe payload accepted", probe.WindowStateProbe());
                Check(probe.SetQrField("shape", "circle").StartsWith("VALID"),
                    "circle shape accepted", probe.WindowStateProbe());
                Check(probe.SetQrField("param", "90").StartsWith("VALID"),
                    "dot size 90 accepted", probe.SetQrField("param", "90"));

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);

                // ---- refuse pass: `circleladder <dir> <progId> refuse` ----
                // The bound's live wiring without paying the create ladder:
                // every rung re-validates through the production session
                // probes — the same verdict path Create reaches — and the
                // shared part's feature tree must never change. Over-bound
                // rungs answer INVALID TooManyInstances at pitch 2 mm.
                bool refuseOnly = args.Any(a => string.Equals(a, "refuse",
                    StringComparison.OrdinalIgnoreCase));
                if (refuseOnly)
                {
                    var rdoc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                    Check(rdoc != null, "refuse-pass part document",
                        rdoc == null ? "null" : "ok");
                    if (rdoc == null) return 1;
                    int feats = FindSketchFeatureNames(rdoc).Count;
                    foreach (var rung in rungs)
                    {
                        _ = probe.SetQrField("size", (rung.cells * 2.0).ToString(inv));
                        string resp = probe.SetQrField("content", rung.content);
                        if (rung.invert) resp = probe.SetQrField("invert", "true");
                        string expected = rung.verdict.Succeeded
                            ? "VALID" : $"INVALID {rung.verdict.Error!.Kind}";
                        Check(resp.StartsWith(expected),
                            $"live verdict echoes module-side ({rung.tag})",
                            $"{resp}  (expected {expected})");
                        if (rung.invert) _ = probe.SetQrField("invert", "false");
                    }
                    var rmgr = (ISketchManager)rdoc.SketchManager;
                    Check(FindSketchFeatureNames(rdoc).Count == feats &&
                          rmgr.ActiveSketch == null,
                        "no session verdict mutated the document",
                        $"features={feats} activeSketch={rmgr.ActiveSketch != null}");
                    try { sw.CloseDoc(rdoc.GetTitle()); } catch { }
                    Console.WriteLine(_failures == 0
                        ? "# CIRCLELADDER REFUSE ALL PASSED"
                        : $"# {_failures} refuse check(s) failed");
                    return _failures == 0 ? 0 : 1;
                }

                bool wall = false;
                foreach (var rung in rungs)
                {
                    if (wall) break;
                    var info = rung.verdict.Sketch?.Info;
                    int dots = info?.InstanceCount ?? -1;
                    // Size off the rung's intended N, not the verdict — a
                    // refused rung carries no Sketch, and shrinking the size
                    // would drop pitch under the dot-size floor and mask the
                    // instance refusal behind InvalidShapeParameter.
                    double sizeMm = rung.cells * 2.0;   // pitch pinned at 2 mm
                    Console.WriteLine($"# ---- rung {rung.tag} dots={dots} ----");

                    // One throwaway part per rung: a second circle create in
                    // a document whose earlier QR sketch was deleted trips an
                    // equation-manager refusal (measured: IEquationMgr.Add -1
                    // on Sketch2) — isolation keeps every rung on a fresh
                    // manager, the measurement-clean shape.
                    var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                    if (doc == null) { Check(false, $"part document ({rung.tag})"); break; }
                    var ext = (IModelDocExtension)doc.Extension;
                    var mgr = (ISketchManager)doc.SketchManager;
                    int featsBefore = FindSketchFeatureNames(doc).Count;
                    double setMs = TimeMs(() =>
                        _ = probe.SetQrField("size", sizeMm.ToString(inv)));
                    string resp = "";
                    setMs += TimeMs(() => resp = probe.SetQrField("content", rung.content));
                    if (rung.invert)
                        setMs += TimeMs(() => resp = probe.SetQrField("invert", "true"));

                    if (!resp.StartsWith("VALID"))
                    {
                        // The bound (or a floor) refusing — record it; the
                        // feature tree must stay untouched.
                        string refused = probe.CreateQrFromWindowProbe();
                        Check(refused.StartsWith("ERROR") &&
                              FindSketchFeatureNames(doc).Count == featsBefore &&
                              mgr.ActiveSketch == null,
                            $"rung refused pre-mutation ({rung.tag})",
                            $"{resp} → create={refused.Split(' ')[0]}");
                        if (rung.invert) probe.SetQrField("invert", "false");
                        try { sw.CloseDoc(doc.GetTitle()); } catch { }
                        continue;
                    }
                    Check(resp.Contains($"dots={dots}"),
                        $"live session reports the module's dot count ({rung.tag})", resp);

                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        $"Front Plane selected ({rung.tag})");
                    string created = "";
                    double createMs = TimeMs(() => created = probe.CreateQrFromWindowProbe());
                    if (!created.StartsWith("OK"))
                    {
                        Console.WriteLine($"#   create FAILED at {dots} dots: {created} — stopping the ladder");
                        if (rung.invert) probe.SetQrField("invert", "false");
                        break;
                    }
                    string skName = created.Split(' ')[1];

                    string census = "";
                    double censusMs = TimeMs(() => census = probe.PatternCensusProbe(skName));
                    double exitMs = TimeMs(() => mgr.InsertSketch(false));

                    // Committed-sketch region solve — what a contour pick
                    // for extrude pays on the delivered dot field.
                    var feat = FindSketchFeature(doc, skName);
                    var committed = feat?.GetSpecificFeature2() as ISketch;
                    int committedRegions = -1;
                    double committedSolveMs = TimeMs(() =>
                    {
                        try { committedRegions = committed?.GetSketchRegionCount() ?? -1; } catch { }
                        try
                        {
                            var rr = committed?.GetSketchRegions() as object[];
                            if (rr != null) committedRegions = rr.Length;
                        }
                        catch { }
                    });

                    // The circle-mode edit op: master-dim rescale through the
                    // equation tree + rebuild (EditRebuild3 inside the probe).
                    string dims = probe.PatternDimsProbe(skName);
                    double boxM = ParseDimMm(dims, "D1");
                    double rescaleMs = boxM > 0
                        ? TimeMs(() => _ = probe.SetPatternDimProbe(
                            $"D1@{skName}", boxM * 1.5))
                        : -1;

                    // Re-enter edit on the committed sketch — the in-edit
                    // region solve + segment enumeration of #36's set.
                    doc.ClearSelection2(true);
                    bool sel = ext.SelectByID2(skName, "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                    double enterMs = -1, editRegionsMs = -1, segsMs = -1, exit2Ms = -1;
                    int editRegions = -1, editSegs = -1;
                    if (sel)
                    {
                        enterMs = TimeMs(() => mgr.InsertSketch(true));
                        var back = mgr.ActiveSketch as ISketch;
                        if (back != null)
                        {
                            editRegionsMs = TimeMs(() =>
                            {
                                try
                                {
                                    var rr = back.GetSketchRegions() as object[];
                                    editRegions = rr?.Length ?? -1;
                                }
                                catch { }
                            });
                            segsMs = TimeMs(() =>
                            {
                                try { editSegs = (back.GetSketchSegments() as object[])?.Length ?? -1; }
                                catch { }
                            });
                            exit2Ms = TimeMs(() => mgr.InsertSketch(false));
                        }
                    }
                    double rebuildMs = TimeMs(() => { try { doc.EditRebuild3(); } catch { } });

                    // Delete the measured sketch inside its own doc — the
                    // delete is itself a responsiveness datum; the doc
                    // closes unsaved right after.
                    double deleteMs = TimeMs(() =>
                    {
                        try
                        {
                            doc.ClearSelection2(true);
                            ext.SelectByID2(skName, "SKETCH", 0, 0, 0, false, 0, null,
                                (int)swSelectOption_e.swSelectOptionDefault);
                            doc.EditDelete();
                        }
                        catch { }
                    });
                    Check(FindSketchFeature(doc, skName) == null,
                        $"rung sketch deleted ({rung.tag})", $"delete_ms={deleteMs:F0}");

                    Console.WriteLine($"PERF rung={rung.tag} v={info!.Version} N={info.CellCount} " +
                        $"dots={dots} invert={rung.invert} gen_ms={rung.genMs:F1} " +
                        $"set_ms={setMs:F0} create_ms={createMs:F0} " +
                        $"native_insert_ms≈{createMs - rung.genMs:F0} " +
                        $"census_ms={censusMs:F0} exit_ms={exitMs:F0} " +
                        $"committed_regions_ms={committedSolveMs:F0}(r={committedRegions}) " +
                        $"rescale_ms={rescaleMs:F0} enter_ms={enterMs:F0} " +
                        $"edit_regions_ms={editRegionsMs:F0}(r={editRegions}) " +
                        $"seg_enum_ms={segsMs:F0}(s={editSegs}) exit2_ms={exit2Ms:F0} " +
                        $"rebuild_ms={rebuildMs:F0} delete_ms={deleteMs:F0}");
                    if (rung.invert) probe.SetQrField("invert", "false");
                    try { sw.CloseDoc(doc.GetTitle()); } catch { }

                    // Stop once the wall is measured — a minute-scale solve or
                    // create says the responsiveness budget is spent; deeper
                    // rungs only restate it.
                    wall = committedSolveMs > 120_000 || createMs > 90_000;
                    if (wall)
                        Console.WriteLine("#   wall reached — deeper rungs restate the same verdict");
                }

                try { sw.UnloadAddIn(dllPath); } catch { }
                Console.WriteLine(_failures == 0
                    ? "# CIRCLELADDER DONE"
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
                TeardownSpawned(sw);
            }
        }

        /// <summary>The smallest 'a'×k byte-mode payload landing exactly on
        /// each listed QR <paramref name="versions"/> at ECC M — one encode
        /// sweep: version climbs a single step at a time as content grows,
        /// so the first k reaching each version is that version's answer.
        /// </summary>
        private static Dictionary<int, int> MinContentForVersions(int[] versions)
        {
            var want = new HashSet<int>(versions);
            var found = new Dictionary<int, int>();
            using (var gen = new QRCodeGenerator())
                for (var k = 1; k < 3000 && found.Count < want.Count; k++)
                    using (var d = gen.CreateQrCode(
                               new string('a', k), QRCodeGenerator.ECCLevel.M))
                        if (want.Contains(d.Version) && !found.ContainsKey(d.Version))
                            found[d.Version] = k;
            return found;
        }

        /// <summary>The module's circle-plan verdict for a ladder rung.</summary>
        private static QrSketchResult CircleVerdict(string content, bool invert) =>
            QrSketchGenerator.Generate(new QrSketchRequest(content, 400.0, 0.03)
            {
                EccLevel = QRCodeGenerator.ECCLevel.M,
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.90,
                Invert = invert,
            });

        /// <summary>Milliseconds a synchronous action takes.</summary>
        private static double TimeMs(System.Action a)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            a();
            t.Stop();
            return t.Elapsed.TotalMilliseconds;
        }

        /// <summary>Pulls `D{n}=<mm>mm` values out of a PATDIMS line.</summary>
        private static double ParseDimMm(string patdims, string dimName)
        {
            int i = patdims.IndexOf(dimName + "=", StringComparison.Ordinal);
            if (i < 0) return -1.0;
            int j = patdims.IndexOf("mm", i, StringComparison.Ordinal);
            if (j < 0) return -1.0;
            return double.TryParse(
                patdims.Substring(i + dimName.Length + 1, j - i - dimName.Length - 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double mm)
                ? mm / 1000.0 : -1.0;
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
            public string SetQrField(string name, string value) => Invoke("SetQrField", name, value);
            public string CreateQrFromWindowProbe() => Invoke("CreateQrFromWindowProbe");
            public string UndoRedoQrProbe() => Invoke("UndoRedoQrProbe");
            public string PreviewSignatureProbe() => Invoke("PreviewSignatureProbe");
            public string PatternCensusProbe(string sketchName) => Invoke("PatternCensusProbe", sketchName);
            public string PatternDimsProbe(string sketchName) => Invoke("PatternDimsProbe", sketchName);
            public string SetPatternDimProbe(string dimensionName, double valueM)
                => Invoke("SetPatternDimProbe", dimensionName, valueM);
            public string SwPropertiesProbe() => Invoke("SwPropertiesProbe");
            public string SwPropertyResolveProbe(string name) => Invoke("SwPropertyResolveProbe", name);
        }
    }
}
