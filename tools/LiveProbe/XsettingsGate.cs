using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QRCoder;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Payload;
using SolidWorksQrAddIn.Core.Preview;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// The `xsettings` gate on GateHost (#62): the spec-11 extended-settings
    /// live gates (WiFi preset composition, ECC/invert plumbing, the
    /// insertion-guard floor on the dense payload). The expectations block
    /// composes the payloads and replays the whole rung table module-side —
    /// all of it prints ahead of the occupied-seat refusal, verbatim. The
    /// run body is the legacy check sequence verbatim against the hosted
    /// session; the stale-build unload/reload is the host's
    /// ProbeSurface + UnloadCandidates ceremony.
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// The xsettings expectations→run state: the composed gate payloads,
        /// their module verdicts and signatures, and the dense payload's
        /// floor rungs.
        /// </summary>
        private sealed class XsettingsGate
        {
            private readonly System.Globalization.CultureInfo _inv =
                System.Globalization.CultureInfo.InvariantCulture;
            private Dictionary<string, string> _wifiFields = null!;
            private QrSketchResult _interim25 = null!, _interimGate = null!,
                _fullAtM = null!, _denseAtGate = null!;
            private QrSketch _expectedGate = null!, _denseSketch = null!;
            private double _gateSizeMm, _denseRefused, _denseAccepted;
            private string _sigInv = "", _sigNorm = "",
                _denseContent = "", _refusedKind = "";

            /// <summary>
            /// The revision banner at its legacy position — printed after the
            /// startup wait, ahead of the spec-11 discriminator probe.
            /// </summary>
            public void RevisionBanner(ISldWorks sw, IAddInProbe? firstTry)
            {
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} visible={sw.Visible} docs={openDocs}");
            }

            /// <summary>
            /// Every live verdict computed module-side, never guessed —
            /// the legacy expectations block verbatim.
            /// </summary>
            public bool Expectations()
            {
                const double growth = QrSketchSession.InitialGrowthFractionPerSide;
                const QRCodeGenerator.ECCLevel gateEcc = QRCodeGenerator.ECCLevel.H;
                var inv = _inv;

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
                    return false;
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
                    return false;
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
                    return false;
                }
                var denseAtGate = Verdict(denseContent, gateSizeMm, growth, gateEcc, invert: true);
                Console.WriteLine($"# dense payload: refused <={denseRefused:g}mm ({refusedKind}), " +
                    $"accepted {denseAccepted:g}mm (N={denseSketch.Info.CellCount}); " +
                    $"at gate size -> {VerdictName(denseAtGate)}");
                _wifiFields = wifiFields;
                _interim25 = interim25; _interimGate = interimGate;
                _fullAtM = fullAtM; _denseAtGate = denseAtGate;
                _expectedGate = expectedGate; _denseSketch = denseSketch;
                _gateSizeMm = gateSizeMm; _denseRefused = denseRefused;
                _denseAccepted = denseAccepted;
                _sigInv = sigInv; _sigNorm = sigNorm;
                _denseContent = denseContent; _refusedKind = refusedKind;
                return true;
            }

            /// <summary>
            /// The check sequence the legacy body ran after the ceremony —
            /// the pane plumbing, the WiFi field-by-field composition, the
            /// non-default create, and the dense floor refuse/accept pair,
            /// verbatim against the hosted session.
            /// </summary>
            public int Run(LiveSession session)
            {
                var sw = session.Sw;
                var probe = session.Probe;
                string dllPath = session.DllPath;
                var inv = _inv;
                var wifiFields = _wifiFields;
                var interim25 = _interim25;
                var interimGate = _interimGate;
                var fullAtM = _fullAtM;
                var denseAtGate = _denseAtGate;
                var expectedGate = _expectedGate;
                var denseSketch = _denseSketch;
                var denseContent = _denseContent;
                var refusedKind = _refusedKind;
                double gateSizeMm = _gateSizeMm;
                double denseRefused = _denseRefused;
                double denseAccepted = _denseAccepted;
                var sigInv = _sigInv;
                var sigNorm = _sigNorm;
                try
                {
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
                    string template = session.DefaultPartTemplate;
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
                    // The host prints `# XSETTINGS ALL CHECKS PASSED` / the
                    // failure line (SummaryPass) and derives the exit code.
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
    }
}
