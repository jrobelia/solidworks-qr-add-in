using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// The `perf` spike migrated onto GateHost (#62): the generation timing
    /// half is the expectations print (host-independent — it always runs,
    /// ahead of the occupied-seat refusal, and prints the PERF lines a
    /// blocked run would still show); the native-insertion half is the Run
    /// sequence.
    ///
    /// Semantics that intentionally change under the uniform policy: the
    /// legacy body treated every SolidWorks-side failure as a skip
    /// (`return 0` — "insertion timing skipped") and never checked for a
    /// running SLDWORKS.exe at all. Hosted, an occupied seat, a visible
    /// session, a failed launch or a missing probe are refusals/failures
    /// (exit 1) like every other gate — and generation timing still runs
    /// first either way.
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// The perf gate's expectations→run state: the payloads that
        /// cleared the insertion guard, with the generation timings the
        /// insertion loop subtracts.
        /// </summary>
        private sealed class PerfGate
        {
            private List<(string tag, QrSketch sketch, double sizeMm, double genMs)>
                _accepted = new List<(string tag, QrSketch sketch, double sizeMm, double genMs)>();

            /// <summary>
            /// The generation half — runs with no SolidWorks, ahead of the
            /// occupied-seat refusal, exactly like the legacy head.
            /// </summary>
            public bool Expectations()
            {
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
                _accepted = accepted;
                return true;
            }

            /// <summary>
            /// The revision banner at its legacy position — first visible
            /// line after the spawn, ahead of the probe acquisition.
            /// </summary>
            public void RevisionBanner(ISldWorks sw, IAddInProbe? firstTry)
            {
                Console.WriteLine($"# SolidWorks revision {sw.RevisionNumber()} — invisible instance, " +
                    "insertion timing enabled");
            }

            /// <summary>
            /// The native-insertion timing loop — verbatim from the legacy
            /// body, against the hosted session.
            /// </summary>
            public int Run(LiveSession session)
            {
                var sw = session.Sw;
                var probe = session.Probe;
                string dllPath = session.DllPath;
                var accepted = _accepted;
                try
                {
                    // ---- native insertion timing (needs a session-free SW) ----
                    string template = session.DefaultPartTemplate;
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
                    // The host prints `# PERF DONE` (SummaryLine) and the
                    // exit code comes from the failure tally.
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# FATAL during insertion timing: {ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
            }
        }
    }
}
