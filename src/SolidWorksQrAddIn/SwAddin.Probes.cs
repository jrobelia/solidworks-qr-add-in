using System;
using SolidWorksQrAddIn.Core;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorksQrAddIn.Core.Interaction;
using SolidWorksQrAddIn.Core.Registration;
using SolidWorksQrAddIn.Host;
using SolidWorksQrAddIn.UI;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

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
                  $"size={_session.SizeText} growth={_session.GrowthText} " +
                  $"preset={_session.ContentPresetId} " +
                  $"ecc={_session.EccLevel} invert={_session.Invert} " +
                  $"ringsplit={_session.RingSplit} " +
                  $"shape={QrCellShapes.CodeFor(_session.CellShape)} " +
                  $"param={_session.ShapeParameterText} " +
                  $"unit={_session.LengthUnit.Symbol} cell={_session.CellSizeReadout} " +
                  (_pane?.InfoSnapshot ?? "view=none");

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

        /// <summary>
        /// The pane's fields, probed by name (spec #67, ticket #65):
        /// <see cref="QrProbeFields"/> resolves the (name, value) pair into
        /// the same session setter the window uses — the session notifies
        /// the open window itself, so the shown fields and picture can
        /// never diverge from what a probe observes. A harness-level
        /// refusal (an unparseable ECC level, cell shape or toggle value,
        /// or a missing field name) answers "ERROR …" before the session
        /// is even consulted.
        /// </summary>
        public string SetQrField(string name, string value)
        {
            if (!QrProbeFields.TryResolve(name, value, out var edit, out var error))
                return error!;
            return FieldProbe(s => edit!(s));
        }

        private static string Describe(QrSketchResult result)
        {
            if (!result.Succeeded)
                return $"INVALID {result.Error!.Kind} {result.Error.Message}";
            var info = result.Sketch!.Info;
            // Under Circle the delivered fact is the pattern's instance
            // count — contour/segment counts are meaningless for circle
            // entities and are not reported.
            return info.CellShape == QrCellShape.Circle
                ? $"VALID cells={info.CellCount} dots={info.InstanceCount} " +
                  $"size={info.FinalSizeMm:F3}"
                : $"VALID cells={info.CellCount} contours={info.ContourCount} " +
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

        // ---------------- custom undo object (SetApiUndoObject) verification ----------------

        /// <summary>
        /// Live check for the custom undo object. On a throwaway part it
        /// draws a small USER sketch first (prior work that must survive),
        /// runs the real session create for a QR sketch, exits it, then
        /// presses EditUndo2 → EditRedo2 → EditUndo2 and reports what each
        /// did to the feature tree. Runs on the SolidWorks UI thread via the
        /// pane's Invoke — the real Create-button context.
        /// </summary>
        public string UndoRedoQrProbe()
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            try
            {
                var pane = _pane;
                if (pane != null && pane.IsHandleCreated)
                    return "APIUNDO " + (pane.Invoke(
                        new Func<string>(RunUndoRedoQrProbe)) as string);
                return "APIUNDO " + RunUndoRedoQrProbe();
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        private string RunUndoRedoQrProbe()
        {
            var sw = _sw!;
            string template = sw.GetUserPreferenceStringValue(
                (int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            if (string.IsNullOrEmpty(template))
                return "no-template";
            var doc = sw.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null) return "no-doc";
            try
            {
                var ext = (IModelDocExtension)doc.Extension;
                var mgr = (ISketchManager)doc.SketchManager;

                // Prior work: a small user sketch on Front Plane.
                ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                mgr.InsertSketch(true);
                var userName = (mgr.ActiveSketch as IFeature)?.Name ?? "SketchUser";
                mgr.CreateLine(-0.01, 0.0, 0, 0.01, 0.0, 0);
                mgr.InsertSketch(false);

                // The real create path, in the window's call context.
                doc.ClearSelection2(true);
                ext.SelectByID2("Front Plane", "PLANE", 0, 0, 0, false, 0, null,
                    (int)swSelectOption_e.swSelectOptionDefault);
                var session = ReadySession!;
                session.SetContent(ProbeContent);
                var result = session.CreateSketch(new SolidWorksSketchHost(sw));
                if (!result.Succeeded)
                    return $"createFailed: {result.Message}";
                string qrName = result.CreatedSketchName ?? "";
                try { mgr.InsertSketch(false); } catch { }   // engineer exits the sketch

                bool Present(string name) => FindSketch(doc, name) != null;
                // Any sketch that is not the user's = the QR sketch (by
                // whatever name its current incarnation carries).
                bool HasQrSketch()
                {
                    for (var f = doc.FirstFeature() as IFeature; f != null;
                         f = f.GetNextFeature() as IFeature)
                        if (string.Equals(f.GetTypeName2(), "ProfileFeature",
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(f.Name, userName, StringComparison.OrdinalIgnoreCase))
                            return true;
                    return false;
                }

                int n0 = CountSketchFeatures(doc);
                var trace = new System.Text.StringBuilder();
                // The QR creation leaves the sketch open; exiting it pushes
                // edit-session steps ABOVE our API undo step. Press undo until
                // the QR sketch is gone (bounded) — mimics a user holding
                // Ctrl+Z; each press is reported so the stack shape is visible.
                int undoPresses = 0;
                try
                {
                    while (undoPresses < 6 && HasQrSketch())
                    {
                        doc.EditUndo2(1);
                        undoPresses++;
                        trace.Append(
                            $" u{undoPresses}:qr=T user={Present(userName)} " +
                            $"n={CountSketchFeatures(doc)} edit={(mgr.ActiveSketch != null)} |");
                        // An undo that re-enters sketch edit mode only
                        // restores the pre-exit state — the user exits the
                        // reopened sketch before pressing undo again.
                        if (mgr.ActiveSketch != null)
                            try { mgr.InsertSketch(false); } catch { }
                    }
                }
                catch (Exception ex) { trace.Append($" undoEX:{ex.GetType().Name} |"); }
                bool removed = !HasQrSketch();

                // Native redo of an API undo item: verified dead — SW never
                // calls the handler. Two presses prove it stays that way.
                int redoPresses = 0;
                try
                {
                    while (redoPresses < 2 && !HasQrSketch())
                    {
                        doc.EditRedo2(1);
                        redoPresses++;
                        trace.Append(
                            $" r{redoPresses}:qr={HasQrSketch()} " +
                            $"n={CountSketchFeatures(doc)} edit={(mgr.ActiveSketch != null)} |");
                    }
                }
                catch (Exception ex) { trace.Append($" redoEX:{ex.GetType().Name} |"); }

                // The restore path this API offers: the handler re-registered
                // itself, so another undo press re-fires DoUndo and the
                // toggle reinserts the sketch.
                int restorePresses = 0;
                try
                {
                    while (restorePresses < 4 && !HasQrSketch())
                    {
                        doc.EditUndo2(1);
                        restorePresses++;
                        trace.Append(
                            $" x{restorePresses}:qr={HasQrSketch()} user={Present(userName)} " +
                            $"n={CountSketchFeatures(doc)} edit={(mgr.ActiveSketch != null)} |");
                        if (mgr.ActiveSketch != null)
                            try { mgr.InsertSketch(false); } catch { }
                    }
                }
                catch (Exception ex) { trace.Append($" restoreEX:{ex.GetType().Name} |"); }

                return $"user='{userName}' qr='{qrName}' n0={n0} undoPresses={undoPresses} " +
                       $"removed={removed} nativeRedoPresses={redoPresses} " +
                       $"restored={HasQrSketch()} restorePresses={restorePresses} " +
                       $"userIntact={Present(userName)} |{trace}";
            }
            finally
            {
                try { sw.CloseDoc(doc.GetTitle()); } catch { }
            }
        }

        // ---------------- circle-pattern census (ticket-#42 live gates) ----------------

        /// <summary>
        /// The pattern/entity census the circle live gates read. Works on
        /// the named committed sketch or — empty name — the sketch open in
        /// edit mode (a fresh create stays open). The segment census counts
        /// by the type code PAT0 proved reliable: sketch-pattern instance
        /// arcs skip ISketch.GetArcs() entirely and one arc/line each can
        /// enumerate as a sibling COM type that fails the is-checks, so the
        /// tally uses ISketchSegment.GetType() — plus "other" for whatever
        /// casts to no segment at all.
        /// </summary>
        public string PatternCensusProbe(string sketchName)
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            try
            {
                var doc = _sw.IActiveDoc2 as IModelDoc2;
                if (doc == null)
                    return "ERROR no active document";

                ISketch? sketch = null;
                string name = sketchName ?? "";
                string featType = "";
                if (name.Length == 0)
                {
                    var mgr = doc.SketchManager as ISketchManager;
                    sketch = mgr?.ActiveSketch as ISketch;
                    var feat = sketch as IFeature;
                    name = feat?.Name ?? "";
                    featType = feat?.GetTypeName2() ?? "";
                }
                else
                {
                    var feat = FindSketch(doc, name);
                    featType = feat?.GetTypeName2() ?? "";
                    sketch = feat?.GetSpecificFeature2() as ISketch;
                }
                if (sketch == null)
                    return $"ERROR no sketch '{sketchName}' to census";

                int segs = 0, arcs = 0, lines = 0, other = 0, cons = 0, regions = -1;
                if (sketch.GetSketchSegments() is object[] segsArr)
                {
                    segs = segsArr.Length;
                    foreach (var so in segsArr)
                    {
                        if (so is not ISketchSegment s) { other++; continue; }
                        int t = -1;
                        try { t = s.GetType(); } catch { }
                        if (t == (int)swSketchSegments_e.swSketchARC) arcs++;
                        else if (t == (int)swSketchSegments_e.swSketchLINE) lines++;
                        else other++;
                        bool isCons = false;
                        try { isCons = s.ConstructionGeometry; } catch { }
                        if (isCons) cons++;
                    }
                }
                try { regions = sketch.GetSketchRegionCount(); } catch { }

                var plan = session.CurrentResult?.Sketch?.CirclePlan;
                string echo = plan == null
                    ? "plan=none"
                    : $"emitted={plan.EmittedCount} skipped={plan.SkippedCount} " +
                      $"cells={plan.PatternCells} pitchMm={plan.PitchM * 1000.0:F4} " +
                      $"diaMm={plan.DotDiameterM * 1000.0:F4} " +
                      $"invert={plan.Inverted}";
                return $"PATCENSUS sketch='{name}' feat='{featType}' " +
                       $"segs={segs} arcs={arcs} lines={lines} other={other} " +
                       $"cons={cons} regions={regions} {echo}";
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        // ---------------- master-geometry dims + equations (ticket-#42 gates) ----------------

        /// <summary>
        /// The dimension/equation surface the circle live gates read after
        /// a create (ticket #40). Works on the named committed sketch or —
        /// empty name — the sketch open in edit mode. Sketch dims are not
        /// sub-features, so they resolve by name through IParameter over
        /// D1..D64 — the same discovery the emitter itself uses (creation
        /// order: the QR-square master dim is D1, then the quiet-zone set,
        /// then the pattern's spacing pair, then the seed dims). Each entry
        /// reports the mm value and DrivenState code; the equation list is
        /// the equation manager's verbatim text.
        /// </summary>
        public string PatternDimsProbe(string sketchName)
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            try
            {
                var doc = _sw.IActiveDoc2 as IModelDoc2;
                if (doc == null)
                    return "ERROR no active document";

                string name = sketchName ?? "";
                if (name.Length == 0)
                {
                    var mgr = doc.SketchManager as ISketchManager;
                    name = ((mgr?.ActiveSketch as ISketch) as IFeature)?.Name ?? "";
                }
                if (name.Length == 0)
                    return "ERROR no sketch name and no sketch open in edit mode";

                var sb = new System.Text.StringBuilder();
                sb.Append($"PATDIMS sketch='{name}'");
                var parts = new System.Collections.Generic.List<string>();
                // The shared D1..D64 IParameter scan — the same discovery
                // the circle emitter runs (creation order).
                foreach (var d in SketchDimensions.OfSketch(doc, name))
                {
                    double v = double.NaN;
                    int st = -8;
                    try { v = d.Dim.SystemValue; } catch { }
                    try { st = d.Dim.DrivenState; } catch { }
                    parts.Add($"{SketchDimensions.DimNumber(d.Name)}={v * 1000.0:F4}mm(s{st})");
                }
                sb.Append($" dims={parts.Count}").Append(' ')
                  .Append(string.Join(" ", parts));

                int nEq = -1;
                var eqs = new System.Collections.Generic.List<string>();
                try
                {
                    var eqm = doc.GetEquationMgr() as IEquationMgr;
                    if (eqm != null)
                    {
                        nEq = eqm.GetCount();
                        for (var i = 0; i < nEq; i++)
                        {
                            string? text = null;
                            try { text = eqm.Equation[i]; } catch { }
                            if (text != null)
                                eqs.Add(text.Trim());
                        }
                    }
                }
                catch { }
                sb.Append($" eqs={nEq}");
                foreach (var e in eqs)
                    sb.Append($" '{e}'");
                sb.Append(session.CurrentResult?.Sketch?.CirclePlan == null
                    ? " mode=square" : " mode=circle");
                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Drives one dimension by name — the live gate's resize handle on
        /// the QR-square master dim. SetSystemValue3 in this configuration,
        /// EditRebuild3, then the stored-value echo so the gate sees what
        /// SolidWorks actually took (rc is the SetSystemValue3 return).
        /// </summary>
        public string SetPatternDimProbe(string dimensionName, double valueM)
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            try
            {
                var doc = _sw.IActiveDoc2 as IModelDoc2;
                if (doc == null)
                    return "ERROR no active document";
                var dim = doc.IParameter(dimensionName ?? "") as IDimension;
                if (dim == null)
                    return $"ERROR no dimension '{dimensionName}'";
                int rc = -8;
                // This-configuration scope — docs/CIRCLE-DIMENSIONS.md's
                // documented semantic (swSetValueInConfiguration_e: 1 is
                // InThisConfiguration; the bare 2 was InAllConfigurations).
                try
                {
                    rc = dim.SetSystemValue3(valueM,
                        (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                }
                catch (Exception ex) { return $"ERROR set '{dimensionName}': {ex.Message}"; }
                try { doc.EditRebuild3(); } catch { }
                double now = double.NaN;
                try { now = dim.SystemValue; } catch { }
                return $"PATSET '{dimensionName}'={now * 1000.0:F4}mm rc={rc}";
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        // ---------------- part-property source (ticket-#44 gates) ----------------

        /// <summary>
        /// Enumerates the resolvable property names of the active part
        /// through the real <see cref="SwPartPropertySource"/> — a fresh
        /// adapter per call, like CreateQrFromWindowProbe's fresh host —
        /// so a stale capture can never hide property or configuration
        /// edits the gate makes between calls.
        /// </summary>
        public string SwPropertiesProbe()
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            try
            {
                var snapshot = new SwPartPropertySource(_sw).CaptureProperties();
                if (snapshot.Document != HostDocumentKind.Part)
                    return $"SWPROPS kind={snapshot.Document} names=0";
                var sb = new System.Text.StringBuilder();
                sb.Append($"SWPROPS kind=part doc='{snapshot.DocumentTitle ?? ""}' " +
                          $"config='{snapshot.ConfigurationName ?? ""}' " +
                          $"names={snapshot.Names.Count}");
                foreach (var n in snapshot.Names)
                    sb.Append($" '{n}'");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>
        /// Resolves one property reference through the real
        /// <see cref="SwPartPropertySource"/>. The verdict echoes the
        /// seam's named kinds — RESOLVED carries the verbatim value;
        /// EMPTY and MISSING are distinct answers, never a silent
        /// empty string.
        /// </summary>
        public string SwPropertyResolveProbe(string name)
        {
            var session = ReadySession;
            if (session == null || _sw == null)
                return NotConnectedError;
            try
            {
                var res = new SwPartPropertySource(_sw).Resolve(name ?? "");
                switch (res.Kind)
                {
                    case PartPropertyResolutionKind.Resolved:
                        return $"SWPROP '{res.Name}' RESOLVED '{res.Value}'";
                    case PartPropertyResolutionKind.Empty:
                        return $"SWPROP '{res.Name}' EMPTY";
                    case PartPropertyResolutionKind.Missing:
                        return $"SWPROP '{res.Name}' MISSING";
                    default:
                        return $"SWPROP '{res.Name}' NODOC";
                }
            }
            catch (Exception ex)
            {
                return $"ERROR {ex.GetType().Name}: {ex.Message}";
            }
        }

        private static int CountSketchFeatures(IModelDoc2 doc)
        {
            int n = 0;
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                if (string.Equals(f.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase))
                    n++;
            return n;
        }

        private static IFeature? FindSketch(IModelDoc2 doc, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (var f = doc.FirstFeature() as IFeature; f != null; f = f.GetNextFeature() as IFeature)
                if (string.Equals(f.GetTypeName2(), "ProfileFeature", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    return f;
            return null;
        }
    }
}
