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
    ///   SolidWorksQrAddIn.LiveProbe.exe list
    /// where deployDir contains the registered SolidWorksQrAddIn.dll.
    /// `list` prints the mode registry without touching COM; named modes
    /// dispatch through the GateHost registry — hosted gates run the
    /// shared live-session ceremony (see GateHost.cs), unmigrated modes
    /// dispatch to their legacy bodies.
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
            // `list` prints the mode registry and exits — it must never
            // touch COM.
            if (args.Length > 0 &&
                string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
                return GateHost.ListModes();

            // The registry is the dispatch table — hosted modes run the
            // GateHost ceremony; unmigrated modes dispatch to their legacy
            // bodies. Anything else falls through to the default suite.
            var mode = FindMode(args.Length > 0 ? args[0] : null);
            if (mode?.Run != null)
                return GateHost.Run(mode, args.Skip(1).ToArray());
            if (mode?.LegacyMain != null)
                return mode.LegacyMain(args.Skip(1).ToArray());
            // No verb matched — the unnamed default suite, hosted like
            // every other production gate.
            return GateHost.Run(DefaultMode, args);
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
