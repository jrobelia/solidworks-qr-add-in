using System;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorksQrAddIn.Host;

namespace SolidWorksQrAddIn
{
    /// <summary>
    /// The <see cref="IAddInProbe"/> surface of <see cref="SwAddin"/> — the
    /// COM test hook LiveProbe drives. Every probe answers with a string: a
    /// probe call can never throw back across the COM boundary into
    /// SolidWorks, and every entry point gates on <see cref="ReadySession"/>
    /// so "not connected" means exactly one thing.
    ///
    /// Probe-driven session edits (SetQr*Probe, RunQrSketchProbe) go through
    /// the same session setters the window uses — the session notifies the
    /// open window itself, so the shown fields and picture can never diverge
    /// from what a probe observes.
    /// </summary>
    public partial class SwAddin
    {
        /// <summary>Payload the fixed-path probes use (was the ticket-2 fixed content).</summary>
        private const string ProbeContent = "SW-QR-MVP";

        /// <summary>The probe-facing not-connected answer — one string, one source.</summary>
        private const string NotConnectedError = "ERROR " + NotConnectedMessage;

        /// <summary>The live session while connected — the single gate every probe shares.</summary>
        private QrSketchSession? ReadySession =>
            _connected && _sw != null ? _session : null;

        public string RunQrSketchProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            session.SetContent(ProbeContent);
            return CreateQrFromWindowProbe();
        }

        public string RunFailingQrSketchProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            session.SetContent(ProbeContent);
            // Wrap the real native host so line 3 of the insertion throws —
            // the command must run the native abort/cleanup path for real.
            var host = new FailureInjectingHost(new SolidWorksSketchHost(_sw!), failOnLine: 3);
            return DescribeCreate(session, host);
        }

        public string RunCleanupFailQrSketchProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            session.SetContent(ProbeContent);
            // Line 3 throws AND the first cleanup attempt reports failure —
            // the "partial sketch may remain" reporting runs for real. The
            // teardown path still makes the host's best-effort second attempt.
            var host = new FailureInjectingHost(
                new SolidWorksSketchHost(_sw!), failOnLine: 3, failCleanup: true);
            return DescribeCreate(session, host);
        }

        /// <summary>
        /// The probe/COM boundary: a create through a probe surface can never
        /// throw back into SolidWorks — the result string is the contract.
        /// </summary>
        private string DescribeCreate(QrSketchSession session, ISketchHost host)
        {
            try
            {
                var result = session.CreateSketch(host);
                return result.Succeeded
                    ? "UNEXPECTED-OK " + result.CreatedSketchName
                    : $"FAILSAFE {result.Message}";
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        public string OpenQrWindowProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            try
            {
                session.OpenOrFocusWindow();
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
            return $"OPEN open={session.HasOpenWindow} windows={session.WindowsCreated}";
        }

        public string CloseQrWindowProbe()
        {
            _session?.CloseWindow();
            return WindowStateProbe();
        }

        public string WindowStateProbe() =>
            _session == null
                ? "STATE session=none"
                : $"STATE open={_session.HasOpenWindow} windows={_session.WindowsCreated} " +
                  $"hasRequest={_session.HasValidRequest} " +
                  $"size={_session.SizeText} growth={_session.GrowthText}";

        /// <summary>
        /// The shared setter-probe shape: gate on the connected session,
        /// apply the field edit verbatim through the session setter (which
        /// also refreshes the shown window), describe the revalidated result.
        /// </summary>
        private string FieldProbe(Func<QrSketchSession, QrSketchResult> edit)
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            return Describe(edit(session));
        }

        public string SetQrContentProbe(string content) =>
            FieldProbe(s => s.SetContent(content));

        public string SetQrSizeProbe(string sizeText) =>
            FieldProbe(s => s.SetSizeText(sizeText));

        public string SetQrGrowthProbe(string growthText) =>
            FieldProbe(s => s.SetGrowthText(growthText));

        private static string Describe(QrSketchResult result)
        {
            if (!result.Succeeded)
                return $"INVALID {result.Error!.Kind} {result.Error.Message}";
            var info = result.Sketch!.Info;
            return $"VALID cells={info.CellCount} contours={info.ContourCount} " +
                   $"segs={info.SegmentCount} size={info.FinalSizeMm:F3}";
        }

        public string CreateQrFromWindowProbe()
        {
            var session = ReadySession;
            if (session == null)
                return NotConnectedError;
            // A probe answer is a string — a create failure or a host
            // exception surfaces as "ERROR …", never as an exception that
            // escapes across the COM boundary into SolidWorks.
            try
            {
                var result = session.CreateSketch(new SolidWorksSketchHost(_sw!));
                return result.Succeeded
                    ? $"OK {result.CreatedSketchName} {result.Message}"
                    : $"ERROR {result.Message}";
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        public string PreviewSignatureProbe()
        {
            var pic = _session?.Picture;
            return pic == null
                ? "NO-PICTURE"
                : $"PIC {pic.WidthPixels}x{pic.HeightPixels} dark={pic.DarkPixelCount} sha={pic.Signature()}";
        }

        public string AddinStateProbe() =>
            _connected ? $"CONNECTED group={AddinIdentity.CommandGroupUserId}" : "NOT-CONNECTED";
    }
}
