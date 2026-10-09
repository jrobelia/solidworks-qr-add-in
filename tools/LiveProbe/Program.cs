using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Live verification harness for ticket 1. Usage:
    ///   SolidWorksQrAddIn.LiveProbe.exe &lt;deployDir&gt;
    /// where deployDir contains the registered SolidWorksQrAddIn.dll.
    ///
    /// Drives SolidWorks via SldWorks.Application.31 + Activator.CreateInstance
    /// (the verified route — PowerShell late binding does NOT work here).
    /// Prints PASS/FAIL lines; exit code 0 when every check passes.
    /// </summary>
    public static class Program
    {
        private static int _failures;

        private static void Check(bool cond, string name, string detail = "")
        {
            Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
            if (!cond) _failures++;
        }

        public static int Main(string[] args)
        {
            string deployDir = args.Length > 0
                ? args[0]
                : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "SolidWorksQrAddIn");
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

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
                string noDoc = probe.RunTestSketchProbe();
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

                // --- rejection: no selection ---
                doc.ClearSelection2(true);
                string r = probe.RunTestSketchProbe();
                Check(r.StartsWith("ERROR"), "rejects with nothing selected", r);

                // --- mid-insertion failure on the real native path ---
                var ext = doc.Extension as IModelDocExtension;
                Check(ext != null && ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected (failure-injection run)");

                var mgr = doc.SketchManager as ISketchManager;
                string fail = probe.RunFailingSketchProbe();
                Check(fail.StartsWith("FAILSAFE") && fail.Contains("removed"),
                    "insertion failure reports cleanup of the partial sketch", fail);
                Check(mgr == null || mgr.ActiveSketch == null,
                    "no sketch left open after failed insertion");
                bool partialGone = ext != null &&
                    !ext.SelectByID2("Sketch1", "SKETCH", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                Check(partialGone, "only the partial sketch was deleted (no leftover feature)");

                // --- happy path: select Front Plane, create the square ---
                doc.ClearSelection2(true);
                Check(ext != null && ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected");

                r = probe.RunTestSketchProbe();
                Check(r.StartsWith("OK"), "test sketch command succeeds", r);

                var sketch = mgr?.ActiveSketch as ISketch;
                Check(sketch != null, "new sketch remains open for editing",
                    sketch == null ? "ActiveSketch is null" : "in edit mode");

                if (sketch != null && mgr != null)
                {
                    var segs = (object[])sketch.GetSketchSegments();
                    Check(segs.Length == 4, "sketch has exactly 4 segments", $"count={segs.Length}");
                    Check(sketch.GetSketchRegionCount() == 1, "sketch encloses one region (closed)",
                        $"regions={sketch.GetSketchRegionCount()}");
                    Check(sketch.GetSketchContourCount() == 1, "sketch has one closed contour",
                        $"contours={sketch.GetSketchContourCount()}");

                    // Centred at the sketch origin: every corner at ±h.
                    var pts = (object[])sketch.GetSketchPoints2();
                    double half = 0.0125, tol = 1e-6;
                    bool centred = pts.Length > 0;
                    foreach (var po in pts)
                    {
                        var p = (ISketchPoint)po;
                        if (Math.Abs(Math.Abs(p.X) - half) > tol || Math.Abs(Math.Abs(p.Y) - half) > tol)
                            centred = false;
                    }
                    Check(centred, "square is centred on the sketch origin", $"points={pts.Length}");

                    // Rejecting a second run while a sketch is open for editing:
                    // the AC requires a useful error, and the open sketch must
                    // be left untouched.
                    string again = probe.RunTestSketchProbe();
                    Check(again.StartsWith("ERROR") && again.Contains("already"),
                        "rejects a second create while the sketch is open", again);
                    Check(((object[])sketch.GetSketchSegments()).Length == 4,
                        "rejected run left the open sketch unchanged");

                    // Normal editing works inside the open sketch: add a line
                    // (CreateLine leaves it selected), delete it, square intact.
                    var extra = mgr.CreateLine(0, 0, 0, 0.005, 0, 0) as ISketchSegment;
                    Check(extra != null, "sketch is editable with normal tools (line added)",
                        extra == null ? "CreateLine returned null" : "added");
                    if (extra != null)
                    {
                        doc.EditDelete();
                        var after = (object[])sketch.GetSketchSegments();
                        Check(after.Length == 4, "deleting the probe line leaves the square intact",
                            $"segments={after.Length}");
                    }

                    // Commit the sketch so the feature lands in the tree.
                    mgr.InsertSketch(false);
                    Check(mgr.ActiveSketch == null, "sketch exits edit mode cleanly");
                }

                // The committed sketch lands in the feature tree as a
                // ProfileFeature. Its name depends on run history (the failed
                // run above deleted its partial 'Sketch1'), so traverse rather
                // than guessing a name.
                var sketchFeatures = new List<string>();
                var featObj = doc.FirstFeature() as IFeature;
                while (featObj != null)
                {
                    if (string.Equals(featObj.GetTypeName2(), "ProfileFeature",
                            StringComparison.OrdinalIgnoreCase))
                        sketchFeatures.Add(featObj.Name);
                    featObj = featObj.GetNextFeature() as IFeature;
                }
                Check(sketchFeatures.Count == 1, "exactly one sketch feature exists in the tree",
                    string.Join(", ", sketchFeatures));

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
            // SW accepts the CLSID in several spellings; try the canonical one
            // first, then bare/lowercase forms, then the ProgId.
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
                        Console.WriteLine($"#   cast to IAddInProbe OK");
                        return asProbe;
                    }
                    // The RCW doesn't QI our managed interface — but the object
                    // IS SwAddin (IsSwAddin=true) and its public methods are
                    // callable via the CCW's dispatch interface, exactly the way
                    // SolidWorks invokes command callbacks by name.
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

            public string RunTestSketchProbe() => Invoke("RunTestSketchProbe");
            public string RunFailingSketchProbe() => Invoke("RunFailingSketchProbe");
            public string AddinStateProbe() => Invoke("AddinStateProbe");
        }
    }
}
