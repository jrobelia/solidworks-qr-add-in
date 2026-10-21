using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Ticket-#46 live gates for the spec-#21 SW Properties content type —
    /// the end-to-end production-path suite over the ticket-#44 fixture and
    /// probe surface. The harness writes a known property fixture into a
    /// session-free part — document-level + configuration-specific +
    /// deliberately empty + whitespace values via CustomPropertyManager.Add3,
    /// on TWO configurations of the same part so the config-switch gate is
    /// meaningful — then drives the pane path (SetQrContentTypeProbe /
    /// SetQrPresetFieldProbe / the WindowStateProbe view echoes /
    /// CreateQrFromWindowProbe) and proves the created sketch encodes exactly
    /// the resolved literal by the established module-identical route
    /// (block-definition census + exploded 1:1 contour match — the same
    /// VerifyXsettingsBlock / VerifyExplodedModuleIdentical helpers the
    /// xsettings suite uses). A harness-side SwHostStateWatcher counts the
    /// host notifications (config switch, document switch, property edits)
    /// so the switch gates record whether the event-driven refresh or the
    /// field-write fallback did the re-resolution. The fixture plan prints
    /// BEFORE the SLDWORKS.exe refusal so a blocked run still shows what the
    /// live half would check.
    ///
    /// Gates (each prints PASS / FAIL / LIMIT lines; exit code 0 only when
    /// nothing failed):
    ///   fixture     — every Add3 write returns AddedOrChanged; the second
    ///                 configuration is created and populated;
    ///   enumerate   — SwPropertiesProbe reports the active part + config and
    ///                 lists every fixture name, deduplicated, config-store
    ///                 names first (SolidWorks' own precedence), no SW-*
    ///                 built-ins;
    ///   resolve     — doc-level and config values verbatim, the config-
    ///                 shadowed name resolving to the config value,
    ///                 whitespace verbatim, EMPTY distinct from MISSING;
    ///   pane        — the swprops content type exposes the template field;
    ///                 the insert dropdown echoes exactly the enumerated
    ///                 names (viewTemplateNames), the Resolved row shows the
    ///                 resolved literal (viewResolved) and the live preview
    ///                 signature equals the module's render of that literal;
    ///   create      — the create lands ONE block instance whose definition
    ///                 and exploded contours match the module's sketch for
    ///                 the RESOLVED literal 1:1 — the encoded payload is the
    ///                 resolved preview, proven not assumed;
    ///   cfgswitch   — activating the second configuration re-resolves the
    ///                 names, the resolve verdicts, the Resolved row and the
    ///                 picture; a second create encodes the new literal (the
    ///                 exploded-contour 1:1 route — positions, not just
    ///                 counts — so a stale same-length payload cannot pass);
    ///   docswitch   — a second part document with its own properties re-
    ///                 resolves names + preview to ITS values; activating
    ///                 the fixture back restores its resolution;
    ///   refusal     — a missing or empty $PRP reference fails the request
    ///                 naming it, and the create is refused BEFORE any host
    ///                 mutation — the feature tree and sketch state are
    ///                 untouched;
    ///   nodoc       — with no document open, resolve reports NODOC, the
    ///                 name list is empty and the Resolved row shows the
    ///                 reason.
    /// </summary>
    public static partial class Program
    {
        /// <summary>One fixture property: a name and the stored value.</summary>
        private sealed class PropFixture
        {
            public string Name = "";
            public string Value = "";
        }

        /// <summary>The second configuration the switch gate activates — it shadows 'Revision' with a different value.</summary>
        private const string SwpropsConfigB = "QrCfgB";

        private static int SwpropsMain(string[] args)
        {
            string deployDir = args.Length > 0 ? args[0] : Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            // ================= module-side fixture plan =================
            // cfgA = the part template's seed configuration (named live);
            // SwpropsConfigB is added by the fixture. Document-level values
            // are shared by every configuration; each configuration shadows
            // 'Revision' and 'QrShared' with its own values so a config
            // switch changes the resolved literal observably.
            var docFixtures = new List<PropFixture>
            {
                new PropFixture { Name = "PartNo",          Value = "PN-0042" },
                new PropFixture { Name = "QrDocProp",       Value = "DOC-VALUE-1" },
                new PropFixture { Name = "QrShared",        Value = "doc-level" },
                new PropFixture { Name = "QrEmpty",         Value = "" },
                new PropFixture { Name = "QrSpaced",        Value = "   " },
                new PropFixture { Name = "Qr Custom Label", Value = "LBL-7" },
            };
            var cfg1Fixtures = new List<PropFixture>
            {
                new PropFixture { Name = "Revision",  Value = "REV-A" },
                new PropFixture { Name = "QrShared",  Value = "cfg-level" },
                new PropFixture { Name = "QrCfgProp", Value = "CFG-VALUE-1" },
            };
            var cfgBFixtures = new List<PropFixture>
            {
                new PropFixture { Name = "Revision",   Value = "REV-B" },
                new PropFixture { Name = "QrShared",   Value = "cfg-B-level" },
                new PropFixture { Name = "QrCfgOnlyB", Value = "CFG-B-ONLY" },
            };
            // The second document carries its OWN values for the template's
            // two names — the doc-switch gate proves re-resolution by watching
            // the Resolved row change to a different literal, not just to ERR.
            var docBFixtures = new List<PropFixture>
            {
                new PropFixture { Name = "PartNo",     Value = "PN-7777" },
                new PropFixture { Name = "Revision",   Value = "D1" },
                new PropFixture { Name = "QrDocBOnly", Value = "B-ONLY" },
            };

            const string gateTemplate = "PART-$PRP:\"PartNo\"-$PRP:\"Revision\"";
            const string missingTemplate = "BAD-$PRP:\"QrNoSuchProp\"-BAD";
            const string emptyTemplate = "BLANK-$PRP:\"QrEmpty\"-BLANK";

            // The merged resolvable sets — config store first, deduplicated,
            // the seam's own precedence (PartPropertyResolver.MergeNames).
            var namesCfg1 = cfg1Fixtures.Select(f => f.Name)
                .Union(docFixtures.Select(f => f.Name), StringComparer.OrdinalIgnoreCase).ToList();
            var namesCfgB = cfgBFixtures.Select(f => f.Name)
                .Union(docFixtures.Select(f => f.Name), StringComparer.OrdinalIgnoreCase).ToList();
            var namesDocB = docBFixtures.Select(f => f.Name).ToList();

            const string literalA = "PART-PN-0042-REV-A";
            const string literalB = "PART-PN-0042-REV-B";
            const string literalDocB = "PART-PN-7777-D1";

            // The module's own expectation per resolved literal — the
            // created sketch must reproduce exactly this geometry (the
            // module-identical route) and the pane's preview must equal
            // this render (the signature route).
            var sketchA = ExpectedOf(literalA);
            var sketchB = ExpectedOf(literalB);
            var sketchDocB = ExpectedOf(literalDocB);
            if (sketchA == null || sketchB == null || sketchDocB == null)
            {
                Console.WriteLine("# FATAL: the module could not generate the " +
                    "gate literals' QR geometry — aborting the live run");
                return 1;
            }
            string sigA = ExpectedSignatureOf(literalA);
            string sigB = ExpectedSignatureOf(literalB);
            string sigDocB = ExpectedSignatureOf(literalDocB);

            Console.WriteLine("# ticket-#46 sw-props end-to-end live gates (spec #21)");
            Console.WriteLine("# fixture plan — harness writes via CustomPropertyManager.Add3:");
            foreach (var f in docFixtures)
                Console.WriteLine($"#   doc store:        '{f.Name}' = '{f.Value}'");
            foreach (var f in cfg1Fixtures)
                Console.WriteLine($"#   cfgA store:       '{f.Name}' = '{f.Value}'");
            foreach (var f in cfgBFixtures)
                Console.WriteLine($"#   {SwpropsConfigB} store:      '{f.Name}' = '{f.Value}'");
            Console.WriteLine($"#   {SwpropsConfigB} store:      'QrCfgProp' DELETED via Delete2 " +
                "(new configurations inherit the active config's specific properties)");
            Console.WriteLine($"#   second doc store: {string.Join(", ",
                docBFixtures.Select(f => $"'{f.Name}' = '{f.Value}'"))}");
            Console.WriteLine($"# gate template: {gateTemplate}");
            Console.WriteLine($"#   cfgA -> '{literalA}'   {SwpropsConfigB} -> '{literalB}'   " +
                $"docB -> '{literalDocB}'");
            Console.WriteLine($"# expected merged names: cfgA={namesCfg1.Count} " +
                $"{SwpropsConfigB}={namesCfgB.Count} docB={namesDocB.Count} — " +
                $"cfgA order [{string.Join(", ", namesCfg1)}]");
            Console.WriteLine($"# module sketch for '{literalA}': N={sketchA.Info.CellCount} " +
                $"contours={sketchA.Info.ContourCount} segs={sketchA.Info.SegmentCount} " +
                $"sig={sigA.Substring(0, 12)}…");
            Console.WriteLine($"# module sketch for '{literalB}': N={sketchB.Info.CellCount} " +
                $"contours={sketchB.Info.ContourCount} segs={sketchB.Info.SegmentCount} " +
                $"sig={sigB.Substring(0, 12)}…");
            Console.WriteLine($"# module sketch for '{literalDocB}': N={sketchDocB.Info.CellCount} " +
                $"contours={sketchDocB.Info.ContourCount} segs={sketchDocB.Info.SegmentCount} " +
                $"sig={sigDocB.Substring(0, 12)}…");
            Console.WriteLine("# expected verdicts: doc value verbatim; QrShared -> the " +
                "active configuration's value (config shadows doc); QrSpaced -> whitespace " +
                "verbatim; QrEmpty -> EMPTY; QrNoSuchProp -> MISSING; no-doc -> NODOC; " +
                "missing/empty templates refuse the create before any host mutation.");

            if (System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: the swprops gates require SolidWorks " +
                    "closed — a SLDWORKS.exe process is already running. Refusing to " +
                    "attach; the probe never mutates or kills an occupied seat.");
                return 1;
            }

            ISldWorks? sw = null;
            IModelDoc2? doc = null;
            IModelDoc2? docB = null;
            IDisposable? watcherBox = null;
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
                Console.WriteLine($"# SolidWorks revision {sw.RevisionNumber()} — " +
                    $"invisible spawned instance, {System.Environment.OSVersion}");

                var probe = TryGetProbe(sw);
                bool hasSurface = ProbeHasSwpropsSurface(probe);
                if (!hasSurface)
                {
                    try { sw.UnloadAddIn(dllPath); } catch { }
                    int loadResult = sw.LoadAddIn(dllPath);
                    Check(loadResult == 0, "LoadAddIn", $"returned {loadResult} for {dllPath}");
                    probe = TryGetProbe(sw);
                    hasSurface = ProbeHasSwpropsSurface(probe);
                }
                Check(hasSurface,
                    "GetAddInObject returns the probe interface with the swprops surface");
                if (probe == null || !hasSurface) return 1;

                // A harness-side watcher counts the host notifications the
                // add-in's own watcher consumes — the event-driven evidence
                // for the doc/config-switch and doc-destroy gates (the
                // `units` precedent: LoadFrom the deployed add-in, hook its
                // Changed event).
                int hostEvents = 0;
                try
                {
                    var addin = System.Reflection.Assembly.LoadFrom(dllPath);
                    var watcher = (IHostStateWatcher)Activator.CreateInstance(
                        addin.GetType("SolidWorksQrAddIn.Host.SwHostStateWatcher", true)!,
                        new object[] { sw });
                    watcherBox = watcher as IDisposable;
                    watcher.Changed += () => { Interlocked.Increment(ref hostEvents); };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# harness-side host watcher unavailable: " +
                        $"{ex.GetType().Name}: {ex.Message} — event counting degrades " +
                        "to echo polling");
                }

                string template = sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
                Check(File.Exists(template), "default part template exists", template);
                if (!File.Exists(template)) return 1;

                // ================= GATE 1 — fixture writes =================
                Console.WriteLine("# ---- gate 1: fixture part + property writes ----");
                doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "NewDocument produces a part", doc?.GetTitle() ?? "null");
                if (doc == null) return 1;
                string titleA = doc.GetTitle();

                string cfgName = "";
                try
                {
                    cfgName = ((doc.ConfigurationManager as IConfigurationManager)
                        ?.ActiveConfiguration as IConfiguration)?.Name ?? "";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# active-configuration read threw {ex.GetType().Name}");
                }
                Check(cfgName.Length > 0, "active configuration name read", $"'{cfgName}'");
                if (cfgName.Length == 0) return 1;

                var ext = doc.Extension as IModelDocExtension;
                Check(ext != null, "model-doc extension available");
                if (ext == null) return 1;

                foreach (var f in docFixtures)
                    WriteProp(ext, "", f, "doc");
                foreach (var f in cfg1Fixtures)
                    WriteProp(ext, cfgName, f, $"config '{cfgName}'");

                // The second configuration — a DIFFERENT value for the
                // shared 'Revision' name makes the switch gate meaningful.
                var cfgMgr = doc.ConfigurationManager as IConfigurationManager;
                IConfiguration? cfgB = null;
                try
                {
                    cfgB = cfgMgr?.AddConfiguration2(
                        SwpropsConfigB, "", "", 0, "", "", true) as IConfiguration;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# AddConfiguration2 threw {ex.GetType().Name}: {ex.Message}");
                }
                Check(cfgB != null, $"second configuration '{SwpropsConfigB}' added");
                foreach (var f in cfgBFixtures)
                    WriteProp(ext, SwpropsConfigB, f, $"config '{SwpropsConfigB}'");

                // A configuration created by AddConfiguration2 INHERITS the
                // active configuration's specific properties — verified
                // live: QrCfgB opens with cfgA's 'QrCfgProp' already in its
                // store. The fixture deletes the inherited name so the
                // post-switch name set differs cleanly — the delete itself
                // exercises the seam's write path. A NotPresent return just
                // means no clone happened (template-dependent) — also fine.
                int delRc = -8;
                try
                {
                    delRc = ext.CustomPropertyManager[SwpropsConfigB].Delete2("QrCfgProp");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# Delete2 'QrCfgProp' on '{SwpropsConfigB}' " +
                        $"threw {ex.GetType().Name}: {ex.Message}");
                }
                Console.WriteLine($"# note: AddConfiguration2 clones the active config's " +
                    $"specific properties — Delete2 'QrCfgProp' on '{SwpropsConfigB}' " +
                    $"-> rc={delRc} (0=deleted, 1=never inherited)");
                Check(delRc == 0 || delRc == 1,
                    "the inherited 'QrCfgProp' is absent from the second configuration",
                    $"rc={delRc}");

                // AddConfiguration2 may leave the new configuration active —
                // pin the seed configuration for gates 2-5.
                bool backToA = false;
                try { backToA = doc.ShowConfiguration2(cfgName); } catch { }
                Check(backToA, "the seed configuration is re-activated for the pane gates",
                    $"'{cfgName}'");
                string activeNow = "";
                try
                {
                    activeNow = ((doc.ConfigurationManager as IConfigurationManager)
                        ?.ActiveConfiguration as IConfiguration)?.Name ?? "";
                }
                catch { }
                Check(string.Equals(activeNow, cfgName, StringComparison.Ordinal),
                    "active configuration confirmed", $"'{activeNow}'");

                // ================= GATE 2 — enumerate =================
                Console.WriteLine("# ---- gate 2: SwPropertiesProbe enumerate ----");
                var list = probe.SwPropertiesProbe();
                var names = SwpropsNames(list);
                Check(list.StartsWith("SWPROPS kind=part"),
                    "enumerate reports the active part", list);
                Check(list.Contains($"config='{cfgName}'"),
                    "the active configuration is named in the echo", list);
                Check(namesCfg1.All(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)),
                    "every fixture name is enumerated",
                    $"got [{string.Join(", ", names)}]");
                Check(names.Count(n => string.Equals(n, "QrShared", StringComparison.OrdinalIgnoreCase)) == 1,
                    "the doc+config name is deduplicated",
                    $"got [{string.Join(", ", names)}]");
                // SolidWorks' precedence: the configuration store's names
                // precede every document-only name in the merged list.
                int lastCfg = IndexOfAny(names, cfg1Fixtures.Select(f => f.Name), last: true);
                int firstDocOnly = IndexOfAny(names,
                    docFixtures.Select(f => f.Name)
                        .Where(n => !cfg1Fixtures.Any(
                            c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))),
                    last: false);
                Check(firstDocOnly < 0 || lastCfg < firstDocOnly,
                    "configuration names precede document-only names",
                    $"lastCfg@{lastCfg} firstDocOnly@{firstDocOnly} — [{string.Join(", ", names)}]");
                Check(!names.Any(n => n.StartsWith("SW-", StringComparison.OrdinalIgnoreCase)),
                    "no SW-* built-ins leak into the enumeration",
                    $"got [{string.Join(", ", names)}]");

                // ================= GATE 3 — resolve =================
                Console.WriteLine("# ---- gate 3: SwPropertyResolveProbe ----");
                CheckResolve(probe, "PartNo", "SWPROP 'PartNo' RESOLVED 'PN-0042'");
                CheckResolve(probe, "Revision", "SWPROP 'Revision' RESOLVED 'REV-A'",
                    "the seed configuration's own value");
                CheckResolve(probe, "QrDocProp", "SWPROP 'QrDocProp' RESOLVED 'DOC-VALUE-1'");
                CheckResolve(probe, "QrCfgProp", "SWPROP 'QrCfgProp' RESOLVED 'CFG-VALUE-1'");
                CheckResolve(probe, "QrShared", "SWPROP 'QrShared' RESOLVED 'cfg-level'",
                    "the configuration store shadows the document value");
                CheckResolve(probe, "Qr Custom Label", "SWPROP 'Qr Custom Label' RESOLVED 'LBL-7'",
                    "a name containing spaces resolves verbatim");
                CheckResolve(probe, "QrSpaced", "SWPROP 'QrSpaced' RESOLVED '   '",
                    "a whitespace-only value resolves, not empty");
                CheckResolve(probe, "QrEmpty", "SWPROP 'QrEmpty' EMPTY",
                    "present-but-empty is its own verdict");
                CheckResolve(probe, "QrNoSuchProp", "SWPROP 'QrNoSuchProp' MISSING",
                    "an absent name is MISSING, not EMPTY");
                CheckResolve(probe, "QrCfgOnlyB", "SWPROP 'QrCfgOnlyB' MISSING",
                    "the second configuration's own name is invisible while inactive");

                // ================= GATE 4 — the pane path =================
                Console.WriteLine("# ---- gate 4: pane path — preset, template, echoes ----");
                string opened = probe.OpenQrWindowProbe();
                Check(opened.Contains("open=True") && opened.Contains("windows=1"),
                    "pane opened through the command entry — the surface the gate drives", opened);
                var pick = probe.SetQrContentTypeProbe("swprops");
                Check(pick.StartsWith("INVALID") && pick.Contains("EmptyContent"),
                    "the SW Properties type opens invalid — the empty template composes " +
                    "to nothing", pick);
                var st0 = probe.WindowStateProbe();
                Check(st0.Contains("preset=swprops"), "preset=swprops selected", st0);
                Check(ViewField(st0, "viewTemplateNames") == names.Count.ToString(),
                    "the insert dropdown echoes exactly the enumerated names",
                    $"viewTemplateNames={ViewField(st0, "viewTemplateNames")} " +
                    $"vs enumerated {names.Count}");

                var set = probe.SetQrPresetFieldProbe("template", gateTemplate);
                Check(set.StartsWith("VALID") && set.Contains($"cells={sketchA.Info.CellCount}"),
                    "the template resolves the fixture properties into a valid request — " +
                    $"live verdict matches the module (cells={sketchA.Info.CellCount})", set);
                var st1 = probe.WindowStateProbe();
                Check(ViewResolved(st1) == literalA,
                    "the Resolved row shows the resolved literal", st1);
                var picA = probe.PreviewSignatureProbe();
                Check(picA.Contains($"sha={sigA}"),
                    "the live preview renders the module's picture of the resolved literal",
                    picA);

                // ================= GATE 5 — create encodes the literal =================
                Console.WriteLine("# ---- gate 5: create — encodes exactly the resolved literal ----");
                var mgr = (ISketchManager)doc.SketchManager;
                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane selected for the swprops create");
                var created = probe.CreateQrFromWindowProbe();
                Check(created.StartsWith("OK"),
                    "the swprops create succeeds on the fixture part", created);
                string skA = created.StartsWith("OK") && created.Split(' ').Length > 1
                    ? created.Split(' ')[1] : "";
                // mgr.ActiveSketch can come back null for the add-in's open
                // create (circlediag D2) — the named feature's
                // GetSpecificFeature2 exposes the same in-edit sketch.
                var skObj = (mgr.ActiveSketch as ISketch)
                    ?? (FindSketchFeature(doc, skA)?.GetSpecificFeature2() as ISketch);
                Check(skObj != null, "the created sketch is open/readable", skA);
                if (skObj != null)
                {
                    // The module-identical proof: the def census and the
                    // exploded 1:1 contour match against the module's sketch
                    // for the RESOLVED literal — the created QR encodes
                    // exactly it, verified not assumed.
                    VerifyXsettingsBlock(skObj, sketchA, "swprops@cfgA");
                    mgr.InsertSketch(false);
                    Check(mgr.ActiveSketch == null, "the swprops sketch exits edit mode");
                    VerifyExplodedModuleIdentical(doc, ext, mgr, skA, sketchA, "swprops@cfgA");
                }

                // ================= GATE 6 — configuration switch =================
                Console.WriteLine("# ---- gate 6: configuration switch re-resolves ----");
                int evCfg = hostEvents;
                bool cfgSwitched = false;
                try { cfgSwitched = doc.ShowConfiguration2(SwpropsConfigB); }
                catch (Exception ex)
                {
                    Console.WriteLine($"# ShowConfiguration2 threw {ex.GetType().Name}: {ex.Message}");
                }
                Check(cfgSwitched, $"the fixture part switches to '{SwpropsConfigB}'");
                Thread.Sleep(400);   // let the post-notify land
                Check(hostEvents > evCfg,
                    "the config switch fires a host notification (ActiveConfigChangePostNotify)",
                    $"events={hostEvents - evCfg}");

                var listB = probe.SwPropertiesProbe();
                var namesB = SwpropsNames(listB);
                Check(listB.Contains($"config='{SwpropsConfigB}'"),
                    "enumerate now reports the second configuration", listB);
                Check(namesCfgB.All(n => namesB.Contains(n, StringComparer.OrdinalIgnoreCase)) &&
                      !namesB.Contains("QrCfgProp", StringComparer.OrdinalIgnoreCase),
                    "the name list re-resolves for the new configuration — " +
                    "the cfgA-only name is gone, the cfgB-only name appears",
                    $"got [{string.Join(", ", namesB)}]");
                CheckResolve(probe, "Revision", "SWPROP 'Revision' RESOLVED 'REV-B'",
                    "the shared name now reads the second configuration's value");
                CheckResolve(probe, "QrShared", "SWPROP 'QrShared' RESOLVED 'cfg-B-level'",
                    "the shadowed name follows the active configuration");
                CheckResolve(probe, "QrCfgProp", "SWPROP 'QrCfgProp' MISSING",
                    "the first configuration's own name is gone");
                CheckResolve(probe, "QrCfgOnlyB", "SWPROP 'QrCfgOnlyB' RESOLVED 'CFG-B-ONLY'",
                    "the second configuration's own name resolves");

                var stB = EnsureResolvedEcho(probe, gateTemplate, literalB);
                Check(ViewResolved(stB) == literalB,
                    "the Resolved row re-resolves to the new configuration's literal", stB);
                Check(ViewField(stB, "viewTemplateNames") == namesB.Count.ToString(),
                    "the insert dropdown re-resolves to the new configuration's names",
                    $"viewTemplateNames={ViewField(stB, "viewTemplateNames")} " +
                    $"vs enumerated {namesB.Count}");
                var picB = probe.PreviewSignatureProbe();
                Check(picB.Contains($"sha={sigB}"),
                    "the preview re-rendered the new literal — no stale picture", picB);

                // A second create on the switched configuration must encode
                // literalB — the decisive no-stale-value proof. REV-A/REV-B
                // share length and matrix size, so the exploded-contour 1:1
                // route (positions, not just counts) is what discriminates.
                doc.ClearSelection2(true);
                Check(ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                        (int)swSelectOption_e.swSelectOptionDefault),
                    "Front Plane re-selected for the cfgB create");
                var createdB = probe.CreateQrFromWindowProbe();
                Check(createdB.StartsWith("OK"),
                    "the cfgB create succeeds — encoding the re-resolved literal", createdB);
                string skB = createdB.StartsWith("OK") && createdB.Split(' ').Length > 1
                    ? createdB.Split(' ')[1] : "";
                var skObjB = (mgr.ActiveSketch as ISketch)
                    ?? (FindSketchFeature(doc, skB)?.GetSpecificFeature2() as ISketch);
                Check(skObjB != null, "the cfgB sketch is open/readable", skB);
                if (skObjB != null)
                {
                    VerifyXsettingsBlock(skObjB, sketchB, "swprops@cfgB");
                    mgr.InsertSketch(false);
                    Check(mgr.ActiveSketch == null, "the cfgB sketch exits edit mode");
                    VerifyExplodedModuleIdentical(doc, ext, mgr, skB, sketchB, "swprops@cfgB");
                }

                // ================= GATE 7 — document switch =================
                Console.WriteLine("# ---- gate 7: document switch re-resolves ----");
                int evDoc = hostEvents;
                docB = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(docB != null, "second part document opens as the active document",
                    docB?.GetTitle() ?? "null");
                string titleB = docB?.GetTitle() ?? "";
                Thread.Sleep(400);
                Check(hostEvents > evDoc,
                    "the document switch fires a host notification (ActiveDocChangeNotify)",
                    $"events={hostEvents - evDoc}");
                var extB = docB?.Extension as IModelDocExtension;
                if (extB != null)
                    foreach (var f in docBFixtures)
                        WriteProp(extB, "", f, "docB");

                var listD2 = probe.SwPropertiesProbe();
                var namesD2 = SwpropsNames(listD2);
                Check(listD2.Contains($"doc='{titleB}'"),
                    "enumerate now reports the SECOND document", listD2);
                Check(namesDocB.All(n => namesD2.Contains(n, StringComparer.OrdinalIgnoreCase)),
                    "the enumerated names are the second document's own",
                    $"got [{string.Join(", ", namesD2)}]");
                Check(!namesD2.Any(n => string.Equals(n, "QrCfgProp", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(n, "QrDocProp", StringComparison.OrdinalIgnoreCase)),
                    "no fixture-part names leak across the document switch",
                    $"got [{string.Join(", ", namesD2)}]");
                CheckResolve(probe, "PartNo", "SWPROP 'PartNo' RESOLVED 'PN-7777'",
                    "the template's reference now reads the second document");

                var stD2 = EnsureResolvedEcho(probe, gateTemplate, literalDocB);
                Check(ViewResolved(stD2) == literalDocB,
                    "the Resolved row re-resolves to the second document's literal", stD2);
                var picD2 = probe.PreviewSignatureProbe();
                Check(picD2.Contains($"sha={sigDocB}"),
                    "the preview renders the second document's literal", picD2);

                // Back to the fixture part — the resolution must follow.
                int evBack = hostEvents;
                int actErr = 0;
                try { sw.IActivateDoc3(titleA, false, ref actErr); }
                catch (Exception ex) { Console.WriteLine($"# IActivateDoc3 threw {ex.GetType().Name}"); }
                Check((sw.IActiveDoc2 as IModelDoc2)?.GetTitle() == titleA,
                    "the fixture document reactivates", $"actErr={actErr}");
                Thread.Sleep(400);
                Check(hostEvents > evBack,
                    "reactivating the fixture fires a host notification",
                    $"events={hostEvents - evBack}");
                var stBack = EnsureResolvedEcho(probe, gateTemplate, literalB);
                Check(ViewResolved(stBack) == literalB,
                    "switching back restores the fixture part's resolution", stBack);
                var picBack = probe.PreviewSignatureProbe();
                Check(picBack.Contains($"sha={sigB}"),
                    "the preview returns to the fixture literal", picBack);

                try { sw.CloseDoc(titleB); } catch { }
                docB = null;

                // ================= GATE 8 — missing/empty refusal =================
                Console.WriteLine("# ---- gate 8: missing/empty reference refused before mutation ----");
                int featsRef = FindSketchFeatureNames(doc).Count;
                var bad1 = probe.SetQrPresetFieldProbe("template", missingTemplate);
                Check(bad1.StartsWith("INVALID") && bad1.Contains("InvalidPayload") &&
                      bad1.Contains("$PRP:\"QrNoSuchProp\""),
                    "a missing reference fails the request naming it", bad1);
                var stBad = probe.WindowStateProbe();
                Check(ViewResolved(stBad).StartsWith("ERR:") &&
                      stBad.Contains("$PRP:\"QrNoSuchProp\""),
                    "the Resolved row reports the failure reason", stBad);
                Check(probe.PreviewSignatureProbe() == "NO-PICTURE",
                    "no picture while the template cannot resolve",
                    probe.PreviewSignatureProbe());
                var refuse1 = probe.CreateQrFromWindowProbe();
                Check(refuse1.StartsWith("ERROR") && refuse1.Contains("QrNoSuchProp"),
                    "create is refused BEFORE mutation — the error names the missing reference",
                    refuse1);
                Check(FindSketchFeatureNames(doc).Count == featsRef && mgr.ActiveSketch == null,
                    "the refused create touched neither the feature tree nor the sketch-edit state",
                    $"sketches=[{string.Join(", ", FindSketchFeatureNames(doc))}]");

                var bad2 = probe.SetQrPresetFieldProbe("template", emptyTemplate);
                Check(bad2.StartsWith("INVALID") && bad2.Contains("$PRP:\"QrEmpty\"") &&
                      bad2.Contains("empty"),
                    "a present-but-empty reference fails distinctly from missing", bad2);
                var refuse2 = probe.CreateQrFromWindowProbe();
                Check(refuse2.StartsWith("ERROR") && refuse2.Contains("QrEmpty"),
                    "create refuses the empty reference before mutation", refuse2);
                Check(FindSketchFeatureNames(doc).Count == featsRef && mgr.ActiveSketch == null,
                    "the empty-reference refusal touched neither tree nor sketch state",
                    $"sketches=[{string.Join(", ", FindSketchFeatureNames(doc))}]");

                var good = probe.SetQrPresetFieldProbe("template", gateTemplate);
                Check(good.StartsWith("VALID"),
                    "the valid template restores the request", good);
                Check(ViewResolved(probe.WindowStateProbe()) == literalB,
                    "the Resolved row recovers the fixture literal", good);

                // ================= GATE 9 — no document =================
                Console.WriteLine("# ---- gate 9: no document ----");
                int evClose = hostEvents;
                try { sw.CloseDoc(titleA); } catch { }
                doc = null;
                Thread.Sleep(400);   // let the post-destroy notify land
                Check(hostEvents > evClose,
                    "the last document's close fires a host notification (DestroyNotify2 — " +
                    "no ActiveDocChangeNotify exists for this transition)",
                    $"events={hostEvents - evClose}");
                CheckResolve(probe, "QrDocProp", "SWPROP 'QrDocProp' NODOC",
                    "with no open document every reference is NODOC");
                var emptyList = probe.SwPropertiesProbe();
                Check(emptyList.Contains("names=0") && !emptyList.StartsWith("SWPROPS kind=part"),
                    "with no open document the name list is empty", emptyList);
                var stNone = EnsureResolvedEcho(probe, gateTemplate, "ERR:");
                Check(ViewResolved(stNone).StartsWith("ERR:") &&
                      stNone.Contains("active part document"),
                    "the Resolved row explains the no-document failure", stNone);
            }
            finally
            {
                try { watcherBox?.Dispose(); } catch { }
                try { if (docB != null) sw?.CloseDoc(docB.GetTitle()); } catch { }
                TeardownSpawned(sw);
            }

            Console.WriteLine(_failures == 0
                ? "ALL CHECKS PASSED"
                : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Writes one fixture property through CustomPropertyManager.Add3 —
        /// store "" = document level, else the named configuration's
        /// specific store — and asserts swCustomInfoAddResult_AddedOrChanged.
        /// </summary>
        private static void WriteProp(IModelDocExtension ext, string store,
            PropFixture fixture, string storeLabel)
        {
            int rc = -8;
            try
            {
                rc = ext.CustomPropertyManager[store]
                    .Add3(fixture.Name,
                        (int)swCustomInfoType_e.swCustomInfoText,
                        fixture.Value,
                        (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# Add3 '{fixture.Name}' threw {ex.GetType().Name}: {ex.Message}");
            }
            Check(rc == (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged,
                $"Add3 {storeLabel} '{fixture.Name}'", $"rc={rc}");
        }

        /// <summary>The quoted-name list from a SWPROPS line, in reported order.</summary>
        private static List<string> SwpropsNames(string probeLine)
        {
            var names = new List<string>();
            if (probeLine == null) return names;
            int marker = probeLine.IndexOf("names=", StringComparison.Ordinal);
            if (marker < 0) return names;
            foreach (Match m in Regex.Matches(probeLine.Substring(marker), "'([^']*)'"))
                names.Add(m.Groups[1].Value);
            return names;
        }

        /// <summary>One "key=value" field of a STATE echo — the value runs to the next space.</summary>
        private static string ViewField(string state, string key)
        {
            int i = state.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return "";
            int start = i + key.Length + 1;
            int end = state.IndexOf(' ', start);
            return end < 0 ? state.Substring(start) : state.Substring(start, end - start);
        }

        /// <summary>
        /// The Resolved row's text — the LAST field of the STATE echo, so the
        /// value runs to end-of-line (an ERR: reason contains spaces).
        /// </summary>
        private static string ViewResolved(string state)
        {
            const string key = "viewResolved=";
            int i = state.IndexOf(key, StringComparison.Ordinal);
            return i < 0 ? "" : state.Substring(i + key.Length).TrimEnd();
        }

        /// <summary>
        /// Waits for the pane's Resolved row to report the expected prefix —
        /// the host notifications re-resolve a Template-kind payload through
        /// the session's own watcher path. Falls back to a field rewrite
        /// (which re-resolves through the same ComposePreset path) when the
        /// event does not land headlessly — a LIMITATION line records which
        /// route did the work, so the record is honest either way.
        /// </summary>
        private static string EnsureResolvedEcho(
            IAddInProbe probe, string template, string expectedPrefix)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            var state = probe.WindowStateProbe();
            while (!ViewResolved(state).StartsWith(expectedPrefix) &&
                   DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                state = probe.WindowStateProbe();
            }
            if (!ViewResolved(state).StartsWith(expectedPrefix))
            {
                Limit("host-event re-resolution of the template did not land within 3s",
                    $"still showing '{ViewResolved(state)}' — forcing the pane path " +
                    "through a template field rewrite");
                probe.SetQrPresetFieldProbe("template", template);
                state = probe.WindowStateProbe();
            }
            return state;
        }

        /// <summary>
        /// The swprops discriminator: only a build carrying the #44 probe
        /// surface answers SwPropertiesProbe — a stale registered build
        /// either lacks the member (throws) or answers without SWPROPS.
        /// </summary>
        private static bool ProbeHasSwpropsSurface(IAddInProbe? probe)
        {
            if (probe == null) { Console.WriteLine("# no add-in object present"); return false; }
            try
            {
                string s = probe.SwPropertiesProbe();
                Console.WriteLine($"# discriminator SwPropertiesProbe -> {s}");
                return s.StartsWith("SWPROPS") || s.StartsWith("ERROR");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# discriminator SwPropertiesProbe -> " +
                    $"{ex.GetType().Name} (the answering build predates spec-21)");
                return false;
            }
        }

        /// <summary>
        /// Index of the first (or, with <paramref name="last"/>, the last)
        /// hay entry matching any needle, ordinal-ignore-case — -1 when
        /// none matches.
        /// </summary>
        private static int IndexOfAny(
            IReadOnlyList<string> hay, IEnumerable<string> needles, bool last)
        {
            var set = new HashSet<string>(needles, StringComparer.OrdinalIgnoreCase);
            int hit = -1;
            for (int k = 0; k < hay.Count; k++)
                if (set.Contains(hay[k]))
                {
                    hit = k;
                    if (!last) break;
                }
            return hit;
        }

        /// <summary>Asserts one resolve verdict; the echo IS the evidence.</summary>
        private static void CheckResolve(IAddInProbe probe, string name, string expected,
            string note = "")
        {
            var got = probe.SwPropertyResolveProbe(name);
            Check(string.Equals(got, expected, StringComparison.Ordinal),
                $"resolve '{name}'{(note.Length > 0 ? " — " + note : "")}",
                $"expected [{expected}] got [{got}]");
        }
    }
}
