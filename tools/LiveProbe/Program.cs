using System;
using System.Linq;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// Live verification harness:
    ///   SolidWorksQrAddIn.LiveProbe.exe [deployDir] [progId]
    ///   SolidWorksQrAddIn.LiveProbe.exe &lt;mode&gt; [mode args]
    ///   SolidWorksQrAddIn.LiveProbe.exe list
    /// where deployDir contains the registered SolidWorksQrAddIn.dll.
    ///
    /// This file is the entry point only — lookup + execute (ticket #63).
    /// `list` prints the mode registry without touching COM; every named
    /// mode is a registry row dispatched through the GateHost ceremony
    /// (see GateHost.cs — the uniform occupied-seat refusal, launch,
    /// probe acquisition, the mode's Run against LiveSession, the
    /// pass/fail summary and the spawned-instance teardown); anything else
    /// runs the unnamed default suite on the raw args (its arg0 may BE a
    /// deployDir). A mode's own extra arguments reach it verbatim via
    /// LiveSession.Args (`pattern [progId] [PAT&lt;n&gt;]`,
    /// `circleladder [deployDir] [progId] [refuse]`).
    ///
    /// Drives SolidWorks via SldWorks.Application.31 + Activator.CreateInstance
    /// (the verified route — PowerShell late binding does NOT work here) and the
    /// add-in through its IAddInProbe COM hook, which exercises the SAME session
    /// + view code paths the QR task-pane tab uses (the *Window* probe names are
    /// historical — the surface is now the docked pane). Prints PASS/FAIL lines;
    /// exit code 0 when every check passes.
    /// </summary>
    public static partial class Program
    {
        public static int Main(string[] args)
        {
            // `list` prints the mode registry and exits — it must never
            // touch COM.
            if (args.Length > 0 &&
                string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
                return GateHost.ListModes();

            // The registry is the ONLY dispatch — every registered verb is
            // a hosted mode running the GateHost ceremony on the post-verb
            // args; no verb matched means the unnamed default suite, which
            // takes the raw args (arg0 may be a deployDir).
            var mode = FindMode(args.Length > 0 ? args[0] : null);
            return mode != null
                ? GateHost.Run(mode, args.Skip(1).ToArray())
                : GateHost.Run(DefaultMode, args);
        }
    }
}
