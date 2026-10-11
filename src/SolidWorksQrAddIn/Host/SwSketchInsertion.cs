using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private double _appliedScale = 1.0;
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
            //    until the block-make step swallows them into the block.
            var drawn = new List<ISketchSegment>(_buffered.Count);
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
                    drawn.Add(segment);
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

            // 2. Select exactly what this session drew — BY OBJECT, not
            //    SelectAll. Measured live (undo-callback reinsert, round-1
            //    blockqr gate): inside the API undo object's redo the blanket
            //    select-all delivered an empty selection and
            //    MakeSketchBlockFromSelected formed no definition. Selecting
            //    each drawn segment via Select4 is exact in every context —
            //    and can never swallow the construction geometry emitted
            //    after this step (there is none yet anyway).
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
                _appliedScale = scaleDocUnits;
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The block instance's Scale could not be applied: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The staged construction-layout emission (spec #10/#17/#18), run
        /// inside the open sketch edit AFTER the block instance exists. The
        /// emission walks the dimension spec's roles in order — the driven
        /// square first (the keep-out's equations reference its dimension):
        /// draw its four edges as construction geometry, coincident-bind them
        /// to the block's outer edges so a Scale edit drags the square, then
        /// land the driven dimension on it — the live size readout — then
        /// emit the equation-linked keep-out square.
        /// </summary>
        private void EmitConstructionLayout()
        {
            // Relations and dimensions need the live solver — AddToDB bulk
            // mode (still on from the contour insert) adds geometry without
            // solving, so the coincident binds' read-back verifies stale
            // positions. Normal insert mode for the whole layout phase; the
            // flag snapshot restores the session state at exit anyway.
            bool prevAddToDB = false;
            try { prevAddToDB = _mgr.AddToDB; _mgr.AddToDB = false; } catch { }
            try { EmitConstructionLayoutBody(); }
            finally { try { _mgr.AddToDB = prevAddToDB; } catch { } }
        }

        private void EmitConstructionLayoutBody()
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

            // The square is drawn at the exact staged extents. Round-2's
            // binding experiment matrix (docs/BLOCK-GATES.md) exhausted every
            // public route for tying a host edge to a block-internal edge —
            // relation-manager AddRelation, coordinate picks in sketch /
            // model / definition space, object-selected definition segments,
            // EXTSKETCHSEG picks, SelectByRay, block-instance object dims,
            // SketchUseEdge2 conversion, and driving zero-gap dimensions —
            // and SolidWorks binds every one of them at the DEFINITION's raw
            // coordinates (or lands an arbitrary internal segment), never at
            // the scaled instance's projected line. A coincident to the raw
            // def position would actively mis-place the square, so the
            // honest shape is exact-drawn host geometry with no fake binds:
            // it represents the footprint correctly and simply does not
            // follow a later Scale edit (recorded as a remaining gate).
            var qrDrawn = DrawConstructionSquare(square);
            MergeSquareCorners(qrDrawn);
            ConstrainQrSquareShape(qrDrawn);
            string qrDimName = AddDrivenDimension(square, qrDrawn[0]);

            var keepOut = _stagedSquares.Find(s => s.Role == spec.EquationLinkedSquare);
            if (keepOut == null)
                throw new SketchInsertionException(
                    "The dimension spec names a construction square that was never staged.");
            EmitKeepOutSquare(square, keepOut, qrDrawn, qrDimName);
        }

        /// <summary>
        /// Draws the square's four edges at their model coordinates as
        /// construction geometry and returns the segment handles — the
        /// binding and dimension steps select them by object, never by
        /// coordinates (coordinate picks at the square's own locations are
        /// ambiguous once block geometry overlaps them).
        /// </summary>
        private List<ISketchSegment> DrawConstructionSquare(
            QrConstructionSquare square, double inset = 0.0)
        {
            string label = square.Role == QrSquareRole.QrSquare ? "QR square" : "keep-out square";
            var drawn = new List<ISketchSegment>(4);
            // An inset square is built from vertices shrunk toward the
            // centroid — adjacent edges still share their shrunken corner,
            // so the corner merge below finds coincident endpoints.
            var verts = square.Vertices;
            var shrunk = new List<SketchPoint2>(verts.Count);
            if (inset > 0.0)
            {
                double cx = verts.Average(q => q.X), cy = verts.Average(q => q.Y);
                foreach (var q in verts)
                    shrunk.Add(new SketchPoint2(
                        q.X + Math.Sign(cx - q.X) * inset,
                        q.Y + Math.Sign(cy - q.Y) * inset));
            }
            int i = 0;
            foreach (var e in square.Edges)
            {
                i++;
                var a = inset > 0.0 ? shrunk[(i - 1) % verts.Count] : e.Start;
                var b = inset > 0.0 ? shrunk[i % verts.Count] : e.End;
                try
                {
                    var segment = _mgr.CreateLine(
                        a.X, a.Y, 0.0, b.X, b.Y, 0.0) as ISketchSegment;
                    if (segment == null)
                        throw new SketchInsertionException(
                            $"CreateLine returned no segment while drawing the {label} " +
                            $"(edge {i} of 4).");
                    segment.ConstructionGeometry = true;
                    drawn.Add(segment);
                }
                catch (SketchInsertionException) { throw; }
                catch (Exception ex)
                {
                    throw new SketchInsertionException(
                        $"CreateLine failed while drawing the {label} " +
                        $"(edge {i} of 4): {ex.Message}", ex);
                }
            }
            return drawn;
        }

        /// <summary>
        /// Pins the square's shape so the solver can never skew it: axis
        /// orientation on all four edges plus an equal relation on adjacent
        /// sides. The corner merges already share endpoints, so this is the
        /// minimum that keeps the construction square square.
        /// </summary>
        private void ConstrainQrSquareShape(IReadOnlyList<ISketchSegment> drawn)
        {
            for (int i = 0; i < 4 && i < drawn.Count; i++)
                AddOrientationConstraint(drawn[i], horizontal: i % 2 == 0);
            if (drawn.Count > 1)
                AddEqualRelation(drawn[0], drawn[1]);
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
        /// Runs <paramref name="body"/> with the two app-level dimension
        /// prompt toggles forced off. Spike-verified trap: IAddDimension2
        /// hangs an invisible SolidWorks session unless BOTH toggles are off
        /// first — swInputDimValOnCreate (the dimension-value dialog) and
        /// swSketchOverdefiningDimsPromptToSetState (the over-defining →
        /// driven prompt). They are user-preference toggles, snapshotted,
        /// forced off for exactly the creation calls, and restored after —
        /// even on failure.
        /// </summary>
        private T WithDimPromptsSuppressed<T>(Func<T> body)
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
                return body();
            }
            finally
            {
                SetToggle(swUserPreferenceToggle_e.swInputDimValOnCreate, prevInput);
                SetToggle(swUserPreferenceToggle_e.swSketchOverdefiningDimsPromptToSetState, prevOverdef);
            }
        }

        /// <summary>
        /// Lands the QR square's driven dimension — the live size readout —
        /// and returns its name ("D1@SketchN"): the handle the keep-out
        /// square's equations reference. With the square constraint-bound to
        /// the block, the new dimension over-defines, so the suppressed
        /// over-defining prompt makes it land DRIVEN — a readout that tracks
        /// Scale, never a driver.
        /// </summary>
        private string AddDrivenDimension(QrConstructionSquare square, ISketchSegment bottomEdge)
        {
            return WithDimPromptsSuppressed(() =>
            {
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
                string name = "";
                try
                {
                    var dim = dd.GetDimension2(0) as IDimension;
                    name = dim?.FullName ?? "";
                    Diag.Log($"QR square dimension '{name}' driven state: {dim?.DrivenState}");
                }
                catch { /* fall through to the empty-name check below */ }
                if (name.Length == 0)
                    throw new SketchInsertionException(
                        "The QR square's dimension has no name the keep-out " +
                        "equations can reference.");
                return name;
            });
        }

        /// <summary>
        /// The keep-out square emission (spec #10/#18) — the working-matrix
        /// boundary per ADR 0002, always drawn, never a setting. It sits
        /// OUTSIDE the block's extent, so it cannot be coincident-bound to
        /// the block the way the QR square is. Instead the emission draws it
        /// at the staged matrix-boundary position, coincident-merges its
        /// corners into a connected square, pins orientation and squareness
        /// with relations, then positions AND sizes it entirely by equations
        /// off the QR square's driven dimension:
        ///   - the side dim carries "D2 = D1 · (N+8)/(N+2r′)" — the
        ///     quiet-zone offset stays the fixed cell count at every Scale,
        ///     because the cell pitch rescales with the block (a constant
        ///     millimetre addend would hold the margin in mm and the cell
        ///     count would drift — wrong per ISO 18004);
        ///   - two offset dims (keep-out edge ↔ the QR square's
        ///     corresponding edge, one per axis) carry
        ///     "gap = D1 · (ratio−1)/2" — they hold the square centred on
        ///     the QR square as it grows or travels with the block. The
        ///     ticket's "offset dim placed" is KEPT equation-linked rather
        ///     than deleted: deleting it would free the position DOF and let
        ///     the square drift off-centre on the next rescale.
        /// Every selection here is an entity handle — no coordinate picks:
        /// the keep-out's corner endpoints come from ISketchLine's own point
        /// accessors, and the dims ride on Select4 of the drawn segments.
        /// </summary>
        private void EmitKeepOutSquare(
            QrConstructionSquare qrStaged, QrConstructionSquare keepStaged,
            IReadOnlyList<ISketchSegment> qrDrawn, string qrDimName)
        {
            var drawn = DrawConstructionSquare(keepStaged);
            MergeSquareCorners(drawn);
            for (int i = 0; i < drawn.Count; i++)
                AddOrientationConstraint(drawn[i], horizontal: i % 2 == 0);
            // Equal on adjacent edges forces w = h; the second equal is
            // normally redundant — but if one orientation relation silently
            // fails to take (they are best-effort, no feedback), all-four-
            // equal still forces the unoriented edge into place. A redundant
            // relation costs nothing; a floating fourth side would corrupt
            // the square's DOF bookkeeping for the dims below.
            AddEqualRelation(drawn[0], drawn[1]);
            AddEqualRelation(drawn[2], drawn[3]);

            var names = AddKeepOutDimensions(keepStaged, qrStaged, drawn, qrDrawn);

            double qrSide = qrStaged.Vertices[1].X - qrStaged.Vertices[0].X;
            double keepSide = keepStaged.Vertices[1].X - keepStaged.Vertices[0].X;
            LinkKeepOutEquations(
                qrDimName, names.Side, names.GapX, names.GapY, keepSide / qrSide);
        }

        /// <summary>
        /// Coincident-merges the square's four corners — bulk-drawn segments
        /// never auto-merge endpoints, so each corner is a pair of free
        /// endpoints until related. The endpoint handles come from the drawn
        /// segments themselves (ISketchLine's start/end point accessors),
        /// making every selection deterministic.
        /// </summary>
        private void MergeSquareCorners(IReadOnlyList<ISketchSegment> drawn)
        {
            for (int i = 0; i < drawn.Count; i++)
            {
                var end = (drawn[i] as ISketchLine)?.IGetEndPoint2() as ISketchPoint;
                var start = (drawn[(i + 1) % drawn.Count] as ISketchLine)?.IGetStartPoint2()
                    as ISketchPoint;
                if (end == null || start == null)
                    throw new SketchInsertionException(
                        "A square corner's endpoints could not be read.");
                try
                {
                    _doc.ClearSelection2(true);
                    bool pickedEnd = end.Select4(false, null);
                    bool pickedStart = start.Select4(true, null);
                    if (!pickedEnd || !pickedStart)
                        throw new SketchInsertionException(
                            "A square corner could not be selected for merging.");
                    _doc.SketchAddConstraints("sgCOINCIDENT");
                }
                catch (SketchInsertionException) { throw; }
                catch (Exception ex)
                {
                    throw new SketchInsertionException(
                        $"A square corner merge failed: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// One equal-length relation between two adjacent square edges —
        /// with the orientation relations it makes the connected quad a
        /// square, leaving only centre-x, centre-y, and side free for the
        /// dimensions to pin.
        /// </summary>
        private void AddEqualRelation(ISketchSegment a, ISketchSegment b)
        {
            try
            {
                _doc.ClearSelection2(true);
                if (!a.Select4(false, null) || !b.Select4(true, null))
                    throw new SketchInsertionException(
                        "The square's edges could not be selected " +
                        "for the equal relation.");
                _doc.SketchAddConstraints("sgSAMELENGTH");
            }
            catch (SketchInsertionException) { throw; }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The keep-out square's equal relation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Lands the keep-out square's three dimensions inside one prompt-
        /// toggle guard and returns their names (the handles the equations
        /// reference): the side dim on the bottom edge, then the two offset
        /// dims that measure each axis's gap between the keep-out edge and
        /// the QR square's corresponding edge. Each pins a still-free DOF —
        /// side, then centre-x, then centre-y — so all three land DRIVING,
        /// ready for the equation links.
        /// </summary>
        private (string Side, string GapX, string GapY) AddKeepOutDimensions(
            QrConstructionSquare keepStaged, QrConstructionSquare qrStaged,
            IReadOnlyList<ISketchSegment> drawn, IReadOnlyList<ISketchSegment> qrDrawn)
        {
            return WithDimPromptsSuppressed(() =>
            {
                double kh = (keepStaged.Vertices[1].X - keepStaged.Vertices[0].X) / 2.0;
                double qh = (qrStaged.Vertices[1].X - qrStaged.Vertices[0].X) / 2.0;
                // Text lands outside the keep-out square: the side dim below
                // its bottom edge, the gap dims inside their margins.
                string side = AddNamedDimension(
                    "the keep-out square's side",
                    new[] { drawn[0] },
                    0.0, keepStaged.Vertices[0].Y - Math.Max(kh * 0.16, 0.002));
                string gapX = AddNamedDimension(
                    "the keep-out square's horizontal offset",
                    new[] { drawn[3], qrDrawn[3] },
                    -(kh + qh) / 2.0, 0.0);
                string gapY = AddNamedDimension(
                    "the keep-out square's vertical offset",
                    new[] { drawn[0], qrDrawn[0] },
                    0.0, -(kh + qh) / 2.0);
                return (side, gapX, gapY);
            });
        }

        /// <summary>
        /// One named display dimension on already-determined free DOFs —
        /// selects the entity handles, lands IAddDimension2 at the given
        /// text point, and returns the dimension's name for equation
        /// reference. Must run inside <see cref="WithDimPromptsSuppressed"/>.
        /// </summary>
        private string AddNamedDimension(string label, ISketchSegment[] entities, double x, double y)
        {
            _doc.ClearSelection2(true);
            bool selected = false;
            try
            {
                selected = entities[0].Select4(false, null);
                for (int i = 1; i < entities.Length && selected; i++)
                    selected = entities[i].Select4(true, null);
            }
            catch { selected = false; }
            if (!selected)
                throw new SketchInsertionException(
                    $"The entities for {label} dimension could not be selected.");
            var dd = _doc.IAddDimension2(x, y, 0.0) as IDisplayDimension;
            if (dd == null)
                throw new SketchInsertionException(
                    $"IAddDimension2 returned no dimension for {label}.");
            string name = "";
            try { name = (dd.GetDimension2(0) as IDimension)?.FullName ?? ""; }
            catch { /* the empty-name check below reports honestly */ }
            if (name.Length == 0)
                throw new SketchInsertionException(
                    $"The dimension for {label} has no name the equations can reference.");
            return name;
        }

        /// <summary>
        /// Wires the keep-out square's three dimension links through the
        /// document's equation manager: the side is the QR-square dim × the
        /// cell-count ratio — (N+8)/(N+2r′) computed from the staged
        /// squares' sides — and each offset dim is the QR dim × half the
        /// margin factor. Solve=true evaluates each equation immediately
        /// (EditRebuild3 would exit the sketch edit — the equation manager's
        /// own solve stays inside it).
        /// </summary>
        private void LinkKeepOutEquations(
            string qrDimName, string sideDimName, string gapXName, string gapYName, double ratio)
        {
            EquationMgr mgr;
            try { mgr = _doc.GetEquationMgr(); }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The equation manager could not be read: {ex.Message}", ex);
            }
            if (mgr == null)
                throw new SketchInsertionException(
                    "The document exposed no equation manager — the keep-out " +
                    "dimension could not be linked to the QR square's dimension.");

            string ratioText = ratio.ToString("G17", CultureInfo.InvariantCulture);
            string gapFactor = ((ratio - 1.0) / 2.0).ToString("G17", CultureInfo.InvariantCulture);
            AddEquation(mgr, $"\"{sideDimName}\" = \"{qrDimName}\" * {ratioText}");
            AddEquation(mgr, $"\"{gapXName}\" = \"{qrDimName}\" * {gapFactor}");
            AddEquation(mgr, $"\"{gapYName}\" = \"{qrDimName}\" * {gapFactor}");
        }

        /// <summary>
        /// Adds one equation to the document's equation manager; a refusal
        /// (return index −1 or a COM error) surfaces as a
        /// <see cref="SketchInsertionException"/> so the command's cleanup
        /// path owns the partial sketch.
        /// </summary>
        private void AddEquation(EquationMgr mgr, string equation)
        {
            int index;
            try { index = mgr.Add2(-1, equation, true); }
            catch (Exception ex)
            {
                throw new SketchInsertionException(
                    $"The equation could not be added ('{equation}'): {ex.Message}", ex);
            }
            if (index < 0)
                throw new SketchInsertionException(
                    $"The equation manager refused '{equation}'.");
            Diag.Log($"keep-out equation added at index {index}: {equation}");
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
