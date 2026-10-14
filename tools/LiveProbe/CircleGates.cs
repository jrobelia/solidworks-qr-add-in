using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Ticket-#42 live gates for the spec-#13 circle-dot pattern — the
    /// production-path suite that composes `circledims` (dimension drive) and
    /// `circleladder` (density bound) logic plus the missing acceptance items:
    /// session plumbing, emission census, per-dot region picks + boss/cut
    /// extrusion, the dimensionless end-state transition, a named-undo pass,
    /// the deleted-sketch equation-orphan check, and a square-mode regression.
    /// Every module-side expectation is computed BEFORE the SLDWORKS.exe
    /// refusal, so a blocked run still prints what the live half would measure.
    ///
    /// Gates (each prints PASS / FAIL / LIMIT lines; exit code 0 only when
    /// nothing failed):
    ///   plumbing    — square default, circle selection, dot-% and invert
    ///                 round-trips through the session probes;
    ///   census      — real ARC entities, arcs == regions == emitted dots,
    ///                 skip list = the light cells, construction squares +
    ///                 the two pattern direction lines accounted for;
    ///   extrude     — SKETCHREGION picks inside retained dots feed
    ///                 FeatureExtrusion3; under invert the same dots feed a
    ///                 Cut-Extrude (Dir=true, the insertion-guard precedent);
    ///   dims        — the QR-square master dim rescales spacing, diameter,
    ///                 seed offsets and the quiet-zone box together, and the
    ///                 measured dot field doubles with them;
    ///   dimensionless — delete the master dim, coincident-bind the square
    ///                 corners to plate vertices, retarget the two spacing
    ///                 equations at the plate dim (docs/CIRCLE-DIMENSIONS.md),
    ///                 verify the pattern still scales off part geometry;
    ///   density     — the largest rung at/under MaxCircleInstances runs the
    ///                 full create/solve/rescale measurement; the first
    ///                 over-bound rung refuses pre-mutation;
    ///   undo        — the API named-undo object removes exactly the new
    ///                 sketch (prior marker sketch survives), sweeping its
    ///                 equations; the toggle reinsert restores it;
    ///   orphans     — a user-side sketch delete leaves orphaned equations;
    ///                 the next create's start-purge must let it succeed;
    ///   square      — the default suite's block path still emits cleanly.
    /// </summary>
    public static partial class Program
    {
        private static int _limitations;
        private static void Limit(string name, string detail = "")
        {
            _limitations++;
            Console.WriteLine($"LIMIT  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
        }

        private static int CircleGatesMain(string[] args)
        {
            string deployDir = args.Length > 0 ? args[0] : Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // ================= module-side expectations =================
            var gate = GateCircleVerdict(PayloadA, 25.0, invert: false);
            var gateInv = GateCircleVerdict(PayloadA, 25.0, invert: true);
            if (!gate.Succeeded || !gateInv.Succeeded ||
                gate.Sketch!.CirclePlan == null || gateInv.Sketch!.CirclePlan == null)
            {
                Console.WriteLine("# FATAL: module-side circle plan for the gate payload failed: " +
                    (gate.Error?.Message ?? gateInv.Error?.Message ?? "?"));
                return 1;
            }
            var plan = gate.Sketch.CirclePlan!;
            var planInv = gateInv.Sketch.CirclePlan!;
            Console.WriteLine("# ticket-#42 circle-pattern live gates (spec #13)");
            Console.WriteLine($"# payload '{PayloadA}' @25mm ECC=M dot=90% -> N={plan.DataCells} " +
                $"M={plan.CellsPerSide} pitch={plan.PitchM * 1000:F4}mm " +
                $"dia={plan.DotDiameterM * 1000:F4}mm emitted={plan.EmittedCount} " +
                $"skipped={plan.SkippedCount}");
            Console.WriteLine($"# inverted twin -> emitted={planInv.EmittedCount} " +
                $"skipped={planInv.SkippedCount} pattern={planInv.PatternCells}x{planInv.PatternCells}");
            Console.WriteLine($"# recorded bound: MaxCircleInstances={QrInsertionGuard.MaxCircleInstances}");

            // Density rungs: the module-side emitted count for every QR
            // version through v20 at both polarities — picks the rung just
            // under the bound and the first over it.
            var versions = Enumerable.Range(1, 20).ToArray();
            var landing = MinContentForVersions(versions);
            var rungs = new List<(string tag, string content, bool invert,
                int cells, int dots)>();
            foreach (var v in versions)
            {
                if (!landing.TryGetValue(v, out int k)) continue;
                string content = new string('a', k);
                foreach (var invF in new[] { false, true })
                {
                    // Counted straight off the encoded matrix — the
                    // generator refuses over-bound payloads, but the table
                    // still needs their real emitted counts.
                    rungs.Add(($"v{v} k={k}{(invF ? " INVERT" : "")}", content, invF,
                        17 + 4 * v, EmittedDotsFor(content, invF)));
                }
            }
            var legal = rungs.Where(r => r.dots >= 0 &&
                r.dots <= QrInsertionGuard.MaxCircleInstances).ToList();
            var over = rungs.Where(r => r.dots > QrInsertionGuard.MaxCircleInstances)
                .OrderBy(r => r.dots).FirstOrDefault();
            var under = legal.OrderByDescending(r => r.dots).FirstOrDefault();
            Console.WriteLine("# density rung table (module-side, dot 90%):");
            foreach (var r in rungs.OrderBy(r => r.dots))
                if (r.dots > 0)
                    Console.WriteLine($"#   {r.tag,-20} N={r.cells} dots={r.dots}");
            Console.WriteLine(under.content != null
                ? $"# just-under rung: {under.tag} dots={under.dots}"
                : "# just-under rung: NONE FOUND — density gate cannot run");
            Console.WriteLine(over.content != null
                ? $"# over-bound rung: {over.tag} dots={over.dots}"
                : "# over-bound rung: none below v20 — checked on the refuse path only");

            if (System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: the circle gates require SolidWorks " +
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

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);
                if (!File.Exists(template)) return 1;

                // ================= GATE 1 — plumbing =================
                Console.WriteLine("# ---- gate 1: session plumbing ----");
                var state0 = probe.WindowStateProbe();
                Check(state0.Contains("shape=square") && state0.Contains("invert=False"),
                    "fresh session defaults to square cells, normal polarity", state0);
                Check(probe.SetQrCellShapeProbe("bogus").StartsWith("ERROR"),
                    "an unknown cell shape is refused",
                    probe.SetQrCellShapeProbe("bogus"));
                Check(probe.SetQrCellShapeProbe("circle").StartsWith("VALID"),
                    "circle shape accepted", probe.WindowStateProbe());
                Check(probe.WindowStateProbe().Contains("shape=circle"),
                    "session echoes shape=circle", probe.WindowStateProbe());
                Check(probe.SetQrShapeParamProbe("90").StartsWith("VALID"),
                    "dot size 90% accepted", probe.SetQrShapeParamProbe("90"));
                Check(probe.WindowStateProbe().Contains("param=90"),
                    "session echoes param=90", probe.WindowStateProbe());
                Check(probe.SetQrShapeParamProbe("abc").StartsWith("INVALID"),
                    "a non-numeric dot size is refused", probe.SetQrShapeParamProbe("abc"));
                Check(probe.SetQrShapeParamProbe("130").StartsWith("INVALID"),
                    "an over-100% dot size is refused", probe.SetQrShapeParamProbe("130"));
                Check(probe.SetQrShapeParamProbe("90").StartsWith("VALID"),
                    "dot size restores to 90%", "");
                Check(probe.SetQrInvertProbe(true).StartsWith("VALID") &&
                      probe.WindowStateProbe().Contains("invert=True"),
                    "invert=true round-trips", probe.WindowStateProbe());
                Check(probe.SetQrInvertProbe(false).StartsWith("VALID") &&
                      probe.WindowStateProbe().Contains("invert=False"),
                    "invert=false round-trips", probe.WindowStateProbe());
                Check(probe.SetQrContentProbe(PayloadA).StartsWith("VALID"),
                    "gate payload accepted", probe.SetQrContentProbe(PayloadA));

                // ================= GATE 2 — emission census =================
                Console.WriteLine("# ---- gate 2: emission census ----");
                var docA = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(docA != null, "part document A", docA == null ? "null" : "ok");
                if (docA == null) return 1;
                var extA = (IModelDocExtension)docA.Extension;
                var mgrA = (ISketchManager)docA.SketchManager;
                docA.ClearSelection2(true);
                Check(extA.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected (A)");
                var created = probe.CreateQrFromWindowProbe();
                Check(created.StartsWith("OK"), "circle create succeeds (A)", created);
                if (!created.StartsWith("OK")) return 1;
                string skA = created.Split(' ')[1];

                var cen = ParseCensus(probe.PatternCensusProbe(skA));
                CheckCensus("open-sketch census (A)", cen, plan, expectedLines: 10);
                mgrA.InsertSketch(false);
                var cenC = ParseCensus(probe.PatternCensusProbe(skA));
                CheckCensus("committed census (A)", cenC, plan, expectedLines: 10);

                // Region picks: emitting cells select, skipped cells don't.
                var selA = (ISelectionMgr)docA.ISelectionManager;
                int picked = PickDotRegions(docA, extA, selA, plan, 3);
                Check(picked == 3, "SKETCHREGION picks land inside retained dots",
                    $"picks={picked}/3");
                int emptyPicks = PickEmptyCells(docA, extA, selA, plan, 3);
                Check(emptyPicks == 0, "skipped (light) cells offer no region",
                    $"emptyPicks={emptyPicks}");
                docA.ClearSelection2(true);

                // ================= GATE 3 — dimension drive =================
                Console.WriteLine("# ---- gate 3: dimension drive ----");
                var dimsA = ParseDims(probe.PatternDimsProbe(skA));
                var eqsA = ParseEquations(probe.PatternDimsProbe(skA));
                Check(dimsA != null && eqsA != null && eqsA.Count == 8,
                    "dimension census: 8 equations installed", probe.PatternDimsProbe(skA));
                double pitchMm = plan.PitchM * 1000.0;
                double boxMm = plan.PitchM * plan.DataCells * 1000.0;
                if (dimsA != null)
                {
                    Check(Math.Abs(DimVal(dimsA, "D1") - boxMm) < 0.01,
                        "D1 master dim = the 25 mm QR square",
                        $"D1={DimVal(dimsA, "D1"):F4}mm want≈{boxMm:F4}");
                    Check(Math.Abs(DimVal(dimsA, "D5") - pitchMm) < 0.01 &&
                          Math.Abs(DimVal(dimsA, "D6") - pitchMm) < 0.01,
                        "spacing dims = box/N",
                        $"D5={DimVal(dimsA, "D5"):F4} D6={DimVal(dimsA, "D6"):F4} want≈{pitchMm:F4}");
                    Check(Math.Abs(DimVal(dimsA, "D4") - pitchMm * plan.CellsPerSide) < 0.02,
                        "keep-out size = spacing·M",
                        $"D4={DimVal(dimsA, "D4"):F4} want≈{pitchMm * plan.CellsPerSide:F4}");
                }
                var fieldBefore = RegionCircleCentres(sw, docA, skA);
                var spanBefore = FieldSpan(fieldBefore);
                Check(fieldBefore.Count == plan.EmittedCount,
                    "every committed region resolves to a circle",
                    $"circles={fieldBefore.Count} emitted={plan.EmittedCount}");
                Check(Math.Abs(fieldBefore[0].r - plan.DotDiameterM / 2) < 1e-6,
                    "dot radius matches the plan", $"r={fieldBefore[0].r * 1000:F4}mm");

                var set = probe.SetPatternDimProbe($"D1@{skA}", boxMm * 2 / 1000.0);
                Check(set.StartsWith("PATSET"), "master dim rescale accepted", set);
                var dimsB = ParseDims(probe.PatternDimsProbe(skA));
                if (dimsB != null)
                {
                    Check(Math.Abs(DimVal(dimsB, "D1") - boxMm * 2) < 0.02,
                        "master dim doubled", $"D1={DimVal(dimsB, "D1"):F4}mm");
                    Check(Math.Abs(DimVal(dimsB, "D5") - pitchMm * 2) < 0.02 &&
                          Math.Abs(DimVal(dimsB, "D6") - pitchMm * 2) < 0.02,
                        "spacing dims track the master",
                        $"D5={DimVal(dimsB, "D5"):F4} D6={DimVal(dimsB, "D6"):F4}");
                    Check(Math.Abs(DimVal(dimsB, "D7") - pitchMm * 2 * 0.9) < 0.02,
                        "dot diameter tracks spacing",
                        $"D7={DimVal(dimsB, "D7"):F4} want≈{pitchMm * 2 * 0.9:F4}");
                    Check(Math.Abs(DimVal(dimsB, "D4") - pitchMm * 2 * plan.CellsPerSide) < 0.05,
                        "quiet-zone box tracks spacing",
                        $"D4={DimVal(dimsB, "D4"):F4} want≈{pitchMm * 2 * plan.CellsPerSide:F4}");
                }
                var fieldAfter = RegionCircleCentres(sw, docA, skA);
                var spanAfter = FieldSpan(fieldAfter);
                Check(fieldAfter.Count == plan.EmittedCount &&
                      Math.Abs(spanAfter.x - spanBefore.x * 2) < 1e-6 &&
                      Math.Abs(spanAfter.y - spanBefore.y * 2) < 1e-6,
                    "measured dot field doubles with the master",
                    $"span {spanBefore.x * 1000:F3}→{spanAfter.x * 1000:F3}mm " +
                    $"(want {(plan.DataCells - 1) * pitchMm * 2:F3})");
                var cenB = ParseCensus(probe.PatternCensusProbe(skA));
                Check(cenB != null && cenB.Regions == plan.EmittedCount,
                    "region count unchanged after rescale",
                    $"regions={cenB?.Regions}");

                // ================= GATE 4 — orphan repro + recreate =================
                Console.WriteLine("# ---- gate 4: deleted-sketch orphan equations ----");
                docA.ClearSelection2(true);
                extA.SelectByID2(skA, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                docA.EditDelete();   // the USER delete path — bypasses our cleanup
                Check(FindSketchFeature(docA, skA) == null,
                    "sketch deleted via the raw EditDelete path", skA);
                var eqsOrphan = ParseEquations(probe.PatternDimsProbe("gone"));
                int orphans = eqsOrphan?.Count(t => t.Contains($"@{skA}")) ?? -1;
                Check(orphans == 8,
                    "the user delete orphans the sketch's 8 equations (the defect's input)",
                    $"orphans={orphans} eqs={eqsOrphan?.Count}");
                docA.ClearSelection2(true);
                Check(extA.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane re-selected (A)");
                var created2 = probe.CreateQrFromWindowProbe();
                Check(created2.StartsWith("OK"),
                    "second circle create survives the orphaned equations (create-start purge)",
                    created2);
                if (created2.StartsWith("OK"))
                {
                    string skA2 = created2.Split(' ')[1];
                    var eqsB = ParseEquations(probe.PatternDimsProbe(skA2));
                    Check(eqsB != null && eqsB.Count == 8 &&
                          eqsB.All(t => t.Contains($"@{skA2}")),
                        "the purge left exactly the new sketch's 8 equations",
                        $"eqs={eqsB?.Count} staleRefs={eqsB?.Count(t => t.Contains($"@{skA}\""))}");
                    mgrA.InsertSketch(false);

                    // ================= GATE 5 — per-dot boss extrude =================
                    Console.WriteLine("# ---- gate 5: boss extrude from dot regions ----");
                    docA.ClearSelection2(true);
                    int bossPicks = PickDotRegions(docA, extA, selA, plan, 3);
                    Check(bossPicks == 3, "dot regions selected for the boss",
                        $"picks={bossPicks}/3");
                    var fmA = (IFeatureManager)docA.FeatureManager;
                    var boss = fmA.FeatureExtrusion3(
                        true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                        0.002, 0.0, false, false, false, false, 0.0, 0.0,
                        false, false, false, false,
                        true, true, true,
                        (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
                    Check(boss != null, "dot regions extrude a boss (PAT3 precedent)",
                        boss == null ? "FeatureExtrusion3 returned null" : boss.Name);
                }
                try { sw.CloseDoc(docA.GetTitle()); } catch { }

                // ================= GATE 6 — invert cut =================
                Console.WriteLine("# ---- gate 6: invert polarity + cut extrude ----");
                var docB = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(docB != null, "part document B", docB == null ? "null" : "ok");
                if (docB == null) return 1;
                var extB = (IModelDocExtension)docB.Extension;
                var mgrB = (ISketchManager)docB.SketchManager;
                var plate = CreateBackingPlate(docB, extB, mgrB,
                    (IFeatureManager)docB.FeatureManager);
                Check(plate != null, "backing plate created (B)",
                    plate == null ? "null" : plate.Name);
                string invResp = probe.SetQrInvertProbe(true);
                Check(invResp.StartsWith("VALID") &&
                      invResp.Contains($"dots={planInv.EmittedCount}"),
                    "invert session verdict echoes the module plan", invResp);
                docB.ClearSelection2(true);
                Check(extB.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected (B)");
                var createdInv = probe.CreateQrFromWindowProbe();
                Check(createdInv.StartsWith("OK"), "inverted circle create succeeds", createdInv);
                if (createdInv.StartsWith("OK"))
                {
                    string skB = createdInv.Split(' ')[1];
                    var cenInv = ParseCensus(probe.PatternCensusProbe(skB));
                    Check(cenInv != null && cenInv.Arcs == planInv.EmittedCount &&
                          cenInv.Regions == planInv.EmittedCount &&
                          cenInv.Skipped == planInv.SkippedCount && cenInv.Invert,
                        "inverted census: arcs==regions==light cells",
                        $"arcs={cenInv?.Arcs} regions={cenInv?.Regions} " +
                        $"skipped={cenInv?.Skipped} invertEcho={cenInv?.Invert}");
                    mgrB.InsertSketch(false);
                    var selB = (ISelectionMgr)docB.ISelectionManager;
                    int cutPicks = PickDotRegions(docB, extB, selB, planInv, 3);
                    Check(cutPicks == 3, "inverted dot regions selected for the cut",
                        $"picks={cutPicks}/3");
                    var cut = ((IFeatureManager)docB.FeatureManager).FeatureCut4(
                        true, false, true, (int)swEndConditions_e.swEndCondBlind, 0,
                        0.001, 0.0, false, false, false, false, 0.0, 0.0,
                        false, false, false, false,
                        false, true, true, false, false, false,
                        (int)swStartConditions_e.swStartSketchPlane, 0.0, false, true) as IFeature;
                    Check(cut != null, "inverted dots cut the plate (Dir=true precedent)",
                        cut == null ? "FeatureCut4 returned null" : cut.Name);
                }
                _ = probe.SetQrInvertProbe(false);
                try { sw.CloseDoc(docB.GetTitle()); } catch { }

                // ================= GATE 7 — dimensionless end-state =================
                Console.WriteLine("# ---- gate 7: dimensionless end-state ----");
                CircleGate7_Dimensionless(sw, probe, template, plan, inv);

                // ================= GATE 8 — named undo =================
                Console.WriteLine("# ---- gate 8: named undo ----");
                CircleGate8_Undo(sw, probe, template, plan);

                // ================= GATE 9 — density at the bound =================
                Console.WriteLine("# ---- gate 9: density at MaxCircleInstances ----");
                CircleGate9_Density(sw, probe, template, under, over, inv);

                // ================= GATE 10 — square regression =================
                Console.WriteLine("# ---- gate 10: square-mode regression ----");
                CircleGate10_Square(sw, probe, template);

                try { sw.UnloadAddIn(dllPath); } catch { }
                Console.WriteLine(_failures == 0
                    ? (_limitations == 0 ? "# CIRCLES ALL PASSED"
                        : $"# CIRCLES PASSED with {_limitations} limitation(s)")
                    : $"# {_failures} check(s) failed" +
                      (_limitations > 0 ? $" ({_limitations} limitation(s))" : ""));
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

        // ---------------- shared gate helpers ----------------

        /// <summary>The module's circle-plan verdict at a specific size/polarity.</summary>
        private static QrSketchResult GateCircleVerdict(string content, double sizeMm, bool invert) =>
            QrSketchGenerator.Generate(new QrSketchRequest(content, sizeMm, 0.03)
            {
                EccLevel = QRCodeGenerator.ECCLevel.M,
                CellShape = QrCellShape.Circle,
                CellShapeParameter = 0.90,
                Invert = invert,
            });

        /// <summary>Parsed fields from a PATCENSUS line.</summary>
        private sealed class CensusFields
        {
            public int Segs = -1, Arcs = -1, Lines = -1, Cons = -1, Regions = -1;
            public int Emitted = -1, Skipped = -1, Cells = -1;
            public bool Invert;
        }

        private static CensusFields? ParseCensus(string line)
        {
            if (line == null || !line.StartsWith("PATCENSUS")) return null;
            return new CensusFields
            {
                Segs = FieldInt(line, "segs"),
                Arcs = FieldInt(line, "arcs"),
                Lines = FieldInt(line, "lines"),
                Cons = FieldInt(line, "cons"),
                Regions = FieldInt(line, "regions"),
                Emitted = FieldInt(line, "emitted"),
                Skipped = FieldInt(line, "skipped"),
                Cells = FieldInt(line, "cells"),
                Invert = line.Contains("invert=True"),
            };
        }

        /// <summary>The census assertions every committed circle sketch must meet.</summary>
        private static void CheckCensus(string tag, CensusFields? cen, QrCirclePlan plan,
            int expectedLines)
        {
            Check(cen != null, $"census parses ({tag})");
            if (cen == null) return;
            Check(cen.Arcs == plan.EmittedCount,
                $"arcs == emitted dots ({tag})", $"arcs={cen.Arcs} emitted={plan.EmittedCount}");
            Check(cen.Regions == plan.EmittedCount,
                $"one closed region per dot ({tag})",
                $"regions={cen.Regions} emitted={plan.EmittedCount}");
            Check(cen.Skipped == plan.SkippedCount && cen.Emitted == plan.EmittedCount,
                $"plan echo: skipped cells = light cells ({tag})",
                $"skipped={cen.Skipped} emitted={cen.Emitted}");
            Check(cen.Lines == expectedLines && cen.Cons >= 8,
                $"construction squares + 2 pattern direction lines accounted for ({tag})",
                $"lines={cen.Lines} (8 square edges + 2 direction lines) cons={cen.Cons}");
            Check(cen.Invert == plan.Inverted,
                $"polarity echo ({tag})", $"invert={cen.Invert}");
        }

        /// <summary>
        /// The circle plan's emitted-dot count computed straight off the
        /// encoded matrix — valid even for payloads the generator's
        /// insertion guard refuses (the rung table needs real counts past
        /// the bound). ModuleMatrix already carries the quiet zone (ADR
        /// 0002): data cells live at [4, Count−4). Normal polarity emits
        /// the dark data cells; invert emits every light cell of the whole
        /// working matrix — exactly <see cref="QrCirclePlan.EmittedCount"/>.
        /// </summary>
        private static int EmittedDotsFor(string content, bool invert)
        {
            using (var gen = new QRCodeGenerator())
            using (var d = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.M))
            {
                var mm = d.ModuleMatrix;
                int m = mm.Count;
                int dark = 0;
                for (int r = 4; r < m - 4; r++)
                    for (int c = 4; c < m - 4; c++)
                        if (mm[r][c]) dark++;
                return invert ? m * m - dark : dark;
            }
        }

        /// <summary>`key=value` integer fields out of a probe line.</summary>
        private static int FieldInt(string s, string key)
        {
            int i = s.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return -1;
            i += key.Length + 1;
            int j = i;
            while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '-')) j++;
            return int.TryParse(s.Substring(i, j - i), out int v) ? v : -1;
        }

        /// <summary>All `D{n}=<mm>mm(s{st})` entries from a PATDIMS line.</summary>
        private static Dictionary<string, double>? ParseDims(string patdims)
        {
            if (string.IsNullOrEmpty(patdims) || !patdims.StartsWith("PATDIMS")) return null;
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            int i = 0;
            while (i < patdims.Length)
            {
                int d = patdims.IndexOf("D", i, StringComparison.Ordinal);
                if (d < 0 || d + 1 >= patdims.Length || !char.IsDigit(patdims[d + 1])) { i = d + 1; if (d < 0) break; continue; }
                int j = d + 1;
                while (j < patdims.Length && char.IsDigit(patdims[j])) j++;
                if (j < patdims.Length && patdims[j] == '=')
                {
                    int mm = patdims.IndexOf("mm", j, StringComparison.Ordinal);
                    if (mm < 0) break;
                    if (double.TryParse(patdims.Substring(j + 1, mm - j - 1),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double v))
                        map[patdims.Substring(d, j - d)] = v;
                    i = mm + 2;
                }
                else i = j;
            }
            return map;
        }

        private static double DimVal(Dictionary<string, double> dims, string name) =>
            dims.TryGetValue(name, out double v) ? v : double.NaN;

        /// <summary>The verbatim equation texts from a PATDIMS line.</summary>
        private static List<string>? ParseEquations(string patdims)
        {
            if (string.IsNullOrEmpty(patdims) || !patdims.StartsWith("PATDIMS")) return null;
            var list = new List<string>();
            int i = patdims.IndexOf(" eqs=", StringComparison.Ordinal);
            if (i < 0) return list;
            i += 5;
            while (i < patdims.Length && char.IsDigit(patdims[i])) i++;
            var rest = patdims.Substring(i);
            int m = rest.IndexOf(" mode=", StringComparison.Ordinal);
            if (m >= 0) rest = rest.Substring(0, m);
            var parts = rest.Split('\'');
            for (int k = 1; k < parts.Length; k += 2)
                if (parts[k].Trim().Length > 0)
                    list.Add(parts[k]);
            return list;
        }

        /// <summary>
        /// Every committed-sketch region's circle — centre mapped into sketch
        /// space, radius in metres — via the regions' edge ICurve.CircleParams
        /// (the sibling-COM arc problem lives on the segment API, not IEdge).
        /// </summary>
        private static List<(double x, double y, double r)> RegionCircleCentres(
            ISldWorks sw, IModelDoc2 doc, string sketchName)
        {
            var list = new List<(double, double, double)>();
            var feat = FindSketchFeature(doc, sketchName);
            var sk = feat?.GetSpecificFeature2() as ISketch;
            if (sk == null) return list;
            MathUtility? mu = null;
            try { mu = sw.IGetMathUtility() as MathUtility; } catch { }
            MathTransform? m2s = null;
            try { m2s = sk.ModelToSketchTransform as MathTransform; } catch { }
            if (sk.GetSketchRegions() is not object[] regions) return list;
            foreach (var ro in regions)
            {
                if (ro is not ISketchRegion rg || rg.GetEdges() is not object[] edges)
                    continue;
                foreach (var eo in edges)
                {
                    if (eo is not IEdge e) continue;
                    try
                    {
                        if (e.GetCurve() is ICurve cur &&
                            cur.CircleParams is double[] cp && cp.Length >= 7)
                        {
                            var p = mu != null && m2s != null
                                ? MapPoint(mu, m2s, cp[0], cp[1], cp[2])
                                : new[] { cp[0], cp[1], cp[2] };
                            list.Add((p[0], p[1], cp[6]));
                            break;   // a dot region is bounded by one circle
                        }
                    }
                    catch { }
                }
            }
            return list;
        }

        private static (double x, double y) FieldSpan(List<(double x, double y, double r)> dots)
        {
            if (dots.Count == 0) return (double.NaN, double.NaN);
            return (dots.Max(d => d.x) - dots.Min(d => d.x),
                    dots.Max(d => d.y) - dots.Min(d => d.y));
        }

        /// <summary>SKETCHREGION picks inside up to <paramref name="max"/> emitting cells.</summary>
        private static int PickDotRegions(IModelDoc2 doc, IModelDocExtension ext,
            ISelectionMgr sel, QrCirclePlan plan, int max)
        {
            doc.ClearSelection2(true);
            try { sel.EnableContourSelection = true; } catch { }
            int picks = 0;
            try
            {
                for (var r = 0; r < plan.CellsPerSide && picks < max; r++)
                    for (var c = 0; c < plan.CellsPerSide && picks < max; c++)
                    {
                        if (!plan.Emits(r, c)) continue;
                        var p = plan.CellCentreM(r, c);
                        try
                        {
                            if (ext.SelectByID2("", "SKETCHREGION", p.X, p.Y, 0,
                                    true, 0, null,
                                    (int)swSelectOption_e.swSelectOptionDefault))
                                picks++;
                        }
                        catch { }
                    }
            }
            finally
            {
                try { sel.EnableContourSelection = false; } catch { }
            }
            return picks;
        }

        /// <summary>SKETCHREGION picks inside skipped cells — must all fail.
        /// Iterates the PATTERN grid only (origin..origin+cells), so a hit
        /// means the skip string really did not take.</summary>
        private static int PickEmptyCells(IModelDoc2 doc, IModelDocExtension ext,
            ISelectionMgr sel, QrCirclePlan plan, int max)
        {
            doc.ClearSelection2(true);
            try { sel.EnableContourSelection = true; } catch { }
            int hits = 0, tried = 0;
            try
            {
                for (var r = plan.PatternOriginRow;
                     r < plan.PatternOriginRow + plan.PatternCells && tried < max; r++)
                    for (var c = plan.PatternOriginCol;
                         c < plan.PatternOriginCol + plan.PatternCells && tried < max; c++)
                    {
                        if (plan.Emits(r, c)) continue;
                        tried++;
                        var p = plan.CellCentreM(r, c);
                        try
                        {
                            if (ext.SelectByID2("", "SKETCHREGION", p.X, p.Y, 0,
                                    true, 0, null,
                                    (int)swSelectOption_e.swSelectOptionDefault))
                                hits++;
                        }
                        catch { }
                    }
            }
            finally
            {
                try { sel.EnableContourSelection = false; } catch { }
            }
            return hits;
        }

        // ================= gate 7 — dimensionless end-state =================
        //
        // docs/CIRCLE-DIMENSIONS.md's manual transition, driven through the
        // API: delete the QR-square master dim, coincident-bind the square's
        // corner sketch points to plate vertices (PAT6), then re-point only
        // the two spacing equations at the plate's driving dim — the rest of
        // the table follows through the spacing names. Afterwards the plate
        // dim alone must rescale the whole dot field: no sketch driving dim
        // remains, which is what "dimensionless" means here.

        private static void CircleGate7_Dimensionless(
            ISldWorks sw, IAddInProbe probe, string template, QrCirclePlan plan,
            System.Globalization.CultureInfo inv)
        {
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            Check(doc != null, "part document C", doc == null ? "null" : "ok");
            if (doc == null) return;
            var c = Pat.Of(sw, doc);
            try
            {
                // Plate: 60 mm square off-origin + driving dim + 5 mm boss —
                // PAT6's exact arrangement so the coincident binds visibly
                // snap the box.
                if (!c.OpenSketch()) { Check(false, "plate sketch opens (C)"); return; }
                const double plateSize = 0.060;
                foreach (var (x1, y1, x2, y2) in new[]
                         { (0.0, 0.0, plateSize, 0.0), (plateSize, 0.0, plateSize, plateSize),
                           (plateSize, plateSize, 0.0, plateSize), (0.0, plateSize, 0.0, 0.0) })
                    c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0);
                doc.ClearSelection2(true);
                c.SelSegAt(plateSize / 2, 0);
                IDimension? plateDim = null;
                try
                {
                    plateDim = (doc.IAddHorizontalDimension2(plateSize / 2, -0.004, 0)
                        as IDisplayDimension)?.GetDimension() as IDimension;
                }
                catch { }
                c.Mgr.InsertSketch(false);
                string plateSketch = FindSketchFeatureNames(doc).Last();
                Check(plateDim != null, "plate carries its driving dim",
                    plateDim?.FullName ?? "null");
                if (plateDim == null) return;
                string plateEq = EqName(plateDim.FullName);   // "D1@SketchN"

                doc.ClearSelection2(true);
                c.Ext.SelectByID2(plateSketch, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                var plate = c.Fm.FeatureExtrusion3(
                    true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                    0.005, 0.0, false, false, false, false, 0.0, 0.0,
                    false, false, false, false,
                    true, true, true,
                    (int)swStartConditions_e.swStartSketchPlane, 0.0, false) as IFeature;
                Check(plate != null, "plate extrudes", plate == null ? "null" : plate.Name);

                // Top face + verts, then the QR create ON the face.
                var topFace = FindTopFace(doc, new[] { 0.0, 0.0, 0.005 });
                Check(topFace != null, "plate top face found (C)");
                if (topFace == null) return;
                var verts = new List<double[]>();
                if (topFace.GetEdges() is object[] edges)
                    foreach (var eo in edges)
                        if (eo is IEdge e)
                            foreach (var v in new[]
                                     { e.GetStartVertex() as IVertex, e.GetEndVertex() as IVertex })
                                if (v?.GetPoint() is double[] p &&
                                    !verts.Any(q => Math.Abs(q[0] - p[0]) < 1e-9 &&
                                                    Math.Abs(q[1] - p[1]) < 1e-9 &&
                                                    Math.Abs(q[2] - p[2]) < 1e-9))
                                    verts.Add(p);
                Check(verts.Count >= 4, "plate corner verts resolved", $"verts={verts.Count}");

                doc.ClearSelection2(true);
                ((IEntity)topFace).Select4(false, null);
                string created = "";
                double createMs = TimeMs(() => created = probe.CreateQrFromWindowProbe());
                Check(created.StartsWith("OK"), "circle create on the plate face (C)",
                    $"{created} ({createMs:F0}ms)");
                if (!created.StartsWith("OK")) return;
                string qr = created.Split(' ')[1];
                var qrSketch = c.Mgr.ActiveSketch as ISketch;
                Check(qrSketch != null, "the face QR sketch is open for the transition");
                if (qrSketch == null) return;

                // Find the master + spacing names from the installed eqs:
                // the spacing pair is the only `"D@sk" = "X@sk"/N` form.
                string qrName = qr;
                string boxRef = "";
                var spacingLhs = new List<string>();
                var spacingIdx = new List<int>();
                var eqm = doc.GetEquationMgr() as IEquationMgr;
                if (eqm != null)
                    for (int i = 0; i < eqm.GetCount(); i++)
                    {
                        string? t = null;
                        try { t = eqm.Equation[i]; } catch { }
                        if (t == null) continue;
                        int eq = t.IndexOf('=');
                        if (eq < 0) continue;
                        string lhs = t.Substring(0, eq).Trim().Trim('"');
                        string rhs = t.Substring(eq + 1);
                        if (!rhs.Contains($"/{plan.DataCells}")) continue;
                        int q1 = rhs.IndexOf('"');
                        int q2 = rhs.IndexOf('"', q1 + 1);
                        if (q1 < 0 || q2 < 0) continue;
                        boxRef = rhs.Substring(q1 + 1, q2 - q1 - 1);
                        spacingLhs.Add(lhs);
                        spacingIdx.Add(i);
                    }
                Check(spacingLhs.Count == 2 && boxRef.Length > 0,
                    "the two spacing equations + master reference resolved",
                    $"spacing=[{string.Join(",", spacingLhs)}] box='{boxRef}'");
                if (spacingLhs.Count != 2) return;

                // 1. Delete the master dim (in-edit).  2. Bind the corners.
                var boxDim = doc.IParameter(boxRef) as IDimension;
                string boxFull = "";
                try { boxFull = boxDim?.FullName ?? ""; } catch { }
                doc.ClearSelection2(true);
                bool dimSel = c.Ext.SelectByID2(boxRef, "DIMENSION", 0, 0, 0, false,
                    0, null, (int)swSelectOption_e.swSelectOptionDefault);
                if (!dimSel && boxFull.Length > 0)
                    dimSel = c.Ext.SelectByID2(boxFull, "DIMENSION",
                        0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                bool deleted = false;
                if (dimSel) { try { doc.EditDelete(); deleted = true; } catch { } }
                bool gone = doc.IParameter(boxRef) == null;
                Check(deleted && gone, "master dim deleted (the dimensionless step)",
                    $"sel={dimSel} deleted={deleted} resolved={(doc.IParameter(boxRef) != null)}");

                var m2s = qrSketch.ModelToSketchTransform;
                double h = plan.PitchM * plan.DataCells / 2.0;
                var mapped = verts.Select(v => MapPoint(c.Mu, m2s, v[0], v[1], v[2]))
                                  .ToList();
                var cornerLo = new[] { -h, -h };
                var cornerHi = new[] { +h, +h };
                int iLo = Enumerable.Range(0, verts.Count)
                    .OrderBy(i => (mapped[i][0] - cornerLo[0]) * (mapped[i][0] - cornerLo[0]) +
                                  (mapped[i][1] - cornerLo[1]) * (mapped[i][1] - cornerLo[1]))
                    .First();
                int iHi = Enumerable.Range(0, verts.Count)
                    .OrderBy(i => (mapped[i][0] - cornerHi[0]) * (mapped[i][0] - cornerHi[0]) +
                                  (mapped[i][1] - cornerHi[1]) * (mapped[i][1] - cornerHi[1]))
                    .First();
                bool b1 = false, b2 = false; double near1 = -1, near2 = -1;
                doc.ClearSelection2(true);
                if (SelSketchPointAt(c, cornerLo[0], cornerLo[1], append: false) &&
                    c.SelVertex(verts[iLo][0], verts[iLo][1], verts[iLo][2], append: true))
                {
                    try { doc.SketchAddConstraints("sgCOINCIDENT"); b1 = true; } catch { }
                    near1 = NearestSketchPointDist(c.Mgr.ActiveSketch as ISketch,
                        mapped[iLo][0], mapped[iLo][1]);
                }
                doc.ClearSelection2(true);
                if (SelSketchPointAt(c, cornerHi[0], cornerHi[1], append: false) &&
                    c.SelVertex(verts[iHi][0], verts[iHi][1], verts[iHi][2], append: true))
                {
                    try { doc.SketchAddConstraints("sgCOINCIDENT"); b2 = true; } catch { }
                    near2 = NearestSketchPointDist(c.Mgr.ActiveSketch as ISketch,
                        mapped[iHi][0], mapped[iHi][1]);
                }
                Check(b1 && b2 && near1 >= 0 && near1 < 1e-6 && near2 >= 0 && near2 < 1e-6,
                    "QR-square corners coincident-bound to plate vertices (PAT6)",
                    $"b1={b1}(near {(near1 < 0 ? "?" : (near1 * 1000).ToString("F3") + "mm")}) " +
                    $"b2={b2}(near {(near2 < 0 ? "?" : (near2 * 1000).ToString("F3") + "mm")})");
                c.Mgr.InsertSketch(false);

                // 3. Retarget the two spacing equations at the plate dim.
                for (int i = spacingIdx.Count - 1; i >= 0; i--)
                    try { eqm!.Delete(spacingIdx[i]); } catch { }
                int added = 0;
                foreach (var lhs in spacingLhs)
                    try { if (eqm!.Add(-1, $"\"{lhs}\" = \"{plateEq}\"/{plan.DataCells}") >= 0) added++; }
                    catch { }
                try { eqm?.EvaluateAll(); } catch { }
                try { doc.EditRebuild3(); } catch { }
                Check(added == 2, "spacing equations retargeted at the plate dim",
                    $"added={added}/2 -> \"{plateEq}\"/{plan.DataCells}");

                // 4. Drive the plate 60 → 90 mm; spacing must follow.
                var setRes = probe.SetPatternDimProbe(plateEq, 0.090);
                Check(setRes.StartsWith("PATSET") && setRes.Contains("90.0000mm"),
                    "plate dim rescale accepted", setRes);
                var dims = ParseDims(probe.PatternDimsProbe(qrName));
                double want = 90.0 / plan.DataCells;
                Check(dims != null &&
                      Math.Abs(DimVal(dims, DimNum(spacingLhs[0])) - want) < 0.02 &&
                      Math.Abs(DimVal(dims, DimNum(spacingLhs[1])) - want) < 0.02,
                    "spacing dims follow the PLATE dim (dimensionless drive)",
                    dims == null ? "no dims" :
                        $"{spacingLhs[0]}={DimVal(dims, DimNum(spacingLhs[0])):F4}mm " +
                        $"{spacingLhs[1]}={DimVal(dims, DimNum(spacingLhs[1])):F4}mm " +
                        $"want≈{want:F4}mm");
                var dots = RegionCircleCentres(sw, doc, qrName);
                var span = FieldSpan(dots);
                Check(dots.Count == plan.EmittedCount &&
                      Math.Abs(span.x - (plan.DataCells - 1) * want / 1000.0) < 1e-6,
                    "the dot field rescales off part geometry alone",
                    $"circles={dots.Count} span={span.x * 1000:F2}mm " +
                    $"(want {(plan.DataCells - 1) * want:F2}mm)");
                var cenEnd = ParseCensus(probe.PatternCensusProbe(qrName));
                Check(cenEnd != null && cenEnd.Regions == plan.EmittedCount,
                    "regions intact on the dimensionless sketch",
                    $"regions={cenEnd?.Regions}");
            }
            finally
            {
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
            }
        }

        /// <summary>"D5@Sketch2" → "D5".</summary>
        private static string DimNum(string eqName)
        {
            int at = eqName.IndexOf('@');
            return at > 0 ? eqName.Substring(0, at) : eqName;
        }

        /// <summary>Distance from (x,y) to the nearest sketch point — bind evidence.</summary>
        private static double NearestSketchPointDist(ISketch? sk, double x, double y)
        {
            if (sk?.GetSketchPoints() is not object[] pts) return -1;
            double best = double.MaxValue;
            foreach (var po in pts)
                if (po is ISketchPoint p)
                    best = Math.Min(best,
                        Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y)));
            return best == double.MaxValue ? -1 : best;
        }

        // ================= gate 8 — named undo =================

        private static void CircleGate8_Undo(
            ISldWorks sw, IAddInProbe probe, string template, QrCirclePlan plan)
        {
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            Check(doc != null, "part document D", doc == null ? "null" : "ok");
            if (doc == null) return;
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            try
            {
                // Prior user work the undo must not touch.
                ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                mgr.InsertSketch(true);
                string marker = (mgr.ActiveSketch as IFeature)?.Name ?? "SketchUser";
                mgr.CreateLine(-0.01, 0.0, 0, 0.01, 0.0, 0);
                mgr.InsertSketch(false);

                doc.ClearSelection2(true);
                ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                var created = probe.CreateQrFromWindowProbe();
                Check(created.StartsWith("OK"), "circle create for the undo gate", created);
                if (!created.StartsWith("OK")) return;
                string qr = created.Split(' ')[1];
                try { mgr.InsertSketch(false); } catch { }

                bool Present(string n) => FindSketchFeature(doc, n) != null;
                bool HasQr() => FindSketchFeatureNames(doc)
                    .Any(n => !string.Equals(n, marker, StringComparison.OrdinalIgnoreCase));

                // The committed sketch's exit can sit above the API step on
                // the undo list — press until gone (bounded), exiting any
                // edit session a press reopens, exactly like the add-in's
                // own UndoRedoQrProbe arrangement.
                int presses = 0;
                while (presses < 6 && HasQr())
                {
                    try { doc.EditUndo2(1); } catch { break; }
                    presses++;
                    if (mgr.ActiveSketch != null)
                        try { mgr.InsertSketch(false); } catch { }
                }
                Check(presses <= 3 && !HasQr(),
                    "one named 'Create QR Sketch' undo step removes the circle sketch",
                    $"presses={presses} qrGone={!HasQr()}");
                Check(Present(marker),
                    "the pre-existing user sketch survives the undo", $"marker='{marker}'");
                var eqsGone = ParseEquations(probe.PatternDimsProbe(marker));
                Check(eqsGone != null && eqsGone.Count == 0,
                    "the undo sweep leaves zero orphaned equations",
                    $"eqs={eqsGone?.Count}");

                // The re-registered handler toggles: the next press reinserts.
                int restores = 0;
                while (restores < 4 && !HasQr())
                {
                    try { doc.EditUndo2(1); } catch { break; }
                    restores++;
                    if (mgr.ActiveSketch != null)
                        try { mgr.InsertSketch(false); } catch { }
                }
                string restored = FindSketchFeatureNames(doc).FirstOrDefault(
                    n => !string.Equals(n, marker, StringComparison.OrdinalIgnoreCase)) ?? "";
                Check(HasQr() && restored.Length > 0,
                    "the undo toggle reinserts the circle sketch",
                    $"restored='{restored}' presses={restores}");
                if (restored.Length > 0)
                {
                    var cen = ParseCensus(probe.PatternCensusProbe(restored));
                    Check(cen != null && cen.Arcs == plan.EmittedCount &&
                          cen.Regions == plan.EmittedCount,
                        "the restored sketch carries the full dot census",
                        $"arcs={cen?.Arcs} regions={cen?.Regions}");
                    var eqsBack = ParseEquations(probe.PatternDimsProbe(restored));
                    Check(eqsBack != null && eqsBack.Count == 8 &&
                          eqsBack.All(t => t.Contains($"@{restored}\"") ||
                                           t.Contains($"@{restored}")),
                        "the restored sketch's equations point at it (no stale refs)",
                        $"eqs={eqsBack?.Count}");
                    Check(Present(marker), "marker still intact after restore");
                    try { mgr.InsertSketch(false); } catch { }
                }
            }
            finally
            {
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
            }
        }

        // ================= gate 9 — density at the bound =================

        private static void CircleGate9_Density(
            ISldWorks sw, IAddInProbe probe, string template,
            (string tag, string content, bool invert, int cells, int dots) under,
            (string tag, string content, bool invert, int cells, int dots) over,
            System.Globalization.CultureInfo inv)
        {
            if (under.content == null)
            {
                Limit("density", "no legal rung under the bound was found module-side");
                return;
            }
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            Check(doc != null, "part document E", doc == null ? "null" : "ok");
            if (doc == null) return;
            var ext = (IModelDocExtension)doc.Extension;
            var mgr = (ISketchManager)doc.SketchManager;
            try
            {
                Console.WriteLine($"#   near-bound rung {under.tag} dots={under.dots} " +
                    $"size={under.cells * 2}mm invert={under.invert}");
                double sizeMm = under.cells * 2.0;   // pitch pinned at 2 mm
                double setMs = TimeMs(() => _ = probe.SetQrSizeProbe(sizeMm.ToString(inv)));
                string resp = "";
                setMs += TimeMs(() => resp = probe.SetQrContentProbe(under.content));
                if (under.invert)
                    setMs += TimeMs(() => resp = probe.SetQrInvertProbe(true));
                Check(resp.StartsWith("VALID") && resp.Contains($"dots={under.dots}"),
                    "near-bound payload validates at the module's count", resp);

                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected (E)");
                string created = "";
                double createMs = TimeMs(() => created = probe.CreateQrFromWindowProbe());
                Check(created.StartsWith("OK"),
                    $"near-bound create succeeds ({under.tag})", created);
                if (created.StartsWith("OK"))
                {
                string sk = created.Split(' ')[1];
                string census = "";
                double censusMs = TimeMs(() => census = probe.PatternCensusProbe(sk));
                var cen = ParseCensus(census);
                Check(cen != null && cen.Arcs == under.dots && cen.Regions == under.dots,
                    "near-bound census: arcs==regions==emitted",
                    $"arcs={cen?.Arcs} regions={cen?.Regions} emitted={under.dots}");
                double exitMs = TimeMs(() => mgr.InsertSketch(false));

                var feat = FindSketchFeature(doc, sk);
                var committed = feat?.GetSpecificFeature2() as ISketch;
                int committedRegions = -1;
                double committedSolveMs = TimeMs(() =>
                {
                    try
                    {
                        var rr = committed?.GetSketchRegions() as object[];
                        if (rr != null) committedRegions = rr.Length;
                    }
                    catch { }
                });

                string dims = probe.PatternDimsProbe(sk);
                double boxM = ParseDimMm(dims, "D1");
                double rescaleMs = boxM > 0
                    ? TimeMs(() => _ = probe.SetPatternDimProbe($"D1@{sk}", boxM * 1.5))
                    : -1;

                doc.ClearSelection2(true);
                bool sel = ext.SelectByID2(sk, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                double enterMs = -1, editRegionsMs = -1, exit2Ms = -1;
                int editRegions = -1;
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
                        exit2Ms = TimeMs(() => mgr.InsertSketch(false));
                    }
                }
                Check(committedRegions == under.dots && editRegions == under.dots,
                    "region solve returns every dot, committed and in-edit",
                    $"committed={committedRegions} inEdit={editRegions}");
                Check(createMs < 90_000 && committedSolveMs < 120_000 && editRegionsMs < 120_000,
                    "near-bound rung stays inside the recorded responsiveness wall",
                    $"create={createMs / 1000:F1}s committed={committedSolveMs / 1000:F1}s " +
                    $"editSolve={editRegionsMs / 1000:F1}s (walls 90s/120s)");
                Console.WriteLine($"PERF rung={under.tag} dots={under.dots} " +
                    $"invert={under.invert} set_ms={setMs:F0} create_ms={createMs:F0} " +
                    $"census_ms={censusMs:F0} exit_ms={exitMs:F0} " +
                    $"committed_regions_ms={committedSolveMs:F0}(r={committedRegions}) " +
                    $"rescale_ms={rescaleMs:F0} enter_ms={enterMs:F0} " +
                    $"edit_regions_ms={editRegionsMs:F0}(r={editRegions}) exit2_ms={exit2Ms:F0}");
                }
                if (under.invert) _ = probe.SetQrInvertProbe(false);

                // ---- over-bound refusal — before ANY host mutation ----
                if (over.content != null)
                {
                    Console.WriteLine($"#   over-bound rung {over.tag} dots={over.dots}");
                    _ = probe.SetQrSizeProbe((over.cells * 2.0).ToString(inv));
                    resp = probe.SetQrContentProbe(over.content);
                    if (over.invert) resp = probe.SetQrInvertProbe(true);
                    int featsBefore = FindSketchFeatureNames(doc).Count;
                    Check(resp.StartsWith("INVALID") && resp.Contains("TooManyInstances"),
                        "over-bound payload answers INVALID TooManyInstances on the session",
                        resp);
                    string refused = probe.CreateQrFromWindowProbe();
                    Check(refused.StartsWith("ERROR") &&
                          refused.IndexOf("instance", StringComparison.OrdinalIgnoreCase) >= 0,
                        "the refused create surfaces the budget message",
                        refused);
                    Check(FindSketchFeatureNames(doc).Count == featsBefore &&
                          mgr.ActiveSketch == null,
                        "refusal left the feature tree untouched (no sketch created)",
                        $"features={FindSketchFeatureNames(doc).Count} before={featsBefore} " +
                        $"activeSketch={mgr.ActiveSketch != null}");
                    if (over.invert) _ = probe.SetQrInvertProbe(false);
                }
                else Limit("over-bound refusal", "no rung over the bound in the scanned range");

                // restore session defaults for the regression gate
                _ = probe.SetQrSizeProbe("25");
                _ = probe.SetQrContentProbe(PayloadA);
            }
            finally
            {
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
            }
        }

        // ================= gate 10 — square regression =================

        private static void CircleGate10_Square(
            ISldWorks sw, IAddInProbe probe, string template)
        {
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            Check(doc != null, "part document F", doc == null ? "null" : "ok");
            if (doc == null) return;
            try
            {
                Check(probe.SetQrCellShapeProbe("square").StartsWith("VALID"),
                    "shape switches back to square", probe.SetQrCellShapeProbe("square"));
                var verdict = QrSketchGenerator.Generate(
                    new QrSketchRequest(PayloadA, 25.0, 0.03));
                Check(verdict.Succeeded && verdict.Sketch!.CirclePlan == null,
                    "square request has no circle plan");
                string contentResp = probe.SetQrContentProbe(PayloadA);
                Check(contentResp.StartsWith("VALID") &&
                      contentResp.Contains($"segs={verdict.Sketch!.Info.SegmentCount}"),
                    "square payload validates with segment count", contentResp);

                var ext = (IModelDocExtension)doc.Extension;
                var mgr = (ISketchManager)doc.SketchManager;
                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected (F)");
                var created = probe.CreateQrFromWindowProbe();
                Check(created.StartsWith("OK"), "square-mode create succeeds", created);
                if (!created.StartsWith("OK")) return;
                var sk = mgr.ActiveSketch as ISketch;
                Check(sk != null, "square sketch left open");
                if (sk == null) return;
                int instCount = sk.GetSketchBlockInstanceCount();
                Check(instCount == 1, "exactly one block instance (regression)",
                    $"inst={instCount}");
                var inst = instCount > 0
                    ? sk.IGetSketchBlockInstances(0) as ISketchBlockInstance : null;
                Check(inst != null && Math.Abs(inst.Scale - 1.0) < 1e-6,
                    "block instance at Scale 1 (regression)",
                    inst == null ? "null" : $"Scale={inst.Scale:F4}");
                int hostSegs = (sk.GetSketchSegments() as object[])?.Length ?? -1;
                Check(hostSegs == 0,
                    "host sketch carries no enumerable segments (regression)",
                    $"hostSegs={hostSegs}");
                var defSegs = inst?.Definition is ISketchBlockDefinition def &&
                              def.GetSketch() is ISketch defSk &&
                              defSk.GetSketchSegments() is object[] ds
                    ? ds.Length : -1;
                int wantSegs = verdict.Sketch?.Info.SegmentCount ?? -1;
                Check(defSegs == wantSegs + 8,
                    "block definition folds contours + 8 construction edges (regression)",
                    $"defSegs={defSegs} want={wantSegs}+8");
                mgr.InsertSketch(false);
            }
            finally
            {
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
            }
        }
    }
}
