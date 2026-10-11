using System;
using System.Collections.Generic;
using SolidWorksQrAddIn.Core.Geometry;
using SolidWorksQrAddIn.Core.Hosting;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksQrAddIn.Host
{
    /// <summary>
    /// Native insertion session bound to exactly one newly created sketch.
    /// KeepOpen leaves that sketch in edit mode; Abort exits edit mode and
    /// deletes only this session's sketch feature. Flags captured at Begin are
    /// restored on every exit path; a committed session redraws the view.
    ///
    /// When <paramref name="undoRecording"/> is set the host opened a
    /// StartRecordingUndoObject scope in BeginSketchInsertion; this session
    /// tries to close it once — named "Create QR Sketch" — on commit or on
    /// abort. Live-verified on SW 2023 SP5: the finish call is refused in
    /// every arrangement (AddToDB-mode insertion bypasses the undo system),
    /// so the named step is best-effort only — a naming aid, never a
    /// rollback mechanism.
    /// </summary>
    public sealed class SwSketchInsertion : ISketchInsertion
    {
        private readonly IModelDoc2 _doc;
        private readonly ISldWorks? _sw;
        private readonly ISketchManager _mgr;
        private readonly IFeature _sketchFeature;
        private readonly IModelDocExtension? _ext;
        private readonly IMathUtility? _math;
        private readonly bool _undoRecording;
        private readonly SketchManagerFlagSnapshot _previousFlags;
        private readonly List<(double X1, double Y1, double X2, double Y2)> _buffered =
            new List<(double, double, double, double)>();
        private readonly List<QrConstructionSquare> _stagedSquares =
            new List<QrConstructionSquare>();
        private QrDimensionSpec? _dimensionSpec;
        private bool _committed;
        private bool _aborted;
        private bool _flagsRestored;
        private bool _undoFinished;
        private CleanupOutcome? _abortOutcome;

        /// <param name="math">
        /// The app's math utility — the block calls need MathPoint insertion
        /// points. Null only when the host could not obtain one; the commit
        /// then fails honestly instead of drawing half a block.
        /// </param>
        /// <param name="sw">
        /// The application object — the dimension emission needs it for the
        /// app-level prompt toggles IAddDimension2 requires off (spike). Null
        /// only when the host cannot supply it; the emission then runs
        /// unguarded and logs.
        /// </param>
        /// <param name="previousFlags">
        /// The sketch-manager flag values from before the session — restored
        /// on every exit path.
        /// </param>
        internal SwSketchInsertion(
            IModelDoc2 doc, ISldWorks? sw, ISketchManager mgr, IFeature sketchFeature,
            IModelDocExtension? ext, IMathUtility? math, bool undoRecording,
            SketchManagerFlagSnapshot previousFlags)
        {
            _doc = doc;
            _sw = sw;
            _mgr = mgr;
            _sketchFeature = sketchFeature;
            _ext = ext;
            _math = math;
            _undoRecording = undoRecording;
            _previousFlags = previousFlags;

            // Bulk-insert mode for the session: verified live, the interactive
            // path sporadically refuses CreateLine on coincident endpoints of a
            // dense contour set (QR ~250 segments). AddToDB + suppressed display
            // inserts every validated edge; flags are restored on every exit
            // path and the committed session redraws. Verified: all 252 segments
            // of the fixed QR insert cleanly this way.
            try { _mgr.AddToDB = true; } catch { }
            try { _mgr.DisplayWhenAdded = false; } catch { }
            try { _mgr.AutoInference = false; } catch { }
        }

        public string SketchName => _sketchFeature.Name;
        public int LineCount => _buffered.Count;

        /// <summary>
        /// The sketch feature this session owns — the undo object needs it to
        /// delete (and re-track) exactly this sketch after a redo re-insert.
        /// </summary>
        internal IFeature Feature => _sketchFeature;

        /// <summary>
        /// Stages one contour edge at its final size in sketch-space metres.
        /// Nothing is drawn yet — the session buffers the staged edges and
        /// realizes them at <see cref="KeepOpen"/>: the QR lands as ONE
        /// sketch block instance whose definition is drawn unit-proportioned
        /// (1 × 1 document unit) and whose Scale reads the staged span in
        /// document units — the PropertyManager scale field IS the QR's
        /// resize handle (spec #10).
        /// </summary>
        public void AddLine(double x1, double y1, double x2, double y2)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            _buffered.Add((x1, y1, x2, y2));
        }

        /// <summary>
        /// Stages the model's construction layout — construction squares plus
        /// the dimension plan — for commit-time emission into the host
        /// sketch. Nothing is drawn yet: the emission runs inside
        /// <see cref="KeepOpen"/> AFTER the block instance exists, because
        /// the select-all that folds the contours into the block would
        /// swallow anything drawn earlier — and because the squares
        /// coincident-constrain to the block's outer edges, so the block has
        /// to be there first.
        /// </summary>
        public void AddConstructionLayout(
            IReadOnlyList<QrConstructionSquare> squares, QrDimensionSpec? dimensionSpec)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            if (squares != null)
                _stagedSquares.AddRange(squares);
            if (dimensionSpec != null)
                _dimensionSpec = dimensionSpec;
        }

        public void KeepOpen()
        {
            if (_buffered.Count > 0)
                RealizeAsBlockInstance();
            if (_dimensionSpec != null)
                EmitConstructionLayout();
            _committed = true;
            // The whole creation was meant to become one named undo step.
            // Measured live (SW 2023 SP5, rev 31.5.0): FinishRecordingUndoObject2
            // returns false in EVERY arrangement tried — recording opened
            // before or after InsertSketch, finished while the sketch is open
            // or after exiting edit mode. AddToDB-mode bulk insertion (which
            // the insertion needs to accept the contour set at all) bypasses
            // the undo system, so no named step ever exists and one EditUndo
            // cannot revert the creation — documented as a limitation in
            // docs/ACCEPTANCE.md. The attempt stays: a host that CAN record
            // still gets the named step, and failure only logs.
            TryFinishUndo();
        }

        /// <summary>
        /// The buffered-edge → block-instance realization (spec #10), run
        /// inside the open sketch edit:
        ///   1. the staged span IS the requested size — the instance Scale
        ///      is that span expressed in the document's own length unit;
        ///   2. the definition is drawn unit-proportioned — every staged edge
        ///      ÷ the scale, so the def spans exactly one document unit;
        ///   3. ClearSelection + SelectAll converts ONLY those drawn lines
        ///      into the definition and its first instance (scale 1 at the
        ///      origin) — construction squares the command emits afterwards
        ///      must therefore be drawn AFTER this step, never before it;
        ///   4. the instance's Scale is set to the document-unit size — the
        ///      PropertyManager field then reads as the QR size.
        /// Any failure surfaces as a <see cref="SketchInsertionException"/>
        /// so the command's single cleanup path owns the partial sketch.
        /// </summary>
        private void RealizeAsBlockInstance()
        {
            double scaleDocUnits = BufferedSpan() / DocLengthUnits.Of(_doc).MetresPerUnit;
            if (scaleDocUnits <= 0.0)
                throw new SketchInsertionException(
                    "The staged QR contours span zero — no block definition can be formed.");

            // 1. Draw the definition: staged edges ÷ scale = unit-proportioned
            //    (one document unit across). These are ordinary sketch lines
            //    until the select-all step swallows them into the block.
            for (var i = 0; i < _buffered.Count; i++)
            {
                var e = _buffered[i];
                try
                {
                    var segment = _mgr.CreateLine(
                        e.X1 / scaleDocUnits, e.Y1 / scaleDocUnits, 0.0,
                        e.X2 / scaleDocUnits, e.Y2 / scaleDocUnits, 0.0) as ISketchSegment;
                    if (segment == null)
                        throw new SketchInsertionException(
                            $"CreateLine returned no segment while drawing the block definition " +
                            $"(line {i + 1} of {_buffered.Count}).");
                }
                catch (SketchInsertionException) { throw; }
                catch (Exception ex)
                {
                    throw new SketchInsertionException(
                        $"CreateLine failed while drawing the block definition " +
                        $"(line {i + 1} of {_buffered.Count}): {ex.Message}", ex);
                }
            }

            if (_ext == null || _math == null)
                throw new SketchInsertionException(
                    "The document exposed no extension/math interface — the QR block could not be formed.");

            // 2. Select exactly what this session drew — the sketch was born
            //    empty, so SelectAll can only reach the definition lines —
            //    then fold the selection into the block definition + instance.
            ISketchBlockDefinition? def;
            try
            {
                _doc.ClearSelection2(true);
                _ext.SelectAll();
                var origin = _math.CreatePoint(new[] { 0.0, 0.0, 0.0 }) as MathPoint
                    ?? throw new SketchInsertionException(
                        "The math utility returned no insertion point.");
                def = _mgr.MakeSketchBlockFromSelected(origin) as ISketchBlockDefinition;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"MakeSketchBlockFromSelected failed: {ex.Message}", ex);
            }
            if (def == null)
                throw new SketchInsertionException(
                    "MakeSketchBlockFromSelected formed no block definition.");

            // 3. The make leaves instance #1 in the sketch at scale 1 — set
            //    its Scale to the document-unit size so the PropertyManager
            //    field reads as the QR size (verified settable live, spike Q2).
            try
            {
                var sketch = _sketchFeature as ISketch
                    ?? throw new SketchInsertionException(
                        "The sketch feature did not expose its sketch interface.");
                if (sketch.GetSketchBlockInstanceCount() < 1)
                    throw new SketchInsertionException(
                        "The block definition formed but placed no instance in the sketch.");
                var instance = sketch.IGetSketchBlockInstances(0) as ISketchBlockInstance
                    ?? throw new SketchInsertionException(
                        "The block instance could not be read back.");
                instance.Scale = scaleDocUnits;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The block instance's Scale could not be applied: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The staged construction-layout emission (spec #10/#17), run inside
        /// the open sketch edit AFTER the block instance exists. The emission
        /// walks the dimension spec's roles in order — today only the driven
        /// square: draw its four edges as construction geometry, coincident-
        /// bind them to the block's outer edges so a Scale edit drags the
        /// square, then land the driven dimension on it — the live size
        /// readout. The equation-linked keep-out square is a later spec step.
        /// </summary>
        private void EmitConstructionLayout()
        {
            var spec = _dimensionSpec!;
            var square = _stagedSquares.Find(s => s.Role == spec.DrivenSquare);
            if (square == null)
                throw new SketchInsertionException(
                    "The dimension spec names a construction square that was never staged.");
            if (_ext == null)
                throw new SketchInsertionException(
                    "The document exposed no extension interface — the QR square " +
                    "could not be constrained to the block's outer edges.");

            var drawn = DrawConstructionSquare(square);
            BindSquareToBlock(square, drawn);
            AddDrivenDimension(square, drawn[0]);
        }

        /// <summary>
        /// Draws the square's four edges at their model coordinates as
        /// construction geometry and returns the segment handles — the
        /// binding and dimension steps select them by object, never by
        /// coordinates (coordinate picks at the square's own locations are
        /// ambiguous once block geometry overlaps them).
        /// </summary>
        private List<ISketchSegment> DrawConstructionSquare(QrConstructionSquare square)
        {
            var drawn = new List<ISketchSegment>(4);
            int i = 0;
            foreach (var e in square.Edges)
            {
                i++;
                try
                {
                    var segment = _mgr.CreateLine(
                        e.Start.X, e.Start.Y, 0.0, e.End.X, e.End.Y, 0.0) as ISketchSegment;
                    if (segment == null)
                        throw new SketchInsertionException(
                            $"CreateLine returned no segment while drawing the QR square " +
                            $"(edge {i} of 4).");
                    segment.ConstructionGeometry = true;
                    drawn.Add(segment);
                }
                catch (SketchInsertionException) { throw; }
                catch (Exception ex)
                {
                    throw new SketchInsertionException(
                        $"CreateLine failed while drawing the QR square " +
                        $"(edge {i} of 4): {ex.Message}", ex);
                }
            }
            return drawn;
        }

        /// <summary>
        /// Coincident-binds each square edge to the block instance's outer
        /// edges so a Scale edit drags the square. Per edge (the square is
        /// CCW from bottom-left: bottom, right, top, left — on the block's
        /// four extent lines):
        ///   - up to TWO coincident relations to distinct block boundary
        ///     vertices on the edge's line (two points pin the line's
        ///     position AND orientation rigidly), plus
        ///   - one horizontal/vertical relation recording the square's
        ///     axis-aligned shape.
        /// Block entities are reachable only by coordinate selection — the
        /// block's internals are not enumerable (spike Q3) — so a boundary
        /// vertex is picked by its coordinates as "SKETCHPOINT". Vertices
        /// strictly interior to a side are tried first: at a square corner
        /// the pick could grab the square's own endpoint instead of the
        /// block's point. Every edge must land at least one coincident or
        /// the square would not track Scale — a failure there throws so the
        /// command's cleanup path owns the partial sketch.
        /// </summary>
        private void BindSquareToBlock(
            QrConstructionSquare square, IReadOnlyList<ISketchSegment> drawn)
        {
            var v = square.Vertices;
            double minX = v[0].X, minY = v[0].Y, maxX = v[2].X, maxY = v[2].Y;
            for (int i = 0; i < 4; i++)
            {
                bool horizontal = i % 2 == 0;
                double lineCoord = i == 0 ? minY : i == 2 ? maxY : i == 1 ? maxX : minX;
                int coincident = BindEdgeToBlock(
                    drawn[i], horizontal, lineCoord, minX, minY, maxX, maxY);
                if (coincident < 1)
                    throw new SketchInsertionException(
                        "A QR square edge found no block boundary point to constrain " +
                        "to — the square would not track a Scale edit.");
                AddOrientationConstraint(drawn[i], horizontal);
            }
        }

        /// <summary>
        /// Applies coincident relations between one square edge and up to two
        /// block boundary vertices on the edge's extent line; returns how
        /// many were applied. The vertex candidates come from the staged
        /// edges — the block's effective geometry in sketch space.
        /// </summary>
        private int BindEdgeToBlock(
            ISketchSegment edge, bool horizontal, double lineCoord,
            double minX, double minY, double maxX, double maxY)
        {
            const double tol = 1e-9;
            var interior = new List<(double X, double Y)>();
            var corner = new List<(double X, double Y)>();
            var seen = new List<(double X, double Y)>();
            foreach (var e in _buffered)
            {
                foreach (var p in new[] { (e.X1, e.Y1), (e.X2, e.Y2) })
                {
                    double onLine = horizontal ? p.Item2 : p.Item1;
                    double cross = horizontal ? p.Item1 : p.Item2;
                    double crossMin = horizontal ? minX : minY;
                    double crossMax = horizontal ? maxX : maxY;
                    if (Math.Abs(onLine - lineCoord) > tol ||
                        cross < crossMin - tol || cross > crossMax + tol)
                        continue;
                    if (seen.Exists(s => Math.Abs(s.X - p.Item1) < tol && Math.Abs(s.Y - p.Item2) < tol))
                        continue;
                    seen.Add((p.Item1, p.Item2));
                    if (cross > crossMin + tol && cross < crossMax - tol)
                        interior.Add((p.Item1, p.Item2));
                    else
                        corner.Add((p.Item1, p.Item2));
                }
            }

            int applied = 0;
            foreach (var cand in Chain(interior, corner))
            {
                if (applied >= 2) break;
                try
                {
                    _doc.ClearSelection2(true);
                    bool picked = _ext!.SelectByID2(
                        "", "SKETCHPOINT", cand.X, cand.Y, 0.0,
                        false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                    bool edgeSelected = false;
                    try { edgeSelected = edge.Select4(true, null); }
                    catch { /* object selection failed — count it as not applied */ }
                    if (!picked || !edgeSelected)
                    {
                        Diag.Log(
                            $"QR square binding: selection failed at " +
                            $"({cand.X:F6},{cand.Y:F6}) block={picked} edge={edgeSelected}");
                        continue;
                    }
                    _doc.SketchAddConstraints("sgCOINCIDENT");
                    applied++;
                }
                catch (Exception ex)
                {
                    Diag.Log($"QR square binding failed at ({cand.X:F6},{cand.Y:F6}): {ex.Message}");
                }
            }
            return applied;
        }

        private static IEnumerable<(double X, double Y)> Chain(
            List<(double X, double Y)> first, List<(double X, double Y)> then)
        {
            foreach (var p in first) yield return p;
            foreach (var p in then) yield return p;
        }

        /// <summary>
        /// One orientation relation on a square edge — the square is
        /// axis-aligned by construction; the constraint pins that shape so a
        /// solve can never skew it. Best-effort: the coincident bindings are
        /// what make the square track; this is insurance.
        /// </summary>
        private void AddOrientationConstraint(ISketchSegment edge, bool horizontal)
        {
            try
            {
                _doc.ClearSelection2(true);
                if (!edge.Select4(false, null)) return;
                _doc.SketchAddConstraints(horizontal ? "sgHORIZONTAL" : "sgVERTICAL");
            }
            catch (Exception ex)
            {
                Diag.Log($"QR square orientation constraint failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Lands the QR square's driven dimension — the live size readout.
        /// Spike-verified trap: IAddDimension2 hangs an invisible SolidWorks
        /// session unless BOTH app-level prompt toggles are off first —
        /// swInputDimValOnCreate (the dimension-value dialog) and
        /// swSketchOverdefiningDimsPromptToSetState (the over-defining →
        /// driven prompt). They are user-preference toggles, snapshotted,
        /// forced off for exactly the creation call, and restored after —
        /// even on failure. With the square constraint-bound to the block,
        /// the new dimension over-defines, so the suppressed prompt makes it
        /// land DRIVEN — a readout that tracks Scale, never a driver.
        /// </summary>
        private void AddDrivenDimension(QrConstructionSquare square, ISketchSegment bottomEdge)
        {
            var prevInput = ReadToggle(
                swUserPreferenceToggle_e.swInputDimValOnCreate, fallback: true);
            var prevOverdef = ReadToggle(
                swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, fallback: true);
            if (_sw == null)
                Diag.Log("No application object — the dimension prompt toggles cannot be guarded");
            try
            {
                SetToggle(swUserPreferenceToggle_e.swInputDimValOnCreate, false);
                SetToggle(swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, false);

                _doc.ClearSelection2(true);
                bool selected;
                try { selected = bottomEdge.Select4(false, null); }
                catch { selected = false; }
                if (!selected)
                    throw new SketchInsertionException(
                        "The QR square's edge could not be selected for its dimension.");

                // Text lands just below the square's bottom edge.
                double side = square.Vertices[1].X - square.Vertices[0].X;
                double dimY = square.Vertices[0].Y - Math.Max(side * 0.08, 0.002);
                var dd = _doc.IAddDimension2(0.0, dimY, 0.0) as IDisplayDimension;
                if (dd == null)
                    throw new SketchInsertionException(
                        "IAddDimension2 returned no dimension for the QR square.");
                try
                {
                    var dim = dd.GetDimension2(0) as IDimension;
                    Diag.Log($"QR square dimension '{dim?.FullName}' driven state: {dim?.DrivenState}");
                }
                catch { /* the log line is diagnostic only — the dim landed */ }
            }
            finally
            {
                SetToggle(swUserPreferenceToggle_e.swInputDimValOnCreate, prevInput);
                SetToggle(swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, prevOverdef);
            }
        }

        /// <summary>Reads an app-level user-preference toggle; the fallback on any failure.</summary>
        private bool ReadToggle(swUserPreferenceToggle_e toggle, bool fallback)
        {
            if (_sw == null) return fallback;
            try { return _sw.GetUserPreferenceToggle((int)toggle); }
            catch { return fallback; }
        }

        /// <summary>Writes an app-level user-preference toggle; failures only log.</summary>
        private void SetToggle(swUserPreferenceToggle_e toggle, bool value)
        {
            if (_sw == null) return;
            try { _sw.SetUserPreferenceToggle((int)toggle, value); }
            catch (Exception ex)
            {
                Diag.Log($"SetUserPreferenceToggle({toggle}) failed: {ex.Message}");
            }
        }

        /// <summary>The staged outline span (max of the x/y extents), metres.</summary>
        private double BufferedSpan()
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var e in _buffered)
            {
                minX = Math.Min(minX, Math.Min(e.X1, e.X2));
                maxX = Math.Max(maxX, Math.Max(e.X1, e.X2));
                minY = Math.Min(minY, Math.Min(e.Y1, e.Y2));
                maxY = Math.Max(maxY, Math.Max(e.Y1, e.Y2));
            }
            return Math.Max(maxX - minX, maxY - minY);
        }

        public CleanupOutcome Abort()
        {
            if (_committed)
                return CleanupOutcome.Cleaned;   // a committed sketch is never removed
            if (_abortOutcome != null)
                return _abortOutcome;            // report the REAL outcome, honestly
            _aborted = true;
            _abortOutcome = AbortOnce();
            return _abortOutcome;
        }

        private CleanupOutcome AbortOnce()
        {
            try
            {
                // Exit edit mode, then delete ONLY this session's sketch
                // feature — the same stages the begin-time abandon path runs.
                // Never a blind Undo — prior user work must survive.
                var exitError = OwnedSketchCleanup.ExitSketchEditMode(_mgr);
                if (exitError != null)
                    return CleanupOutcome.Failed(
                        $"could not exit sketch edit mode: {exitError}");

                var deleteError = OwnedSketchCleanup.DeleteSketchFeature(_doc, _sketchFeature);
                return deleteError == null
                    ? CleanupOutcome.Cleaned
                    : CleanupOutcome.Failed(deleteError);
            }
            finally
            {
                RestoreFlags();
                TryFinishUndo();
            }
        }

        public void Dispose()
        {
            if (_committed)
            {
                RestoreFlags();
                try { _doc.GraphicsRedraw2(); } catch { /* redraw is best-effort */ }
                return;
            }
            if (!_aborted)
            {
                try { Abort(); }
                catch (Exception ex) { Diag.Log($"dispose-time abort failed: {ex.Message}"); }
            }
        }

        private void RestoreFlags()
        {
            if (_flagsRestored) return;
            _flagsRestored = true;
            _previousFlags.Restore(_mgr);
        }

        /// <summary>
        /// Attempts to close the undo recording opened in BeginSketchInsertion,
        /// naming the step "Create QR Sketch". Returns true when the host
        /// accepted the finish. Best-effort: a host that refuses still leaves
        /// correct geometry.
        /// </summary>
        private bool TryFinishUndo()
        {
            if (_undoFinished || !_undoRecording || _ext == null) return false;
            try
            {
                if (_ext.FinishRecordingUndoObject2(SketchHostContract.CreationUndoName, false))
                {
                    _undoFinished = true;
                    return true;
                }
                Diag.Log("FinishRecordingUndoObject2 returned false");
            }
            catch (Exception ex)
            {
                Diag.Log($"FinishRecordingUndoObject2 failed: {ex.Message}");
            }
            return false;
        }
    }
}
