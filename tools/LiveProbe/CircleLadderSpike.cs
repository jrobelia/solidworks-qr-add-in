using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// The `circleladder` spike migrated onto GateHost (#63):
    /// `LiveProbe.exe circleladder [deployDir] [progId] [refuse]` — the
    /// ticket-#41 density ladder that sizes MaxCircleInstances: circle-mode
    /// creates through the REAL add-in path (session probes →
    /// CreateQrFromWindowProbe → the pattern emitter) at increasing
    /// emitted-dot counts, timing the same responsiveness set ticket #36
    /// measured for the segment bound — create round-trip, sketch
    /// exit/re-enter, the region solve a contour pick pays — plus the
    /// circle-mode edit operation itself (a master-dim rescale through
    /// SetPatternDimProbe). The module-side rung table is the expectations
    /// print — ahead of the occupied-seat refusal, so a blocked run still
    /// shows what the live half would measure. Rungs the deployed build
    /// refuses (over the recorded bound, or a dot-% cap) are logged and
    /// skipped — the ladder doubles as the bound's live re-verification:
    /// measure with an unbounded build, re-run with the bound to watch it
    /// refuse. `refuse` anywhere in the args runs the bound's live wiring
    /// without paying the create ladder.
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// The circleladder spike's expectations→run state: the sorted rung
        /// table the live half climbs, and the `refuse`-flag parse the
        /// registry's SummaryLine reads back.
        /// </summary>
        private sealed class CircleLadderSpike
        {
            private List<(string tag, string content, bool invert,
                int cells, QrSketchResult verdict, double genMs)> _rungs =
                new List<(string, string, bool, int, QrSketchResult, double)>();

            /// <summary>The `refuse` arg landed — the SummaryLine reads it back.</summary>
            public bool RefuseOnly { get; private set; }

            /// <summary>
            /// The module-side rung table — runs with no SolidWorks, ahead
            /// of the occupied-seat refusal, exactly like the legacy head.
            /// A rung is a QR version (the minimal 'a'×k byte-mode payload
            /// landing on it at ECC M) or its inverted twin. Dots, not
            /// cells, are the currency — emitted counts come from the real
            /// plan.
            /// </summary>
            public bool Expectations()
            {
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
                _rungs = rungs;
                return true;
            }

            /// <summary>
            /// The live ladder — verbatim from the legacy body, against the
            /// hosted session (the probe checks, the refuse pass, and the
            /// per-rung create/measure/delete loop are untouched).
            /// </summary>
            public int Run(LiveSession session)
            {
                var sw = session.Sw;
                var probe = session.Probe;
                string dllPath = session.DllPath;
                var rungs = _rungs;
                var inv = System.Globalization.CultureInfo.InvariantCulture;

                Check(probe.SetQrField("content", PayloadA).StartsWith("VALID"),
                    "probe payload accepted", probe.WindowStateProbe());
                Check(probe.SetQrField("shape", "circle").StartsWith("VALID"),
                    "circle shape accepted", probe.WindowStateProbe());
                Check(probe.SetQrField("param", "90").StartsWith("VALID"),
                    "dot size 90 accepted", probe.SetQrField("param", "90"));

                string template = session.DefaultPartTemplate;
                Check(File.Exists(template), "default part template exists", template);

                // ---- refuse pass: `circleladder <dir> <progId> refuse` ----
                // The bound's live wiring without paying the create ladder:
                // every rung re-validates through the production session
                // probes — the same verdict path Create reaches — and the
                // shared part's feature tree must never change. Over-bound
                // rungs answer INVALID TooManyInstances at pitch 2 mm.
                bool refuseOnly = session.Args.Any(a => string.Equals(a, "refuse",
                    StringComparison.OrdinalIgnoreCase));
                RefuseOnly = refuseOnly;
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
                    // The host prints `# CIRCLELADDER REFUSE ALL PASSED` /
                    // the refuse-failure line (SummaryLine reads RefuseOnly)
                    // and derives the exit code from the failure tally.
                    return 0;
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
                // The host prints `# CIRCLELADDER DONE` / the failure line
                // (SummaryLine) and derives the exit code from the tally.
                return 0;
            }
        }
    }
}
