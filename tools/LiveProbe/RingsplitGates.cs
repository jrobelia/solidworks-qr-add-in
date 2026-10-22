using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Ticket-#55 live gates for the spec-#51 open-nested-rings option —
    /// the production-path suite over the ticket-#52/#53 geometry and the
    /// ticket-#54 pane surface. A real option-on create driven through the
    /// pane path (SetQrField "ringsplit" + CreateQrFromWindowProbe) proves the
    /// delivered artefact, not the assumption:
    ///   the emitted block's definition census shows the opened geometry —
    ///   no island-bearing hole, the former islands promoted to top-level
    ///   contours, the finder channel width matching the module's derived
    ///   <see cref="QrSketchInfo.FinderChannelWidthMm"/> — verified by the
    ///   module-identical route (VerifyXsettingsBlock's 1:1 edge census)
    ///   plus the <see cref="QrChannelGaps"/> facing-wall scan measured on
    ///   the definition's enumerable segments;
    ///   the exploded sketch keeps every material region selectable through
    ///   ISketchRegion.Select (never a bbox-centre pick — a C-shaped opened
    ///   band's bbox centre can land in its channel mouth, the documented
    ///   notch hazard), with the region census reflecting the promoted
    ///   islands (<see cref="QrNestingCensus"/>);
    ///   insertion guard, named-undo, preview-signature and option-off
    ///   surfaces unchanged; the below-M ECC refusal names the field before
    ///   any host mutation; circle output stays inert.
    /// Automatic profile selection itself cannot be driven headless — the
    /// exploded census + ISketchRegion.Select route is the honest proxy,
    /// and docs/ACCEPTANCE.md carries the manual click-and-extrude row.
    ///
    /// Gates (each prints PASS / FAIL / LIMIT; exit code 0 only when
    /// nothing failed):
    ///   plumbing   — the toggle round-trips through the session probes,
    ///                the below-M refusal and the circle inertness report
    ///                the module's own verdicts, and the preview signature
    ///                equals the module's render of the split sketch;
    ///   create     — the option-on create lands ONE block instance whose
    ///                definition folds exactly the module's opened
    ///                geometry: channel walls measured at the derived
    ///                width, zero island-bearing holes, promoted islands;
    ///   exploded   — exploded census module-identical, ISketchRegion
    ///                total == the nesting census, every material region
    ///                (opened bands AND promoted islands) object-selects;
    ///   undo       — the named-undo surface is unchanged on a split
    ///                create;
    ///   guard/ecc  — the insertion guard and the below-M refusal both
    ///                refuse before any host mutation;
    ///   dense      — a dense payload's option-on create exercises the
    ///                module-aligned cell-drop path end-to-end;
    ///   regression — the option-off create delivers the historical nested
    ///                geometry byte-identically (no channel pairs at all).
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// The `ringsplit` gate state across the expectations→run hand-off
        /// (#62): the verdict fixtures, expected sketches, nesting censuses,
        /// channel scans and preview signatures the live gates compare
        /// against. The legacy RingsplitMain split at the ceremony seams —
        /// every check line, print and gate ordering below is its body
        /// verbatim; the launch/probe/teardown ceremony, the occupied-seat
        /// refusal and the spec-51 surface reload are the GateHost's
        /// uniform policy.
        /// </summary>
        private sealed class RingsplitGate
        {
            private const double sizeMm = QrSketchSession.InitialSizeMm;              // 25
            private QrSketchResult _plain = null!, _split = null!,
                _denseSplit = null!, _invSplit = null!,
                _eccLRefuse = null!, _smallSplit = null!, _circleSplit = null!;
            private QrSketch _expectedPlain = null!, _expectedSplit = null!,
                _expectedDense = null!, _expectedInv = null!;
            private QrNestingCensus _cenPlain = null!, _cenSplit = null!,
                _cenDense = null!;
            private List<QrChannelGap> _chansSplit = null!, _chansDense = null!;
            private double _chanWidthSplitM, _chanWidthDenseM;
            private string _sigPlain = "", _sigSplit = "", _denseContent = "";
            private readonly System.Globalization.CultureInfo _inv =
                System.Globalization.CultureInfo.InvariantCulture;

            /// <summary>Half the module's minimum light gap, in metres.</summary>
            private static double MaxGapM(QrSketch s) => s.Info.MinLightGapMm / 2000.0;

            /// <summary>
            /// The module-side expectations — the full verdict/census/
            /// channel table, printed ahead of the occupied-seat refusal
            /// exactly as the legacy head printed it.
            /// </summary>
            public bool Expectations()
            {
            var inv = _inv;

            // ================= module-side expectations =================
            const double growth = QrSketchSession.InitialGrowthFractionPerSide;         // 3 %
            const double denseSizeMm = 40.0, denseGrowth = 0.10;
            string denseContent = new string('K', 200);   // nests beyond the finders

            var plain = RingsplitVerdict(PayloadA, sizeMm, growth,
                QRCodeGenerator.ECCLevel.M, invert: false, ringSplit: false);
            var split = RingsplitVerdict(PayloadA, sizeMm, growth,
                QRCodeGenerator.ECCLevel.M, invert: false, ringSplit: true);
            var densePlain = RingsplitVerdict(denseContent, denseSizeMm, denseGrowth,
                QRCodeGenerator.ECCLevel.M, invert: false, ringSplit: false);
            var denseSplit = RingsplitVerdict(denseContent, denseSizeMm, denseGrowth,
                QRCodeGenerator.ECCLevel.M, invert: false, ringSplit: true);
            var invSplit = RingsplitVerdict(PayloadA, sizeMm, growth,
                QRCodeGenerator.ECCLevel.M, invert: true, ringSplit: true);
            var eccLRefuse = RingsplitVerdict(PayloadA, sizeMm, growth,
                QRCodeGenerator.ECCLevel.L, invert: false, ringSplit: true);
            var smallSplit = RingsplitVerdict(PayloadA, 10.0, growth,
                QRCodeGenerator.ECCLevel.M, invert: false, ringSplit: true);
            var circleSplit = QrSketchGenerator.Generate(new QrSketchRequest(
                PayloadA, sizeMm, growth)
            { CellShape = QrCellShape.Circle, CellShapeParameter = 0.9, RingSplit = true });
            var circleSplitL = QrSketchGenerator.Generate(new QrSketchRequest(
                PayloadA, sizeMm, growth)
            {
                CellShape = QrCellShape.Circle, CellShapeParameter = 0.9,
                RingSplit = true, EccLevel = QRCodeGenerator.ECCLevel.L,
            });

            // The module half must be sound before any SolidWorks contact —
            // a degenerate expectation would make every gate meaningless.
            if (!split.Succeeded || !plain.Succeeded || !denseSplit.Succeeded ||
                !densePlain.Succeeded || !invSplit.Succeeded ||
                !circleSplit.Succeeded || circleSplit.Sketch!.CirclePlan == null ||
                eccLRefuse.Succeeded || eccLRefuse.Error!.Kind != QrSketchErrorKind.InvalidEccLevel ||
                smallSplit.Succeeded)
            {
                Console.WriteLine("# FATAL: module-side ring-split expectations are unsound: " +
                    $"split={(split.Succeeded ? "ok" : split.Error!.Kind.ToString())} " +
                    $"dense={(denseSplit.Succeeded ? "ok" : denseSplit.Error!.Kind.ToString())} " +
                    $"invSplit={(invSplit.Succeeded ? "ok" : invSplit.Error!.Kind.ToString())} " +
                    $"circle={(circleSplit.Succeeded ? "ok" : circleSplit.Error!.Kind.ToString())} " +
                    $"eccL={(eccLRefuse.Succeeded ? "ACCEPTED(!)" : eccLRefuse.Error!.Kind.ToString())} " +
                    $"small={(smallSplit.Succeeded ? "ACCEPTED(!)" : smallSplit.Error!.Kind.ToString())}");
                return false;
            }
            var expectedSplit = split.Sketch!;
            var expectedPlain = plain.Sketch!;
            var expectedDense = denseSplit.Sketch!;
            var expectedDensePlain = densePlain.Sketch!;
            var expectedInv = invSplit.Sketch!;

            var cenPlain = QrNestingCensus.Of(expectedPlain);
            var cenSplit = QrNestingCensus.Of(expectedSplit);
            var cenDensePlain = QrNestingCensus.Of(expectedDensePlain);
            var cenDense = QrNestingCensus.Of(expectedDense);
            var cenInv = QrNestingCensus.Of(expectedInv);

            var chansSplit = QrChannelGaps.Find(expectedSplit.Edges, MaxGapM(expectedSplit));
            var chansDense = QrChannelGaps.Find(expectedDense.Edges, MaxGapM(expectedDense));
            var chansPlain = QrChannelGaps.Find(expectedPlain.Edges, MaxGapM(expectedPlain));
            double chanWidthSplitM = expectedSplit.Info.FinderChannelWidthMm / 1000.0;
            double chanWidthDenseM = expectedDense.Info.FinderChannelWidthMm / 1000.0;

            bool moduleSound = cenSplit.DoublyNestedFree && cenDense.DoublyNestedFree &&
                !cenPlain.DoublyNestedFree &&
                cenSplit.TopLevelRegions == cenPlain.TopLevelRegions + cenPlain.IslandCount &&
                expectedSplit.Info.OpenedSiteCount == cenPlain.IslandBearingHoleCount &&
                expectedDense.Info.OpenedSiteCount == cenDensePlain.IslandBearingHoleCount &&
                expectedDense.Info.DroppedCellCount > 0 &&
                chansSplit.Count == 3 && chansDense.Count == 3 && chansPlain.Count == 0 &&
                chansSplit.All(c => Math.Abs(c.GapM - chanWidthSplitM) < 1e-9) &&
                chansDense.All(c => Math.Abs(c.GapM - chanWidthDenseM) < 1e-9);
            if (!moduleSound)
            {
                Console.WriteLine("# FATAL: module-side census/channel expectations are unsound: " +
                    $"cenPlain(bearing={cenPlain.IslandBearingHoleCount}) " +
                    $"cenSplit(top={cenSplit.TopLevelRegions} bearing={cenSplit.IslandBearingHoleCount}) " +
                    $"cenDense(dropped={expectedDense.Info.DroppedCellCount}) " +
                    $"chans={chansSplit.Count}/{chansDense.Count}/{chansPlain.Count}");
                return false;
            }

            string sigPlain = QrPictureRenderer.Render(expectedPlain,
                QrSketchSession.PreviewPixelsAcross).Signature();
            string sigSplit = QrPictureRenderer.Render(expectedSplit,
                QrSketchSession.PreviewPixelsAcross).Signature();
            string sigDense = QrPictureRenderer.Render(expectedDense,
                QrSketchSession.PreviewPixelsAcross).Signature();

            Console.WriteLine("# spec-#51 live gates — open nested rings (ticket #55)");
            Console.WriteLine($"# baseline '{PayloadA}' @ {sizeMm}mm/{growth:P0} ECC=M: " +
                $"contours={expectedPlain.Info.ContourCount} segs={expectedPlain.Info.SegmentCount} " +
                $"regions={cenPlain.TopLevelRegions} holes={cenPlain.HoleCount} " +
                $"islands={cenPlain.IslandCount} islandBearing={cenPlain.IslandBearingHoleCount} " +
                $"material={cenPlain.MaterialRegionCount} sketchRegions={cenPlain.SketchRegionCount} " +
                $"sig={sigPlain.Substring(0, 12)}…");
            Console.WriteLine($"# split twin: contours={expectedSplit.Info.ContourCount} " +
                $"segs={expectedSplit.Info.SegmentCount} regions={cenSplit.TopLevelRegions} " +
                $"holes={cenSplit.HoleCount} islands={cenSplit.IslandCount} " +
                $"material={cenSplit.MaterialRegionCount} sketchRegions={cenSplit.SketchRegionCount} " +
                $"opened={expectedSplit.Info.OpenedSiteCount} dropped={expectedSplit.Info.DroppedCellCount} " +
                $"channel={expectedSplit.Info.FinderChannelWidthMm:F4}mm ({chansSplit.Count} walls-pairs) " +
                $"sig={sigSplit.Substring(0, 12)}…");
            Console.WriteLine($"# dense '{denseContent.Length}-char' @ {denseSizeMm}mm/{denseGrowth:P0}: " +
                $"unsplit bearing={cenDensePlain.IslandBearingHoleCount} " +
                $"islands={cenDensePlain.IslandCount} -> split regions={cenDense.TopLevelRegions} " +
                $"segs={expectedDense.Info.SegmentCount} opened={expectedDense.Info.OpenedSiteCount} " +
                $"dropped={expectedDense.Info.DroppedCellCount} " +
                $"channel={expectedDense.Info.FinderChannelWidthMm:F4}mm ({chansDense.Count}) " +
                $"material={cenDense.MaterialRegionCount} sketchRegions={cenDense.SketchRegionCount} " +
                $"sig={sigDense.Substring(0, 12)}…");
            Console.WriteLine($"# inverted split twin: contours={expectedInv.Info.ContourCount} " +
                $"segs={expectedInv.Info.SegmentCount} regions={cenInv.TopLevelRegions} " +
                $"sketchRegions={cenInv.SketchRegionCount}");
            Console.WriteLine($"# circle twin (inert): dots={circleSplit.Sketch!.CirclePlan!.EmittedCount} " +
                $"opened={circleSplit.Sketch.Info.OpenedSiteCount} — the ECC gate stays inert under " +
                $"circle too (L verdict: {(circleSplitL.Succeeded ? "VALID" : circleSplitL.Error!.Kind.ToString())})");
            Console.WriteLine($"# expected refusals: split@ECC=L -> {VerdictName(eccLRefuse)}; " +
                $"split@10mm -> {VerdictName(smallSplit)}");
            _plain = plain; _split = split; _denseSplit = denseSplit;
            _invSplit = invSplit; _eccLRefuse = eccLRefuse; _smallSplit = smallSplit;
            _circleSplit = circleSplit;
            _expectedPlain = expectedPlain; _expectedSplit = expectedSplit;
            _expectedDense = expectedDense; _expectedInv = expectedInv;
            _cenPlain = cenPlain; _cenSplit = cenSplit; _cenDense = cenDense;
            _chansSplit = chansSplit; _chansDense = chansDense;
            _chanWidthSplitM = chanWidthSplitM; _chanWidthDenseM = chanWidthDenseM;
            _sigPlain = sigPlain; _sigSplit = sigSplit; _denseContent = denseContent;
            return true;
            }

            /// <summary>
            /// The revision banner at its legacy position — the first
            /// visible line after the spawn, ahead of the spec-51
            /// discriminator and the reload.
            /// </summary>
            public void SessionBanner(ISldWorks sw, IAddInProbe? firstTry)
            {
                Console.WriteLine($"# SolidWorks revision {sw.RevisionNumber()} — " +
                    $"invisible spawned instance, {System.Environment.OSVersion}");
            }

            /// <summary>
            /// The check sequence the legacy body ran after the ceremony —
            /// gates 1–9 verbatim, driven against the hosted session.
            /// </summary>
            public int Run(LiveSession session)
            {
            var sw = session.Sw;
            var probe = session.Probe;
            string dllPath = session.DllPath;
            var plain = _plain; var split = _split; var denseSplit = _denseSplit;
            var invSplit = _invSplit; var eccLRefuse = _eccLRefuse;
            var smallSplit = _smallSplit; var circleSplit = _circleSplit;
            var expectedPlain = _expectedPlain; var expectedSplit = _expectedSplit;
            var expectedDense = _expectedDense; var expectedInv = _expectedInv;
            var cenPlain = _cenPlain; var cenSplit = _cenSplit; var cenDense = _cenDense;
            var chansSplit = _chansSplit; var chansDense = _chansDense;
            double chanWidthSplitM = _chanWidthSplitM, chanWidthDenseM = _chanWidthDenseM;
            var sigPlain = _sigPlain; var sigSplit = _sigSplit;
            var denseContent = _denseContent;
            var inv = _inv;
            try
            {
                Check(probe.AddinStateProbe().StartsWith("CONNECTED"),
                    "add-in connected", probe.AddinStateProbe());

                string template = session.DefaultPartTemplate;
                Check(File.Exists(template), "default part template exists", template);
                if (!File.Exists(template)) return 1;

                // ================= GATE 1 — session plumbing + preview parity =================
                Console.WriteLine("# ---- gate 1: session plumbing + preview parity ----");
                string opened = probe.OpenQrWindowProbe();
                Check(opened.Contains("open=True") && opened.Contains("windows=1"),
                    "pane opened through the command entry — the surface the gate drives", opened);
                Check(probe.OpenQrWindowProbe().Contains("windows=1"),
                    "a second open refocuses the same pane — no duplicate", probe.OpenQrWindowProbe());
                string state0 = probe.WindowStateProbe();
                Check(state0.Contains("hasRequest=False") && state0.Contains("ringsplit=False") &&
                      state0.Contains("shape=square") && state0.Contains("viewRingSplitVisible=True"),
                    "a fresh session defaults the option OFF with the control visible", state0);

                var pre = probe.SetQrField("ringsplit", "true");
                Check(pre.StartsWith("INVALID") && pre.Contains("EmptyContent"),
                    "the toggle applies with no content — INVALID EmptyContent is the contract", pre);
                string stateT = probe.WindowStateProbe();
                Check(stateT.Contains("ringsplit=True") && stateT.Contains("viewRingSplit=True"),
                    "the flag and the pane control both landed", stateT);
                var preOff = probe.SetQrField("ringsplit", "false");
                Check(preOff.StartsWith("INVALID") && preOff.Contains("EmptyContent") &&
                      probe.WindowStateProbe().Contains("ringsplit=False"),
                    "toggle back off pre-content", preOff);

                string c1 = probe.SetQrField("content", PayloadA);
                Check(ResponseMatchesVerdict(c1, plain),
                    "payload A accepted — the option-off census is the historical baseline", c1);
                Check(probe.PreviewSignatureProbe().Contains($"sha={sigPlain}"),
                    "the preview renders the module's UNSPLIT sketch", probe.PreviewSignatureProbe());

                var rs = probe.SetQrField("ringsplit", "true");
                Check(ResponseMatchesVerdict(rs, split) &&
                      rs.Contains($"contours={expectedSplit.Info.ContourCount}") &&
                      rs.Contains($"segs={expectedSplit.Info.SegmentCount}"),
                    "the option-on verdict reproduces the module's opened census", rs);
                string stateS = probe.WindowStateProbe();
                Check(stateS.Contains("ringsplit=True") && stateS.Contains("viewRingSplit=True"),
                    "the session and the rendered checkbox agree", stateS);
                string picSplit = probe.PreviewSignatureProbe();
                Check(picSplit.Contains($"sha={sigSplit}"),
                    "the preview renders exactly the module's SPLIT sketch (parity contract — " +
                    "the inner-edge slit can render sub-pixel at 240px, so the signature may " +
                    "coincide with the unsplit one)",
                    picSplit);
                var offAgain = probe.SetQrField("ringsplit", "false");
                Check(offAgain.StartsWith("VALID") &&
                      probe.PreviewSignatureProbe().Contains($"sha={sigPlain}"),
                    "toggling off restores the unsplit picture byte-identically", offAgain);
                var onAgain = probe.SetQrField("ringsplit", "true");
                Check(onAgain.StartsWith("VALID") &&
                      probe.PreviewSignatureProbe().Contains($"sha={sigSplit}"),
                    "and on again — left ON for the create gates", onAgain);

                // ================= GATE 2 — ECC refusal verdict =================
                Console.WriteLine("# ---- gate 2: ECC refusal verdict ----");
                var eL = probe.SetQrField("ecc", "L");
                Check(ResponseMatchesVerdict(eL, eccLRefuse) && eL.Contains("EccLevel"),
                    "ECC L under the option is refused naming the EccLevel field " +
                    $"({VerdictName(eccLRefuse)})", eL);
                Check(probe.WindowStateProbe().Contains("hasRequest=False") &&
                      probe.PreviewSignatureProbe() == "NO-PICTURE",
                    "the refused request carries no request and no picture",
                    probe.WindowStateProbe());
                var eM = probe.SetQrField("ecc", "M");
                Check(ResponseMatchesVerdict(eM, split),
                    "raising ECC restores the module's split verdict", eM);

                // ================= GATE 3 — circle inert =================
                Console.WriteLine("# ---- gate 3: circle shape leaves the flag inert ----");
                var cc = probe.SetQrField("shape", "circle");
                Check(cc.StartsWith("VALID") &&
                      cc.Contains($"dots={circleSplit.Sketch!.CirclePlan!.EmittedCount}"),
                    "circle output under the flag emits exactly the module's dot plan", cc);
                string stateC = probe.WindowStateProbe();
                Check(stateC.Contains("shape=circle") && stateC.Contains("ringsplit=True") &&
                      stateC.Contains("viewRingSplitVisible=False"),
                    "the flag stays stored while the control hides under circle", stateC);
                var cL = probe.SetQrField("ecc", "L");
                Check(cL.StartsWith("VALID") && cL.Contains("dots="),
                    "the ECC gate stays inert under circle — dots spend no correction budget", cL);
                Check(probe.SetQrField("ecc", "M").StartsWith("VALID"), "ECC restores to M", "");
                var back = probe.SetQrField("shape", "square");
                Check(ResponseMatchesVerdict(back, split) &&
                      probe.WindowStateProbe().Contains("viewRingSplitVisible=True"),
                    "back on square the stored flag applies with the control visible", back);

                // ================= GATE 4 — inverted split verdict =================
                Console.WriteLine("# ---- gate 4: inverted split verdict ----");
                var iv = probe.SetQrField("invert", "true");
                Check(ResponseMatchesVerdict(iv, invSplit) &&
                      iv.Contains($"contours={expectedInv.Info.ContourCount}"),
                    "invert + split reproduces the module's opened-pocket census", iv);
                var ivOff = probe.SetQrField("invert", "false");
                Check(ResponseMatchesVerdict(ivOff, split),
                    "polarity back to normal — the split census restored", ivOff);

                // ================= GATE 5 — option-on create + censuses =================
                Console.WriteLine("# ---- gate 5: option-on create, definition + exploded censuses ----");
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "gate part document created");
                if (doc == null) return 1;
                try
                {
                    var ext = (IModelDocExtension)doc.Extension;
                    var mgr = (ISketchManager)doc.SketchManager;

                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane selected for the option-on create");
                    string cr = probe.CreateQrFromWindowProbe();
                    Check(cr.StartsWith("OK"), "the option-on create succeeds through the pane path", cr);
                    string sk1 = cr.StartsWith("OK") && cr.Split(' ').Length > 1
                        ? cr.Split(' ')[1] : "";
                    var openSketch = (mgr.ActiveSketch as ISketch) ??
                        FindSketchFeature(doc, sk1)?.GetSpecificFeature2() as ISketch;
                    Check(openSketch != null, "the created sketch is open in edit mode", sk1);
                    if (openSketch == null) return 1;

                    VerifyXsettingsBlock(openSketch, expectedSplit, "split@25mm");

                    // The definition's own facing-wall scan: the def sketch
                    // reports an offset frame, so channel WIDTHS (frame-
                    // independent) are compared, not mouth positions.
                    var defSketch = ((openSketch.IGetSketchBlockInstances(0) as ISketchBlockInstance)
                        ?.Definition as ISketchBlockDefinition)?.GetSketch() as ISketch;
                    var defEdges = defSketch == null
                        ? new List<SketchEdge>()
                        : SketchLineSpans(defSketch).Where(s => !s.Construction).Select(NativeEdge).ToList();
                    CheckChannelGaps(defEdges, chansSplit, chanWidthSplitM,
                        MaxGapM(expectedSplit), mouthsComparable: false, tag: "block def");

                    mgr.InsertSketch(false);
                    Check(mgr.ActiveSketch == null, "the split sketch exits edit mode");
                    VerifyExplodedModuleIdentical(doc, ext, mgr, sk1, expectedSplit, "split@25mm");

                    // Exploded census: the committed sketch now carries the
                    // opened contours as native segments — region totals and
                    // the ISketchRegion.Select route follow.
                    int regions = CommittedRegionCount(doc, sk1);
                    Check(regions == cenSplit.SketchRegionCount,
                        "exploded sketch's ISketchRegion total == the nesting census " +
                        "(material faces + void interiors, promoted islands included)",
                        $"native={regions} expected={cenSplit.SketchRegionCount} " +
                        $"(material={cenSplit.MaterialRegionCount} void={cenSplit.HoleCount})");
                    var hostEdges = SketchLineSpans(
                            FindSketchFeature(doc, sk1)?.GetSpecificFeature2() as ISketch ??
                            throw new InvalidOperationException("sketch feature lost"))
                        .Where(s => !s.Construction).Select(NativeEdge).ToList();
                    CheckChannelGaps(hostEdges, chansSplit, chanWidthSplitM,
                        MaxGapM(expectedSplit), mouthsComparable: true, tag: "exploded");

                    doc.ClearSelection2(true);
                    Check(SelectQrMaterialRegions(sw, doc, ext, mgr, sk1, expectedSplit),
                        "every material region — opened bands AND promoted islands — " +
                        "object-selects through ISketchRegion.Select (never a bbox-centre pick)",
                        $"material={cenSplit.MaterialRegionCount}");
                    doc.ClearSelection2(true);

                    // The extrudability the option exists for: the picked
                    // regions feed a real boss + cut on a backing plate.
                    FeatureChecks(sw, doc, ext, mgr, sk1, expectedSplit);
                    doc.ClearSelection2(true);

                    // ================= GATE 6 — named undo surface =================
                    Console.WriteLine("# ---- gate 6: named undo surface unchanged ----");
                    string undo = probe.UndoRedoQrProbe();
                    Check(undo.Contains("APIUNDO") && undo.Contains("removed=True"),
                        "the API undo object removes the split create exactly", undo);
                    Check(undo.Contains("userIntact=True"),
                        "prior user work survives the undo presses", undo);
                    Check(undo.Contains("restored=True"),
                        "the toggle reinserts the split sketch", undo);

                    // ================= GATE 7 — refusals before mutation =================
                    Console.WriteLine("# ---- gate 7: guard + ECC refusals before any mutation ----");
                    var sz10 = probe.SetQrField("size", "10");
                    Check(ResponseMatchesVerdict(sz10, smallSplit),
                        $"the insertion guard still floors the split request ({VerdictName(smallSplit)})",
                        sz10);
                    int featsBefore = FindSketchFeatureNames(doc).Count;
                    string refused = probe.CreateQrFromWindowProbe();
                    Check(refused.StartsWith("ERROR"), "create refuses the below-floor request", refused);
                    Check(FindSketchFeatureNames(doc).Count == featsBefore &&
                          mgr.ActiveSketch == null,
                        "the refused create touched neither the feature tree nor the sketch-edit state");
                    var sz25 = probe.SetQrField("size", sizeMm.ToString(inv));
                    Check(ResponseMatchesVerdict(sz25, split), "size restores to the gate fixture", sz25);

                    var eL2 = probe.SetQrField("ecc", "L");
                    Check(ResponseMatchesVerdict(eL2, eccLRefuse),
                        "the below-M verdict refuses on the live document too", eL2);
                    string refused2 = probe.CreateQrFromWindowProbe();
                    Check(refused2.StartsWith("ERROR"), "create refuses the below-M request", refused2);
                    Check(FindSketchFeatureNames(doc).Count == featsBefore &&
                          mgr.ActiveSketch == null,
                        "the ECC refusal also left no partial sketch");
                    Check(ResponseMatchesVerdict(probe.SetQrField("ecc", "M"), split),
                        "ECC M restores the valid split request", probe.SetQrField("ecc", "M"));

                    // ================= GATE 8 — dense cell-drop create =================
                    Console.WriteLine("# ---- gate 8: dense payload — the module-aligned cell-drop path ----");
                    Check(probe.SetQrField("growth", "10").StartsWith("VALID"),
                        "growth 10% accepted", probe.SetQrField("growth", "10"));
                    Check(probe.SetQrField("size", "40").StartsWith("VALID"),
                        "size 40 mm accepted", probe.SetQrField("size", "40"));
                    var dc = probe.SetQrField("content", denseContent);
                    Check(ResponseMatchesVerdict(dc, denseSplit),
                        "the dense payload's split verdict matches the module " +
                        $"(opened={expectedDense.Info.OpenedSiteCount} " +
                        $"dropped={expectedDense.Info.DroppedCellCount})", dc);
                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane selected for the dense split create");
                    string dcr = probe.CreateQrFromWindowProbe();
                    Check(dcr.StartsWith("OK"), "the dense option-on create lands", dcr);
                    string sk2 = dcr.StartsWith("OK") && dcr.Split(' ').Length > 1
                        ? dcr.Split(' ')[1] : "";
                    var denseSketch = (mgr.ActiveSketch as ISketch) ??
                        FindSketchFeature(doc, sk2)?.GetSpecificFeature2() as ISketch;
                    if (denseSketch != null)
                    {
                        VerifyXsettingsBlock(denseSketch, expectedDense, "dense-split@40mm");
                        var dDef = ((denseSketch.IGetSketchBlockInstances(0) as ISketchBlockInstance)
                            ?.Definition as ISketchBlockDefinition)?.GetSketch() as ISketch;
                        var dEdges = dDef == null
                            ? new List<SketchEdge>()
                            : SketchLineSpans(dDef).Where(s => !s.Construction)
                                .Select(NativeEdge).ToList();
                        CheckChannelGaps(dEdges, chansDense, chanWidthDenseM,
                            MaxGapM(expectedDense), mouthsComparable: false, tag: "dense def");
                        mgr.InsertSketch(false);
                        Check(mgr.ActiveSketch == null, "the dense sketch exits edit mode");
                        VerifyExplodedModuleIdentical(doc, ext, mgr, sk2, expectedDense, "dense-split@40mm");
                        int dRegions = CommittedRegionCount(doc, sk2);
                        Check(dRegions == cenDense.SketchRegionCount,
                            "the dense sketch's region census reflects the promoted islands",
                            $"native={dRegions} expected={cenDense.SketchRegionCount} " +
                            $"(material={cenDense.MaterialRegionCount} void={cenDense.HoleCount})");
                        doc.ClearSelection2(true);
                        Check(SelectQrMaterialRegions(sw, doc, ext, mgr, sk2, expectedDense),
                            "every dense material region object-selects too",
                            $"material={cenDense.MaterialRegionCount}");
                        doc.ClearSelection2(true);
                    }

                    // ================= GATE 9 — option-off regression =================
                    Console.WriteLine("# ---- gate 9: option-off regression — byte-identical history ----");
                    Check(probe.SetQrField("content", PayloadA).StartsWith("VALID"),
                        "payload A restored", probe.SetQrField("content", PayloadA));
                    Check(probe.SetQrField("growth", "3").StartsWith("VALID"),
                        "growth back to 3%", probe.SetQrField("growth", "3"));
                    Check(ResponseMatchesVerdict(probe.SetQrField("size", "25"), plain),
                        "the historical 25mm/3% verdict returns", probe.SetQrField("size", "25"));
                    var rsOff = probe.SetQrField("ringsplit", "false");
                    Check(ResponseMatchesVerdict(rsOff, plain) &&
                          probe.PreviewSignatureProbe().Contains($"sha={sigPlain}"),
                        "option off restores the module's UNSPLIT census and picture", rsOff);
                    int featsPreRegression = FindSketchFeatureNames(doc).Count;
                    doc.ClearSelection2(true);
                    Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault),
                        "Front Plane selected for the option-off create");
                    string ocr = probe.CreateQrFromWindowProbe();
                    Check(ocr.StartsWith("OK"), "the option-off create lands", ocr);
                    string sk3 = ocr.StartsWith("OK") && ocr.Split(' ').Length > 1
                        ? ocr.Split(' ')[1] : "";
                    var plainSketch = (mgr.ActiveSketch as ISketch) ??
                        FindSketchFeature(doc, sk3)?.GetSpecificFeature2() as ISketch;
                    if (plainSketch != null)
                    {
                        VerifyXsettingsBlock(plainSketch, expectedPlain, "plain@25mm");
                        mgr.InsertSketch(false);
                        VerifyExplodedModuleIdentical(doc, ext, mgr, sk3, expectedPlain, "plain@25mm");
                        int pRegions = CommittedRegionCount(doc, sk3);
                        Check(pRegions == cenPlain.SketchRegionCount,
                            "the untouched option delivers the historical nested region census",
                            $"native={pRegions} expected={cenPlain.SketchRegionCount} " +
                            $"(islandBearing={cenPlain.IslandBearingHoleCount} stays closed)");
                        var pEdges = SketchLineSpans(
                                FindSketchFeature(doc, sk3)?.GetSpecificFeature2() as ISketch ??
                                throw new InvalidOperationException("sketch feature lost"))
                            .Where(s => !s.Construction).Select(NativeEdge).ToList();
                        Check(QrChannelGaps.Find(pEdges, MaxGapM(expectedPlain), 5e-9).Count == 0,
                            "no channel pairs on the option-off output — byte-identical topology");
                        doc.ClearSelection2(true);
                        Check(SelectQrMaterialRegions(sw, doc, ext, mgr, sk3, expectedPlain),
                            "the nested material regions still object-select",
                            $"material={cenPlain.MaterialRegionCount}");
                    }
                    // Gate 5 adds TWO sketches (the QR + the extrudability
                    // plate), gates 8-9 one each, so the honest invariant is
                    // "the regression create adds exactly one sketch and every
                    // prior QR sketch survives", not a fixed tree total.
                    var namesNow = FindSketchFeatureNames(doc);
                    Check(namesNow.Count == featsPreRegression + 1 &&
                          namesNow.Contains(sk1) && namesNow.Contains(sk2) &&
                          namesNow.Contains(sk3),
                        "all three gate sketches co-exist untouched",
                        $"sketches=[{string.Join(", ", namesNow)}]");
                    string after = probe.WindowStateProbe();
                    Check(after.Contains("ringsplit=False"),
                        "the option stays off after the regression create", after);
                }
                finally
                {
                    try { sw.CloseDoc(doc.GetTitle()); } catch { }
                }

                Limit("automatic profile selection is interactive-only — the exploded " +
                    "census + ISketchRegion.Select route is the headless proxy; " +
                    "docs/ACCEPTANCE.md D21 is the manual click-and-extrude row");

                try { sw.UnloadAddIn(dllPath); } catch { }
                // The host prints the pass/fail summary (SummaryPass) and
                // derives the exit code from the failure tally.
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            }
        }

        /// <summary>
        /// The spec-51 discriminator: only a build carrying the ring-split
        /// option reports `ringsplit=` in WindowStateProbe. A stale build
        /// either lacks the member (throws) or reports a state string
        /// without the field.
        /// </summary>
        private static bool ProbeHasRingSplitSurface(IAddInProbe? probe)
        {
            if (probe == null) { Console.WriteLine("# no add-in object present"); return false; }
            try
            {
                string state = probe.WindowStateProbe();
                Console.WriteLine($"# discriminator WindowStateProbe -> {state}");
                return state.Contains("ringsplit=");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# discriminator WindowStateProbe -> {ex.GetType().Name} " +
                    "(the answering build predates spec-51)");
                return false;
            }
        }

        /// <summary>The module's verdict on one ring-split-capable request.</summary>
        private static QrSketchResult RingsplitVerdict(
            string content, double sizeMm, double growth,
            QRCodeGenerator.ECCLevel ecc, bool invert, bool ringSplit) =>
            QrSketchGenerator.Generate(new QrSketchRequest(content, sizeMm, growth)
            { EccLevel = ecc, Invert = invert, RingSplit = ringSplit });

        /// <summary>A native segment as a module edge, for the channel scan.</summary>
        private static SketchEdge NativeEdge(SegSpan s) =>
            new SketchEdge(new SketchPoint2(s.X1, s.Y1), new SketchPoint2(s.X2, s.Y2));

        /// <summary>
        /// The carrier-side facing-wall scan: the block definition's or the
        /// exploded sketch's enumerable material segments must reproduce the
        /// module's thin channels — same pair count, every measured gap at
        /// the derived width, and (on carriers whose coordinates are
        /// sketch-space) the channel mouths pairing 1:1 with the module's.
        /// </summary>
        private static void CheckChannelGaps(
            IReadOnlyList<SketchEdge> nativeEdges, IReadOnlyList<QrChannelGap> expected,
            double expectedWidthM, double maxGapM, bool mouthsComparable, string tag)
        {
            var found = QrChannelGaps.Find(nativeEdges, maxGapM, toleranceM: 5e-9);
            Check(found.Count == expected.Count,
                $"the carrier carries exactly the module's thin finder channels ({tag})",
                $"found={found.Count} expected={expected.Count}");
            if (found.Count != expected.Count) return;
            foreach (var g in found)
                Check(Math.Abs(g.GapM - expectedWidthM) < 1e-6,
                    $"a measured channel width == the module's derived width ({tag})",
                    $"gap={g.GapM * 1000:F4}mm expected={expectedWidthM * 1000:F4}mm");
            if (!mouthsComparable) return;
            foreach (var ec in expected)
                Check(found.Any(g => MouthPairMatch(g, ec, 5e-9)),
                    $"a native channel sits at the module channel's locus ({tag})",
                    $"mouths ({ec.MouthA.X * 1000:F3},{ec.MouthA.Y * 1000:F3}) -> " +
                    $"({ec.MouthB.X * 1000:F3},{ec.MouthB.Y * 1000:F3}) mm");
        }

        private static bool MouthPairMatch(QrChannelGap a, QrChannelGap b, double tol) =>
            (MouthNear(a.MouthA, b.MouthA, tol) && MouthNear(a.MouthB, b.MouthB, tol)) ||
            (MouthNear(a.MouthA, b.MouthB, tol) && MouthNear(a.MouthB, b.MouthA, tol));

        private static bool MouthNear(SketchPoint2 a, SketchPoint2 b, double tol) =>
            Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;

        /// <summary>GetSketchRegionCount on the committed feature, or -1.</summary>
        private static int CommittedRegionCount(IModelDoc2 doc, string sketchName)
        {
            var sk = FindSketchFeature(doc, sketchName)?.GetSpecificFeature2() as ISketch;
            int n = -1;
            try { if (sk != null) n = sk.GetSketchRegionCount(); } catch { }
            return n;
        }
    }
}
