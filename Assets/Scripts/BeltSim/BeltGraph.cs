using System.Collections.Generic;
using UnityEngine;

// Minimal graph: build straight/corner runs from tiles; map endpoints at ends
public class BeltGraph
{
    public readonly List<BeltRun> runs = new List<BeltRun>(64);
    public readonly List<Vector2Int> headCells = new List<Vector2Int>(64);
    public readonly List<Vector2Int> tailCells = new List<Vector2Int>(64);

    // Build from directional tiles; contiguous forward connectivity merges into a run
    public void BuildRuns(IReadOnlyList<BeltTile> tiles)
    {
        runs.Clear(); headCells.Clear(); tailCells.Clear();
        // index tiles by cell
        var map = new Dictionary<Vector2Int, BeltTile>(tiles.Count);
        foreach (var t in tiles) map[t.cell] = t;
        var used = new HashSet<Vector2Int>();

        foreach (var t in tiles)
        {
            if (used.Contains(t.cell)) continue;

            // 1) grow backward to find true head
            var head = t;
            while (true)
            {
                var prevCell = head.cell - Dir2D.ToVec(head.dir); // a tile whose forward reaches 'head'
                if (map.TryGetValue(prevCell, out var prev) && head.cell == prev.cell + Dir2D.ToVec(prev.dir))
                {
                    head = prev;
                }
                else break;
            }

            // 2) from head, grow forward to tail collecting cells
            var path = ListCache<Vector2Int>.Get();
            var cur = head;
            used.Add(cur.cell);
            path.Add(cur.cell);
            while (true)
            {
                var nextCell = cur.cell + Dir2D.ToVec(cur.dir);
                if (map.TryGetValue(nextCell, out var next))
                {
                    path.Add(nextCell);
                    used.Add(nextCell);
                    cur = next;
                }
                else break;
            }

            // 3) create run
            var run = new BeltRun();
            run.BuildFromCells(path);
            runs.Add(run);
            headCells.Add(path[0]);
            tailCells.Add(path[path.Count - 1]);
            ListCache<Vector2Int>.Release(path);
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
