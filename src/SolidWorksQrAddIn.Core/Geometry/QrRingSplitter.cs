using System.Collections.Generic;

namespace SolidWorksQrAddIn.Core.Geometry
{
    /// <summary>
    /// The ring-split cell-drop pass (spec #51, ticket #52): on the emitted
    /// set AFTER polarity selection, open every island-bearing void — an
    /// enclosed component of un-emitted cells holding emitted islands, the
    /// grown union's ring → hole → island nesting that SolidWorks' profile
    /// solver refuses as "doubly nested" — by dropping the minimal
    /// module-aligned path of emitted cells that joins the void to
    /// exterior-connected space, before the union pass. The finder rings
    /// are ordinary sites here — the thin-channel upgrade is ticket #53.
    /// Square-shape only; under Circle the flag is inert and the pass
    /// never runs (dots emit no contours).
    ///
    /// Cell topology mirrors the grown union exactly (g &lt; U/2): emitted
    /// cells are 8-connected material — grown rects bridge diagonal
    /// contacts — while un-emitted cells are 4-connected void, so an
    /// enclosed 4-connected void component is exactly a union hole and its
    /// enclosed 8-connected emitted components are exactly its islands.
    /// The exterior is the un-emitted flood from the working-matrix border;
    /// under invert the border itself emits, so a channel may instead
    /// terminate on a border cell — dropping it vents the void past the
    /// field edge (the field is clipped at the matrix square).
    /// </summary>
    internal static class QrRingSplitter
    {
        /// <summary>
        /// Drops emitted cells in <paramref name="emit"/> (mutated in place)
        /// until no enclosed void bears an island, iterating the
        /// detect→drop loop — a channel may legitimately span several cells
        /// through stacked bands. Returns null on success; a named
        /// <see cref="QrSketchErrorKind.InvalidGeometry"/> failure when a
        /// site cannot be opened or the loop does not converge —
        /// all-or-nothing: a partially opened QR is never delivered.
        /// </summary>
        public static QrSketchResult? OpenIslandBearingVoids(
            bool[,] emit, out int openedSites, out int droppedCells) =>
            OpenIslandBearingVoids(
                emit, emit.GetLength(0) * emit.GetLength(1),
                out openedSites, out droppedCells);

        /// <summary>
        /// <paramref name="maxPasses"/> bounds the detect→drop loop (the
        /// cell count normally — every pass drops ≥1 emitted cell). The
        /// bound-overshoot refusal cannot be produced by a real matrix —
        /// every finite site is openable — it is internal for the suite.
        /// </summary>
        internal static QrSketchResult? OpenIslandBearingVoids(
            bool[,] emit, int maxPasses, out int openedSites, out int droppedCells)
        {
            openedSites = 0;
            droppedCells = 0;
            int m = emit.GetLength(0);
            for (var pass = 0; pass < maxPasses; pass++)
            {
                var nesting = Analyze(emit);
                if (nesting.IslandBearing.Count == 0)
                    return null;
                foreach (int voidId in nesting.IslandBearing)
                {
                    var drops = FindChannel(emit, nesting.VoidCells[voidId]);
                    if (drops == null)
                        return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                            "Ring split found an island-bearing void it cannot open — no " +
                            "module-aligned cell path joins it to exterior-connected " +
                            "space. The request is refused rather than partially opened.");
                    openedSites++;
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
            }
            return QrSketchResult.Fail(QrSketchErrorKind.InvalidGeometry,
                "Ring split could not open every island-bearing void — the cell-drop " +
                "pass did not converge. The request is refused rather than partially opened.");
        }

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
                    voidOf[r, c] = emit[r, c] ? -1 : -2;
                    emitOf[r, c] = emit[r, c] ? -2 : -1;
                }

            var voidCells = new List<List<int>> { new List<int>() };
            var queue = new Queue<int>();
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if ((r == 0 || c == 0 || r == m - 1 || c == m - 1) && voidOf[r, c] == -2)
                    {
                        voidOf[r, c] = 0;
                        queue.Enqueue(r * m + c);
                    }
            FloodVoid(voidOf, 0, voidCells[0], queue);
            for (var r = 0; r < m; r++)
                for (var c = 0; c < m; c++)
                    if (voidOf[r, c] == -2)
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
                    if (emitOf[r, c] == -2)
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
                                    if (emitOf[nr, nc] != -2) continue;
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
                if (voidOf[r, c] != -2) return;
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
    }
}
