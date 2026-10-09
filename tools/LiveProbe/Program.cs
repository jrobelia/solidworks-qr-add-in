using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Live verification harness for ticket 4 (custom size + outline growth),
    /// retaining the ticket-3 and ticket-2 checks.
    ///   SolidWorksQrAddIn.LiveProbe.exe &lt;deployDir&gt;
    /// where deployDir contains the registered SolidWorksQrAddIn.dll.
    ///
    /// Drives SolidWorks via SldWorks.Application.31 + Activator.CreateInstance
    /// (the verified route — PowerShell late binding does NOT work here) and the
    /// add-in through its IAddInProbe COM hook, which exercises the SAME session
    /// + window code paths the WPF window uses. Prints PASS/FAIL lines; exit
    /// code 0 when every check passes.
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

                // ================= close / reopen =================

                Check(probe.CloseQrWindowProbe().Contains("open=False"),
                    "window closes", probe.WindowStateProbe());
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
                    FeatureChecks(doc!, ext, mgr, sketchAName, expectedA);

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

            var line = mgr.CreateLine(0.011, 0.007, 0.0, 0.023, 0.013, 0.0) as ISketchLine;
            Check(line != null, $"convention probe: marker CreateLine accepted on {tag}");

            // The sketch's own feature name, captured while it is still the
            // active sketch — the cleanup below deletes exactly this sketch,
            // never whatever happens to sit last in the feature tree.
            string probeSketchName = (sketch as IFeature)?.Name ?? "";

            double[]? p1 = null, p2 = null;
            if (line != null)
            {
                var s = line.GetStartPoint2() as ISketchPoint;
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

        /// <summary>Plane params (root + unit normal) of a planar face.</summary>
        private static bool TryFacePlane(IFace2 face, out double[] root, out double[] normal)
        {
            root = new double[3]; normal = new double[3];
            try
            {
                var surf = face.GetSurface() as ISurface;
                if (surf?.IsPlane() != true) return false;
                var pp = surf.PlaneParams as double[];
                if (pp == null || pp.Length < 6) return false;
                for (var i = 0; i < 3; i++) { root[i] = pp[i]; normal[i] = pp[i + 3]; }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Plane params (root + unit normal) of a RefPlane feature.</summary>
        private static bool TryRefPlane(IFeature plane, out double[] root, out double[] normal)
        {
            root = new double[3]; normal = new double[3];
            try
            {
                var rp = plane.GetSpecificFeature2() as IRefPlane;
                var pp = rp?.GetRefPlaneParams() as double[];
                if (pp == null || pp.Length < 6) return false;
                for (var i = 0; i < 3; i++) { root[i] = pp[i]; normal[i] = pp[i + 3]; }
                return true;
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
            VerifyNativeSketch(sketch, expected, tag);
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
            Check(angledName != null, "rotated reference plane created via InsertRefPlane",
                angledName ?? "none");

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
                    FeatureChecks(doc, ext, mgr, faceQrName, expectedA, "face-QR");
                }
            }

            // ---------- unsupported selections ----------
            int sketchCountBeforeNeg = FindSketchFeatureNames(doc).Count;

            // a cylindrical face (non-planar)
            var cylFace = topFace.root == null
                ? null
                : CreateCylinderSideFace(doc, ext, mgr, fm, topFace.root);
            if (cylFace != null)
            {
                doc.ClearSelection2(true);
                ((IEntity)cylFace).Select4(false, null);
                string e = probe.CreateQrFromWindowProbe();
                Check(e.StartsWith("ERROR") && e.Contains("not planar"),
                    "non-planar face refused with a useful error", e);
            }
            else Check(false, "cylinder side face created for the non-planar check");

            // an edge
            var anEdge = plateFaces.Count > 0 ? FirstEdgeOf(plateFaces[0].face) : null;
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

            // angular reference: the OTHER face containing the hinge.
            IFace2? refFace = null;
            double[] refNormal = new double[3];
            if (hinge.GetTwoAdjacentFaces2() is object[] adj)
                foreach (var ao in adj)
                    if (ao is IFace2 f && !ReferenceEquals(f, top.face) &&
                        TryFacePlane(f, out _, out var fn) && Math.Abs(fn[2]) < 0.5)
                    { refFace = f; refNormal = fn; break; }
            if (refFace == null) { Console.WriteLine("#   no side face adjacent to the hinge edge"); return null; }

            int planesBefore = CountRefPlanes(doc);
            double rad = deg * Math.PI / 180.0;

            // Recipe: [edge -> Coincident] then [face -> Angle]. If the result
            // isn't rotated, retry with the constraints swapped.
            foreach (var order in new[] { 0, 1 })
            {
                doc.ClearSelection2(true);
                if (order == 0)
                {
                    ((IEntity)hinge).Select4(false, null);
                    ((IEntity)refFace).Select4(true, null);
                }
                else
                {
                    ((IEntity)refFace).Select4(false, null);
                    ((IEntity)hinge).Select4(true, null);
                }
                var feat = order == 0
                    ? fm.InsertRefPlane(
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0.0,
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle, rad,
                        0, 0.0) as IFeature
                    : fm.InsertRefPlane(
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Angle, rad,
                        (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0.0,
                        0, 0.0) as IFeature;
                if (feat == null) continue;

                if (TryRefPlane(feat, out var r, out var n) &&
                    Math.Abs(Dot(n, hingeDir)) < 1e-6 &&          // plane contains the hinge
                    Math.Abs(Math.Acos(Math.Min(1.0, Math.Abs(Dot(n, refNormal)))) - rad) < 0.02)
                {
                    Console.WriteLine($"#   angled plane '{feat.Name}' (order={order}) " +
                        $"root={Fmt(r)} normal={Fmt(n)} refNormal={Fmt(refNormal)}");
                    return feat.Name;
                }
                Console.WriteLine($"#   InsertRefPlane order={order} gave an unexpected plane — trying next");
            }
            return CountRefPlanes(doc) > planesBefore
                ? null // a plane appeared but didn't verify — still report failure
                : null;
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
        private static void FeatureChecks(IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr,
            string qrSketchName, QrSketch expected, string tag)
        {
            if (qrSketchName.Length == 0) { Check(false, $"QR sketch present for feature use ({tag})"); return; }

            // Boss-Extrude the QR material regions (2 mm).
            doc.ClearSelection2(true);
            Check(SelectQrMaterialRegions(doc, qrSketchName, expected),
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
            Check(SelectQrMaterialRegions(doc, qrSketchName, expected),
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

        /// <summary>Native sketch must reproduce the module's contour set 1:1.</summary>
        private static void VerifyNativeSketch(ISketch sketch, QrSketch expected, string tag)
        {
            var segs = (object[])sketch.GetSketchSegments();
            Check(segs.Length == expected.Info.SegmentCount,
                $"native segment count matches the module's contour edges ({tag})",
                $"native={segs.Length} expected={expected.Info.SegmentCount}");
            Check(sketch.GetSketchContourCount() == expected.Info.ContourCount,
                $"native contour count matches incl. holes+islands ({tag})",
                $"native={sketch.GetSketchContourCount()} expected={expected.Info.ContourCount}");
            // SolidWorks exposes ONE sketch region per contour: the bounded
            // area each contour encloses (a hole's interior is a region too).
            Check(sketch.GetSketchRegionCount() == expected.Info.ContourCount,
                $"native region count = contour count ({tag})",
                $"native={sketch.GetSketchRegionCount()} contours={expected.Info.ContourCount}");

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
        private static void FeatureChecks(IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr,
            string qrSketchName, QrSketch expected)
        {
            var plate = CreateBackingPlate(doc, ext, mgr, (IFeatureManager)doc.FeatureManager);
            Check(plate != null, "backing plate Boss-Extrude succeeds",
                plate == null ? "creation failed" : plate.Name);
            if (plate == null) return;
            FeatureChecks(doc, ext, mgr, qrSketchName, expected, "plate");
        }

        /// <summary>
        /// Selects only the MATERIAL regions of the committed QR sketch —
        /// outer bodies plus islands nested inside holes. Hole-interior (void)
        /// regions are skipped so a Boss/Cut-Extrude reproduces the QR pattern.
        /// </summary>
        private static bool SelectQrMaterialRegions(IModelDoc2 doc, string qrSketchName, QrSketch expected)
        {
            var sketchFeature = FindSketchFeature(doc, qrSketchName);
            if (sketchFeature == null) return false;
            var sketch = sketchFeature.GetSpecificFeature2() as ISketch;
            if (sketch == null) return false;

            var regions = sketch.GetSketchRegions() as object[];
            if (regions == null || regions.Length == 0) return false;

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
                var bb = RegionBBox(region);
                if (material.Any(mb => BBoxEquals(bb, mb, 1e-9)) && region.Select(true, 0))
                    selected++;
            }
            return selected == material.Count;
        }

        private static (double minX, double maxX, double minY, double maxY) RegionBBox(
            ISketchRegion region)
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

        private static ISldWorks? AttachOrLaunch(string progId)
        {
            var t = Type.GetTypeFromProgID(progId);
            if (t == null) { Console.WriteLine($"FAIL  ProgID {progId} not registered"); return null; }
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
            public string AddinStateProbe() => Invoke("AddinStateProbe");
            public string OpenQrWindowProbe() => Invoke("OpenQrWindowProbe");
            public string CloseQrWindowProbe() => Invoke("CloseQrWindowProbe");
            public string WindowStateProbe() => Invoke("WindowStateProbe");
            public string SetQrContentProbe(string content) => Invoke("SetQrContentProbe", content);
            public string SetQrSizeProbe(string sizeText) => Invoke("SetQrSizeProbe", sizeText);
            public string SetQrGrowthProbe(string growthText) => Invoke("SetQrGrowthProbe", growthText);
            public string CreateQrFromWindowProbe() => Invoke("CreateQrFromWindowProbe");
            public string PreviewSignatureProbe() => Invoke("PreviewSignatureProbe");
        }
    }
}
