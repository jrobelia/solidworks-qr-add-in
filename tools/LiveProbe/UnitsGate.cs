using System;
using System.IO;
using System.Threading;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.LiveProbe
{
    /// <summary>
    /// The `units` gate on GateHost (#62). The legacy UnitsMain body runs
    /// verbatim here: it loads the deployed add-in's own
    /// SolidWorksSketchHost + SwHostStateWatcher through reflection and
    /// drives a real QrSketchSession — the native unit-watcher checks and
    /// the rendered-pane echoes are untouched. The occupied-seat refusal,
    /// launch and probe acquisition are the host's uniform ceremony; the
    /// "installed add-in probe available" verdict the legacy body emitted
    /// mid-gate is the host's probe-surface check (ProbeCheckName) — on the
    /// success path it prints at the ceremony position.
    /// </summary>
    public static partial class Program
    {
        private static int UnitsRun(LiveSession live)
        {
            ISldWorks? sw = live.Sw;
            IDisposable? nativeWatcher = null;
            QrSketchSession? session = null;
            try
            {
                var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(live.DeployDir, "SolidWorksQrAddIn.dll"));
                var host = (ISketchHost)Activator.CreateInstance(
                    assembly.GetType("SolidWorksQrAddIn.Host.SolidWorksSketchHost", true)!, new object[] { sw });
                var watcher = (IHostStateWatcher)Activator.CreateInstance(
                    assembly.GetType("SolidWorksQrAddIn.Host.SwHostStateWatcher", true)!, new object[] { sw });
                nativeWatcher = (IDisposable)watcher;
                session = new QrSketchSession(() => throw new InvalidOperationException("No test window"), () => host, watcher);
                int changes = 0;
                watcher.Changed += () =>
                {
                    Interlocked.Increment(ref changes);
                    Console.WriteLine($"# unit event: host={host.CaptureState().LengthUnit.Symbol}, cell={session.CellSizeReadout}");
                };
                string template = live.DefaultPartTemplate;
                var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (doc == null) return 1;
                doc.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitSystem,
                    (int)swUnitSystem_e.swUnitSystem_MMGS);
                Thread.Sleep(1000);
                session.SetContent(PayloadA);
                Check(session.CellSizeReadout.EndsWith(" mm"), "millimetre baseline", session.CellSizeReadout);
                // The host's probe acquisition already ran — the legacy
                // TryGetProbe/LoadAddIn/`installed add-in probe available`
                // verdict is the ceremony's ProbeCheckName slot.
                var paneProbe = live.Probe;
                paneProbe.SetQrField("content", PayloadA);
                string state = paneProbe.WindowStateProbe();
                Check(state.Contains("viewSizeLabel=QR code size (mm):") && state.Contains("viewCell=1.187 mm"),
                    "pre-created pane is synchronized without invoking the menu command", state);

                int before = changes;
                doc.SetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitSystem,
                    (int)swUnitSystem_e.swUnitSystem_IPS);
                Thread.Sleep(1000);
                Check(doc.LengthUnit == (int)swLengthUnit_e.swINCHES, "native document changed to inches");
                Check(changes > before, "native unit change notified the watcher", $"events={changes - before}");
                Check(session.CellSizeReadout.EndsWith(" in"),
                    "information refreshes without a field edit or Create", session.CellSizeReadout);
                Check(Math.Abs(session.RequestedSizeMm!.Value - 25) < 1e-9,
                    "unit switch preserves the physical QR size", $"sizeMm={session.RequestedSizeMm}");
                state = paneProbe.WindowStateProbe();
                Check(state.Contains("viewSizeLabel=QR code size (in):") && state.Contains("viewCell=0.04674 in"),
                    "rendered pane updates on the native event without Create or a menu command", state);
                Check(state.Contains("viewSize=0.984251968504"), "rendered input converts its physical size", state);

                before = changes;
                doc.SetUnits((short)swLengthUnit_e.swMM, 0, 2, 0, false);
                Thread.Sleep(1000);
                Check(changes > before && session.CellSizeReadout.EndsWith(" mm"),
                    "SetUnits also refreshes the information", $"events={changes - before}, cell={session.CellSizeReadout}");
                before = changes;
                doc.LengthUnit = (int)swLengthUnit_e.swINCHES;
                Thread.Sleep(1000);
                Console.WriteLine($"# API LengthUnit setter: native={doc.LengthUnit}, notifications={changes - before}; " +
                    "a silent API assignment does not test the interactive UnitsChangeNotify path.");

                var second = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
                if (second == null) return 1;
                second.SetUnits((short)swLengthUnit_e.swMM, 0, 2, 0, false);
                Thread.Sleep(1000);
                Check(session.CellSizeReadout.EndsWith(" mm"), "new active document refreshes the information", session.CellSizeReadout);
                doc.SetUnits((short)swLengthUnit_e.swINCHES, 0, 4, 0, false);
                Thread.Sleep(1000);
                Check(session.CellSizeReadout.EndsWith(" mm"), "inactive document does not change the displayed unit", session.CellSizeReadout);
                sw.ActivateDoc(doc.GetTitle());
                Thread.Sleep(1000);
                Check(session.CellSizeReadout.EndsWith(" in"), "switching documents refreshes the information", session.CellSizeReadout);
                state = paneProbe.WindowStateProbe();
                Check(state.Contains("viewSizeLabel=QR code size (in):") && state.Contains("viewCell=0.04674 in"),
                    "rendered pane follows the active document", state);
                // The host prints `# UNITS ALL PASSED` / the failure line
                // (SummaryLine) and derives the exit code from the tally.
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"# FATAL: {ex}");
                return 1;
            }
            finally
            {
                session?.Release();
                nativeWatcher?.Dispose();
            }
        }
    }
}
