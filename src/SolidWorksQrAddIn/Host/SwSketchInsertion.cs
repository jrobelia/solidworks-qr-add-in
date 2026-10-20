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
        private readonly ISketchManager _mgr;
        private readonly IFeature _sketchFeature;
        private readonly IModelDocExtension? _ext;
        private readonly IMathUtility? _math;
        private readonly bool _undoRecording;
        private readonly SketchManagerFlagSnapshot _previousFlags;
        private readonly List<SketchEdge> _buffered = new List<SketchEdge>();
        private readonly List<QrConstructionSquare> _stagedSquares =
            new List<QrConstructionSquare>();
        private QrCirclePlan? _stagedCirclePlan;
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
        /// <param name="previousFlags">
        /// The sketch-manager flag values from before the session — restored
        /// on every exit path.
        /// </param>
        internal SwSketchInsertion(
            IModelDoc2 doc, ISketchManager mgr, IFeature sketchFeature,
            IModelDocExtension? ext, IMathUtility? math, bool undoRecording,
            SketchManagerFlagSnapshot previousFlags)
        {
            _doc = doc;
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
        /// sketch block instance whose definition is drawn at the staged
        /// size and whose instance stays at Scale 1 — the PropertyManager
        /// scale field is a resize FACTOR (spec #10, ADR 0004: the earlier
        /// unit-proportioned definition broke SolidWorks' interactive
        /// contour picker; a user-made block is always real-size + Scale 1).
        /// </summary>
        public void AddLine(double x1, double y1, double x2, double y2)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            // The contract's mutual exclusion (ISketchInsertion): a staged
            // circle plan owns the sketch — contour edges staged under it
            // would be silently dropped at commit, so refuse instead.
            if (_stagedCirclePlan != null)
                throw new SketchInsertionException(
                    "A circle-dot plan is already staged on this session — " +
                    "contour edges and the circle pattern are mutually exclusive.");
            _buffered.Add(new SketchEdge(new SketchPoint2(x1, y1), new SketchPoint2(x2, y2)));
        }

        /// <summary>
        /// Stages the model's construction squares — the QR square and the
        /// keep-out square in sketch-space metres at the requested size —
        /// for commit-time emission INSIDE the block definition (ADR 0003).
        /// Nothing is drawn yet: <see cref="KeepOpen"/> draws them into
        /// the same real-size definition as the contours, flagged
        /// construction, BEFORE the selection fold that forms the block —
        /// the squares then scale and move with the instance with no
        /// host-side constraints, dimensions, or equations.
        /// </summary>
        public void AddConstructionSquares(IReadOnlyList<QrConstructionSquare> squares)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            if (squares != null)
                _stagedSquares.AddRange(squares);
        }

        /// <summary>
        /// Stages a circle-dot emission plan (spec #13, ticket #38) — the
        /// post-invert emission mask, pitch, dot diameter and pattern grid.
        /// <see cref="KeepOpen"/> realizes it through the ticket-#39 native
        /// emitter: one seed circle plus a two-directional sketch linear
        /// pattern with per-instance skip (docs/PATTERN-SPIKE.md).
        /// </summary>
        public void AddCirclePlan(QrCirclePlan plan)
        {
            if (_committed || _aborted)
                throw new SketchInsertionException("The insertion session is already finished.");
            // Mutual exclusion, enforced the other way round: KeepOpen would
            // silently prefer the plan and drop every buffered contour edge.
            if (_buffered.Count > 0)
                throw new SketchInsertionException(
                    $"Contour edges are already staged on this session ({_buffered.Count}) — " +
                    "a circle-dot plan and contour edges are mutually exclusive.");
            _stagedCirclePlan = plan;
        }

        public void KeepOpen()
        {
            if (_stagedCirclePlan != null)
                RealizeAsCirclePattern();
            else if (_buffered.Count > 0)
                RealizeAsBlockInstance();
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
        /// The staged-geometry → block-instance realization (spec #10,
        /// amended by ADR 0004), run inside the open sketch edit:
        ///   1. the definition is drawn at the STAGED size — every buffered
        ///      contour edge and construction square lands as ordinary
        ///      sketch lines at its real sketch-space metres, exactly the
        ///      geometry a user draws before Tools > Blocks > Make;
        ///   2. the staged construction squares are construction-flagged
        ///      — they ride INTO the block definition (ADR 0003), so the
        ///      QR footprint and the four-cell quiet-zone boundary scale
        ///      and move with the instance, and construction geometry
        ///      never feeds a profile;
        ///   3. the by-object selection converts ONLY those drawn lines —
        ///      contours and squares alike — into the definition and its
        ///      first instance (scale 1 at the origin);
        ///   4. the instance stays at Scale 1 — its Scale field is a resize
        ///      FACTOR of the created size, not a size readout.
        /// ADR 0004: an earlier design drew the def unit-proportioned
        /// (1 doc unit) and carried the size on instance Scale — at
        /// Scale 25 SolidWorks' interactive contour picker refused the
        /// sketch ("doubly nested contour"); engineer-verified the same
        /// block extrudes at Scale 1 and 2, refuses at 10+ — the cutoff
        /// sits between 2 and 10. A
        /// block the user makes by hand is always real-size + Scale 1 —
        /// matching that shape restores the interactive extrude path
        /// while keeping every block property (move/scale as a unit,
        /// def-internal squares, clean explode).
        /// Live-verified (consexp, docs/BLOCK-GATES.md round 4): construction
        /// segments inside a block definition render on the instance, track
        /// Scale edits and drags, keep the construction flag through explode,
        /// and never add a sketch region.
        /// Any failure surfaces as a <see cref="SketchInsertionException"/>
        /// so the command's single cleanup path owns the partial sketch.
        /// </summary>
        private void RealizeAsBlockInstance()
        {
            // 1. Draw the definition at the staged size — real sketch-space
            //    metres, no normalization (ADR 0004). These are ordinary
            //    sketch lines until the block-make step swallows them.
            var drawn = new List<ISketchSegment>(_buffered.Count + 8);
            for (var i = 0; i < _buffered.Count; i++)
            {
                var e = _buffered[i];
                drawn.Add(DrawSketchEdge(
                    e.Start.X, e.Start.Y, e.End.X, e.End.Y,
                    "block definition", $"line {i + 1} of {_buffered.Count}"));
            }

            // 2. The staged construction squares — same real-size draw,
            //    then construction-flagged, so they land in the block's
            //    selection fold below and become definition content that
            //    tracks the instance's transforms.
            DrawConstructionSquares((role, segment) => drawn.Add(segment));

            if (_ext == null || _math == null)
                throw new SketchInsertionException(
                    "The document exposed no extension/math interface — the QR block could not be formed.");

            // 3. Select exactly what this session drew — BY OBJECT, not
            //    SelectAll. Measured live (undo-callback reinsert, round-1
            //    blockqr gate): inside the API undo object's redo the blanket
            //    select-all delivered an empty selection and
            //    MakeSketchBlockFromSelected formed no definition. Selecting
            //    each drawn segment via Select4 is exact in every context —
            //    and the fold takes the construction squares too, which is
            //    the point: they become definition geometry, not host lines.
            ISketchBlockDefinition? def;
            try
            {
                _doc.ClearSelection2(true);
                var selected = 0;
                foreach (var segment in drawn)
                {
                    try { if (segment.Select4(true, null)) selected++; }
                    catch { /* one refused segment is not fatal — counted below */ }
                }
                if (selected < drawn.Count)
                    throw new SketchInsertionException(
                        $"Only {selected} of {drawn.Count} block-definition segments could be " +
                        "selected — the block make would be partial or empty.");
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

            // 4. The make leaves instance #1 in the sketch at scale 1 — the
            //    honest check is only that one landed. Scale stays 1: the
            //    field is a resize FACTOR of the created size (ADR 0004).
            try
            {
                var sketch = _sketchFeature as ISketch
                    ?? throw new SketchInsertionException(
                        "The sketch feature did not expose its sketch interface.");
                if (sketch.GetSketchBlockInstanceCount() < 1)
                    throw new SketchInsertionException(
                        "The block definition formed but placed no instance in the sketch.");
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The block instance could not be read back: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The circle-mode commit stage (spec #13, tickets #39/#40) — the
        /// recipe docs/PATTERN-SPIKE.md proved live, run inside the open
        /// sketch edit:
        ///   1. interactive-mode insertion — the pattern route was proven
        ///      with AddToDB OFF (the contour path's bulk mode exists for
        ///      its dense line sets); the session flag snapshot restores
        ///      everything on exit;
        ///   2. the staged construction squares land as PLAIN construction
        ///      sketch entities — circle mode has no block definition to
        ///      carry them — drawn as CreateCornerRectangle closed
        ///      profiles (one merged sketch point per corner; the PR-#43
        ///      QA defect was per-edge CreateLine's stacked endpoints),
        ///      keeping the drawn edges per role: the #40 dimensions hang
        ///      on them;
        ///   3. the master-geometry dimensions (ticket #40, PAT4/PAT7): one
        ///      DRIVING dim on the QR square's bottom edge (the user-facing
        ///      resize handle, "D_box"), then the keep-out square's offset
        ///      and size dims;
        ///   4. one seed circle via CreateCircleByRadius at the plan's seed
        ///      centre — a guaranteed-emitting cell; pattern instance (1,1)
        ///      can never be skipped, so the seed is safe there;
        ///   5. the seed selected BY OBJECT (Select4, coordinate picks are
        ///      ambiguous next to the direction lines), then
        ///      ISketchManager.CreateLinearSketchStepAndRepeat spans the
        ///      grid at cell pitch — columns +x, rows −y (AngleX 0, AngleY
        ///      3π/2 — the −π/2 literal is refused, PAT12 — the plan's
        ///      upright-code frame). The plan's
        ///      DeleteInstances string is passed verbatim: 1-based
        ///      "(col,row)" pairs, the only skip format that works on this
        ///      build. X/Y spacing dims stay ON — they are the driving
        ///      dims the equations below target. Instance-count dims and
        ///      the angle dim stay off;
        ///   6. the remaining driven dims (PAT4/PAT5): a diameter dim on the
        ///      seed arc, and horizontal + vertical dims from the pattern-
        ///      anchor corner (the square corner the seed cell sits inside)
        ///      to the seed's centre SKETCHPOINT — points select via
        ///      GetSketchPoints + Select4, never coordinate picks;
        ///   7. the equation table (QrCircleEquations) installs through
        ///      IEquationMgr.Add + EvaluateAll + EditRebuild3 — spacing =
        ///      box/N, diameter = spacing·f, seed offsets = spacing/2,
        ///      quiet-zone dims = spacing·4 / spacing·M — all inside the
        ///      open sketch edit;
        ///   8. an arc census is the honest check: exactly one non-
        ///      construction ARC per emitted dot, each centre claiming one
        ///      emitted cell, no skipped cell carrying an arc — the same
        ///      QrCentreCensus the live gate runs. A region count was the
        ///      first pick, but a face support mixes its boundary loop and
        ///      edge-split dots into GetSketchRegionCount, so regions read
        ///      high on faces; arc entities are support-agnostic. A
        ///      mismatch means the skip string was ignored — wrong
        ///      geometry — and the abort path owns the partial sketch.
        /// Never EditDelete instances (freezes the dim drive) and never
        /// EditLinearSketchStepAndRepeat after skipping (resurrects every
        /// instance) — the create-time string is the only route.
        /// </summary>
        private void RealizeAsCirclePattern()
        {
            var plan = _stagedCirclePlan!;
            var sketch = _sketchFeature as ISketch
                ?? throw new SketchInsertionException(
                    "The sketch feature did not expose its sketch interface.");
            string sketchName = _sketchFeature.Name ?? "Sketch";

            // 1. The spike's flag arrangement for the pattern route —
            //    interactive-mode draws, inference off (AddToDB is the
            //    contour path's bulk-insert workaround, not this route's).
            try { _mgr.AddToDB = false; } catch { }
            try { _mgr.AutoInference = false; } catch { }

            // 2. The construction squares as plain sketch entities — drawn
            //    before the seed so the pattern step's selection state is
            //    only ever the seed circle. The drawn edges are kept per
            //    role: the master-geometry dimensions reference them.
            //    The CLOSED draw (CreateCornerRectangle — one merged corner
            //    point per square corner) is required here: the corner
            //    sketch points are what the seed-offset dims and the PAT6
            //    coincident binds grab, and per-edge CreateLine leaves two
            //    stacked endpoints at every corner (the PR-#43 QA defect).
            var qrEdges = new List<ISketchSegment>();
            var keepEdges = new List<ISketchSegment>();
            DrawConstructionSquaresClosed((role, segment) =>
                (role == QrSquareRole.QrSquare ? qrEdges : keepEdges).Add(segment));
            double qrMin = 0, keepMin = 0;
            foreach (var square in _stagedSquares)
            {
                double lo = Math.Min(square.Vertices[0].X, square.Vertices[0].Y);
                if (square.Role == QrSquareRole.QrSquare) qrMin = lo; else keepMin = lo;
            }
            if (qrEdges.Count < 4)
                throw new SketchInsertionException(
                    "The staged layout carried no QR square — the master dimension " +
                    "has no edge to sit on.");

            // 3. The driving dimension the user edits (PAT4's "D_box") on the
            //    QR square's bottom edge, then the quiet-zone dims: both
            //    edge-to-edge offsets and the keep-out width (PAT7 — the
            //    equal-relation route is a no-op; dims + equations are it).
            //    Dimension names are discovered, never assumed — the sketch
            //    numbers them D1.. in creation order, and the equation table
            //    binds to the discovered "D{n}@{sketch}" names.
            var dims = new QrCircleDimScheme();
            double p = plan.PitchM;
            var boxDim = DimEdge(qrEdges[0], vertical: false, 0.0, qrMin - p,
                "QR-square master dimension");
            dims.Box = EqName(boxDim, sketchName, "QR-square master dimension");
            if (keepEdges.Count >= 4)
            {
                var qox = DimBetweenEdges(qrEdges[3], keepEdges[3],
                    vertical: false, qrMin - 2 * p, qrMin - p,
                    "quiet-zone left-offset dimension");
                dims.QuietOffsetX = EqName(qox, sketchName, "quiet-zone left-offset dimension");
                dims.QuietOffsetXSign = SignOf(qox);
                var qoy = DimBetweenEdges(qrEdges[0], keepEdges[0],
                    vertical: true, keepMin - p, (qrMin + keepMin) / 2.0,
                    "quiet-zone bottom-offset dimension");
                dims.QuietOffsetY = EqName(qoy, sketchName, "quiet-zone bottom-offset dimension");
                dims.QuietOffsetYSign = SignOf(qoy);
                var quietSize = DimEdge(keepEdges[0],
                    vertical: false, 0.0, keepMin - p, "keep-out square size dimension");
                dims.QuietSize = EqName(quietSize, sketchName, "keep-out square size dimension");
            }
            var dimsBeforePattern = new HashSet<string>();
            foreach (var d in ScanSketchDimNames(sketchName))
                dimsBeforePattern.Add(d.Name);

            // 4. The seed circle at the plan's seed centre — a cell that
            //    always emits, since instance (1,1) cannot be skipped.
            double radius = plan.DotDiameterM / 2.0;
            ISketchSegment seed;
            try
            {
                seed = _mgr.CreateCircleByRadius(
                    plan.SeedCentreM.X, plan.SeedCentreM.Y, 0.0, radius) as ISketchSegment
                    ?? throw new SketchInsertionException(
                        "CreateCircleByRadius returned no segment for the seed circle.");
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"CreateCircleByRadius failed for the seed circle: {ex.Message}", ex);
            }

            // 5. Pattern the seed across the grid. Object-select the seed —
            //    the spike's coordinate-pick fallback (a point ON the
            //    circle) stays as the last resort.
            try { _doc.ClearSelection2(true); } catch { }
            bool seedSelected = false;
            try { seedSelected = seed.Select4(false, null); } catch { }
            if (!seedSelected && _ext != null)
            {
                try
                {
                    seedSelected = _ext.SelectByID2(
                        "", "SKETCHSEGMENT",
                        plan.SeedCentreM.X + radius, plan.SeedCentreM.Y, 0.0,
                        false, 0, null, (int)swSelectOption_e.swSelectOptionDefault);
                }
                catch { }
            }
            if (!seedSelected)
                throw new SketchInsertionException(
                    "The seed circle could not be selected for patterning.");

            bool created;
            try
            {
                created = _mgr.CreateLinearSketchStepAndRepeat(
                    plan.PatternCells, plan.PatternCells,
                    plan.PitchM, plan.PitchM,
                    0.0, 3.0 * Math.PI / 2.0,   // −y rows; −π/2 itself is REFUSED (PAT12)
                    plan.DeleteInstances,
                    true, true,          // X/Y spacing dims — driving dims for #40
                    false, false, false);
            }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"CreateLinearSketchStepAndRepeat failed: {ex.Message}", ex);
            }
            if (!created)
                throw new SketchInsertionException(
                    "CreateLinearSketchStepAndRepeat returned false — " +
                    "the circle-dot pattern was not created.");

            // 5b. Find the two spacing dims the pattern just created: the
            //     new "D{n}@{sketch}" names whose value is the pitch (count
            //     dims, when a build also emits them, hold the instance
            //     count instead). Creation order puts X first (PAT4's
            //     D2=X, D3=Y); both equations are identical either way.
            var spacing = new List<string>();
            foreach (var d in ScanSketchDimNames(sketchName))
            {
                if (dimsBeforePattern.Contains(d.Name)) continue;
                double v;
                try { v = d.Dim.SystemValue; } catch { continue; }
                if (Math.Abs(v - p) < p * 0.001)
                    spacing.Add(d.Name);
            }
            if (spacing.Count < 2)
                throw new SketchInsertionException(
                    $"The pattern produced {spacing.Count} pitch-valued spacing " +
                    "dimensions, not two — the master-equation wiring has nothing " +
                    "to drive.");
            dims.SpacingX = spacing[0];
            dims.SpacingY = spacing[1];

            // 6. The seed's diameter dim (PAT5) and the box-corner →
            //    seed-centre position dims (PAT4): the anchor corner is the
            //    square corner the seed cell sits inside — the QR square's
            //    top-left normally, the keep-out's under invert. Points
            //    select by object enumeration, never coordinate picks.
            var seedDia = DimArcDiameter(seed, plan, "seed-dot diameter dimension");
            dims.Diameter = EqName(seedDia, sketchName, "seed-dot diameter dimension");
            double cornerX = plan.SeedCentreM.X - p / 2.0;
            double cornerY = plan.SeedCentreM.Y + p / 2.0;
            var sox = DimBetweenPoints(sketch,
                cornerX, cornerY, plan.SeedCentreM.X, plan.SeedCentreM.Y,
                vertical: false, cornerX + p, cornerY + p,
                "seed horizontal-offset dimension");
            dims.SeedOffsetX = EqName(sox, sketchName, "seed horizontal-offset dimension");
            dims.SeedOffsetXSign = SignOf(sox);
            var soy = DimBetweenPoints(sketch,
                cornerX, cornerY, plan.SeedCentreM.X, plan.SeedCentreM.Y,
                vertical: true, cornerX - p, cornerY - p,
                "seed vertical-offset dimension");
            dims.SeedOffsetY = EqName(soy, sketchName, "seed vertical-offset dimension");
            dims.SeedOffsetYSign = SignOf(soy);

            // 7. The equation table — the proven sequence: IEquationMgr.Add
            //    per entry, then EvaluateAll + EditRebuild3. Every driven
            //    dim funnels through the one master (the box dim): spacing
            //    = box/N, diameter = spacing·f, offsets = spacing/2, quiet
            //    = spacing·4 / spacing·M (docs/CIRCLE-DIMENSIONS.md). The
            //    equations reproduce the drawn values, so driving them
            //    changes nothing visibly — until the master moves.
            // A deleted earlier QR sketch leaves orphaned "D@{sketch}"
            // equations in the manager, and Add refuses (-1) while any
            // exist — the deleted-sketch defect ticket #42 reproduces.
            // Purge them before installing our table; anything that survives
            // references a live feature and stays.
            SketchEquationHygiene.PurgeOrphanedEquations(_doc);
            var eqm = _doc.GetEquationMgr() as IEquationMgr;
            if (eqm == null)
                throw new SketchInsertionException(
                    "The document exposed no equation manager — the pattern " +
                    "could not be wired to the master dimension.");
            foreach (var eq in QrCircleEquations.For(plan, dims))
            {
                int rc;
                try { rc = eqm.Add(-1, eq.Text); }
                catch (Exception ex)
                {
                    throw new SketchInsertionException(
                        $"Installing the equation '{eq.Text}' failed: {ex.Message}", ex);
                }
                if (rc < 0)
                    throw new SketchInsertionException(
                        $"SolidWorks refused the equation '{eq.Text}' (IEquationMgr.Add " +
                        $"returned {rc}).");
            }
            try { eqm.EvaluateAll(); } catch { }
            try { _doc.EditRebuild3(); } catch { }

            // 8. The honest check: one ARC entity per emitted dot, each
            //    centre claiming exactly one emitted cell, every skipped
            //    cell arc-free — the same census the live gate runs
            //    (QrCentreCensus). The earlier version counted REGIONS, but
            //    a face support folds its own boundary loop and any dots
            //    the edge splits into GetSketchRegionCount — a face create
            //    reads high (241 for 228 dots measured, ticket #42) and was
            //    wrongly aborted. Arc entities are support-agnostic; the
            //    PAT0 sibling-COM cast gap resolves through the sketch-point
            //    census inside Evaluate. An unreadable enumeration is not
            //    proof of wrong geometry — the check simply doesn't run.
            object[]? segs = null;
            try { segs = sketch.GetSketchSegments() as object[]; } catch { }
            if (segs != null)
            {
                var arcCentres = new List<SketchPoint2>();
                int arcSegs = 0, siblingArcs = 0;
                foreach (var so in segs)
                {
                    if (so is not ISketchSegment s) continue;
                    int t = -1;
                    try { t = s.GetType(); } catch { }
                    if (t != (int)swSketchSegments_e.swSketchARC) continue;
                    bool cons = false;
                    try { cons = s.ConstructionGeometry; } catch { }
                    if (cons) continue;
                    arcSegs++;
                    ISketchPoint? cp = null;
                    try { cp = (so as ISketchArc)?.GetCenterPoint2() as ISketchPoint; }
                    catch { }
                    if (cp != null) arcCentres.Add(new SketchPoint2(cp.X, cp.Y));
                    else siblingArcs++;
                }
                var sketchPoints = new List<SketchPoint2>();
                try
                {
                    if (sketch.GetSketchPoints() is object[] pts)
                        foreach (var po in pts)
                            if (po is ISketchPoint skp)
                                sketchPoints.Add(new SketchPoint2(skp.X, skp.Y));
                }
                catch { }
                var v = QrCentreCensus.Evaluate(plan, arcCentres, sketchPoints,
                    arcSegs, siblingArcs, plan.PitchM * 0.25);
                if (!v.Exact)
                    throw new SketchInsertionException(
                        $"The circle pattern census failed: arcs={v.ArcSegments} " +
                        $"matched={v.Matched} pointOnly={v.PointOnly} " +
                        $"missing={v.Missing} stray={v.Stray} " +
                        $"forbiddenPresent={v.ForbiddenPresent} " +
                        $"(want {plan.EmittedCount} dots, {plan.SkippedCount} " +
                        "skipped) — skipped instances did not take or dot " +
                        "geometry landed off-plan.");
            }

            // KeepOpen promises the sketch is left in edit mode — but the
            // equation step's EditRebuild3 EXITS it (live-verified in the
            // block spike: "EditRebuild3 exits sketch edit mode ... re-enter
            // first", tools/LiveProbe/Program.cs). The square path never
            // rebuilds so it stays in edit; re-enter the way the spike did —
            // select the sketch feature by name, then InsertSketch(true).
            // Best-effort: a refused re-entry leaves the (fully correct)
            // sketch closed rather than deleted on a technicality.
            if (_mgr.ActiveSketch == null)
            {
                try { _doc.ClearSelection2(true); } catch { }
                bool picked = false;
                if (_ext != null)
                {
                    try
                    {
                        picked = _ext.SelectByID2(
                            sketchName, "SKETCH", 0.0, 0.0, 0.0,
                            false, 0, null,
                            (int)swSelectOption_e.swSelectOptionDefault);
                    }
                    catch { }
                }
                if (picked)
                    try { _mgr.InsertSketch(true); } catch { }
            }
        }

        /// <summary>
        /// Every "D{n}@{sketchName}" parameter the document resolves —
        /// the shared D1..D64 IParameter scan of <see cref="SketchDimensions"/>
        /// (the probes file reads the same surface).
        /// </summary>
        private List<(string Name, IDimension Dim)> ScanSketchDimNames(string sketchName) =>
            SketchDimensions.OfSketch(_doc, sketchName);

        /// <summary>
        /// "D6@Sketch1@Part5.Part" → "D6@Sketch1" — the equation-reference
        /// form (PAT4): dimension name, then "@{sketch}", then a document
        /// tail. The name-shape decision is <see cref="QrEquationText"/>.
        /// LocalReferenceFor (pure, headless-tested); a FullName that does
        /// not carry the expected "…@{sketch}" part could only mint a
        /// silently dead equation reference, so the create refuses instead.
        /// </summary>
        private static string EqName(IDimension dim, string sketchName, string what)
        {
            string full = "";
            try { full = dim.FullName ?? ""; } catch { }
            var reference = QrEquationText.LocalReferenceFor(full, sketchName);
            if (reference == null)
                throw new SketchInsertionException(
                    $"The {what}'s FullName '{full}' is not the expected " +
                    $"'D{{n}}@{sketchName}@<document>' form — its equation " +
                    "reference cannot be derived.");
            return reference;
        }

        /// <summary>
        /// The sign a created distance dim measured, +1/−1 — SolidWorks can
        /// sign point/edge distance dims by direction; the equation mirrors
        /// it so the driven value keeps the geometry on the drawn side.
        /// An unreadable value assumes positive — the drawn geometry is
        /// correct either way and a wrong sign surfaces as a flipped side at
        /// the next master-dim edit (PAT12's live check).
        /// </summary>
        private static int SignOf(IDimension dim)
        {
            try { return dim.SystemValue < 0.0 ? -1 : 1; }
            catch { return 1; }
        }

        /// <summary>
        /// A driving dimension on one sketch edge: select it by object, add
        /// the dim at the given text position, return the IDimension.
        /// </summary>
        private IDimension DimEdge(
            ISketchSegment edge, bool vertical, double textX, double textY, string what)
        {
            _doc.ClearSelection2(true);
            bool sel = false;
            try { sel = edge.Select4(false, null); } catch { }
            if (!sel)
                throw new SketchInsertionException(
                    $"The {what}'s edge could not be selected.");
            return AddDim(vertical, textX, textY, what);
        }

        /// <summary>
        /// A driving distance dimension between two sketch edges — PAT7's
        /// quiet-zone offset pattern (Select4 both, then the dim call).
        /// </summary>
        private IDimension DimBetweenEdges(
            ISketchSegment a, ISketchSegment b, bool vertical,
            double textX, double textY, string what)
        {
            _doc.ClearSelection2(true);
            bool selA = false, selB = false;
            try { selA = a.Select4(false, null); } catch { }
            try { selB = b.Select4(true, null); } catch { }
            if (!selA || !selB)
                throw new SketchInsertionException(
                    $"The {what}'s edges could not be selected.");
            return AddDim(vertical, textX, textY, what);
        }

        /// <summary>
        /// A driving distance dimension between two sketch POINTS — PAT4's
        /// box-corner → seed-centre offsets. Points select via
        /// GetSketchPoints + Select4 (coordinate picks are ambiguous next to
        /// arcs and direction lines — the spike's gotcha).
        /// </summary>
        private IDimension DimBetweenPoints(
            ISketch sketch, double ax, double ay, double bx, double by,
            bool vertical, double textX, double textY, string what)
        {
            _doc.ClearSelection2(true);
            bool selA = SelSketchPointAt(sketch, ax, ay, append: false);
            bool selB = SelSketchPointAt(sketch, bx, by, append: true);
            if (!selA || !selB)
                throw new SketchInsertionException(
                    $"The {what}'s endpoints could not be selected " +
                    $"(corner: {selA}, seed centre: {selB}).");
            return AddDim(vertical, textX, textY, what);
        }

        /// <summary>
        /// A driving diameter dimension on the seed circle — PAT5. The kept
        /// seed handle is selected first; if the pattern invalidated it the
        /// arc is re-found by centre (the only unambiguous match).
        /// </summary>
        private IDimension DimArcDiameter(ISketchSegment seed, QrCirclePlan plan, string what)
        {
            _doc.ClearSelection2(true);
            bool sel = false;
            try { sel = seed.Select4(false, null); } catch { }
            if (!sel)
                sel = SelectCircleArcAt(plan.SeedCentreM.X, plan.SeedCentreM.Y);
            if (!sel)
                throw new SketchInsertionException(
                    $"The seed circle could not be selected for its {what}.");
            IDimension? dim = null;
            try
            {
                var dd = _doc.IAddDiameterDimension2(
                    plan.SeedCentreM.X + plan.PitchM,
                    plan.SeedCentreM.Y + plan.PitchM, 0)
                    as IDisplayDimension;
                dim = DimOf(dd);
            }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The {what} could not be created: {ex.Message}", ex);
            }
            if (dim == null)
                throw new SketchInsertionException(
                    $"SolidWorks created no {what}.");
            return dim;
        }

        /// <summary>Selects the sketch arc whose centre sits at (cx, cy).</summary>
        private bool SelectCircleArcAt(double cx, double cy)
        {
            var sketch = _sketchFeature as ISketch;
            object[]? segs = null;
            try { segs = sketch?.GetSketchSegments() as object[]; } catch { }
            if (segs == null) return false;
            foreach (var so in segs)
            {
                if (so is not ISketchArc arc || so is not ISketchSegment seg) continue;
                var centre = arc.GetCenterPoint2() as ISketchPoint;
                if (centre == null) continue;
                double dx = centre.X - cx, dy = centre.Y - cy;
                if (dx * dx + dy * dy > 1e-12) continue;
                try { if (seg.Select4(false, null)) return true; } catch { }
            }
            return false;
        }

        /// <summary>
        /// The shared "select two points" helper from the spike: the sketch
        /// point NEAREST (x,y), object-selected — SelectByID2 coordinates
        /// are ambiguous next to arcs, endpoints and direction lines.
        /// </summary>
        private static bool SelSketchPointAt(ISketch sketch, double x, double y, bool append)
        {
            object[]? pts = null;
            try { pts = sketch.GetSketchPoints() as object[]; } catch { }
            if (pts == null) return false;
            ISketchPoint? best = null;
            double bd = double.MaxValue;
            foreach (var po in pts)
            {
                if (po is not ISketchPoint pt) continue;
                double dx = pt.X - x, dy = pt.Y - y, d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = pt; }
            }
            if (best == null || bd > 1e-12) return false;   // >~1 µm = miss
            try { return best.Select4(append, null); }
            catch { return false; }
        }

        /// <summary>The actual IAdd*Dimension2 call — horizontal or vertical.</summary>
        private IDimension AddDim(bool vertical, double textX, double textY, string what)
        {
            IDimension? dim = null;
            try
            {
                var dd = vertical
                    ? _doc.IAddVerticalDimension2(textX, textY, 0) as IDisplayDimension
                    : _doc.IAddHorizontalDimension2(textX, textY, 0) as IDisplayDimension;
                dim = DimOf(dd);
            }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The {what} could not be created: {ex.Message}", ex);
            }
            if (dim == null)
                throw new SketchInsertionException(
                    $"SolidWorks created no {what}.");
            return dim;
        }

        /// <summary>DisplayDimension → IDimension (PAT4's GetDimension; GetDimension2 fallback).</summary>
        private static IDimension? DimOf(IDisplayDimension? dd)
        {
            if (dd == null) return null;
            object? raw = null;
            try { raw = dd.GetDimension(); } catch { }
            return (raw as IDimension) ?? (dd.GetDimension2(0) as IDimension);
        }

        /// <summary>
        /// Draws every staged construction-square edge and flags it
        /// construction — the BLOCK path's square loop (the circle path
        /// draws closed rectangles instead — see
        /// <see cref="DrawConstructionSquaresClosed"/>): per-edge
        /// CreateLine is what folds into the definition byte-identically,
        /// and the unclosed corner endpoints never matter inside a block —
        /// the instance renders the definition, no host dim or constraint
        /// touches its corners. <paramref name="sink"/> collects each
        /// drawn segment with its square's role. A swallowed flag failure
        /// would land the square as SOLID geometry — profile content that
        /// feeds extrudes — so it propagates honestly and the abort path
        /// owns the partial sketch.
        /// </summary>
        private void DrawConstructionSquares(Action<QrSquareRole, ISketchSegment> sink)
        {
            var squareIndex = 0;
            foreach (var square in _stagedSquares)
            {
                string label = square.Role == QrSquareRole.QrSquare
                    ? "QR square" : "keep-out square";
                foreach (var e in square.Edges)
                {
                    squareIndex++;
                    var segment = DrawSketchEdge(
                        e.Start.X, e.Start.Y, e.End.X, e.End.Y,
                        label, $"edge {squareIndex}");
                    try { segment.ConstructionGeometry = true; }
                    catch (Exception ex)
                    {
                        throw new SketchInsertionException(
                            $"The construction-geometry flag could not be applied to the " +
                            $"{label} (edge {squareIndex}): {ex.Message}", ex);
                    }
                    sink(square.Role, segment);
                }
            }
        }

        /// <summary>
        /// The circle path's construction-square draw — one
        /// ISketchManager.CreateCornerRectangle per staged square: the
        /// canonical rectangle primitive (the UI rectangle tool's own
        /// emission) whose four edges SHARE corner sketch points, so the
        /// square lands as a genuinely closed profile. Per-edge CreateLine
        /// — still what the block path draws, unchanged — leaves TWO
        /// stacked endpoints at every corner: SolidWorks never merges the
        /// endpoints of separately created lines, so the unclosed square
        /// was not a rigid profile and each corner grab (the seed-offset
        /// dims' corner point, the PAT6 coincident binds) held only one of
        /// the two edges meeting there — the PR-#43 QA defect.
        /// The rectangle's returned segments carry no contractual order —
        /// <paramref name="sink"/> receives them in the square's canonical
        /// edge order (<see cref="QrConstructionSquare.Edges"/>: bottom,
        /// right, top, left), matched by bound via
        /// <see cref="QrSquareEdges.OrderFor"/>, so the dim wiring's index
        /// roles are exactly the line-drawn ones. A swallowed flag failure
        /// would land the square as SOLID profile geometry, so it
        /// propagates honestly like the shared loop's.
        /// </summary>
        private void DrawConstructionSquaresClosed(
            Action<QrSquareRole, ISketchSegment> sink)
        {
            var squareIndex = 0;
            foreach (var square in _stagedSquares)
            {
                squareIndex++;
                string label = square.Role == QrSquareRole.QrSquare
                    ? "QR square" : "keep-out square";
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var v in square.Vertices)
                {
                    minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y);
                    maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y);
                }
                object? raw = null;
                try
                {
                    raw = _mgr.CreateCornerRectangle(
                        minX, minY, 0.0, maxX, maxY, 0.0);
                }
                catch (Exception ex)
                {
                    throw new SketchInsertionException(
                        $"CreateCornerRectangle failed for the {label} " +
                        $"(square {squareIndex}): {ex.Message}", ex);
                }
                var segments = new List<ISketchSegment>(4);
                if (raw is object[] returned)
                    foreach (var o in returned)
                        if (o is ISketchSegment s)
                            segments.Add(s);
                if (segments.Count != 4)
                    throw new SketchInsertionException(
                        $"CreateCornerRectangle returned {segments.Count} segments " +
                        $"for the {label} (square {squareIndex}), not the " +
                        "rectangle's four.");
                var observed = new List<SketchEdge>(4);
                foreach (var s in segments)
                    observed.Add(ObservedEdge(s, label, squareIndex));
                // Quarter-side tolerance: the endpoints read back exactly the
                // staged vertices, and no two sides sit closer than a side.
                var order = QrSquareEdges.OrderFor(
                    square, observed, (maxX - minX) * 0.25);
                if (order == null)
                    throw new SketchInsertionException(
                        $"The {label}'s rectangle returned an edge set that is " +
                        $"not the square's four sides (square {squareIndex}) — " +
                        "the dim wiring cannot identify the bottom/left edges.");
                foreach (int idx in order)
                {
                    var segment = segments[idx];
                    try { segment.ConstructionGeometry = true; }
                    catch (Exception ex)
                    {
                        throw new SketchInsertionException(
                            "The construction-geometry flag could not be applied " +
                            $"to the {label} (square {squareIndex}): {ex.Message}",
                            ex);
                    }
                    sink(square.Role, segment);
                }
            }
        }

        /// <summary>
        /// One returned rectangle edge as a <see cref="SketchEdge"/> for the
        /// side matching — endpoints live on the ISketchLine sibling
        /// interface (GetStartPoint2/GetEndPoint2; ISketchSegment carries
        /// none).
        /// </summary>
        private static SketchEdge ObservedEdge(
            ISketchSegment segment, string label, int squareIndex)
        {
            try
            {
                if (segment is ISketchLine line &&
                    line.GetStartPoint2() is ISketchPoint a &&
                    line.GetEndPoint2() is ISketchPoint b)
                    return new SketchEdge(
                        new SketchPoint2(a.X, a.Y), new SketchPoint2(b.X, b.Y));
            }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"A returned edge of the {label} (square {squareIndex}) " +
                    $"could not be read for the side matching: {ex.Message}", ex);
            }
            throw new SketchInsertionException(
                $"A returned edge of the {label} (square {squareIndex}) " +
                "exposed no endpoints.");
        }

        /// <summary>
        /// One CreateLine + error wrap for the staged-edge draw — shared by
        /// the contour loop and the block path's square loop so every
        /// staged edge lands the same way and fails the same way.
        /// </summary>
        private ISketchSegment DrawSketchEdge(
            double x1, double y1, double x2, double y2, string what, string which)
        {
            try
            {
                var segment = _mgr.CreateLine(x1, y1, 0.0, x2, y2, 0.0) as ISketchSegment;
                if (segment == null)
                    throw new SketchInsertionException(
                        $"CreateLine returned no segment while drawing the {what} ({which}).");
                return segment;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"CreateLine failed while drawing the {what} ({which}): {ex.Message}", ex);
            }
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
