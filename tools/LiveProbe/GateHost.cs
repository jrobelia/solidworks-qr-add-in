using System;
using System.Diagnostics;
using System.IO;
using SolidWorksQrAddIn.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// GateHost — the live-session ceremony every gate used to re-implement
    /// (~60 lines per mode), owned once here (ticket #61, spec #67):
    /// deploy-dir/ProgID argument parsing, the expectations-before-refusal
    /// ordering, the uniform occupied-seat refusal (a running SLDWORKS.exe
    /// is never a valid target — a spawned instance still shares per-user
    /// state with it; no gate opts out), launch + startup wait, the
    /// visible/open-documents refusal, probe acquisition (including the
    /// LoadAddIn retry and a per-gate capability predicate), teardown
    /// (restore the captured AddInsStartup flag, close documents, kill the
    /// spawned process by PID — never ExitApp, never taskkill /IM), and the
    /// pass/fail summary + exit code.
    ///
    /// A gate is a small <see cref="LiveMode"/> registration: a name, a
    /// category (gates vs spikes/diagnostics), an expectations print, an
    /// optional probe-surface predicate, and a <see cref="LiveMode.Run"/>
    /// check sequence driven against a <see cref="LiveSession"/>. The
    /// registry is Main's dispatch table — `list` prints it without
    /// touching COM, hosted rows run the ceremony here, and unmigrated
    /// modes keep their <see cref="LiveMode.LegacyMain"/> bodies untouched.
    /// </summary>
    public static partial class Program
    {
        /// <summary>
        /// The bucket a mode reports under in `list` — `gates` are the
        /// pass/fail verdict modes (the *-GATES.md records); `spikes` and
        /// `diagnostics` produce evidence rather than verdicts
        /// (GLOSSARY.md).
        /// </summary>
        private enum GateCategory { Gate, Spike, Diagnostic }

        /// <summary>
        /// One registry row. Hosted rows carry <see cref="Run"/> (the gate
        /// check sequence); legacy rows carry <see cref="LegacyMain"/> and
        /// dispatch to the unmigrated body verbatim.
        /// </summary>
        private sealed class LiveMode
        {
            /// <summary>
            /// The dispatch verb (`circledims`). The default-suite row keeps
            /// it empty — it runs when no verb matches, never by lookup.
            /// </summary>
            public string Name { get; set; } = "";
            public GateCategory Category { get; set; }
            /// <summary>The one-line blurb `list` prints beside the name.</summary>
            public string Summary { get; set; } = "";
            /// <summary>Printed as `# {SummaryPass}` when a hosted run ends clean.</summary>
            public string SummaryPass { get; set; } = "";
            /// <summary>
            /// Prints the module-side expectations BEFORE the occupied-seat
            /// refusal — a blocked run still shows what the live half would
            /// check. Returning false aborts before any SolidWorks contact
            /// (unsound expectations).
            /// </summary>
            public Func<bool>? Expectations { get; set; }
            /// <summary>
            /// The per-gate probe-surface capability predicate — null means
            /// "any probe object answers". A failing predicate drives the
            /// unload-stale + LoadAddIn retry, the same way `xsettings`,
            /// `swprops` and `ringsplit` re-acquire a stale registered build.
            /// </summary>
            public Func<IAddInProbe?, bool>? ProbeSurface { get; set; }
            /// <summary>The Check name for the probe-acquisition verdict.</summary>
            public string ProbeCheckName { get; set; } =
                "GetAddInObject returns the probe interface";
            /// <summary>
            /// The hosted check sequence — return 0 to let the host print
            /// the pass/fail summary, non-zero to abort immediately (the
            /// legacy `return 1` early-outs).
            /// </summary>
            public Func<LiveSession, int>? Run { get; set; }
            /// <summary>The unmigrated mode body — dispatched verbatim.</summary>
            public Func<string[], int>? LegacyMain { get; set; }
            /// <summary>true when the mode runs the GateHost ceremony.</summary>
            public bool Hosted => Run != null;
            /// <summary>The probe verdict the capability gate applies.</summary>
            public bool SurfaceOk(IAddInProbe? probe) =>
                ProbeSurface?.Invoke(probe) ?? (probe != null);
        }

        /// <summary>
        /// The context a hosted gate's check sequence runs against: the
        /// <see cref="ISldWorks"/> handle, the late-bound 16-member
        /// <see cref="IAddInProbe"/> surface, the deploy dir, the
        /// Check/failure tally, and the part-document helpers.
        /// </summary>
        private sealed class LiveSession
        {
            public LiveSession(ISldWorks sw, IAddInProbe probe,
                string deployDir, string dllPath)
            {
                Sw = sw;
                Probe = probe;
                DeployDir = deployDir;
                DllPath = dllPath;
            }

            public ISldWorks Sw { get; }
            public IAddInProbe Probe { get; }
            public string DeployDir { get; }
            /// <summary>deployDir + the add-in dll — the LoadAddIn target.</summary>
            public string DllPath { get; }

            /// <summary>The run's failure tally — the host summary reads it.</summary>
            public int Failures => Program._failures;

            /// <summary>One PASS/FAIL line; failures accumulate into the tally.</summary>
            public void Check(bool cond, string name, string detail = "") =>
                Program.Check(cond, name, detail);

            private string? _partTemplate;

            /// <summary>
            /// The configured default part template — NewDocument needs an
            /// explicit path; an empty one pops a template dialog that hangs
            /// a headless run.
            /// </summary>
            public string DefaultPartTemplate =>
                _partTemplate ??= Sw.GetUserPreferenceStringValue(
                    (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);

            /// <summary>A fresh part document on the default template.</summary>
            public IModelDoc2? NewPartDocument() =>
                Sw.NewDocument(DefaultPartTemplate, 0, 0, 0) as IModelDoc2;

            /// <summary>
            /// Clears the selection, then selects Front Plane as the
            /// insertion support — the pair every part-document gate opens
            /// with.
            /// </summary>
            public bool SelectFrontPlane(IModelDoc2 doc)
            {
                doc.ClearSelection2(true);
                return ((IModelDocExtension)doc.Extension).SelectByID2(
                    "Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
            }
        }

        /// <summary>
        /// The ceremony owner: <see cref="GateHost.Run"/> drives one hosted
        /// mode end-to-end; <see cref="GateHost.ListModes"/> prints the
        /// registry with no COM contact.
        /// </summary>
        private static class GateHost
        {
            /// <summary>
            /// `LiveProbe.exe list` — prints the registry (gates vs
            /// spikes/diagnostics, hosted vs legacy). Static metadata only:
            /// it must never touch COM.
            /// </summary>
            public static int ListModes()
            {
                int hosted = 0;
                foreach (var m in Modes)
                    if (m.Hosted) hosted++;
                Console.WriteLine($"# LiveProbe mode registry — {Modes.Length} modes " +
                    $"({hosted} hosted by GateHost, {Modes.Length - hosted} legacy)");
                PrintGroup("gates", GateCategory.Gate);
                PrintGroup("spikes", GateCategory.Spike);
                PrintGroup("diagnostics", GateCategory.Diagnostic);
                return 0;
            }

            private static void PrintGroup(string header, GateCategory category)
            {
                Console.WriteLine($"{header}:");
                foreach (var m in Modes)
                {
                    if (m.Category != category) continue;
                    string name = m.Name.Length > 0 ? m.Name : "(default)";
                    Console.WriteLine($"  {name,-14} " +
                        $"{(m.Hosted ? "hosted" : "legacy"),-8} {m.Summary}");
                }
            }

            /// <summary>
            /// Runs one hosted mode through the live-session ceremony:
            /// expectations print → occupied-seat refusal → launch →
            /// startup wait → visible/open-docs refusal → probe acquisition
            /// → the gate's Run(session) → pass/fail summary → teardown.
            /// </summary>
            public static int Run(LiveMode mode, string[] args)
            {
                string deployDir = args.Length > 0
                    ? args[0]
                    : Path.Combine(System.Environment.GetFolderPath(
                            System.Environment.SpecialFolder.LocalApplicationData),
                        "SolidWorksQrAddIn");
                string progId = args.Length > 1 ? args[1] : "SldWorks.Application.31";
                string dllPath = Path.Combine(deployDir, "SolidWorksQrAddIn.dll");

                // Expectations BEFORE the refusal — a run blocked by an
                // occupied seat still shows what the live half would check.
                if (mode.Expectations != null && !mode.Expectations())
                    return 1;

                // The occupied-seat refusal is uniform — a running
                // SLDWORKS.exe is never a valid target, because a spawned
                // instance still shares per-user state (settings snapshot,
                // AddInsStartup flag, registration, license seat) with it.
                if (Process.GetProcessesByName("SLDWORKS").Length != 0)
                {
                    Console.WriteLine($"# FATAL: the {mode.Name} " +
                        $"{CategoryNoun(mode.Category)} requires SolidWorks closed — " +
                        "a SLDWORKS.exe process is already running. Refusing to " +
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
                    // A VISIBLE instance or one that already holds documents
                    // is somebody's interactive session — refuse to mutate it.
                    if (sw.Visible || openDocs > 0)
                    {
                        Console.WriteLine("# FATAL: session is visible or holds " +
                            "documents — refusing.");
                        return 1;
                    }
                    _spawnedFresh = true;

                    // Probe acquisition — an auto-loaded answering build may
                    // be stale, so the capability predicate decides; a
                    // failure retries through unload + LoadAddIn.
                    var probe = TryGetProbe(sw);
                    if (!mode.SurfaceOk(probe))
                    {
                        try { sw.UnloadAddIn(dllPath); } catch { }
                        int loadResult = sw.LoadAddIn(dllPath);
                        Check(loadResult == 0, "LoadAddIn",
                            $"returned {loadResult} for {dllPath}");
                        probe = TryGetProbe(sw);
                    }
                    Check(mode.SurfaceOk(probe), mode.ProbeCheckName,
                        probe == null ? "null" : "obtained");
                    if (probe == null || !mode.SurfaceOk(probe)) return 1;

                    var session = new LiveSession(sw, probe, deployDir, dllPath);
                    int gateExit = mode.Run!(session);
                    if (gateExit != 0) return gateExit;

                    Console.WriteLine(_failures == 0
                        ? $"# {mode.SummaryPass}"
                        : $"# {_failures} check(s) failed");
                    return _failures == 0 ? 0 : 1;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# FATAL: {ex.GetType().Name}: {ex.Message}");
                    return 1;
                }
                finally
                {
                    // Only ever tears down the instance this run spawned —
                    // restores the Start-Up flag, closes documents, kills by
                    // PID. Never ExitApp, never taskkill /IM.
                    TeardownSpawned(sw);
                }
            }

            private static string CategoryNoun(GateCategory category)
            {
                switch (category)
                {
                    case GateCategory.Spike: return "spike";
                    case GateCategory.Diagnostic: return "diagnostic";
                    default: return "gate";
                }
            }
        }

        // ============================ the registry ============================

        /// <summary>
        /// Main's dispatch table — every live mode exactly once. Hosted rows
        /// run the GateHost ceremony; legacy rows dispatch to the unmigrated
        /// `*Main` bodies. Migrating a mode is flipping its row from
        /// LegacyMain to Expectations/ProbeSurface/Run (#62, #63).
        /// </summary>
        private static readonly LiveMode[] Modes =
        {
            // The unnamed default suite — list-visible only; it runs when no
            // verb matches (FindMode never returns an empty-named row).
            new LiveMode
            {
                Category = GateCategory.Gate,
                Summary = "the retained tickets-2–6 suite — runs when no mode " +
                    "name matches",
                LegacyMain = DefaultMain,
            },

            // ---- hosted gates ----
            new LiveMode
            {
                Name = "circledims",
                Category = GateCategory.Gate,
                Summary = "ticket-#40 circle-mode dims/equations live gate",
                SummaryPass = "CIRCLEDIMS ALL PASSED",
                Expectations = () =>
                {
                    Console.WriteLine(
                        "# ticket-#40 circle-mode dims/equations live gate");
                    return true;
                },
                Run = CircleDimsRun,
            },

            // ---- legacy gates (unmigrated bodies) ----
            new LiveMode
            {
                Name = "circles",
                Category = GateCategory.Gate,
                Summary = "ticket-#42 circle-pattern live gates",
                LegacyMain = CircleGatesMain,
            },
            new LiveMode
            {
                Name = "xsettings",
                Category = GateCategory.Gate,
                Summary = "spec-11 extended-settings live gates",
                LegacyMain = XsettingsMain,
            },
            new LiveMode
            {
                Name = "swprops",
                Category = GateCategory.Gate,
                Summary = "ticket-#46 SW Properties end-to-end live gates",
                LegacyMain = SwpropsMain,
            },
            new LiveMode
            {
                Name = "ringsplit",
                Category = GateCategory.Gate,
                Summary = "ticket-#55 open-nested-rings live gates",
                LegacyMain = RingsplitMain,
            },
            new LiveMode
            {
                Name = "units",
                Category = GateCategory.Gate,
                Summary = "native unit-watcher + rendered-pane checks",
                LegacyMain = UnitsMain,
            },

            // ---- spikes / diagnostics (evidence harnesses) ----
            new LiveMode
            {
                Name = "perf",
                Category = GateCategory.Spike,
                Summary = "generation + native-insertion timings",
                LegacyMain = PerfMain,
            },
            new LiveMode
            {
                Name = "undoapi",
                Category = GateCategory.Spike,
                Summary = "SetApiUndoObject verification spike",
                LegacyMain = UndoApiMain,
            },
            new LiveMode
            {
                Name = "pattern",
                Category = GateCategory.Spike,
                Summary = "sketch-linear-pattern QR-dot spike (PAT<n>)",
                LegacyMain = PatternSpikeMain,
            },
            new LiveMode
            {
                Name = "circleladder",
                Category = GateCategory.Spike,
                Summary = "circle-mode instance-bound density ladder",
                LegacyMain = CircleLadderMain,
            },
            new LiveMode
            {
                Name = "circlediag",
                Category = GateCategory.Diagnostic,
                Summary = "circle-pipeline raw-evidence driver",
                LegacyMain = CircleDiagMain,
            },
        };

        /// <summary>
        /// Registry lookup — case-insensitive on the verb, exactly like the
        /// legacy dispatch chain. The unnamed default row never matches.
        /// </summary>
        private static LiveMode? FindMode(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var m in Modes)
                if (m.Name.Length > 0 &&
                    string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                    return m;
            return null;
        }

        // ===================== hosted gates' check sequences =====================

        /// <summary>
        /// The `circledims` check sequence (ticket #40) — the GateHost
        /// pilot: a real circle-mode create through the session, the
        /// PatternDimsProbe surface, a master-dim rescale via
        /// SetPatternDimProbe, then re-census. Identical checks to the
        /// retired CircleDimsMain; the occupied-seat refusal its docstring
        /// always claimed is now the uniform host policy.
        /// </summary>
        private static int CircleDimsRun(LiveSession session)
        {
            var probe = session.Probe;
            session.Check(probe.SetQrField("content", PayloadA).StartsWith("VALID"),
                "probe payload accepted", probe.WindowStateProbe());
            session.Check(probe.SetQrField("shape", "circle").StartsWith("VALID"),
                "circle shape accepted", probe.WindowStateProbe());
            session.Check(probe.SetQrField("param", "90").StartsWith("VALID"),
                "dot size 90 accepted", probe.SetQrField("param", "90"));

            string template = session.DefaultPartTemplate;
            session.Check(File.Exists(template), "default part template exists", template);
            var doc = session.NewPartDocument();
            session.Check(doc != null, "part document created", doc == null ? "null" : "ok");
            if (doc == null) return 1;
            var mgr = (ISketchManager)doc.SketchManager;
            session.Check(session.SelectFrontPlane(doc),
                "Front Plane selected as insertion support");

            string created = probe.CreateQrFromWindowProbe();
            session.Check(created.StartsWith("OK"), "circle-mode create succeeds", created);
            if (!created.StartsWith("OK")) return 1;
            string skName = created.Split(' ')[1];

            string census = probe.PatternCensusProbe(skName);
            session.Check(census.StartsWith("PATCENSUS"), "census on the open sketch", census);
            string dims = probe.PatternDimsProbe(skName);
            session.Check(dims.StartsWith("PATDIMS") && dims.Contains("eqs=8")
                    && dims.Contains("mode=circle"),
                "dim census + 8 equations on the circle sketch", dims);

            // Exit edit mode — committed-state reads are the accurate
            // ones (in-edit spacing-dim reads can be stale, PAT12).
            mgr.InsertSketch(false);
            doc.ClearSelection2(true);

            double boxM = ParseDimMm(dims, "D1");
            session.Check(boxM > 0.0, "master dim D1 resolved from census",
                $"D1={boxM * 1000}mm");
            if (boxM <= 0.0) return 1;
            string set = probe.SetPatternDimProbe($"D1@{skName}", boxM * 2.0);
            session.Check(set.StartsWith("PATSET") && set.Contains($"{boxM * 2000.0:F4}mm"),
                "master dim rescale accepted", set);

            string dims2 = probe.PatternDimsProbe(skName);
            double pitch1 = ParseDimMm(dims, "D5");    // spacingX — creation order
            double pitch2 = ParseDimMm(dims2, "D5");
            session.Check(dims2.Contains("eqs=8"), "equations intact after rescale", dims2);
            session.Check(pitch1 > 0 && Math.Abs(pitch2 - pitch1 * 2.0) < 1e-9,
                "spacing dim doubled with the master", dims2);

            string census2 = probe.PatternCensusProbe(skName);
            session.Check(census2.StartsWith("PATCENSUS") && census2.Contains("regions="),
                "census after rescale", census2);
            return 0;
        }
    }
}
