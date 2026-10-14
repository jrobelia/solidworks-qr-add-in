using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using QRCoder;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// `LiveProbe.exe pattern [progId] [only]` — the sketch-linear-pattern
    /// QR-dot spike (docs/PATTERN-SPIKE.md). Runs out-of-process against a
    /// session-free invisible instance; each experiment gets a fresh
    /// throwaway part via <see cref="Program.WithSpikeDoc"/> and prints one
    /// `PAT&lt;n&gt;` evidence line. Optional third arg runs only that
    /// experiment number.
    ///
    /// The question under test: can dark QR cells be emitted as REAL circle
    /// sketch entities produced by a 2-directional sketch linear pattern
    /// (`CreateLinearSketchStepAndRepeat`) with light cells as skipped
    /// instances — and can the whole grid scale off a driving dimension on
    /// a construction square (the "box is master geometry" vision)?
    /// </summary>
    public static partial class Program
    {
        private static int PatternSpikeMain(string[] args)
        {
            string progId = args.Length > 0 && !int.TryParse(args[0], out _)
                ? args[0] : "SldWorks.Application.31";
            int? only = null;
            foreach (var a in args)
                if (int.TryParse(a, out var n)) { only = n; break; }

            Console.WriteLine("# sketch-linear-pattern QR-dot spike — evidence for docs/PATTERN-SPIKE.md");
            if (Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: a SLDWORKS.exe process is already running — " +
                    "refusing to attach; the probe never mutates or kills an occupied seat.");
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

                var tests = new (int n, string tag, Func<ISldWorks, IModelDoc2, string> f)[]
                {
                    (0,  "diag",       PatExp0_Diag),
                    (1,  "create",     PatExp1_Create),
                    (2,  "skip",       PatExp2_Skip),
                    (3,  "perInstance",PatExp3_PerInstance),
                    (4,  "dimDriven",  PatExp4_DimDriven),
                    (5,  "seedDia",    PatExp5_SeedDia),
                    (6,  "dimLess",    PatExp6_Dimensionless),
                    (7,  "quietBox",   PatExp7_QuietBox),
                    (8,  "timing",     PatExp8_Timing),
                    (9,  "undo",       PatExp9_Undo),
                    (10, "invert",     PatExp10_Invert),
                    (11, "fallback",   PatExp11_Fallback),
                    (12, "prodOrder",  PatExp12_ProductionOrder),
                };
                foreach (var t in tests)
                {
                    if (only.HasValue && t.n != only.Value) continue;
                    Console.WriteLine($"PAT{t.n}({t.tag}) " + WithSpikeDoc(sw, t.f));
                }
                Console.WriteLine("# PAT DONE");
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

        // ================= shared spike context / helpers =================

        private sealed class Pat
        {
            public ISldWorks Sw = null!;
            public IModelDoc2 Doc = null!;
            public IModelDocExtension Ext = null!;
            public ISketchManager Mgr = null!;
            public ISelectionMgr Sel = null!;
            public MathUtility Mu = null!;
            public IFeatureManager Fm = null!;

            public static Pat Of(ISldWorks sw, IModelDoc2 doc) => new Pat
            {
                Sw = sw,
                Doc = doc,
                Ext = (IModelDocExtension)doc.Extension,
                Mgr = (ISketchManager)doc.SketchManager,
                Sel = (ISelectionMgr)doc.ISelectionManager,
                Mu = (MathUtility)sw.IGetMathUtility(),
                Fm = (IFeatureManager)doc.FeatureManager,
            };

            public bool OpenSketch()
            {
                bool ok = OpenFrontSketch(Doc, Ext, Mgr);
                try { Mgr.AutoInference = false; } catch { }
                return ok;
            }

            public bool SelSegAt(double x, double y, bool append = false)
            {
                try
                {
                    return Ext.SelectByID2("", "SKETCHSEGMENT", x, y, 0, append, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                }
                catch { return false; }
            }

            public bool SelPtAt(double x, double y, bool append = false)
            {
                try
                {
                    return Ext.SelectByID2("", "SKETCHPOINT", x, y, 0, append, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                }
                catch { return false; }
            }

            public bool SelVertex(double x, double y, double z, bool append)
            {
                try
                {
                    return Ext.SelectByID2("", "VERTEX", x, y, z, append, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault);
                }
                catch { return false; }
            }
        }

        /// <summary>Select the sketch POINT nearest (x,y) by object — avoids
        /// SelectByID2 ambiguity between centre points, endpoints and arcs.</summary>
        private static bool SelSketchPointAt(Pat c, double x, double y, bool append) =>
            SelSketchPointAt(c.Mgr.ActiveSketch as ISketch, x, y, append);

        /// <summary>
        /// The same nearest-point object select on an explicitly supplied
        /// sketch — used where the target sketch does not resolve through
        /// <c>ISketchManager.ActiveSketch</c> (the add-in's open circle
        /// create, circlediag D2: the named feature's GetSpecificFeature2
        /// exposes it instead).
        /// </summary>
        private static bool SelSketchPointAt(ISketch? sk, double x, double y, bool append)
        {
            try
            {
                if (sk?.GetSketchPoints() is not object[] pts) return false;
                ISketchPoint? best = null; double bd = double.MaxValue;
                foreach (var po in pts)
                {
                    if (po is not ISketchPoint p) continue;
                    double dx = p.X - x, dy = p.Y - y, d = dx * dx + dy * dy;
                    if (d < bd) { bd = d; best = p; }
                }
                if (best == null || bd > 1e-12) return false;   // >~1 µm² = miss
                return best.Select4(append, null);
            }
            catch { return false; }
        }

        /// <summary>Entity census of a sketch (edit-mode or committed).
        /// NOTE: ISketch.GetArcs() does NOT enumerate sketch-pattern instance
        /// arcs (live-verified, PAT0) — census via GetSketchSegments.</summary>
        private static string Census(ISketch? sk)
        {
            if (sk == null) return "noSketch";
            int segs = -1, arcs = -1, lines = -1, regions = -1;
            try
            {
                var so = sk.GetSketchSegments() as object[];
                segs = so?.Length ?? 0;
                if (so != null)
                    foreach (var s in so)
                    {
                        if (s is ISketchArc) arcs++;
                        else if (s is ISketchLine) lines++;
                    }
                if (arcs < 0) arcs = 0;
                if (lines < 0) lines = 0;
            }
            catch { }
            try { regions = sk.GetSketchRegionCount(); } catch { }
            return $"segs={segs} arcs={arcs} lines={lines} regions={regions}";
        }

        /// <summary>All circle/arc entities as (cx, cy, r) in sketch space.</summary>
        private static List<(double x, double y, double r)> ArcCenters(ISketch? sk)
        {
            var list = new List<(double, double, double)>();
            if (sk?.GetSketchSegments() is not object[] segs) return list;
            foreach (var so in segs)
            {
                if (so is not ISketchArc a) continue;
                var c = a.GetCenterPoint2() as ISketchPoint;
                double radius = -1;
                try { radius = a.GetRadius(); } catch { }
                list.Add((c?.X ?? double.NaN, c?.Y ?? double.NaN, radius));
            }
            return list;
        }

        /// <summary>All sketch segments as (name, kind, construction) for dumps.</summary>
        private static List<(string name, string kind, bool construction)> SegDump(ISketch? sk)
        {
            var list = new List<(string, string, bool)>();
            if (sk?.GetSketchSegments() is not object[] segs) return list;
            foreach (var so in segs)
            {
                if (so is not ISketchSegment s) continue;
                string nm = "";
                try { nm = s.GetName(); } catch { }
                bool cons = false;
                try { cons = s.ConstructionGeometry; } catch { }
                string kind = s is ISketchArc ? "arc" : s is ISketchLine ? "line" : "other";
                list.Add((nm, kind, cons));
            }
            return list;
        }

        /// <summary>Grid centers whose cell has no arc within tolerance, as "c,r".</summary>
        private static string MissingCells(List<(double x, double y, double r)> arcs,
            int n, double pitch, double tol = 2e-4)
        {
            var missing = new List<string>();
            for (int row = 0; row < n; row++)
                for (int col = 0; col < n; col++)
                {
                    double cx = pitch / 2 + col * pitch, cy = pitch / 2 + row * pitch;
                    if (!arcs.Any(a => Math.Abs(a.x - cx) < tol && Math.Abs(a.y - cy) < tol))
                        missing.Add($"{col},{row}");
                }
            return missing.Count == 0 ? "none" : string.Join(";", missing);
        }

        private static IFeature? FindPatternFeature(IModelDoc2 doc)
        {
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                if (f.GetTypeName2().IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0)
                    return f;
                for (var s = f.IGetFirstSubFeature(); s != null; s = s.IGetNextSubFeature())
                {
                    if (s.GetTypeName2().IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0)
                        return s;
                    for (var s2 = s.IGetFirstSubFeature(); s2 != null; s2 = s2.IGetNextSubFeature())
                        if (s2.GetTypeName2().IndexOf("pattern", StringComparison.OrdinalIgnoreCase) >= 0)
                            return s2;
                }
            }
            return null;
        }

        /// <summary>Feature-tree dump: "name|type2 [sub1|type2 [subsub|type2]]".</summary>
        private static string FeatureTree(IModelDoc2 doc)
        {
            var sb = new StringBuilder();
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
            {
                sb.Append($"{f.Name}|{f.GetTypeName2()} ");
                for (var s = f.IGetFirstSubFeature(); s != null; s = s.IGetNextSubFeature())
                {
                    sb.Append($"[{s.Name}|{s.GetTypeName2()}");
                    for (var s2 = s.IGetFirstSubFeature(); s2 != null; s2 = s2.IGetNextSubFeature())
                        sb.Append($" <{s2.Name}|{s2.GetTypeName2()}>");
                    sb.Append("] ");
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Dimension probing by name — sketch dims are NOT sub-features
        /// (live-verified, PAT0: IGetFirstSubFeature finds none), so probe
        /// D1..D{max} on every sketch feature plus pattern-style names.
        /// </summary>
        private static List<(string name, double val)> FindDims(IModelDoc2 doc, int max = 14)
        {
            var list = new List<(string, double)>();
            var seen = new HashSet<string>();
            // probe D1..Dmax on each sketch name and common pattern spellings
            var candidates = new List<string>();
            foreach (var sk in FindSketchFeatureNames(doc))
                for (int i = 1; i <= max; i++)
                {
                    candidates.Add($"D{i}@{sk}");
                    candidates.Add($"D{i}@LPattern1.{sk}");
                    candidates.Add($"D{i}@LocalLPattern1.{sk}");
                }
            foreach (var cand in candidates)
            {
                if (seen.Contains(cand)) continue;
                IDimension? d = null;
                try { d = doc.IParameter(cand) as IDimension; } catch { }
                if (d == null) continue;
                seen.Add(cand);
                double v = double.NaN;
                try { v = d.SystemValue; } catch { }
                list.Add((cand, v));
            }
            return list;
        }

        /// <summary>"D1@Sketch1@Part1" → "D1@Sketch1" (equation reference form).</summary>
        private static string EqName(string fullName)
        {
            int at = fullName.LastIndexOf('@');
            return at > 0 ? fullName.Substring(0, at) : fullName;
        }

        private static string AddEquation(IModelDoc2 doc, string lhsEqName, string rhs)
        {
            var eqm = doc.GetEquationMgr() as IEquationMgr;
            if (eqm == null) return "noEqMgr";
            try
            {
                int rc = eqm.Add(-1, $"\"{lhsEqName}\" = {rhs}");
                return rc >= 0 ? $"ok(idx={rc})" : $"rc={rc}";
            }
            catch (Exception ex) { return "EX:" + ex.GetType().Name; }
        }

        /// <summary>
        /// The production skip recipe: enumerate the pattern's instance arcs
        /// by OBJECT, map each to its grid cell by centre position,
        /// Select4(append) every light cell's arc, one EditDelete. No
        /// coordinate picks (ambiguous near direction lines/points — PAT2).
        /// </summary>
        private static (int picked, int deleted) SkipLightCells(
            Pat c, int n, double pitch, Func<int, int, bool> isDark)
        {
            int picked = 0, deleted = 0;
            var sk = c.Mgr.ActiveSketch as ISketch;
            if (sk?.GetSketchSegments() is not object[] segs) return (0, 0);
            var targets = new List<ISketchSegment>();
            foreach (var so in segs)
            {
                if (so is not ISketchArc a || so is not ISketchSegment s) continue;
                var cp = a.GetCenterPoint2() as ISketchPoint;
                if (cp == null) continue;
                int col = (int)Math.Round((cp.X - pitch / 2) / pitch);
                int row = (int)Math.Round((cp.Y - pitch / 2) / pitch);
                if (col < 0 || col >= n || row < 0 || row >= n) continue;
                if (!isDark(col, row)) targets.Add(s);
            }
            c.Doc.ClearSelection2(true);
            foreach (var t in targets)
            {
                try { if (t.Select4(true, null)) picked++; } catch { }
            }
            int before = ArcCenters(sk).Count;
            if (picked > 0)
                try { c.Doc.EditDelete(); } catch { }
            deleted = before - ArcCenters(c.Mgr.ActiveSketch as ISketch).Count;
            return (picked, deleted);
        }

        /// <summary>Seed circle + 2-dir linear sketch pattern; returns the seed name.</summary>
        private static (ISketchSegment? seed, string seedName, bool ok) CreatePattern(
            Pat c, int nx, int ny, double px, double py,
            double cx, double cy, double r, string delStr, bool spacingDims)
        {
            string name = "";
            var seed = c.Mgr.CreateCircleByRadius(cx, cy, 0, r) as ISketchSegment;
            if (seed != null) { try { name = seed.GetName(); } catch { } }
            c.Doc.ClearSelection2(true);
            bool sel = false;
            try { sel = seed != null && seed.Select4(false, null); } catch { }
            if (!sel && seed != null)
            {
                c.SelSegAt(cx + r, cy);
            }
            bool ok = false;
            try
            {
                ok = c.Mgr.CreateLinearSketchStepAndRepeat(nx, ny, px, py, 0, Math.PI / 2,
                    delStr, spacingDims, spacingDims, false, false, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"#   pattern create threw {ex.GetType().Name}: {ex.Message}");
            }
            return (seed, name, ok);
        }

        /// <summary>Deletes a feature by name — rollback after feature-use checks.</summary>
        private static bool DeleteFeatureByName(Pat c, IFeature? feat)
        {
            if (feat == null) return false;
            c.Doc.ClearSelection2(true);
            bool sel = false;
            try { sel = feat.Select2(false, 0); } catch { }
            if (!sel) return false;
            try { c.Doc.EditDelete(); return true; } catch { return false; }
        }

        // ================= experiments =================

        /// <summary>PAT0 — diagnostics: what did CreateLinearSketchStepAndRepeat
        /// actually make? segment types/names, sub-features, point census,
        /// dim probes, and pattern-feature discovery via selection.</summary>
        private static string PatExp0_Diag(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            if (!c.OpenSketch()) return "openSketch=fail";
            var (seed, seedName, ok) = CreatePattern(c, 3, 3, 0.002, 0.002,
                0.001, 0.001, 0.0005, "", spacingDims: true);
            sb.Append($"create={ok} ");
            var sk = c.Mgr.ActiveSketch as ISketch;
            if (sk?.GetSketchSegments() is object[] segs)
            {
                var typeCounts = new Dictionary<int, int>();
                var names = new List<string>();
                foreach (var so in segs)
                {
                    if (so is not ISketchSegment s) continue;
                    int t = -1;
                    try { t = s.GetType(); } catch { }
                    typeCounts[t] = (typeCounts.TryGetValue(t, out var tc) ? tc : 0) + 1;
                    string nm = "";
                    try { nm = s.GetName(); } catch { }
                    if (names.Count < 12) names.Add($"{nm}:t{t}" + (s is ISketchArc ? "ARC" : s is ISketchLine ? "LINE" : "?"));
                }
                sb.Append($"segTypes[{string.Join(" ", typeCounts.Select(k => $"t{k.Key}x{k.Value}"))}] ");
                sb.Append($"names[{string.Join(" ", names)}] ");
            }
            int pts = -1, pts2 = -1;
            try { pts = sk?.GetSketchPointsCount() ?? -1; } catch { }
            try { pts2 = sk?.GetSketchPointsCount2() ?? -1; } catch { }
            sb.Append($"pts={pts} pts2={pts2} ");
            // the 2 direction lines: endpoints + construction flags; try
            // flagging construction and deleting one — does the pattern care?
            var dump = SegDump(sk);
            sb.Append($"segs[{string.Join(" ", dump.Select(s => $"{s.name}:{s.kind}:{(s.construction ? "cons" : "solid")}"))}] ");
            foreach (var so in (sk?.GetSketchSegments() as object[]) ?? Array.Empty<object>())
            {
                if (so is not ISketchLine l) continue;
                var a = l.GetStartPoint2() as ISketchPoint;
                var b = l.GetEndPoint2() as ISketchPoint;
                sb.Append($"line({a?.X * 1000:F1},{a?.Y * 1000:F1})->({b?.X * 1000:F1},{b?.Y * 1000:F1}) ");
                bool consSet = false;
                if (l is ISketchSegment ls)
                    try { ls.ConstructionGeometry = true; consSet = ls.ConstructionGeometry; } catch { }
                sb.Append($"consSet={consSet} ");
            }
            c.Mgr.InsertSketch(false);
            // sub-features via the OBJECT variant too
            var sf = FindSketchFeature(doc, FindSketchFeatureNames(doc).Last());
            var sub = new List<string>();
            for (var s = sf?.GetFirstSubFeature() as IFeature; s != null; s = s.GetNextSubFeature() as IFeature)
                sub.Add($"{s.Name}|{s.GetTypeName2()}");
            sb.Append($"objSubs[{string.Join(" ", sub)}] ");
            // dimension probes by name
            foreach (var cand in new[]
                     { "D1@Sketch1", "D2@Sketch1", "D3@Sketch1", "D4@Sketch1",
                       "D1@LPattern1", "D1@LPattern1.Sketch1", "D2@LPattern1.Sketch1",
                       "D1@LocalLPattern1.Sketch1" })
            {
                var d = doc.IParameter(cand) as IDimension;
                if (d != null) sb.Append($"param[{cand}={d.SystemValue * 1000:F3}] ");
            }
            // pattern feature via name selection
            foreach (var cand in new[] { ("LPattern1", "SKETCHPATTERN"), ("LPattern1", "SETPATTERN"),
                        ("LPattern1", "LOCALPATTERN") })
            {
                c.Doc.ClearSelection2(true);
                bool s2 = false;
                try { s2 = c.Ext.SelectByID2(cand.Item1, cand.Item2, 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault); } catch { }
                if (s2)
                {
                    int t = -1;
                    try { t = c.Sel.GetSelectedObjectType3(1, -1); } catch { }
                    var obj = c.Sel.GetSelectedObject6(1, -1);
                    sb.Append($"sel[{cand.Item2}]={s2} t{t} obj={(obj?.GetType().Name ?? "null")} ");
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>PAT1 — can a 2-dir sketch linear pattern be created at all,
        /// and what feature does it make? 7×7 circles at 2 mm pitch.</summary>
        private static string PatExp1_Create(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            if (!c.OpenSketch()) return "openSketch=fail";
            var (seed, seedName, ok) = CreatePattern(c, 7, 7, 0.002, 0.002,
                0.001, 0.001, 0.0008, "", spacingDims: true);
            sb.Append($"seedName='{seedName}' create={ok} ");
            sb.Append(Census(c.Mgr.ActiveSketch as ISketch) + " ");
            c.Mgr.InsertSketch(false);
            sb.Append("tree[" + FeatureTree(doc) + "] ");
            var pf = FindPatternFeature(doc);
            if (pf != null)
            {
                sb.Append($"patFeat='{pf.Name}|{pf.GetTypeName2()}' ");
                try
                {
                    var d = pf.GetDefinition();
                    sb.Append($"defType={(d == null ? "null" : d.GetType().Name)} ");
                }
                catch (Exception ex) { sb.Append($"defEX:{ex.GetType().Name} "); }
            }
            var sk = doc.FirstFeature() as IFeature;
            var last = FindSketchFeature(doc, FindSketchFeatureNames(doc).Last());
            sb.Append("committed[" + Census(last?.GetSpecificFeature2() as ISketch) + "] ");
            sb.Append("dims[" + string.Join(" ", FindDims(doc).Select(d => $"{d.name}={d.val * 1000:F3}")) + "]");
            return sb.ToString().Trim();
        }

        /// <summary>PAT2 — the gating question: instance skipping. Phase A:
        /// select an instance arc + EditDelete (does the pattern stay
        /// parametric?); Phase B: DeleteInstances string-format matrix at
        /// create time; Phase C: edit-time delete with the pattern selected.</summary>
        private static string PatExp2_Skip(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            const int n = 3; const double pitch = 0.002, r = 0.0005;

            // ================= Phase A: object-select instance arcs + EditDelete =================
            if (!c.OpenSketch()) return "openSketch=fail";
            var (seed, seedName, ok) = CreatePattern(c, n, n, pitch, pitch,
                pitch / 2, pitch / 2, r, "", spacingDims: true);
            sb.Append($"A[create={ok} seed='{seedName}' " + Census(c.Mgr.ActiveSketch as ISketch) + " ");
            // object-select the arcs whose centres are cells (1,0) and (0,1), one EditDelete
            var res = SkipLightCells(c, n, pitch, (col, row) => !(col == 1 && row == 0) && !(col == 0 && row == 1));
            var arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
            sb.Append($"objDel picked={res.picked} arcs={arcs.Count} missing[{MissingCells(arcs, n, pitch)}] " +
                Census(c.Mgr.ActiveSketch as ISketch) + " ");
            // THE GATE: set the DRIVING spacing dim D1 with skips in place —
            // survivors must move AND holes stay missing (no EditLinear yet).
            var d1 = doc.IParameter("D1@Sketch1") as IDimension;
            int setRc = -9;
            try { setRc = d1?.SetSystemValue3(pitch * 2, 2 /* swSetValue_InAllConfigurations */, null) ?? -8; } catch { }
            arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
            sb.Append($"setD1(2x)={setRc} missing2x[{MissingCells(arcs, n, pitch * 2)}] " +
                $"centres[{string.Join(";", arcs.OrderBy(a => a.y).ThenBy(a => a.x)
                    .Select(a => $"({a.x * 1000:F1},{a.y * 1000:F1})"))}] ");
            // THEN probe EditLinear — does it resurrect the deleted instances?
            bool editAlive = false; string eex = "";
            try
            {
                editAlive = c.Mgr.EditLinearSketchStepAndRepeat(n, n, pitch * 2, pitch * 2,
                    0, Math.PI / 2, "", false, false, false, false, false, seedName + "_");
            }
            catch (Exception ex) { eex = ex.GetType().Name; }
            arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
            sb.Append($"editAlive={(eex.Length > 0 ? eex : editAlive.ToString())} " +
                $"arcs={arcs.Count} missingE[{MissingCells(arcs, n, pitch * 2)}]] ");
            c.Mgr.InsertSketch(false);
            doc.EditRebuild3();

            // ================= Phase A2: deleting the SEED instance (0,0) =================
            if (!c.OpenSketch()) { sb.Append("A2=openFail "); }
            else
            {
                var (sA2, nA2, okA2) = CreatePattern(c, n, n, pitch, pitch,
                    pitch / 2, pitch / 2, r, "", spacingDims: false);
                var resA2 = SkipLightCells(c, n, pitch, (col, row) => !(col == 0 && row == 0));
                var arcsA2 = ArcCenters(c.Mgr.ActiveSketch as ISketch);
                // pattern survives the seed's removal? probe: can we still edit-skip, and are arcs still patterned
                bool probe = false;
                try { probe = c.Mgr.EditLinearSketchStepAndRepeat(n, n, pitch, pitch, 0, Math.PI / 2,
                        "", false, false, false, false, false, nA2 + "_"); }
                catch { }
                sb.Append($"A2[delSeed picked={resA2.picked} arcs={arcsA2.Count} " +
                    $"missing[{MissingCells(arcsA2, n, pitch)}] editStill={probe}] ");
                c.Mgr.InsertSketch(false);
            }

            // ================= Phase B: create-time DeleteInstances formats =================
            // API doc says "(a) (b) (c)"; UI docs say the box shows "(col,row)"
            // coordinates — try both families plus 1-based variants.
            var formats = new[]
            {
                "(2)", "(2) ", "2", "(2);(5)", "(2)(5)", "(02)", "(2) (5) ",
                "(1,0) (0,1)", "(1,0);(0,1)", "(2,1) (1,2)", "(3) (4)",
                "(1) (3)", "(4) (5) ", "(0,1)"
            };
            string lastSketchName = "";
            foreach (var fmt in formats)
            {
                if (!c.OpenSketch()) { sb.Append($"B[{fmt}]=openFail "); continue; }
                var (sB, nB, okB) = CreatePattern(c, n, n, pitch, pitch,
                    pitch / 2, pitch / 2, r, fmt, spacingDims: true);
                var arcsB = ArcCenters(c.Mgr.ActiveSketch as ISketch);
                lastSketchName = c.Mgr.ActiveSketch is ISketch skB
                    ? ((IFeature)skB).Name ?? "" : "";
                sb.Append($"B[{fmt}]={okB} arcs={arcsB.Count} missing[{MissingCells(arcsB, n, pitch)}] ");
                c.Mgr.InsertSketch(false);
            }
            // ---- B2: THE GATE — create-time skips + dim-driven spacing -------
            if (!c.OpenSketch()) { sb.Append("B2=openFail "); }
            else
            {
                var (sB2, nB2, okB2) = CreatePattern(c, n, n, pitch, pitch,
                    pitch / 2, pitch / 2, r, "(2,1) (1,2)", spacingDims: true);
                var skName = c.Mgr.ActiveSketch is ISketch skN
                    ? ((IFeature)skN).Name ?? "Sketch?" : "Sketch?";
                var d1B = doc.IParameter($"D1@{skName}") as IDimension;
                int rcB = -9;
                try { rcB = d1B?.SetSystemValue3(pitch * 2, 2 /* swSetValue_InAllConfigurations */, null) ?? -8; } catch { }
                var arcsB2 = ArcCenters(c.Mgr.ActiveSketch as ISketch);
                sb.Append($"B2[skips+setD1(2x)={rcB} arcs={arcsB2.Count} " +
                    $"missing2x[{MissingCells(arcsB2, n, pitch * 2)}] " +
                    $"centres[{string.Join(";", arcsB2.OrderBy(a => a.y).ThenBy(a => a.x)
                        .Select(a => $"({a.x * 1000:F1},{a.y * 1000:F1})"))}]] ");
                c.Mgr.InsertSketch(false);
            }

            // ================= Phase C: edit-time delete, pattern selected =================
            if (!c.OpenSketch()) { sb.Append("C=openFail"); }
            else
            {
                var (sC, nC, okC) = CreatePattern(c, n, n, pitch, pitch,
                    pitch / 2, pitch / 2, r, "", spacingDims: false);
                c.Doc.ClearSelection2(true);
                bool selP = c.SelSegAt(pitch / 2 + pitch + r, pitch / 2);   // an instance arc
                bool eok = false; string eexC = "";
                try
                {
                    eok = c.Mgr.EditLinearSketchStepAndRepeat(n, n, pitch, pitch, 0, Math.PI / 2,
                        "(2)", false, false, false, false, false, nC + "_");
                }
                catch (Exception ex) { eexC = ex.GetType().Name; }
                var arcsC = ArcCenters(c.Mgr.ActiveSketch as ISketch);
                sb.Append($"C[selPat={selP} edit={(eexC.Length > 0 ? eexC : eok.ToString())} " +
                    $"arcs={arcsC.Count} missing[{MissingCells(arcsC, n, pitch)}]] ");
                c.Mgr.InsertSketch(false);
            }
            return sb.ToString().Trim();
        }

        /// <summary>PAT3 — per-instance reality: a plus-mask on 7×7; each kept
        /// dot is a real circle; a dot's closed region is selectable and
        /// boss-extrudable.</summary>
        private static string PatExp3_PerInstance(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            if (!c.OpenSketch()) return "openSketch=fail";
            const int n = 7; const double pitch = 0.002, r = 0.0008;
            // plus-mask via the create-time (col,row) skip string
            var skipStr = new StringBuilder();
            for (int row = 0; row < n; row++)
                for (int col = 0; col < n; col++)
                    if (!(col == 3 || row == 3)) skipStr.Append($"({col + 1},{row + 1}) ");
            var (seed, seedName, ok) = CreatePattern(c, n, n, pitch, pitch,
                pitch / 2, pitch / 2, r, skipStr.ToString().TrimEnd(), spacingDims: false);
            sb.Append($"create={ok} ");
            var arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
            sb.Append($"arcs={arcs.Count} ");
            var kept = new List<string>();
            var extra = new List<string>();
            for (int row = 0; row < n; row++)
                for (int col = 0; col < n; col++)
                {
                    double cx = pitch / 2 + col * pitch, cy = pitch / 2 + row * pitch;
                    bool present = arcs.Any(a => Math.Abs(a.x - cx) < 2e-4 && Math.Abs(a.y - cy) < 2e-4);
                    if (col == 3 || row == 3) { if (present) kept.Add($"{col},{row}"); }
                    else if (present) extra.Add($"{col},{row}");
                }
            sb.Append($"kept={kept.Count} extra[{string.Join(";", extra)}] ");
            c.Mgr.InsertSketch(false);

            // region-select three kept dots and extrude them
            c.Doc.ClearSelection2(true);
            try { c.Sel.EnableContourSelection = true; } catch { }
            int picks = 0;
            foreach (var (px, py) in new[] { (pitch / 2, pitch / 2), (pitch / 2 + 3 * pitch, pitch / 2), (pitch / 2, pitch / 2 + 3 * pitch) })
                try
                {
                    if (c.Ext.SelectByID2("", "SKETCHREGION", px, py, 0, true, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault))
                        picks++;
                }
                catch { }
            try { c.Sel.EnableContourSelection = false; } catch { }
            sb.Append($"regionPicks={picks} ");
            IFeature? boss = null;
            try
            {
                boss = c.Fm.FeatureExtrusion3(
                    true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                    0.002, 0.0, false, false, false, false, 0.0, 0.0,
                    false, false, false, false,
                    true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false)
                    as IFeature;
            }
            catch (Exception ex) { sb.Append($"extEX:{ex.GetType().Name} "); }
            sb.Append($"boss={(boss == null ? "null" : "'" + boss.Name + "'")} ");
            if (boss != null) sb.Append($"delBoss={DeleteFeatureByName(c, boss)}");
            return sb.ToString().Trim();
        }

        /// <summary>PAT4 — the user's vision, end to end. One sketch: master
        /// construction square (driving dim Dbox) + circle pattern created
        /// WITH a (col,row) skip list + spacing dims. Equations tie spacing to
        /// Dbox/N. Rescale Dbox and check pitch, positions, and that create-
        /// time skips persist. Also: seed diameter + position equations.</summary>
        private static string PatExp4_DimDriven(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            const int n = 3; const double pitch = 0.002, r = 0.0005;
            double size = n * pitch;   // 6 mm box

            if (!c.OpenSketch()) return "openSketch=fail";
            // ---- master construction square, corner at origin ---------------
            foreach (var (x1, y1, x2, y2) in new[]
                     { (0.0, 0.0, size, 0.0), (size, 0.0, size, size),
                       (size, size, 0.0, size), (0.0, size, 0.0, 0.0) })
            {
                var s = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                if (s != null) s.ConstructionGeometry = true;
            }
            c.Doc.ClearSelection2(true);
            c.SelSegAt(size / 2, 0);
            IDimension? boxDim = null;
            try
            {
                var dd = doc.IAddHorizontalDimension2(size / 2, -0.004, 0) as IDisplayDimension;
                boxDim = dd?.GetDimension() as IDimension;
            }
            catch (Exception ex) { sb.Append($"boxEX:{ex.GetType().Name} "); }
            sb.Append($"box='{boxDim?.FullName}' ");

            // ---- pattern with create-time skips (light cells 1,0 and 0,1) ----
            var (seed, seedName, ok) = CreatePattern(c, n, n, pitch, pitch,
                pitch / 2, pitch / 2, r, "(2,1) (1,2)", spacingDims: true);
            var skName = c.Mgr.ActiveSketch is ISketch sk0
                ? ((IFeature)sk0).Name ?? "Sketch?" : "Sketch?";
            sb.Append($"create={ok} sk='{skName}' ");
            var arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
            sb.Append($"arcs={arcs.Count} missing[{MissingCells(arcs, n, pitch)}] ");

            // ---- seed diameter dim + seed position dims ---------------------
            c.Doc.ClearSelection2(true);
            c.SelSegAt(pitch / 2 + r, pitch / 2);
            IDimension? diaDim = null;
            try
            {
                var dd = doc.IAddDiameterDimension2(pitch / 2 + r + 0.003, pitch / 2 + 0.003, 0) as IDisplayDimension;
                diaDim = dd?.GetDimension() as IDimension;
            }
            catch (Exception ex) { sb.Append($"diaEX:{ex.GetType().Name} "); }
            // seed centre point → horizontal+vertical dims to box origin
            c.Doc.ClearSelection2(true);
            bool sA = SelSketchPointAt(c, 0, 0, append: false);
            bool sB = SelSketchPointAt(c, pitch / 2, pitch / 2, append: true);
            int selCnt = -1;
            try { selCnt = c.Sel.GetSelectedObjectCount(); } catch { }
            IDimension? posHx = null, posVy = null;
            if (selCnt >= 2)
            {
                try
                {
                    var ddH = doc.IAddHorizontalDimension2(-0.002, 0.001, 0) as IDisplayDimension;
                    posHx = ddH?.GetDimension() as IDimension;
                }
                catch (Exception ex) { sb.Append($"hxEX:{ex.GetType().Name} "); }
                // the dim call consumes the selection — re-pick both points
                c.Doc.ClearSelection2(true);
                SelSketchPointAt(c, 0, 0, append: false);
                SelSketchPointAt(c, pitch / 2, pitch / 2, append: true);
                try
                {
                    var ddV = doc.IAddVerticalDimension2(0.001, -0.002, 0) as IDisplayDimension;
                    posVy = ddV?.GetDimension() as IDimension;
                }
                catch (Exception ex) { sb.Append($"vyEX:{ex.GetType().Name} "); }
            }
            sb.Append($"dia='{diaDim?.FullName}' selPts={sA}&{sB} " +
                $"hx='{posHx?.FullName}' vy='{posVy?.FullName}' ");
            sb.Append("dims[" + string.Join(" ", FindDims(doc)
                .Select(d => $"{d.name}={d.val * 1000:F2}")) + "] ");
            c.Mgr.InsertSketch(false);
            doc.EditRebuild3();

            // ---- equations: spacing = box/N, dia = spacing·0.5,
            //      posHx = spacing/2, posVy = spacing/2 ------------------------
            string D1 = $"D1@{skName}", D2 = $"D2@{skName}", D3 = $"D3@{skName}";
            var eqm = doc.GetEquationMgr() as IEquationMgr;
            var eqIdx = new List<int>();
            foreach (var (lhs, rhs) in new[]
            {
                (D2, $"\"{D1}\"/{n}"),                    // X spacing
                (D3, $"\"{D1}\"/{n}"),                    // Y spacing
                (diaDim != null ? EqName(diaDim.FullName!) : "", $"\"{D2}\"*0.5"),
                (posHx != null ? EqName(posHx.FullName!) : "", $"\"{D2}\"/2"),
                (posVy != null ? EqName(posVy.FullName!) : "", $"\"{D3}\"/2"),
            })
            {
                if (lhs.Length == 0 || eqm == null) continue;
                try
                {
                    int idx = eqm.Add(-1, $"\"{lhs}\" = {rhs}");
                    eqIdx.Add(idx);
                    sb.Append($"eq({lhs}={rhs})={idx} ");
                }
                catch (Exception ex) { sb.Append($"eqEX:{ex.GetType().Name} "); }
            }
            try { eqm?.EvaluateAll(); } catch { }
            if (eqm != null)
            {
                int ovSt = -9; try { ovSt = eqm.Status; } catch { }
                sb.Append($"eqStatus={ovSt} ");
                for (int i = 0; i < eqm.GetCount(); i++)
                {
                    string t = "?"; double v = -9;
                    try { t = eqm.Equation[i]; v = eqm.Value[i]; } catch { }
                    sb.Append($"[{i}]{t}={v * 1000:F2} ");
                }
            }
            doc.EditRebuild3();
            sb.Append("dims1[" + string.Join(" ", FindDims(doc)
                .Select(d => $"{d.name}={d.val * 1000:F2}")) + "] ");

            // ---- rescale the box: 6 → 12 mm — everything must double ---------
            int setRc = -9;
            try { setRc = boxDim?.SetSystemValue3(size * 2, 2 /* swSetValue_InAllConfigurations */, null) ?? -8; } catch { }
            doc.EditRebuild3();
            sb.Append($"setBox={setRc} ");
            sb.Append("dims2[" + string.Join(" ", FindDims(doc)
                .Select(d =>
                {
                    int st = -9;
                    try { st = (doc.IParameter(d.name) as IDimension)?.DrivenState ?? -8; }
                    catch { }
                    return $"{d.name}={d.val * 1000:F2}st{st}";
                })) + "] ");
            var sk = FindSketchFeature(doc, skName)?.GetSpecificFeature2() as ISketch;
            arcs = ArcCenters(sk);
            sb.Append($"arcs={arcs.Count} " +
                $"centres[{string.Join(";", arcs.OrderBy(a => a.y).ThenBy(a => a.x)
                    .Select(a => $"({a.x * 1000:F1},{a.y * 1000:F1})"))}] " +
                (arcs.Count > 0 ? $"r0={arcs[0].r * 1000:F3}mm " : ""));
            // count-dim probe: D4/D5 hold the instance count — can they resize it?
            var d4 = doc.IParameter($"D4@{skName}") as IDimension;
            if (d4 != null)
            {
                int rcC = -9;
                try { rcC = d4.SetSystemValue3(n + 1, 2 /* swSetValue_InAllConfigurations */, null); } catch { }
                doc.EditRebuild3();
                arcs = ArcCenters(sk);
                sb.Append($"setD4(4)={rcC} arcs={arcs.Count}");
            }
            return sb.ToString().Trim();
        }

        /// <summary>PAT5 — seed diameter as an editable constraint driven by
        /// spacing × f (isolated from exp 4: drive the spacing dim directly).</summary>
        private static string PatExp5_SeedDia(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            if (!c.OpenSketch()) return "openSketch=fail";
            const int n = 5; const double pitch = 0.002, r = 0.0008;
            var (seed, seedName, ok) = CreatePattern(c, n, n, pitch, pitch,
                pitch / 2, pitch / 2, r, "", spacingDims: true);
            sb.Append($"create={ok} ");
            // diameter dim on the seed arc
            c.Doc.ClearSelection2(true);
            bool selArc = c.SelSegAt(pitch / 2 + r, pitch / 2);
            IDimension? diaDim = null;
            try
            {
                var dd = doc.IAddDiameterDimension2(0.004, 0.004, 0) as IDisplayDimension;
                diaDim = dd?.GetDimension() as IDimension;
            }
            catch (Exception ex) { sb.Append($"diaEX:{ex.GetType().Name} "); }
            sb.Append($"selArc={selArc} dia='{diaDim?.FullName}' ");
            var spacing = FindDims(doc).Where(d => Math.Abs(d.val - pitch) < 1e-9)
                                       .Select(d => d.name).ToList();
            sb.Append($"spacingCands[{string.Join(",", spacing)}] ");
            c.Mgr.InsertSketch(false);
            // NOTE: FindDims names are already "D@Sketch" — use them raw.
            if (diaDim != null && spacing.Count > 0)
                sb.Append($"eqDia={AddEquation(doc, EqName(diaDim.FullName), $"\"{spacing[0]}\"*0.5")} ");
            doc.EditRebuild3();
            // change spacing directly → diameter must follow ×0.5
            var spDim = spacing.Count > 0 ? doc.IParameter(spacing[0]) as IDimension : null;
            int rc = -9;
            try { rc = spDim?.SetSystemValue3(pitch * 2, 2 /* swSetValue_InAllConfigurations */, null) ?? -8; }
            catch (Exception ex) { sb.Append($"setSpEX:{ex.GetType().Name} "); }
            doc.EditRebuild3();
            var sk = FindSketchFeature(doc, FindSketchFeatureNames(doc).Last())
                     ?.GetSpecificFeature2() as ISketch;
            var arcs = ArcCenters(sk);
            sb.Append($"setSp={rc} arcs={arcs.Count} " +
                (arcs.Count > 0 ? $"r0={arcs[0].r * 1000:F3}mm(want 1.000)" : "noArcs"));
            return sb.ToString().Trim();
        }

        /// <summary>PAT6 — dimensionless end-state: box corners constrained to
        /// part-feature vertices (a plate), spacing equation targets the
        /// plate's dim — pattern scales when the part feature changes.</summary>
        private static string PatExp6_Dimensionless(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            // --- plate: 60 mm square at origin + driving dim, extruded 5 mm
            if (!c.OpenSketch()) return "plateSketch=fail";
            const double plateSize = 0.060;
            foreach (var (x1, y1, x2, y2) in new[]
                     { (0.0, 0.0, plateSize, 0.0), (plateSize, 0.0, plateSize, plateSize),
                       (plateSize, plateSize, 0.0, plateSize), (0.0, plateSize, 0.0, 0.0) })
                c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0);
            c.Doc.ClearSelection2(true);
            c.SelSegAt(plateSize / 2, 0);
            IDimension? plateDim = null;
            try
            {
                var dd = doc.IAddHorizontalDimension2(plateSize / 2, -0.004, 0) as IDisplayDimension;
                plateDim = dd?.GetDimension() as IDimension;
            }
            catch (Exception ex) { sb.Append($"plateDimEX:{ex.GetType().Name} "); }
            sb.Append($"plateDim='{plateDim?.FullName}' ");
            c.Mgr.InsertSketch(false);
            string plateSketchName = FindSketchFeatureNames(doc).Last();
            c.Doc.ClearSelection2(true);
            if (!c.Ext.SelectByID2(plateSketchName, "SKETCH", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault))
                return sb + "selPlateSketch=fail";
            var plate = c.Fm.FeatureExtrusion3(
                true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                0.005, 0.0, false, false, false, false, 0.0, 0.0,
                false, false, false, false,
                true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false)
                as IFeature;
            sb.Append($"plate={(plate == null ? "null" : plate.Name)} ");
            if (plate == null) return sb.ToString().Trim();

            // --- top face + its vertices (model space)
            var topFace = FindTopFace(doc, new[] { 0.0, 0.0, 0.005 });
            if (topFace == null) return sb + "topFace=null";
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
            sb.Append($"verts={verts.Count} ");
            if (verts.Count < 2) return sb + "noVerts";
            var vMin = verts.OrderBy(p => p[0] + p[1]).First();
            var vMax = verts.OrderByDescending(p => p[0] + p[1]).First();

            c.Doc.ClearSelection2(true);
            ((IEntity)topFace).Select4(false, null);
            c.Mgr.InsertSketch(true);
            var fsk = c.Mgr.ActiveSketch as ISketch;
            if (fsk == null) return sb + "faceSketch=fail";
            try { c.Mgr.AutoInference = false; } catch { }
            var m2s = fsk.ModelToSketchTransform;
            var s0 = MapPoint(c.Mu, m2s, vMin[0], vMin[1], vMin[2]);
            var s1 = MapPoint(c.Mu, m2s, vMax[0], vMax[1], vMax[2]);
            sb.Append($"s0=({s0[0] * 1000:F1},{s0[1] * 1000:F1}) s1=({s1[0] * 1000:F1},{s1[1] * 1000:F1}) ");

            // --- QR box: square drawn OFFSET (+8 mm) from the plate corners so
            //     the coincident constraint visibly snaps it onto the vertices.
            double bs = Math.Min(Math.Abs(s1[0] - s0[0]), Math.Abs(s1[1] - s0[1]));
            double bx = Math.Min(s0[0], s1[0]) + 0.008, by = Math.Min(s0[1], s1[1]) + 0.008;
            foreach (var (x1, y1, x2, y2) in new[]
                     { (bx, by, bx + bs, by), (bx + bs, by, bx + bs, by + bs),
                       (bx + bs, by + bs, bx, by + bs), (bx, by + bs, bx, by) })
            {
                var s = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                if (s != null) s.ConstructionGeometry = true;
            }
            c.Doc.ClearSelection2(true);
            bool k1 = SelSketchPointAt(c, bx, by, append: false);
            int cnt1 = -1; try { cnt1 = c.Sel.GetSelectedObjectCount(); } catch { }
            bool kv1 = c.SelVertex(vMin[0], vMin[1], vMin[2], append: true);
            int cnt2 = -1; try { cnt2 = c.Sel.GetSelectedObjectCount(); } catch { }
            sb.Append($"k1pt={k1}(c{cnt1}) k1v={kv1}(c{cnt2}) ");
            bool add1 = false;
            if (k1 && kv1) { try { doc.SketchAddConstraints("sgCOINCIDENT"); add1 = true; } catch { } }
            // did the corner move? the box point should now sit at vMin
            var skNow = c.Mgr.ActiveSketch as ISketch;
            var ptsNow = skNow?.GetSketchPoints() as object[];
            double near1 = ptsNow == null ? -1 : ptsNow.OfType<ISketchPoint>()
                .Select(p => Math.Sqrt((p.X - s0[0]) * (p.X - s0[0]) + (p.Y - s0[1]) * (p.Y - s0[1])))
                .DefaultIfEmpty(-1).Min();
            sb.Append($"add1={add1} near1={(near1 < 0 ? "?" : (near1 * 1000).ToString("F2") + "mm")} ");
            c.Doc.ClearSelection2(true);
            bool k2 = SelSketchPointAt(c, bx + bs, by + bs, append: false);
            bool kv2 = c.SelVertex(vMax[0], vMax[1], vMax[2], append: true);
            sb.Append($"k2pt={k2} k2v={kv2} ");
            bool add2 = false;
            if (k2 && kv2) { try { doc.SketchAddConstraints("sgCOINCIDENT"); add2 = true; } catch { } }
            skNow = c.Mgr.ActiveSketch as ISketch;
            ptsNow = skNow?.GetSketchPoints() as object[];
            double near2 = ptsNow == null ? -1 : ptsNow.OfType<ISketchPoint>()
                .Select(p => Math.Sqrt((p.X - s1[0]) * (p.X - s1[0]) + (p.Y - s1[1]) * (p.Y - s1[1])))
                .DefaultIfEmpty(-1).Min();
            sb.Append($"add2={add2} near2={(near2 < 0 ? "?" : (near2 * 1000).ToString("F2") + "mm")} ");

            // --- pattern anchored at the SNAPPED corner (s0), spacing targets
            //     the PLATE's dim / N
            const int n = 6;
            double pitch = bs / n, r = pitch * 0.4;
            var (seed, seedName, ok) = CreatePattern(c, n, n, pitch, pitch,
                s0[0] + pitch / 2, s0[1] + pitch / 2, r, "", spacingDims: true);
            sb.Append($"create={ok} ");
            var spacing = FindDims(doc).Where(d => Math.Abs(d.val - pitch) < 1e-6)
                                       .Select(d => d.name).ToList();
            sb.Append($"spacingCands[{string.Join(",", spacing)}] ");
            c.Mgr.InsertSketch(false);
            // NOTE: FindDims names are already "D@Sketch" — use them raw.
            if (plateDim != null && spacing.Count > 0)
                foreach (var s in spacing)
                    sb.Append($"eq={AddEquation(doc, s, $"\"{EqName(plateDim.FullName)}\"/{n}")} ");
            doc.EditRebuild3();
            // --- grow the plate 60 → 90 mm: pattern must follow
            int rc = -9;
            try { rc = plateDim?.SetSystemValue3(0.090, 2 /* swSetValue_InAllConfigurations */, null) ?? -8; }
            catch (Exception ex) { sb.Append($"setEX:{ex.GetType().Name} "); }
            doc.EditRebuild3();
            double plateAfter = -9;
            try { plateAfter = plateDim?.SystemValue ?? -8; } catch { }
            var sk = FindSketchFeature(doc, FindSketchFeatureNames(doc).Last())
                     ?.GetSpecificFeature2() as ISketch;
            var arcs = ArcCenters(sk);
            double pa = arcs.Count >= 2
                ? arcs.OrderBy(a => a.y).ThenBy(a => a.x).Skip(1).First().x -
                  arcs.OrderBy(a => a.y).ThenBy(a => a.x).First().x : -1;
            sb.Append($"setPlate={rc} plateAfter={plateAfter * 1000:F1}mm arcs={arcs.Count} " +
                $"pitchAfter={(pa > 0 ? (pa * 1000).ToString("F3") : "?")}mm(want 15.0) " +
                $"c0={(arcs.Count > 0 ? $"({arcs[0].x * 1000:F1},{arcs[0].y * 1000:F1})" : "?")}");
            return sb.ToString().Trim();
        }

        /// <summary>PAT7 — quiet-zone box: second construction square offset
        /// by 4·pitch, driven off the same master dim so both scale together.</summary>
        private static string PatExp7_QuietBox(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            if (!c.OpenSketch()) return "openSketch=fail";
            const int n = 7; const double pitch = 0.002;
            double size = n * pitch, q = 4 * pitch;

            var qrSegs = new List<ISketchSegment>();
            foreach (var (x1, y1, x2, y2) in new[]
                     { (0.0, 0.0, size, 0.0), (size, 0.0, size, size),
                       (size, size, 0.0, size), (0.0, size, 0.0, 0.0) })
            {
                var s = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                if (s != null) { s.ConstructionGeometry = true; qrSegs.Add(s); }
            }
            // quiet box at -q .. size+q
            var qSegs = new List<ISketchSegment>();
            foreach (var (x1, y1, x2, y2) in new[]
                     { (-q, -q, size + q, -q), (size + q, -q, size + q, size + q),
                       (size + q, size + q, -q, size + q), (-q, size + q, -q, -q) })
            {
                var s = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                if (s != null) { s.ConstructionGeometry = true; qSegs.Add(s); }
            }
            // master dim on the QR box
            c.Doc.ClearSelection2(true);
            c.SelSegAt(size / 2, 0);
            IDimension? boxDim = null;
            try
            {
                var dd = doc.IAddHorizontalDimension2(size / 2, -0.012, 0) as IDisplayDimension;
                boxDim = dd?.GetDimension() as IDimension;
            }
            catch (Exception ex) { sb.Append($"boxDimEX:{ex.GetType().Name} "); }
            sb.Append($"boxDim='{boxDim?.FullName}' ");

            // relate quiet box to QR box: try (1) equal relations on opposite
            // sides + coincident midpoints? — simplest proven route is dims +
            // equations; try an equal relation first for evidence.
            c.Doc.ClearSelection2(true);
            bool e1 = c.SelSegAt(-q + (size + 2 * q) / 2, -q) &&        // quiet bottom
                      c.SelSegAt(size / 2, size, append: true);         // QR top (wrong lengths — expect refuse/noop)
            sb.Append($"eqRelTry={e1} ");
            if (e1) { try { doc.SketchAddConstraints("sgEQUAL"); } catch { } }

            // offset dim: distance between QR left edge and quiet left edge
            IDimension? offDim = null;
            c.Doc.ClearSelection2(true);
            bool s1 = c.SelSegAt(0, size / 2), s2 = c.SelSegAt(-q, size / 2, append: true);
            sb.Append($"selOffEdges={s1}&{s2} ");
            try
            {
                var dd = doc.IAddHorizontalDimension2(-q / 2, -q - 0.004, 0) as IDisplayDimension;
                offDim = dd?.GetDimension() as IDimension;
            }
            catch (Exception ex) { sb.Append($"offDimEX:{ex.GetType().Name} "); }
            sb.Append($"offDim='{offDim?.FullName}' ");
            // quiet box width dim
            IDimension? qDim = null;
            c.Doc.ClearSelection2(true);
            c.SelSegAt(0, -q);
            try
            {
                var dd = doc.IAddHorizontalDimension2(size / 2, -q - 0.008, 0) as IDisplayDimension;
                qDim = dd?.GetDimension() as IDimension;
            }
            catch (Exception ex) { sb.Append($"qDimEX:{ex.GetType().Name} "); }
            sb.Append($"qDim='{qDim?.FullName}' ");
            c.Mgr.InsertSketch(false);
            sb.Append("dims[" + string.Join(" ",
                FindDims(doc).Select(d => $"{d.name}={d.val * 1000:F3}")) + "] ");

            // equations: offset = box*(4/n), quiet width = box*(1+8/n)
            if (boxDim != null && offDim != null)
                sb.Append($"eqOff={AddEquation(doc, EqName(offDim.FullName), $"\"{EqName(boxDim.FullName)}\"*{4.0 / n}")} ");
            if (boxDim != null && qDim != null)
                sb.Append($"eqQ={AddEquation(doc, EqName(qDim.FullName), $"\"{EqName(boxDim.FullName)}\"*{1.0 + 8.0 / n}")} ");
            doc.EditRebuild3();
            int rc = -9;
            try { rc = boxDim?.SetSystemValue3(size * 2, 2 /* swSetValue_InAllConfigurations */, null) ?? -8; }
            catch (Exception ex) { sb.Append($"setEX:{ex.GetType().Name} "); }
            doc.EditRebuild3();
            sb.Append($"setBox={rc} dims2[" + string.Join(" ",
                FindDims(doc).Select(d => $"{d.name}={d.val * 1000:F3}")) + "]");
            return sb.ToString().Trim();
        }

        /// <summary>PAT8 — density/timing: real QR masks at N=21 and N=41 via
        /// the CREATE-TIME skip-string route (the production recipe). Also
        /// times the deprecated object-delete route on N21 for contrast.</summary>
        private static string PatExp8_Timing(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            foreach (var (label, n) in new[] { ("N21", FindQrMask(21)), ("N41", FindQrMask(41)) })
            {
                int want = int.Parse(label.Substring(1));
                var mask = n.mask ?? SyntheticMask(want);
                int nReal = mask.Length;
                int dark = mask.Sum(row => row.Count(v => v));
                double pitch = 25e-3 / nReal, r = pitch * 0.4;
                var skips = new StringBuilder();
                int light = 0;
                for (int row = 0; row < nReal; row++)
                    for (int col = 0; col < nReal; col++)
                        if (!mask[row][col]) { skips.Append($"({col + 1},{row + 1}) "); light++; }
                var tsw = Stopwatch.StartNew();
                if (!c.OpenSketch()) { sb.Append($"{label}=openFail "); continue; }
                double tOpen = tsw.Elapsed.TotalMilliseconds;
                tsw.Restart();
                var (seed, seedName, ok) = CreatePattern(c, nReal, nReal, pitch, pitch,
                    pitch / 2, pitch / 2, r, skips.ToString().TrimEnd(), spacingDims: false);
                double tCreate = tsw.Elapsed.TotalMilliseconds;
                var arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
                tsw.Restart();
                c.Mgr.InsertSketch(false);
                double tCommit = tsw.Elapsed.TotalMilliseconds;
                // which light cells survived? (extra arcs = ignored skip entries)
                var extraCells = new List<string>();
                for (int row = 0; row < nReal && extraCells.Count < 8; row++)
                    for (int col = 0; col < nReal; col++)
                    {
                        if (mask[row][col]) continue;
                        double cx = pitch / 2 + col * pitch, cy = pitch / 2 + row * pitch;
                        if (arcs.Any(a => Math.Abs(a.x - cx) < 2e-4 && Math.Abs(a.y - cy) < 2e-4))
                            extraCells.Add($"{col},{row}");
                    }
                sb.Append($"{label}[{(n.mask == null ? "synth " : "")}open={tOpen:F0} " +
                    $"create+skip={tCreate:F0} commit={tCommit:F0}ms " +
                    $"dark={dark} light={light} str={skips.Length}B arcs={arcs.Count} ok={ok} " +
                    $"extra[{string.Join(";", extraCells)}]] ");
            }
            // contrast: object-delete cost on a 21 grid (the deprecated route)
            {
                var mask = SyntheticMask(21);
                double pitch = 25e-3 / 21, r = pitch * 0.4;
                var tsw = Stopwatch.StartNew();
                if (c.OpenSketch())
                {
                    var (s8, n8, ok8) = CreatePattern(c, 21, 21, pitch, pitch,
                        pitch / 2, pitch / 2, r, "", spacingDims: false);
                    double tCreate = tsw.Elapsed.TotalMilliseconds;
                    tsw.Restart();
                    var res = SkipLightCells(c, 21, pitch, (col, row) => mask[row][col]);
                    double tSkip = tsw.Elapsed.TotalMilliseconds;
                    var arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
                    c.Mgr.InsertSketch(false);
                    sb.Append($"N21objDel[create={tCreate:F0} skip={tSkip:F0}" +
                        $"(picked{res.picked}) arcs={arcs.Count}] ");
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Deterministic ~50% pseudo-QR mask for timing runs.</summary>
        private static bool[][] SyntheticMask(int n)
        {
            var m = new bool[n][];
            for (int i = 0; i < n; i++)
            {
                m[i] = new bool[n];
                for (int j = 0; j < n; j++)
                    m[i][j] = ((i * 31 + j * 17 + i * j) % 7) < 3;
            }
            return m;
        }

        /// <summary>PAT9 — undo collapse. Phase A: bare EditUndo2 after the
        /// recipe (baseline: usually does nothing to a committed sketch).
        /// Phase B: the named-undo-object route — StartRecordingUndoObject /
        /// FinishRecordingUndoObject2 around the whole recipe.</summary>
        private static string PatExp9_Undo(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            // marker (prior work)
            if (!c.OpenSketch()) return "marker=fail";
            c.Mgr.CreateLine(0.1, 0.1, 0, 0.11, 0.1, 0);
            c.Mgr.InsertSketch(false);
            int marksBefore = FindSketchFeatureNames(doc).Count;

            void Recipe(string skipStr)
            {
                c.OpenSketch();
                const int n = 5; const double pitch = 0.002, r = 0.0008;
                double size = n * pitch;
                foreach (var (x1, y1, x2, y2) in new[]
                         { (0.0, 0.0, size, 0.0), (size, 0.0, size, size),
                           (size, size, 0.0, size), (0.0, size, 0.0, 0.0) })
                {
                    var s = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                    if (s != null) s.ConstructionGeometry = true;
                }
                c.Doc.ClearSelection2(true);
                c.SelSegAt(size / 2, 0);
                try { doc.IAddHorizontalDimension2(size / 2, -0.004, 0); } catch { }
                CreatePattern(c, n, n, pitch, pitch, pitch / 2, pitch / 2, r,
                    skipStr, spacingDims: true);
                c.Mgr.InsertSketch(false);
            }

            // ---- Phase A: plain recipe + EditUndo2 --------------------------
            Recipe("(2,4) (4,2)");
            int featsA = FindSketchFeatureNames(doc).Count;
            sb.Append($"A[sketches={marksBefore}->{featsA} ");
            try { doc.EditUndo2(1); } catch (Exception ex) { sb.Append($"undoEX:{ex.GetType().Name} "); }
            sb.Append($"afterUndo={FindSketchFeatureNames(doc).Count} ");
            try { doc.EditRedo2(1); } catch { }
            sb.Append($"afterRedo={FindSketchFeatureNames(doc).Count}] ");

            // ---- Phase B: recipe inside a named undo object -----------------
            bool started = false, finished = false;
            try { c.Ext.StartRecordingUndoObject(); started = true; } catch { }
            Recipe("(3,2) (2,3)");
            try { c.Ext.FinishRecordingUndoObject2("QR pattern spike", false); finished = true; } catch { }
            int featsB = FindSketchFeatureNames(doc).Count;
            sb.Append($"B[recStart={started} recFin={finished} sketches={featsB} ");
            try { doc.EditUndo2(1); } catch (Exception ex) { sb.Append($"undoEX:{ex.GetType().Name} "); }
            int featsU = FindSketchFeatureNames(doc).Count;
            sb.Append($"afterUndo={featsU} ");
            try { doc.EditRedo2(1); } catch { }
            sb.Append($"afterRedo={FindSketchFeatureNames(doc).Count}]");
            return sb.ToString().Trim();
        }

        /// <summary>PAT10 — invert: same recipe, flipped mask — dots land on
        /// the light cells.</summary>
        private static string PatExp10_Invert(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            if (!c.OpenSketch()) return "openSketch=fail";
            const int n = 7; const double pitch = 0.002, r = 0.0008;
            // INVERTED plus: keep light cells → skip the plus cells instead
            var skipStr = new StringBuilder();
            for (int row = 0; row < n; row++)
                for (int col = 0; col < n; col++)
                    if (col == 3 || row == 3) skipStr.Append($"({col + 1},{row + 1}) ");
            var (seed, seedName, ok) = CreatePattern(c, n, n, pitch, pitch,
                pitch / 2, pitch / 2, r, skipStr.ToString().TrimEnd(), spacingDims: false);
            sb.Append($"create={ok} ");
            var arcs = ArcCenters(c.Mgr.ActiveSketch as ISketch);
            int kept = 0;
            for (int row = 0; row < n; row++)
                for (int col = 0; col < n; col++)
                    if (col != 3 && row != 3)
                    {
                        double cx = pitch / 2 + col * pitch, cy = pitch / 2 + row * pitch;
                        if (arcs.Any(a => Math.Abs(a.x - cx) < 2e-4 && Math.Abs(a.y - cy) < 2e-4))
                            kept++;
                    }
            sb.Append($"arcs={arcs.Count} keptLight={kept}/36");
            return sb.ToString().Trim();
        }

        /// <summary>PAT11 — fallback: per-cell CreateCircle (no pattern) —
        /// true circles that region-select and extrude.</summary>
        private static string PatExp11_Fallback(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            if (!c.OpenSketch()) return "openSketch=fail";
            const int n = 7; const double pitch = 0.002, r = 0.0008;
            int drawn = 0;
            for (int row = 0; row < n; row++)
                for (int col = 0; col < n; col++)
                    if (col == 3 || row == 3)
                    {
                        var s = c.Mgr.CreateCircleByRadius(
                            pitch / 2 + col * pitch, pitch / 2 + row * pitch, 0, r);
                        if (s != null) drawn++;
                    }
            sb.Append($"drawn={drawn} ");
            sb.Append(Census(c.Mgr.ActiveSketch as ISketch) + " ");
            c.Mgr.InsertSketch(false);
            c.Doc.ClearSelection2(true);
            try { c.Sel.EnableContourSelection = true; } catch { }
            int picks = 0;
            foreach (var (px, py) in new[] { (pitch / 2, pitch / 2), (pitch / 2 + 3 * pitch, pitch / 2) })
                try
                {
                    if (c.Ext.SelectByID2("", "SKETCHREGION", px, py, 0, true, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault))
                        picks++;
                }
                catch { }
            try { c.Sel.EnableContourSelection = false; } catch { }
            IFeature? boss = null;
            try
            {
                boss = c.Fm.FeatureExtrusion3(
                    true, false, false, (int)swEndConditions_e.swEndCondBlind, 0,
                    0.002, 0.0, false, false, false, false, 0.0, 0.0,
                    false, false, false, false,
                    true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0.0, false)
                    as IFeature;
            }
            catch (Exception ex) { sb.Append($"extEX:{ex.GetType().Name} "); }
            sb.Append($"regionPicks={picks} boss={(boss == null ? "null" : "'" + boss.Name + "'")} ");
            if (boss != null) sb.Append($"delBoss={DeleteFeatureByName(c, boss)}");
            return sb.ToString().Trim();
        }

        /// <summary>PAT12 — the production order, inside sketch edit. PAT4
        /// installed its equations AFTER InsertSketch(false); the ticket-#40
        /// emitter does everything in the open edit — dims, equations,
        /// EvaluateAll/EditRebuild3, then a master-dim rescale — also in
        /// edit. This replicates that ordering and reports whether the
        /// driven dims hold the box/N law and the geometry follows.</summary>
        private static string PatExp12_ProductionOrder(ISldWorks sw, IModelDoc2 doc)
        {
            var c = Pat.Of(sw, doc);
            var sb = new StringBuilder();
            const int n = 3, q = 4; const double pitch = 0.002, f = 0.5;
            double size = n * pitch, qo = q * pitch, keep = size + 2 * qo;
            double r = pitch * f / 2.0;

            if (!c.OpenSketch()) return "openSketch=fail";
            // QR square 0..size, keep-out square −qo..size+qo (construction)
            foreach (var (x1, y1, x2, y2) in new[]
                     { (0.0, 0.0, size, 0.0), (size, 0.0, size, size),
                       (size, size, 0.0, size), (0.0, size, 0.0, 0.0),
                       (-qo, -qo, size + qo, -qo), (size + qo, -qo, size + qo, size + qo),
                       (size + qo, size + qo, -qo, size + qo), (-qo, size + qo, -qo, -qo) })
            {
                var s = c.Mgr.CreateLine(x1, y1, 0, x2, y2, 0) as ISketchSegment;
                if (s != null) s.ConstructionGeometry = true;
            }
            // dims BEFORE the seed/pattern — the emitter's order
            c.Doc.ClearSelection2(true);
            c.SelSegAt(size / 2, 0);
            IDimension? boxDim = null;
            try { boxDim = (doc.IAddHorizontalDimension2(size / 2, -qo, 0) as IDisplayDimension)
                ?.GetDimension() as IDimension; }
            catch (Exception ex) { sb.Append($"boxEX:{ex.GetType().Name} "); }
            c.Doc.ClearSelection2(true);
            c.SelSegAt(0, size / 2); c.SelSegAt(-qo, size / 2, append: true);
            IDimension? qox = null;
            try { qox = (doc.IAddHorizontalDimension2(-qo / 2, -qo, 0) as IDisplayDimension)
                ?.GetDimension() as IDimension; }
            catch (Exception ex) { sb.Append($"qoxEX:{ex.GetType().Name} "); }
            c.Doc.ClearSelection2(true);
            c.SelSegAt(size / 2, 0); c.SelSegAt(size / 2, -qo, append: true);
            IDimension? qoy = null;
            try { qoy = (doc.IAddVerticalDimension2(size + qo + 0.003, -qo / 2, 0) as IDisplayDimension)
                ?.GetDimension() as IDimension; }
            catch (Exception ex) { sb.Append($"qoyEX:{ex.GetType().Name} "); }
            c.Doc.ClearSelection2(true);
            c.SelSegAt(size / 2, -qo);
            IDimension? qsz = null;
            try { qsz = (doc.IAddHorizontalDimension2(size / 2, -qo - 0.003, 0) as IDisplayDimension)
                ?.GetDimension() as IDimension; }
            catch (Exception ex) { sb.Append($"qszEX:{ex.GetType().Name} "); }
            sb.Append($"box='{EqName(boxDim?.FullName ?? "?")}' qox='{EqName(qox?.FullName ?? "?")}' " +
                $"qoy='{EqName(qoy?.FullName ?? "?")}' qsz='{EqName(qsz?.FullName ?? "?")}' ");
            var beforePattern = new HashSet<string>(
                FindDims(doc).Select(d => d.name));

            // seed + pattern — still in edit, the production arrangement:
            // seed at the square's TOP-LEFT cell, rows descending (−π/2)
            var seed = c.Mgr.CreateCircleByRadius(pitch / 2, size - pitch / 2, 0, r)
                as ISketchSegment;
            c.Doc.ClearSelection2(true);
            bool seedSel0 = false;
            try { seedSel0 = seed != null && seed.Select4(false, null); } catch { }
            bool ok = false;
            try
            {
                ok = c.Mgr.CreateLinearSketchStepAndRepeat(n, n, pitch, pitch,
                    0.0, -Math.PI / 2.0, "(2,1) (1,2)", true, true, false, false, false);
            }
            catch (Exception ex) { sb.Append($"patEX:{ex.GetType().Name} "); }
            sb.Append($"create={ok}(sel={seedSel0}) ");
            if (!ok)
            {
                // isolate: is it the negative angle? retry same seed at
                // 3π/2 — the positive-form equivalent of −π/2
                c.Doc.ClearSelection2(true);
                try { seedSel0 = seed != null && seed.Select4(false, null); } catch { }
                try
                {
                    ok = c.Mgr.CreateLinearSketchStepAndRepeat(n, n, pitch, pitch,
                        0.0, 3.0 * Math.PI / 2.0, "(2,1) (1,2)", true, true, false, false, false);
                }
                catch (Exception ex) { sb.Append($"pat3piEX:{ex.GetType().Name} "); }
                sb.Append($"retry3pi/2={ok}(sel={seedSel0}) ");
            }
            if (!ok)
            {
                // isolate: same sketch, fresh seed, the spike's proven
                // +π/2 — if THAT works the descending direction is refused
                var seed2 = c.Mgr.CreateCircleByRadius(pitch / 2, pitch / 2, 0, r)
                    as ISketchSegment;
                c.Doc.ClearSelection2(true);
                try { seedSel0 = seed2 != null && seed2.Select4(false, null); } catch { }
                try
                {
                    ok = c.Mgr.CreateLinearSketchStepAndRepeat(n, n, pitch, pitch,
                        0.0, Math.PI / 2.0, "(2,1) (1,2)", true, true, false, false, false);
                }
                catch (Exception ex) { sb.Append($"pat2EX:{ex.GetType().Name} "); }
                sb.Append($"retry+pi/2={ok}(sel={seedSel0}) ");
                if (ok) seed = seed2;
            }
            var skName = c.Mgr.ActiveSketch is ISketch sk0
                ? ((IFeature)sk0).Name ?? "Sketch?" : "Sketch?";
            sb.Append($"sk='{skName}' ");
            var spacing = new List<string>();
            foreach (var d in FindDims(doc))
                if (!beforePattern.Contains(d.name) &&
                    Math.Abs(d.val - pitch) < pitch * 0.001)
                    spacing.Add(d.name);
            sb.Append($"spacing[{string.Join(" ", spacing)}] ");
            string sx = spacing.Count > 0 ? EqName(spacing[0]) : "";
            string sy = spacing.Count > 1 ? EqName(spacing[1]) : "";

            // seed diameter + corner→centre dims — still in edit
            c.Doc.ClearSelection2(true);
            bool seedSel = false;
            try { seedSel = seed != null && seed.Select4(false, null); } catch { }
            IDimension? dia = null;
            if (seedSel)
                try { dia = (doc.IAddDiameterDimension2(pitch + r + 0.002, size + 0.002, 0)
                    as IDisplayDimension)?.GetDimension() as IDimension; }
                catch (Exception ex) { sb.Append($"diaEX:{ex.GetType().Name} "); }
            c.Doc.ClearSelection2(true);
            bool pA = SelSketchPointAt(c, 0, size, append: false);      // QR top-left corner
            bool pB = SelSketchPointAt(c, pitch / 2, size - pitch / 2, append: true); // seed centre
            IDimension? ox = null, oy = null;
            try { ox = (doc.IAddHorizontalDimension2(0, size + 0.003, 0)
                as IDisplayDimension)?.GetDimension() as IDimension; } catch { }
            c.Doc.ClearSelection2(true);
            SelSketchPointAt(c, 0, size, append: false);
            SelSketchPointAt(c, pitch / 2, size - pitch / 2, append: true);
            try { oy = (doc.IAddVerticalDimension2(-0.003, size - pitch / 4, 0)
                as IDisplayDimension)?.GetDimension() as IDimension; } catch { }
            sb.Append($"dia='{EqName(dia?.FullName ?? "?")}' pts={pA}&{pB} " +
                $"ox='{EqName(ox?.FullName ?? "?")}'({ox?.SystemValue * 1000:F2}mm) " +
                $"oy='{EqName(oy?.FullName ?? "?")}'({oy?.SystemValue * 1000:F2}mm) ");

            // equations INSIDE the open edit — the production order's risk
            string boxEq = boxDim != null ? EqName(boxDim.FullName!) : "";
            var eqm = doc.GetEquationMgr() as IEquationMgr;
            int refused = 0;
            foreach (var (lhs, rhs) in new[]
            {
                (sx, $"\"{boxEq}\"/{n}"),
                (sy, $"\"{boxEq}\"/{n}"),
                (dia != null ? EqName(dia.FullName!) : "", $"\"{sx}\"*{f}"),
                (ox != null ? EqName(ox.FullName!) : "",
                    (ox != null && ox.SystemValue < 0 ? "-" : "") + $"\"{sx}\"/2"),
                (oy != null ? EqName(oy.FullName!) : "",
                    (oy != null && oy.SystemValue < 0 ? "-" : "") + $"\"{sy}\"/2"),
                (qox != null ? EqName(qox.FullName!) : "",
                    (qox != null && qox.SystemValue < 0 ? "-" : "") + $"\"{sx}\"*{q}"),
                (qoy != null ? EqName(qoy.FullName!) : "",
                    (qoy != null && qoy.SystemValue < 0 ? "-" : "") + $"\"{sy}\"*{q}"),
                (qsz != null ? EqName(qsz.FullName!) : "", $"\"{sx}\"*{n + 2 * q}"),
            })
            {
                if (lhs.Length == 0 || eqm == null) continue;
                try { if (eqm.Add(-1, $"\"{lhs}\" = {rhs}") < 0) refused++; }
                catch { refused++; }
            }
            try { eqm?.EvaluateAll(); } catch { }
            try { doc.EditRebuild3(); } catch { }
            sb.Append($"eqInEdit rc={refused} dims1[{string.Join(" ", FindDims(doc)
                .Select(d => $"{d.name}={d.val * 1000:F2}"))}] ");

            // master-dim rescale INSIDE the edit — the dimensioned end-state's
            // whole point; then exit and re-verify on the committed sketch
            int setRc = -9;
            try { setRc = boxDim?.SetSystemValue3(size * 2, 2 /* swSetValue_InAllConfigurations */, null) ?? -8; } catch { }
            try { doc.EditRebuild3(); } catch { }
            sb.Append($"setBoxInEdit={setRc} dims2[{string.Join(" ", FindDims(doc)
                .Select(d => $"{d.name}={d.val * 1000:F2}"))}] ");
            c.Mgr.InsertSketch(false);
            try { doc.EditRebuild3(); } catch { }
            var sk = FindSketchFeature(doc, skName)?.GetSpecificFeature2() as ISketch;
            var arcs = ArcCenters(sk);
            // expectation: 9 cells − 2 skipped = 7 arcs, radius doubled to 1 mm
            sb.Append($"afterExit arcs={arcs.Count} " +
                $"r0={(arcs.Count > 0 ? $"{arcs[0].r * 1000:F3}mm" : "?")}");
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Finds a real QR mask of the requested module count (payloads are
        /// 'A'-runs at ECC M; returns null when no version yields that N).
        /// </summary>
        private static (bool[][]? mask, int N, int len) FindQrMask(int want)
        {
            for (int len = 1; len < 500; len += 1)
            {
                try
                {
                    using var gen = new QRCodeGenerator();
                    var data = gen.CreateQrCode(new string('A', len), QRCodeGenerator.ECCLevel.M);
                    var mm = data.ModuleMatrix;
                    if (mm.Count < want) continue;
                    if (mm.Count > want) return (null, mm.Count, len);
                    var mask = new bool[want][];
                    for (int i = 0; i < want; i++)
                    {
                        mask[i] = new bool[want];
                        for (int j = 0; j < want; j++) mask[i][j] = mm[i][j];
                    }
                    return (mask, want, len);
                }
                catch { }
            }
            return (null, -1, -1);
        }
    }
}
