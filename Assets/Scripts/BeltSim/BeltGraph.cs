using System.Collections.Generic;
using UnityEngine;

// Minimal graph: build straight/corner runs from tiles; map endpoints at ends
public class BeltGraph
{
    public readonly List<BeltRun> runs = new List<BeltRun>(64);
    public readonly List<Vector2Int> headCells = new List<Vector2Int>(64);
    public readonly List<Vector2Int> tailCells = new List<Vector2Int>(64);
    public readonly List<List<int>> incoming = new List<List<int>>(64);
    public readonly List<List<int>> outgoing = new List<List<int>>(64);

    // Build from directional tiles; contiguous connectivity between junctions merges into a run
    public void BuildRuns(IReadOnlyList<BeltTile> tiles)
    {
        runs.Clear(); headCells.Clear(); tailCells.Clear(); incoming.Clear(); outgoing.Clear();
        if (tiles == null || tiles.Count == 0) return;

        // 1) index tiles by cell
        var map = new Dictionary<Vector2Int, BeltTile>(tiles.Count);
        foreach (var t in tiles) map[t.cell] = t;

        // 2) compute in-degree for each cell (how many neighbors feed into it)
        var indeg = new Dictionary<Vector2Int, int>(tiles.Count);
        foreach (var t in tiles)
        {
            var to = t.cell + Dir2D.ToVec(t.dir);
            if (map.ContainsKey(to)) indeg[to] = indeg.TryGetValue(to, out var v) ? v + 1 : 1;
        }

        // 3) find starting cells: any cell with indegree != 1 (0 or >1) and all not-yet-used cells as fallback
        var used = new HashSet<Vector2Int>();
        var starts = new List<Vector2Int>(tiles.Count);
        foreach (var t in tiles)
        {
            int d = indeg.TryGetValue(t.cell, out var v) ? v : 0;
            if (d != 1) starts.Add(t.cell);
        }
        // include any remaining cells that might be in a simple loop (all indegree==1)
        if (starts.Count == 0) foreach (var t in tiles) starts.Add(t.cell);

        // 4) grow runs from starts until next junction (cell where indegree != 1) or dead end
        foreach (var s in starts)
        {
            if (!map.ContainsKey(s) || used.Contains(s)) continue;

            var path = ListCache<Vector2Int>.Get();
            var inPath = new HashSet<Vector2Int>();
            var cur = map[s];
            path.Add(cur.cell); used.Add(cur.cell); inPath.Add(cur.cell);

            while (true)
            {
                var nextCell = cur.cell + Dir2D.ToVec(cur.dir);
                if (!map.TryGetValue(nextCell, out var next)) break;
                if (inPath.Contains(nextCell)) { break; } // cycle guard
                // stop extending if next cell is a junction (indegree != 1)
                int d = indeg.TryGetValue(nextCell, out var v) ? v : 0;
                if (d != 1)
                {
                    // Add the junction cell as the tail, but DON'T mark it used, so a new run can start there.
                    path.Add(nextCell);
                    break;
                }
                path.Add(nextCell); used.Add(nextCell); inPath.Add(nextCell);
                cur = next;
            }

            if (path.Count >= 1)
            {
                var run = new BeltRun();
                run.BuildFromCells(path); // positions will be rebuilt with world coords later
                runs.Add(run);
                headCells.Add(path[0]);
                tailCells.Add(path[path.Count - 1]);
                incoming.Add(new List<int>(2));
                outgoing.Add(new List<int>(2));
            }
            ListCache<Vector2Int>.Release(path);
        }

        // 5) Build adjacency lists between runs (tail -> head)
        var headLookup = new Dictionary<Vector2Int, int>(headCells.Count);
        for (int i = 0; i < headCells.Count; i++) headLookup[headCells[i]] = i;
        for (int i = 0; i < tailCells.Count; i++)
        {
            var tail = tailCells[i];
            // First, if a run starts at the same cell (junction), connect to it
            if (headLookup.TryGetValue(tail, out var headAtSameCell))
            {
                outgoing[i].Add(headAtSameCell);
                incoming[headAtSameCell].Add(i);
                continue;
            }
            // Otherwise, connect to the head at the next cell along tail's direction (continuous/loop case)
            if (!map.TryGetValue(tail, out var tile)) continue;
            var next = tail + Dir2D.ToVec(tile.dir);
            if (headLookup.TryGetValue(next, out var headIdx))
            {
                outgoing[i].Add(headIdx);
                incoming[headIdx].Add(i);
            }
        }
    }
}

// Small List cache to avoid allocs during BuildRuns
static class ListCache<T>
{
    static readonly Stack<List<T>> pool = new Stack<List<T>>();
    public static List<T> Get()
    {
        if (pool.Count > 0) { var l = pool.Pop(); l.Clear(); return l; }
        return new List<T>(32);
    }
    public static void Release(List<T> list)
    {
        if (list == null) return; list.Clear(); pool.Push(list);
    }
}
