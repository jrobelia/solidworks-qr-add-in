using System;
using System.Collections.Generic;
using Clipper2Lib;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The ring-split pass (spec #51): on the emitted set AFTER polarity
    /// selection, open every island-bearing hole — an enclosed component of
    /// un-emitted cells (a void component, below) holding emitted islands,
    /// the grown union's ring → hole → island nesting that SolidWorks'
    /// profile solver refuses as "doubly nested" — before the union pass.
    /// The treatment is hybrid
    /// (the live-proven route of docs/RING-SPLIT-RESEARCH.md §6):
    ///
    ///   * The three finder rings — regular, repeatable geometry at fixed
    ///     matrix locations — stop paying whole modules (ticket #53): a
    ///     thin axis-aligned channel rectangle is subtracted from the grown
    ///     subject at a corner locus of the ring band (off the
    ///     horizontal/vertical centre-lines the finder detection runs
    ///     sample), spanning the band's outer and inner boundaries so the
    ///     ring's hole joins the surrounding void. The channel is sub-cell —
    ///     it cannot exist on the cell mask — so the pass returns the
    ///     channel rectangles for the caller to Difference out of the
    ///     grown subject, and claims the void by cell-set identity so the
    ///     mask-side iteration does not loop on it forever.
    ///   * Every other island-bearing site, at any depth, opens by
    ///     dropping the minimal module-aligned path of emitted cells that
    ///     joins the void to exterior-connected space (ticket #52).
    ///
    /// Cell topology mirrors the grown union exactly (g &lt; U/2): emitted
    /// cells are 8-connected material — grown rects bridge diagonal
    /// contacts — while un-emitted cells are 4-connected void, so an
    /// enclosed 4-connected void component is exactly a union hole and its
    /// enclosed 8-connected emitted components are exactly its islands.
    /// The exterior is the un-emitted flood from the working-matrix border;
    /// under invert the border itself emits, so a channel may instead
    /// terminate on a border cell — dropping it vents the void past the
    /// field edge (the field is clipped at the matrix square) — and the
    /// finder channel vents the same way, a slot through the field to the
    /// matrix edge.
    ///
    /// Square-shape only; under Circle the flag is inert and the pass
    /// never runs (dots emit no contours).
    /// </summary>
    internal static class QrRingSplitter
    {
        /// <summary>
        /// The thin finder-channel width in grid units — the larger of the
        /// integer-grid feature floor (<see cref="QrInsertionGuard.MinGridFeatureUnits"/>)
        /// and the recorded insertion floor
        /// (<see cref="QrInsertionGuard.MinInsertableFeatureMm"/>) expressed
        /// in units at <paramref name="mmPerUnit"/>. Derived from grid
        /// constants and the insertion floor only — never user-entered.
        /// </summary>
        internal static long FinderChannelWidthUnits(double mmPerUnit) =>
            Math.Max(QrInsertionGuard.MinGridFeatureUnits,
                (long)Math.Ceiling(QrInsertionGuard.MinInsertableFeatureMm / mmPerUnit));

        /// <summary>
        /// Opens island-bearing holes in <paramref name="emit"/> (mutated in
        /// place for the cell drops) until none remain, iterating the
        /// detect→treat loop: finder-ring voids are claimed by thin channel
        /// rectangles appended to <paramref name="finderChannels"/> (grid
        /// coordinates of the grown subject's space, for a Difference pass
        /// before the union); every other site drops the minimal cell path —
        /// a channel may legitimately span several cells through stacked
        /// bands. <paramref name="growthUnits"/> is the per-side growth g;
        /// <paramref name="channelWidthUnits"/> the derived channel width
        /// (<see cref="FinderChannelWidthUnits"/>). Returns null on success;
        /// a named <see cref="QrSketchErrorKind.InvalidGeometry"/> error
        /// when a site cannot be opened, no channel locus clears, or the loop
        /// does not converge — all-or-nothing: a partially opened QR is never
        /// delivered.
        /// </summary>
        public static QrSketchError? OpenIslandBearingHoles(
            bool[,] emit, long growthUnits, long channelWidthUnits,
            out int openedSites, out int droppedCells, out Paths64 finderChannels) =>
            OpenIslandBearingHoles(
                emit, growthUnits, channelWidthUnits,
                emit.GetLength(0) * emit.GetLength(1) + 8,
                out openedSites, out droppedCells, out finderChannels);

        /// <summary>
        /// <paramref name="maxPasses"/> bounds the detect→treat loop (the
        /// caller passes cells + 8 — every pass drops ≥1 emitted cell, plus
        /// a small margin over the ≤3 finder-channel claims). The
        /// bound-overshoot refusal cannot be produced by a real matrix —
        /// every finite site is openable — it is internal for the suite.
        /// </summary>
        internal static QrSketchError? OpenIslandBearingHoles(
            bool[,] emit, long growthUnits, long channelWidthUnits, int maxPasses,
            out int openedSites, out int droppedCells, out Paths64 finderChannels)
        {
            openedSites = 0;
            droppedCells = 0;
            finderChannels = new Paths64();
            int m = emit.GetLength(0);
            // Finder voids already claimed by a thin channel: the channel is
            // sub-cell, so the mask goes on reporting the site as
            // island-bearing — claim it by cell-set identity and let the
            // post-union tree check prove the opening. A void that later
            // MERGES into a larger void loses the identity and falls back
            // under the ordinary cell-drop rule.
            var claimed = new List<HashSet<int>>();
            for (var pass = 0; pass < maxPasses; pass++)
            {
                var nesting = Analyze(emit);
                var pending = new List<int>();
                foreach (int voidId in nesting.IslandBearing)
                    if (!claimed.Exists(s => s.SetEquals(nesting.VoidCells[voidId])))
                        pending.Add(voidId);
                if (pending.Count == 0)
                    return null;
                foreach (int voidId in pending)
                {
                    var voidCells = nesting.VoidCells[voidId];
                    var site = MatchFinderVoid(voidCells, m);
                    if (site != null)
                    {
                        var channel = TryPlaceFinderChannel(
                            emit, voidCells, site, growthUnits, channelWidthUnits);
                        if (channel == null)
                            return new QrSketchError(QrSketchErrorKind.InvalidGeometry,
                                "Ring split found a finder ring it cannot open — no " +
                                "corner-locus channel clears the surrounding contours " +
                                "(islands, deeper bands, disjoint neighbours). The " +
                                "request is refused rather than partially opened.");
                        finderChannels.Add(channel);
                        claimed.Add(new HashSet<int>(voidCells));
                    }
                    else
                    {
                        var drops = FindChannel(emit, voidCells);
                        if (drops == null)
                            return new QrSketchError(QrSketchErrorKind.InvalidGeometry,
                                "Ring split found an island-bearing hole it cannot open — no " +
                                "module-aligned cell path joins it to exterior-connected " +
                                "space. The request is refused rather than partially opened.");
                        foreach (int cell in drops)
                        {
                            int row = cell / m, col = cell % m;
                            if (emit[row, col])
                            {
                                emit[row, col] = false;
                                droppedCells++;
                            }
                        }
                    }
                    openedSites++;
                }
            }
            return new QrSketchError(QrSketchErrorKind.InvalidGeometry,
                "Ring split could not open every island-bearing hole — the treatment " +
                "pass did not converge. The request is refused rather than partially opened.");
        }

        // Component-labelling sentinels: a grid entry holds a component id
        // once labelled; before that −2 marks an unvisited cell of the kind
        // being labelled and −1 a cell of the OPPOSITE kind (emitted while
        // labelling void components, un-emitted while labelling emitted).
        private const int NotComponent = -1;
        private const int Unvisited = -2;

        /// <summary>
        /// The nesting structure of the mask: void components are
        /// 4-connected un-emitted cells (component 0 is the exterior — the
        /// border flood, empty under invert), emitted components are
        /// 8-connected. An alternating void↔emitted BFS from the exterior
        /// assigns every component its enclosing parent — a non-exterior
        /// void's non-parent emitted adjacencies are exactly its islands.
        /// </summary>
        private sealed class CellNesting
        {
            public List<int>[] VoidCells = null!;
            public List<int> IslandBearing = null!;
        }

        private static CellNesting Analyze(bool[,] emit)
        {
            int m = emit.GetLength(0);
            var voidOf = new int[m, m];
            var emitOf = new int[m, m];
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                {
                    voidOf[r, c] = emit[r, c] ? NotComponent : Unvisited;
                    emitOf[r, c] = emit[r, c] ? Unvisited : NotComponent;
                }

            var voidCells = new List<List<int>> { new List<int>() };
            var queue = new Queue<int>();
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if ((r == 0 || c == 0 || r == m - 1 || c == m - 1) && voidOf[r, c] == Unvisited)
                    {
                        voidOf[r, c] = 0;
                        queue.Enqueue(r * m + c);
                    }
            FloodVoid(voidOf, 0, voidCells[0], queue);
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if (voidOf[r, c] == Unvisited)
                    {
                        var cells = new List<int>();
                        voidCells.Add(cells);
                        voidOf[r, c] = voidCells.Count - 1;
                        queue.Enqueue(r * m + c);
                        FloodVoid(voidOf, voidCells.Count - 1, cells, queue);
                    }

            var emitCount = 0;
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if (emitOf[r, c] == Unvisited)
                    {
                        emitOf[r, c] = emitCount;
                        queue.Enqueue(r * m + c);
                        while (queue.Count > 0)
                        {
                            int cell = queue.Dequeue();
                            int cr = cell / m, cc = cell % m;
                            for (var dr = -1; dr <= 1; dr++)
                                for (var dc = -1; dc <= 1; dc++)
                                {
                                    if (dr == 0 && dc == 0) continue;
                                    int nr = cr + dr, nc = cc + dc;
                                    if (nr < 0 || nc < 0 || nr >= m || nc >= m) continue;
                                    if (emitOf[nr, nc] != Unvisited) continue;
                                    emitOf[nr, nc] = emitCount;
                                    queue.Enqueue(nr * m + nc);
                                }
                        }
                        emitCount++;
                    }

            // Void↔emitted adjacency over shared cell sides; an emitted
            // border cell is adjacent to the exterior void too (under invert
            // the field touches the matrix edge).
            var voidToEmit = new List<HashSet<int>>(voidCells.Count);
            for (var i = 0; i < voidCells.Count; i++)
                voidToEmit.Add(new HashSet<int>());
            var emitToVoid = new List<HashSet<int>>(emitCount);
            for (var i = 0; i < emitCount; i++)
                emitToVoid.Add(new HashSet<int>());
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                {
                    if (!emit[r, c]) continue;
                    int e = emitOf[r, c];
                    if (r == 0 || c == 0 || r == m - 1 || c == m - 1)
                        Link(voidToEmit, emitToVoid, 0, e);
                    Link4(emit, voidOf, voidToEmit, emitToVoid, r, c, e);
                }

            var visitedVoid = new bool[voidCells.Count];
            var visitedEmit = new bool[emitCount];
            var children = new List<List<int>>(voidCells.Count);
            for (var i = 0; i < voidCells.Count; i++)
                children.Add(new List<int>());
            // todo tokens: void comps ≥0, emit comps ~e.
            var todo = new Queue<int>();
            visitedVoid[0] = true;
            todo.Enqueue(0);
            while (todo.Count > 0)
            {
                int token = todo.Dequeue();
                if (token >= 0)
                {
                    foreach (int e in voidToEmit[token])
                        if (!visitedEmit[e])
                        {
                            visitedEmit[e] = true;
                            children[token].Add(e);
                            todo.Enqueue(~e);
                        }
                }
                else
                {
                    int e = ~token;
                    foreach (int v in emitToVoid[e])
                        if (!visitedVoid[v])
                        {
                            visitedVoid[v] = true;
                            todo.Enqueue(v);
                        }
                }
            }

            var islandBearing = new List<int>();
            for (var v = 1; v < voidCells.Count; v++)
                if (children[v].Count > 0)
                    islandBearing.Add(v);
            return new CellNesting
            {
                VoidCells = voidCells.ToArray(),
                IslandBearing = islandBearing,
            };
        }

        /// <summary>4-connected flood of un-emitted cells — the void components.</summary>
        private static void FloodVoid(int[,] voidOf, int id, List<int> cells, Queue<int> queue)
        {
            int m = voidOf.GetLength(0);
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                cells.Add(cell);
                int cr = cell / m, cc = cell % m;
                TryEnqueue(cr - 1, cc);
                TryEnqueue(cr + 1, cc);
                TryEnqueue(cr, cc - 1);
                TryEnqueue(cr, cc + 1);
            }

            void TryEnqueue(int r, int c)
            {
                if (r < 0 || c < 0 || r >= m || c >= m) return;
                if (voidOf[r, c] != Unvisited) return;
                voidOf[r, c] = id;
                queue.Enqueue(r * m + c);
            }
        }

        private static void Link(
            List<HashSet<int>> voidToEmit, List<HashSet<int>> emitToVoid, int v, int e)
        {
            voidToEmit[v].Add(e);
            emitToVoid[e].Add(v);
        }

        /// <summary>The four side-adjacent void↔emitted links of emitted cell (r, c).</summary>
        private static void Link4(
            bool[,] emit, int[,] voidOf,
            List<HashSet<int>> voidToEmit, List<HashSet<int>> emitToVoid,
            int r, int c, int e)
        {
            int m = emit.GetLength(0);
            Try(r - 1, c);
            Try(r + 1, c);
            Try(r, c - 1);
            Try(r, c + 1);

            void Try(int nr, int nc)
            {
                if (nr < 0 || nc < 0 || nr >= m || nc >= m) return;
                int v = voidOf[nr, nc];
                if (v < 0) return;
                Link(voidToEmit, emitToVoid, v, e);
            }
        }

        /// <summary>
        /// The minimal path of emitted cells joining the void component's
        /// cells to exterior-connected space: 0-1 BFS over the whole matrix
        /// — an un-emitted cell is free to traverse (a channel may
        /// legitimately span several cells through stacked bands), each
        /// emitted cell costs one drop. Terminals are the exterior flood
        /// recomputed on the CURRENT mask (earlier sites' channels count)
        /// plus the matrix border — under invert the border emits, and a
        /// dropped border cell vents the void past the field edge. Null when
        /// unreachable.
        /// </summary>
        private static List<int>? FindChannel(bool[,] emit, List<int> voidCells)
        {
            int m = emit.GetLength(0);
            var exterior = new bool[m, m];
            var queue = new Queue<int>();
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if ((r == 0 || c == 0 || r == m - 1 || c == m - 1) &&
                        !emit[r, c] && !exterior[r, c])
                    {
                        exterior[r, c] = true;
                        queue.Enqueue(r * m + c);
                    }
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int cr = cell / m, cc = cell % m;
                TryFlood(cr - 1, cc);
                TryFlood(cr + 1, cc);
                TryFlood(cr, cc - 1);
                TryFlood(cr, cc + 1);
            }

            void TryFlood(int r, int c)
            {
                if (r < 0 || c < 0 || r >= m || c >= m) return;
                if (emit[r, c] || exterior[r, c]) return;
                exterior[r, c] = true;
                queue.Enqueue(r * m + c);
            }

            var dist = new int[m, m];
            var prev = new int[m, m];
            var done = new bool[m, m];
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                {
                    dist[r, c] = int.MaxValue;
                    prev[r, c] = -1;
                }
            var deque = new LinkedList<int>();
            foreach (int cell in voidCells)
            {
                dist[cell / m, cell % m] = 0;
                deque.AddLast(cell);
            }
            while (deque.Count > 0)
            {
                int cell = deque.First!.Value;
                deque.RemoveFirst();
                int r = cell / m, c = cell % m;
                if (done[r, c]) continue;
                done[r, c] = true;
                if (exterior[r, c] || r == 0 || c == 0 || r == m - 1 || c == m - 1)
                {
                    var path = new List<int>();
                    for (int cur = cell; cur >= 0; cur = prev[cur / m, cur % m])
                        if (emit[cur / m, cur % m])
                            path.Add(cur);
                    return path;
                }
                TryRelax(r - 1, c, cell);
                TryRelax(r + 1, c, cell);
                TryRelax(r, c - 1, cell);
                TryRelax(r, c + 1, cell);
            }
            return null;

            void TryRelax(int nr, int nc, int cell)
            {
                if (nr < 0 || nc < 0 || nr >= m || nc >= m) return;
                if (done[nr, nc]) return;
                int r = cell / m, c = cell % m;
                int w = emit[nr, nc] ? 1 : 0;
                if (dist[nr, nc] <= dist[r, c] + w) return;
                dist[nr, nc] = dist[r, c] + w;
                prev[nr, nc] = cell;
                if (w == 0)
                    deque.AddFirst(nr * m + nc);
                else
                    deque.AddLast(nr * m + nc);
            }
        }

        // ----- ticket #53: the thin finder channels -----

        /// <summary>
        /// A matched finder site: the 7×7 window origin (working-matrix
        /// cells), the window corner nearest the matrix corner, and which
        /// channel shape the void's position calls for — <b>Band</b>: the
        /// void sits at the inner-ring positions (normal polarity: the
        /// light ring inside the dark band), so the channel crosses the
        /// emitted band ring to the void beyond; <b>Vent</b>: the void IS
        /// the window's edge ring (invert: the dark finder ring, enclosed
        /// by the emitting field), so the channel slots through the field
        /// to the matrix edge — the same "vent past the field edge" move a
        /// border-cell drop makes.
        /// </summary>
        private sealed class FinderSite
        {
            /// <summary>The 7×7 window's origin row/column (working-matrix cells).</summary>
            public int WindowR0, WindowC0;
            /// <summary>The window corner nearest the matrix corner — the preferred locus.</summary>
            public int CornerR, CornerC;
            /// <summary>Vent = edge-ring void under invert; Band = inner-ring void.</summary>
            public bool Vent;
        }

        /// <summary>
        /// The finder-ring test: the island-bearing hole's cells sit wholly
        /// within one of the three fixed 7×7 finder windows — at its inner
        /// ring positions (Band) or its edge ring positions (Vent). Any
        /// other shape or position is not the regular ring and falls to
        /// the cell-drop rule. Windows may coincide on tiny fixtures; the
        /// claimed-set dedupe keeps the same void single-treated.
        /// </summary>
        private static FinderSite? MatchFinderVoid(List<int> voidCells, int m)
        {
            int q = QrSketchInfo.QuietZoneCellsPerSide;
            var origins = new[] { (q, q), (q, m - q - 7), (m - q - 7, q) };
            foreach (var (r0, c0) in origins)
            {
                if (r0 < 0 || c0 < 0 || r0 + 7 > m || c0 + 7 > m)
                    continue;
                var allInner = true;
                var allEdge = true;
                foreach (int cell in voidCells)
                {
                    int r = cell / m - r0, c = cell % m - c0;
                    if (r < 1 || r > 5 || c < 1 || c > 5 ||
                        (r != 1 && r != 5 && c != 1 && c != 5))
                        allInner = false;
                    if (r < 0 || r > 6 || c < 0 || c > 6 ||
                        (r != 0 && r != 6 && c != 0 && c != 6))
                        allEdge = false;
                }
                if (!allInner && !allEdge)
                    continue;
                return new FinderSite
                {
                    WindowR0 = r0,
                    WindowC0 = c0,
                    CornerR = r0 < m / 2 ? r0 : r0 + 6,
                    CornerC = c0 < m / 2 ? c0 : c0 + 6,
                    Vent = allEdge,
                };
            }
            return null;
        }

        /// <summary>
        /// Picks a channel rectangle for a matched finder site: every
        /// candidate is an axis-aligned channel at a corner locus — one
        /// cell in from a ring corner, off the detection runs' centre-lines —
        /// whose ends land at void-cell centres (inside the un-emitted free
        /// core, so caps and walls can never touch a contour: the grown
        /// grid keeps material ≥ g from cell borders and the width sits
        /// strictly inside the core). A candidate clears when the inner end
        /// lands in the void being opened, the channel crosses emitted band
        /// material, and — Band style — the outer end lands in a different
        /// void. The fallback order prefers the corner nearest the matrix
        /// corner, then the next. Null when nothing clears.
        /// </summary>
        private static Path64? TryPlaceFinderChannel(
            bool[,] emit, List<int> voidCells, FinderSite site, long g, long w)
        {
            int m = emit.GetLength(0);
            long u = QrInsertionGuard.GridUnitsPerCell;

            // Width feasibility: the channel plus a ≥w sliver margin on each
            // side must fit inside one cell's free core (u−2g), so no wall
            // ever grazes a grown edge (Clipper noise is ±1 unit, the floor
            // is 10 — a coincident wall would be a validator violation).
            if (u - 2 * g < 3 * w)
                return null;

            var voidSet = new HashSet<int>(voidCells);
            foreach (var cand in Candidates(site, m, w))
            {
                // The inner tip must land inside THIS void; the channel must
                // span emitted band material (Band style); the outer tip must
                // land in a DIFFERENT void (never re-enter the one being
                // opened). Between the verified cells the corridor keeps ≥w
                // from any neighbour's grown edge (w < u−2g above), and the
                // grown grid keeps material ≥g off every cell border — so a
                // clearing candidate clears ALL contours: islands, deeper
                // bands and disjoint neighbours alike.
                if (!voidSet.Contains(cand.TipInR * m + cand.TipInC))
                    continue;
                if (cand.BandR >= 0 &&
                    (!InBounds(cand.BandR, cand.BandC, m) || !emit[cand.BandR, cand.BandC]))
                    continue;
                if (cand.TipOutR >= 0 &&
                    (!InBounds(cand.TipOutR, cand.TipOutC, m) ||
                     emit[cand.TipOutR, cand.TipOutC] ||
                     voidSet.Contains(cand.TipOutR * m + cand.TipOutC)))
                    continue;
                return cand.Rect;
            }
            return null;
        }

        private static bool InBounds(int r, int c, int m) =>
            r >= 0 && c >= 0 && r < m && c < m;

        /// <summary>
        /// One corner-locus candidate, in fallback order (<see cref="Order"/>
        /// = <see cref="OrderingDist"/>, then <see cref="TiebreakDist"/>,
        /// then the listed sequence): the channel axis runs through
        /// <see cref="BandR"/>/<see cref="BandC"/> (the band cell it must
        /// span — negative for a Vent candidate, which crosses the field to
        /// the matrix edge instead of a band); <see cref="TipInR"/>/
        /// <see cref="TipInC"/> is the void cell the inner end must land in;
        /// <see cref="TipOutR"/>/<see cref="TipOutC"/> the void cell the
        /// outer end must land in (negative = past the matrix edge).
        /// </summary>
        private sealed class ChannelCandidate
        {
            public int BandR = -1, BandC = -1, TipInR, TipInC, TipOutR = -1, TipOutC = -1;

            /// <summary>
            /// The primary fallback-ordering distance: a Band candidate's
            /// band-cell distance to the window corner nearest the matrix
            /// corner (the preferred locus wins); a Vent candidate's distance
            /// from its slot's exit edge.
            /// </summary>
            public int OrderingDist;

            /// <summary>
            /// The secondary fallback-ordering distance: a Vent candidate's
            /// tip-cell distance to the preferred window corner; zero for a
            /// Band candidate (no tiebreak — the locus is fully pinned).
            /// </summary>
            public int TiebreakDist;

            /// <summary>Listed-sequence position — the last ordering key.</summary>
            public int Order;
            public Path64 Rect = new Path64();
        }

        /// <summary>
        /// Enumerates the locus candidates in fallback order. Band sites:
        /// the eight arm-end cuts — each of the four arms, one cell in from
        /// either adjacent corner — ordered by the band cell's distance to
        /// the window corner nearest the matrix corner. Vent sites: a slot
        /// from a void arm cell to the matrix edge, directions ordered by
        /// edge distance, each direction's two arm-end loci ordered by
        /// distance to that corner. The order is fully deterministic.
        /// </summary>
        private static List<ChannelCandidate> Candidates(FinderSite site, int m, long w)
        {
            long u = QrInsertionGuard.GridUnitsPerCell;
            var list = new List<ChannelCandidate>();
            int r0 = site.WindowR0, c0 = site.WindowC0;
            int cornerR = site.CornerR, cornerC = site.CornerC;
            var seq = 0;
            if (!site.Vent)
            {
                // The eight band cuts: for the top/bottom arms the channel
                // runs vertically at the second/sixth column; for the
                // left/right arms horizontally at the second/sixth row.
                foreach (int cb in new[] { c0 + 1, c0 + 5 })
                {
                    list.Add(BandCand(r0, cb, r0 + 1, cb, r0 - 1, cb, true));
                    list.Add(BandCand(r0 + 6, cb, r0 + 5, cb, r0 + 7, cb, true));
                }
                foreach (int rb in new[] { r0 + 1, r0 + 5 })
                {
                    list.Add(BandCand(rb, c0, rb, c0 + 1, rb, c0 - 1, false));
                    list.Add(BandCand(rb, c0 + 6, rb, c0 + 5, rb, c0 + 7, false));
                }
            }
            else
            {
                // Vent candidates: a slot from the void's arm cell to the
                // matrix edge, its end landing a half-cell past the border
                // so the slot vents past the field edge like a dropped
                // border cell does.
                foreach (var (dr, dc) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
                {
                    int edgeDist = dr < 0 ? r0 : dr > 0 ? m - r0 - 7
                                 : dc < 0 ? c0 : m - c0 - 7;
                    foreach (int k in new[] { 1, 5 })
                    {
                        int tipR = dr != 0 ? (dr < 0 ? r0 : r0 + 6) : r0 + k;
                        int tipC = dc != 0 ? (dc < 0 ? c0 : c0 + 6) : c0 + k;
                        long axis, lo, hi;
                        bool vertical = dr != 0;
                        if (vertical)
                        {
                            axis = (long)tipC * u + u / 2;
                            lo = dr < 0 ? -u / 2 : (long)tipR * u + u / 2;
                            hi = dr < 0 ? (long)tipR * u + u / 2 : (long)m * u + u / 2;
                        }
                        else
                        {
                            axis = (long)tipR * u + u / 2;
                            lo = dc < 0 ? -u / 2 : (long)tipC * u + u / 2;
                            hi = dc < 0 ? (long)tipC * u + u / 2 : (long)m * u + u / 2;
                        }
                        list.Add(new ChannelCandidate
                        {
                            TipInR = tipR,
                            TipInC = tipC,
                            OrderingDist = edgeDist,
                            TiebreakDist = Dist(tipR, tipC, cornerR, cornerC),
                            Order = seq++,
                            Rect = ChannelRect(vertical, axis, lo, hi, w),
                        });
                    }
                }
            }
            list.Sort((a, b) =>
                a.OrderingDist != b.OrderingDist ? a.OrderingDist - b.OrderingDist :
                a.TiebreakDist != b.TiebreakDist ? a.TiebreakDist - b.TiebreakDist : a.Order - b.Order);
            return list;

            ChannelCandidate BandCand(
                int bandR, int bandC, int inR, int inC, int outR, int outC,
                bool vertical)
            {
                long lo, hi, axis;
                if (vertical)
                {
                    axis = (long)bandC * u + u / 2;
                    lo = Math.Min(outR, inR) * u + u / 2;
                    hi = Math.Max(outR, inR) * u + u / 2;
                }
                else
                {
                    axis = (long)bandR * u + u / 2;
                    lo = Math.Min(outC, inC) * u + u / 2;
                    hi = Math.Max(outC, inC) * u + u / 2;
                }
                return new ChannelCandidate
                {
                    BandR = bandR,
                    BandC = bandC,
                    TipInR = inR,
                    TipInC = inC,
                    TipOutR = outR,
                    TipOutC = outC,
                    OrderingDist = Dist(bandR, bandC, cornerR, cornerC),
                    TiebreakDist = 0,
                    Order = seq++,
                    Rect = ChannelRect(vertical, axis, lo, hi, w),
                };
            }
        }

        private static int Dist(int r, int c, int rr, int cc) =>
            Math.Abs(r - rr) + Math.Abs(c - cc);

        /// <summary>
        /// The channel rectangle <paramref name="w"/> units wide:
        /// <paramref name="vertical"/> channels run along the axis (a cell
        /// column centre-line), horizontal along a row centre-line.
        /// <paramref name="lo"/>/<paramref name="hi"/> are the two tip
        /// ordinates — void-cell centres, or a half-cell past the matrix
        /// edge for a vent slot — so each end lands in void free core and
        /// can never graze a contour.
        /// </summary>
        private static Path64 ChannelRect(bool vertical, long axis, long lo, long hi, long w)
        {
            long half = w / 2;
            long x0, y0, x1, y1;
            if (vertical)
            {
                x0 = axis - half;
                x1 = x0 + w;
                y0 = lo;
                y1 = hi;
            }
            else
            {
                y0 = axis - half;
                y1 = y0 + w;
                x0 = lo;
                x1 = hi;
            }
            return new Path64(4)
            {
                new Point64(x0, y0), new Point64(x1, y0),
                new Point64(x1, y1), new Point64(x0, y1),
            };
        }
    }
}
