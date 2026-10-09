using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Commands;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Live verification harness for ticket 2. Usage:
    ///   SolidWorksQrAddIn.LiveProbe.exe &lt;deployDir&gt;
    /// where deployDir contains the registered SolidWorksQrAddIn.dll.
    ///
    /// Drives SolidWorks via SldWorks.Application.31 + Activator.CreateInstance
    /// (the verified route — PowerShell late binding does NOT work here).
    /// Prints PASS/FAIL lines; exit code 0 when every check passes.
    ///
    /// QR-specific checks: native contour/region counts vs the module's
    /// expectation, finder-pattern hole/island nesting matched by bounding
    /// boxes, then Boss-Extrude and shallow Cut-Extrude attempts on a backing
    /// plate created through the same COM API.
    /// </summary>
    public static class Program
    {
        private static int _failures;

        private static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
            if (!cond) _failures++;
        }

        // The module's own expectation for the fixed request — the native
        // sketch must reproduce exactly this contour set.
        private static readonly QrSketch Expected = QrSketchGenerator
            .Generate(CreateQrSketchCommand.FixedContent,
                CreateQrSketchCommand.FixedSizeMm,
                CreateQrSketchCommand.FixedGrowthFractionPerSide)
            .Sketch!;

        public static int Main(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            if (Expected == null)
            {
                Console.WriteLine("# FATAL: module could not generate the fixed QR — aborting live run");
                return 1;
            }
            Console.WriteLine($"# expected QR: {Expected.Info.ContourCount} contours, " +
                $"{Expected.Info.SegmentCount} segments, {Expected.Regions.Count} regions, " +
                $"N={Expected.Info.CellCount}, size={Expected.Info.FinalSizeMm}mm, " +
                $"minEdge={Expected.Info.MinEdgeMm:F3}mm");

            ISldWorks? sw = null;
            try
            {
                sw = AttachOrLaunch();
                if (sw == null) { Check(false, "SolidWorks launch"); return 1; }

                Console.WriteLine($"# SolidWorks revision {sw.RevisionNumber()}  visible={sw.Visible}");

                WaitForStartup(sw);

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

                // --- rejection: no document open ---
                string noDoc = probe.RunQrSketchProbe();
                Check(noDoc.StartsWith("ERROR") && noDoc.Contains("document"),
                    "rejects with no document open", noDoc);

                // --- new part ---
                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "new part document created",
                    doc == null ? "null" : $"type={doc.GetType()}");
                if (doc == null) return 1;
                var ext = (IModelDocExtension)doc.Extension;
                var mgr = (ISketchManager)doc.SketchManager;

                // --- rejection: no selection ---
                doc.ClearSelection2(true);
                string r = probe.RunQrSketchProbe();
                Check(r.StartsWith("ERROR"), "rejects with nothing selected", r);

                // --- mid-insertion failure on the real native path ---
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected (failure-injection run)");

                string fail = probe.RunFailingQrSketchProbe();
                Check(fail.StartsWith("FAILSAFE") && fail.Contains("removed"),
                    "insertion failure reports cleanup of the partial sketch", fail);
                Check(mgr.ActiveSketch == null, "no sketch left open after failed insertion");
                Check(!ext.SelectByID2("Sketch1", "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "only the partial sketch was deleted (no leftover feature)");

                // --- happy path: select Front Plane, create the fixed QR ---
                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected");

                r = probe.RunQrSketchProbe();
                Check(r.StartsWith("OK"), "QR sketch command succeeds", r);

                var sketch = mgr.ActiveSketch as ISketch;
                Check(sketch != null, "new sketch remains open for editing",
                    sketch == null ? "ActiveSketch is null" : "in edit mode");
                string qrSketchName = r.Split(' ')[1];

                if (sketch != null)
                {
                    var segs = (object[])sketch.GetSketchSegments();
                    Check(segs.Length == Expected.Info.SegmentCount,
                        "native segment count matches the module's contour edges",
                        $"native={segs.Length} expected={Expected.Info.SegmentCount}");
                    Check(sketch.GetSketchContourCount() == Expected.Info.ContourCount,
                        "native contour count matches (all loops incl. holes+islands)",
                        $"native={sketch.GetSketchContourCount()} expected={Expected.Info.ContourCount}");
                    // SolidWorks exposes ONE sketch region per contour: the
                    // bounded area each contour encloses (a hole contour's
                    // interior is a region too — a void). So the native count
                    // equals the contour count, NOT the "material" area count.
                    int regionCount = sketch.GetSketchRegionCount();
                    int materialRegions = Expected.Regions.Count +
                        Expected.Regions.Sum(rg => rg.Holes.Sum(h => h.Islands.Count));
                    Console.WriteLine($"# native regions={regionCount} (one per contour), " +
                        $"material regions={materialRegions} (bodies + islands)");
                    Check(regionCount == Expected.Info.ContourCount,
                        "native region count = contour count (one bounded area per loop)",
                        $"native={regionCount} contours={Expected.Info.ContourCount}");

                    VerifyContoursNatively(sketch);
                    VerifyFinderNesting();

                    // Second create while the QR sketch is open must reject.
                    string again = probe.RunQrSketchProbe();
                    Check(again.StartsWith("ERROR") && again.Contains("already"),
                        "rejects a second create while the sketch is open", again);
                    Check(((object[])sketch.GetSketchSegments()).Length == Expected.Info.SegmentCount,
                        "rejected run left the open sketch unchanged");

                    // Commit the QR sketch so it becomes a feature.
                    mgr.InsertSketch(false);
                    Check(mgr.ActiveSketch == null, "QR sketch exits edit mode cleanly");
                }

                // --- backing plate + feature use ---
                FeatureChecks(doc, ext, mgr, qrSketchName);

                // --- reload: unload, then load again — no duplicate commands ---
                int unload = sw.UnloadAddIn(dllPath);
                Check(unload == 0, "UnloadAddIn", $"returned {unload}");
                Check(TryGetProbe(sw) == null, "probe gone after unload (callbacks removed)");

                int reload = sw.LoadAddIn(dllPath);
                Check(reload == 0, "add-in reloads after unload", $"returned {reload}");
                var probe2 = TryGetProbe(sw);
                Check(probe2 != null && probe2.AddinStateProbe().StartsWith("CONNECTED"),
                    "reloaded add-in is connected", probe2?.AddinStateProbe() ?? "null");

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
                try { sw?.ExitApp(); } catch { }
            }
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
        /// Every module contour — outlines, holes, islands — must exist as a
        /// native contour with a matching bounding box. Matched 1:1.
        /// </summary>
        private static void VerifyContoursNatively(ISketch sketch)
        {
            const double tol = 1e-9; // metres — tighter than SW's own tolerance
            var native = ((object[])sketch.GetSketchContours()).Cast<ISketchContour>().ToList();
            var nativeBoxes = native.Select(NativeBBox).ToList();
            var used = new bool[nativeBoxes.Count];
            int matched = 0;

            foreach (var mc in Expected.Contours)
            {
                var mb = ModuleBBox(mc);
                int hit = -1;
                for (var i = 0; i < nativeBoxes.Count; i++)
                    if (!used[i] && BBoxEquals(mb, nativeBoxes[i], tol)) { hit = i; break; }
                if (hit >= 0) { used[hit] = true; matched++; }
            }

            Check(matched == Expected.Contours.Count && native.Count == Expected.Contours.Count,
                "every generated contour exists natively with a matching bounding box",
                $"matched={matched} native={native.Count} expected={Expected.Contours.Count}");
        }

        /// <summary>
        /// Finder-pattern proof: a module-level ring→hole→island nesting must
        /// appear in the native sketch — i.e. the island loop is still inside
        /// the hole loop which is inside the region loop.
        /// </summary>
        private static void VerifyFinderNesting()
        {
            var nested = Expected.Regions
                .Where(rg => rg.Holes.Any(h => h.Islands.Count > 0))
                .ToList();
            Check(nested.Count >= 3,
                "module produced finder-pattern ring/hole/island nestings",
                $"nested regions={nested.Count} (QR has 3 finders)");

            // Also: physical sanity — each island bbox strictly inside its
            // hole bbox strictly inside the region bbox.
            bool allInside = nested.All(rg => rg.Holes.All(h =>
                h.Islands.All(isl =>
                {
                    var ib = ModuleBBox(isl.Boundary);
                    var hb = ModuleBBox(h.Boundary);
                    var rb = ModuleBBox(rg.Boundary);
                    return ib.minX > hb.minX && ib.maxX < hb.maxX &&
                           ib.minY > hb.minY && ib.maxY < hb.maxY &&
                           hb.minX > rb.minX && hb.maxX < rb.maxX &&
                           hb.minY > rb.minY && hb.maxY < rb.maxY;
                })));
            Check(allInside, "finder islands are strictly nested inside holes inside regions");
        }

        // ---------------- feature use on a backing plate ----------------

        private static void FeatureChecks(IModelDoc2 doc, IModelDocExtension ext, ISketchManager mgr, string qrSketchName)
        {
            // 60×60×5 mm backing plate on the same Front Plane (z = 0 base).
            doc.ClearSelection2(true);
            if (!ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
            {
                Check(false, "plate: Front Plane re-selected");
                return;
            }
            mgr.InsertSketch(true);
            var plateSketch = mgr.ActiveSketch as ISketch;
            if (plateSketch == null) { Check(false, "plate sketch opened"); return; }
            const double h = 0.030; // 60 mm plate
            mgr.CreateLine(-h, -h, 0, h, -h, 0);
            mgr.CreateLine(h, -h, 0, h, h, 0);
            mgr.CreateLine(h, h, 0, -h, h, 0);
            mgr.CreateLine(-h, h, 0, -h, -h, 0);
            mgr.InsertSketch(false);

            var fm = (IFeatureManager)doc.FeatureManager;
            doc.ClearSelection2(true);
            // The just-committed sketch is the last ProfileFeature in the tree.
            var sketchNames = FindSketchFeatureNames(doc);
            string plateSketchName = sketchNames.Last();
            Check(ext.SelectByID2(plateSketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault),
                "plate sketch selected for extrusion", plateSketchName);
            var plate = fm.FeatureExtrusion3(
                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                0.005, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
            Check(plate != null, "backing plate Boss-Extrude succeeds",
                plate == null ? "FeatureExtrusion3 returned null" : plate.Name);

            // Boss-Extrude the QR material regions onto the plate (2 mm).
            doc.ClearSelection2(true);
            Check(SelectQrMaterialRegions(doc, qrSketchName),
                "QR material regions selectable in the native sketch");
            var boss = fm.FeatureExtrusion3(
                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                0.002, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
            Check(boss != null, "QR Boss-Extrude succeeds on the backing plate",
                boss == null ? "FeatureExtrusion3 returned null" : boss.Name);

            // Shallow Cut-Extrude the QR material regions into the plate (1 mm).
            // Dir=true: on this machine FeatureExtrusion3(Dir=false) grows the
            // plate away from the sketch-normal side, so the cut must run with
            // the reversed-direction flag to reach into the material —
            // verified empirically (Dir=false returns null).
            doc.ClearSelection2(true);
            Check(SelectQrMaterialRegions(doc, qrSketchName),
                "QR material regions re-selectable for the cut");
            var cut = fm.FeatureCut4(
                true, false, true, (int)swEndConditions_e.swEndCondBlind, 0,
                0.001, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                false, true, true, false, false, false,
                (int)swStartConditions_e.swStartSketchPlane, 0.0, false, true) as IFeature;
            Check(cut != null, "shallow QR Cut-Extrude succeeds into the plate",
                cut == null ? "FeatureCut4 returned null" : cut.Name);
        }

        /// <summary>
        /// Selects only the MATERIAL regions of the committed QR sketch —
        /// outer bodies plus islands nested inside holes — the programmatic
        /// equivalent of the engineer picking the QR's dark areas for a
        /// feature. Hole-interior (void) regions are deliberately skipped so
        /// a Boss/Cut-Extrude reproduces the QR pattern rather than a solid
        /// square. Selecting the whole sketch feature does NOT work — SW
        /// returns null for multi-contour sketches.
        ///
        /// Each SolidWorks region's bounding box equals its outer bounding
        /// contour's box, so material regions are identified 1:1 against the
        /// module's body/island contour boxes (exact 1e-9 m match — the
        /// contours were inserted verbatim).
        /// </summary>
        private static bool SelectQrMaterialRegions(IModelDoc2 doc, string qrSketchName)
        {
            var sketchFeature = FindSketchFeature(doc, qrSketchName);
            if (sketchFeature == null) return false;
            // NB: the sketch feature's RCW does NOT QI to ISketch — the sketch
            // object is reached through GetSpecificFeature2().
            var sketch = sketchFeature.GetSpecificFeature2() as ISketch;
            if (sketch == null) return false;

            var regions = sketch.GetSketchRegions() as object[];
            if (regions == null || regions.Length == 0) return false;

            var material = new List<(double minX, double maxX, double minY, double maxY)>();
            foreach (var rg in Expected.Regions)
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

        /// <summary>
        /// Bounding box of a native sketch region, from the vertices of its
        /// boundary edges (sketch space, metres).
        /// </summary>
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

        private static ISldWorks? AttachOrLaunch()
        {
            var t = Type.GetTypeFromProgID("SldWorks.Application.31");
            if (t == null) { Console.WriteLine("FAIL  ProgID SldWorks.Application.31 not registered"); return null; }
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
                    // The RCW doesn't QI our managed interface — but the object
                    // IS SwAddin and its public methods are callable via the
                    // CCW's dispatch interface, exactly the way SolidWorks
                    // invokes command callbacks by name.
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

            private string Invoke(string method)
            {
                var result = _addin.GetType().InvokeMember(
                    method,
                    System.Reflection.BindingFlags.InvokeMethod |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance,
                    null, _addin, Array.Empty<object>());
                return result as string ?? "(no result)";
            }

            public string RunQrSketchProbe() => Invoke("RunQrSketchProbe");
            public string RunFailingQrSketchProbe() => Invoke("RunFailingQrSketchProbe");
            public string AddinStateProbe() => Invoke("AddinStateProbe");
        }
    }
}
