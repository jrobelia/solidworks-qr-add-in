using System;
using System.Collections.Generic;
using System.IO;
using SolidWorksQrAddIn.Core;
using SolidWorks.Interop.sldworks;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// The `undoapi` spike migrated onto GateHost (#63):
    /// `LiveProbe.exe undoapi [deployDir] [progId]` — the custom undo
    /// object (SetApiUndoObject) verification through
    /// <see cref="IAddInProbe.UndoRedoQrProbe"/> on a session-free
    /// instance. The probe call IS the capability test, exactly like the
    /// legacy body's warm-up: a stale auto-loaded build misses the method,
    /// which drove the unload + LoadAddIn retry. The verdict line the
    /// probe returns is retained by the predicate so the experiment still
    /// runs exactly once — the Run sequence checks it verbatim.
    ///
    /// Semantics that intentionally change under the uniform policy: the
    /// legacy body never checked for a running SLDWORKS.exe before
    /// attaching (only the visible/open-documents refusal after). Hosted,
    /// the occupied-seat refusal applies to spikes like every other mode.
    /// The legacy `# LoadAddIn(...) -> rc` line is the ceremony's uniform
    /// LoadAddIn check.
    /// </summary>
    public static partial class Program
    {
        private sealed class UndoApiSpike
        {
            private string? _verdict;
            private bool _firstTry = true;

            /// <summary>The spike banner at its legacy position — before the refusal.</summary>
            public bool Expectations()
            {
                Console.WriteLine("# Custom undo object (SetApiUndoObject) verification");
                return true;
            }

            /// <summary>
            /// The revision banner — first line after the spawn, ahead of
            /// the warm-up (the legacy body's position).
            /// </summary>
            public void RevisionBanner(ISldWorks sw, IAddInProbe? firstTry)
            {
                int openDocs = 0;
                try { openDocs = sw.GetDocumentCount(); } catch { }
                Console.WriteLine($"# revision {sw.RevisionNumber()} " +
                    $"visible={sw.Visible} docs={openDocs}");
            }

            /// <summary>
            /// The probe-surface test IS the legacy warm-up: the first
            /// answering probe runs <c>UndoRedoQrProbe</c> once and keeps
            /// the verdict line; a throw or a null line means a stale
            /// build, which drives the host's unload + LoadAddIn retry.
            /// The host asks the predicate twice — the cached verdict
            /// answers the second ask without re-running the experiment.
            /// The post-reload call is uncaught like the legacy body's —
            /// a throw there escapes to the ceremony's `# FATAL:` catch.
            /// </summary>
            public bool ProbeSurface(IAddInProbe? probe)
            {
                bool firstTry = _firstTry;
                _firstTry = false;
                if (probe == null) return false;
                if (_verdict != null) return true;   // proven once
                if (!firstTry)
                {
                    _verdict = probe.UndoRedoQrProbe();
                    return _verdict != null;
                }
                try
                {
                    _verdict = probe.UndoRedoQrProbe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"# probe missing on loaded build " +
                        $"({ex.GetType().Name}) — loading requested DLL");
                    return false;
                }
                if (_verdict != null)
                    Console.WriteLine("# auto-loaded build already has the " +
                        "probe — warm-up ran it");
                return _verdict != null;
            }

            /// <summary>
            /// The legacy stale-build unload list — the per-user registered
            /// copy first, then the requested dll when it differs.
            /// </summary>
            public IEnumerable<string> UnloadCandidates(string dll)
            {
                string registered = Path.Combine(DefaultDeployDir, "SolidWorksQrAddIn.dll");
                yield return registered;
                if (!string.Equals(registered, dll, StringComparison.OrdinalIgnoreCase))
                    yield return dll;
            }

            /// <summary>
            /// Stage report: undo presses walk the stack (the open sketch's
            /// edit-session steps sit above our API step) until the QR
            /// sketch is gone; redo presses restore it. "user" = the
            /// pre-existing user sketch that must always survive.
            /// </summary>
            public int Run(LiveSession session)
            {
                string line = _verdict ?? session.Probe.UndoRedoQrProbe();
                Console.WriteLine(line);
                session.Check(line.Contains("removed=True"),
                    "undo removed exactly the QR sketch", line);
                session.Check(line.Contains("userIntact=True"),
                    "pre-existing user sketch survived everything", line);
                session.Check(line.Contains("restored=True"),
                    "redo restored the QR sketch", line);
                // The host prints `# UNDO-API ALL PASSED` (SummaryPass) /
                // the failure line and derives the exit code from the tally.
                return 0;
            }
        }
    }
}
