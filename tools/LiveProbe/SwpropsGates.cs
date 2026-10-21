using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorksQrAddIn.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Ticket-#44 live gates for the spec-#21 part-property source seam —
    /// the harness writes a known property fixture into a session-free
    /// part (document-level + configuration-specific + deliberately
    /// empty + whitespace values via CustomPropertyManager.Add3), then
    /// drives the add-in's SwPropertiesProbe / SwPropertyResolveProbe
    /// surfaces and compares verdicts against the fixture. The fixture
    /// plan prints BEFORE the SLDWORKS.exe refusal so a blocked run still
    /// shows what the live half would check.
    ///
    /// Gates (each prints PASS / FAIL / LIMIT lines; exit code 0 only when
    /// nothing failed):
    ///   fixture     — every Add3 write returns AddedOrChanged;
    ///   enumerate   — SwPropertiesProbe reports the active part + config
    ///                 and lists every fixture name, deduplicated, with
    ///                 every configuration-store name ahead of every
    ///                 document-only name (SolidWorks' own precedence);
    ///   resolve     — doc-level and config values verbatim, the
    ///                 config-shadowed name resolving to the config value,
    ///                 whitespace resolved verbatim, EMPTY distinct from
    ///                 MISSING;
    ///   nodoc       — with no document open, resolve reports NODOC and
    ///                 enumerate reports names=0.
    /// </summary>
    public static partial class Program
    {
        /// <summary>One fixture property: which store, name, stored value.</summary>
        private sealed class PropFixture
        {
            public bool InConfig;
            public string Name = "";
            public string Value = "";
        }

        private static int SwpropsMain(string[] args)
        {
            string deployDir = args.Length > 0 ? args[0] : Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksQrAddIn");
            string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
            string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

            // ================= module-side fixture plan =================
            // Written in this order; the merged expectation below applies
            // the same config-store-first precedence the seam documents.
            var fixtures = new List<PropFixture>
            {
                new PropFixture { InConfig = false, Name = "QrDocProp",     Value = "DOC-VALUE-1" },
                new PropFixture { InConfig = false, Name = "QrShared",      Value = "doc-level" },
                new PropFixture { InConfig = false, Name = "QrEmpty",       Value = "" },
                new PropFixture { InConfig = false, Name = "QrSpaced",      Value = "   " },
                new PropFixture { InConfig = false, Name = "Qr Custom Label", Value = "LBL-7" },
                new PropFixture { InConfig = true,  Name = "QrCfgProp",     Value = "CFG-VALUE-1" },
                new PropFixture { InConfig = true,  Name = "QrShared",      Value = "cfg-level" },
            };
            var cfgNames = fixtures.Where(f => f.InConfig).Select(f => f.Name).ToList();
            var docNames = fixtures.Where(f => !f.InConfig).Select(f => f.Name).ToList();
            // The resolvable set: every fixture name once (QrShared dedups).
            var expectedNames = new HashSet<string>(
                cfgNames.Concat(docNames), StringComparer.OrdinalIgnoreCase);

            Console.WriteLine("# ticket-#44 part-property-source live gates (spec #21)");
            Console.WriteLine("# fixture plan — harness writes via CustomPropertyManager.Add3:");
            foreach (var f in fixtures)
                Console.WriteLine($"#   {(f.InConfig ? "config" : "doc")} store: " +
                    $"'{f.Name}' = '{f.Value}'");
            Console.WriteLine($"# expected resolvable names: {string.Join(", ", expectedNames)}");
            Console.WriteLine("# expected verdicts: doc value verbatim; QrShared -> 'cfg-level' " +
                "(config shadows doc); QrSpaced -> whitespace verbatim; " +
                "QrEmpty -> EMPTY; QrNoSuchProp -> MISSING; no-doc -> NODOC");

            if (System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length != 0)
            {
                Console.WriteLine("# FATAL: the swprops gates require SolidWorks " +
                    "closed — a SLDWORKS.exe process is already running. Refusing to " +
                    "attach; the probe never mutates or kills an occupied seat.");
                return 1;
            }

            ISldWorks? sw = null;
            IModelDoc2? doc = null;
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

                // ================= GATE 1 — fixture writes =================
                Console.WriteLine("# ---- gate 1: fixture part + property writes ----");
                doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                Check(doc != null, "NewDocument produces a part", doc?.GetTitle() ?? "null");
                if (doc == null) return 1;

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

                foreach (var f in fixtures)
                {
                    int rc = -8;
                    try
                    {
                        rc = ext.CustomPropertyManager[f.InConfig ? cfgName : ""]
                            .Add3(f.Name,
                                (int)swCustomInfoType_e.swCustomInfoText,
                                f.Value,
                                (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"# Add3 '{f.Name}' threw {ex.GetType().Name}: {ex.Message}");
                    }
                    Check(rc == (int)swCustomInfoAddResult_e.swCustomInfoAddResult_AddedOrChanged,
                        $"Add3 {(f.InConfig ? "config" : "doc")} '{f.Name}'", $"rc={rc}");
                }

                // ================= GATE 2 — enumerate =================
                Console.WriteLine("# ---- gate 2: SwPropertiesProbe enumerate ----");
                var list = probe.SwPropertiesProbe();
                var names = SwpropsNames(list);
                Check(list.StartsWith("SWPROPS kind=part"),
                    "enumerate reports the active part", list);
                Check(expectedNames.All(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)),
                    "every fixture name is enumerated",
                    $"got [{string.Join(", ", names)}]");
                Check(names.Count(n => string.Equals(n, "QrShared", StringComparison.OrdinalIgnoreCase)) == 1,
                    "the doc+config name is deduplicated",
                    $"got [{string.Join(", ", names)}]");
                // SolidWorks' precedence: the configuration store's names
                // precede every document-only name in the merged list.
                int lastCfg = LastIndexOfAny(names, cfgNames);
                int firstDocOnly = FirstIndexOfAny(names,
                    docNames.Where(n => !cfgNames.Contains(n, StringComparer.OrdinalIgnoreCase)));
                Check(firstDocOnly < 0 || lastCfg < firstDocOnly,
                    "configuration names precede document-only names",
                    $"lastCfg@{lastCfg} firstDocOnly@{firstDocOnly} — [{string.Join(", ", names)}]");
                Check(!names.Any(n => n.StartsWith("SW-", StringComparison.OrdinalIgnoreCase)),
                    "no SW-* built-ins leak into the enumeration",
                    $"got [{string.Join(", ", names)}]");

                // ================= GATE 3 — resolve =================
                Console.WriteLine("# ---- gate 3: SwPropertyResolveProbe ----");
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

                // ================= GATE 4 — no document =================
                Console.WriteLine("# ---- gate 4: no document ----");
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
                doc = null;
                CheckResolve(probe, "QrDocProp", "SWPROP 'QrDocProp' NODOC",
                    "with no open document every reference is NODOC");
                var emptyList = probe.SwPropertiesProbe();
                Check(emptyList.Contains("names=0") && !emptyList.StartsWith("SWPROPS kind=part"),
                    "with no open document the name list is empty", emptyList);
            }
            finally
            {
                TeardownSpawned(sw);
            }

            Console.WriteLine(_failures == 0
                ? "ALL CHECKS PASSED"
                : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
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

        private static int LastIndexOfAny(IReadOnlyList<string> hay, IEnumerable<string> needles)
        {
            int hit = -1;
            foreach (var n in needles)
            {
                int i = -1;
                for (int k = 0; k < hay.Count; k++)
                    if (string.Equals(hay[k], n, StringComparison.OrdinalIgnoreCase)) i = k;
                if (i > hit) hit = i;
            }
            return hit;
        }

        private static int FirstIndexOfAny(IReadOnlyList<string> hay, IEnumerable<string> needles)
        {
            int hit = -1;
            foreach (var n in needles)
            {
                int i = -1;
                for (int k = 0; k < hay.Count && i < 0; k++)
                    if (string.Equals(hay[k], n, StringComparison.OrdinalIgnoreCase)) i = k;
                if (i >= 0 && (hit < 0 || i < hit)) hit = i;
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
