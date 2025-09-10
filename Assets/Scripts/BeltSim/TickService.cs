using System.Collections.Generic;
using UnityEngine;

// Central tick: advances all runs, handles ejection/admission
public class BeltTickService : MonoBehaviour
{
    [SerializeField] float fixedSpeed = 2f;
    [SerializeField, Min(0f)] float itemSpacing = 0.6f;
    [SerializeField] bool debugLogs = false;

    readonly List<BeltRun> runs = new List<BeltRun>(64);
    readonly List<List<BeltItem>> ejectedPerRun = new List<List<BeltItem>>(64);

    // Optional endpoints per run end
    public readonly List<BeltEndpoint> heads = new List<BeltEndpoint>(); // feeders at head
    public readonly List<BeltEndpoint> tails = new List<BeltEndpoint>(); // consumers at tail

    // Snapshot of previous head cells to help remap items on graph rebuild
    readonly List<Vector2Int> prevHeads = new List<Vector2Int>();

    public IReadOnlyList<BeltRun> Runs => runs;

    public void SetGraph(BeltGraph graph, bool preserveItems)
    {
        // capture old state for remapping
        var oldRuns = new List<BeltRun>(runs);
        var oldHeads = new List<Vector2Int>(prevHeads);

        runs.Clear(); heads.Clear(); tails.Clear(); ejectedPerRun.Clear();
        for (int i = 0; i < graph.runs.Count; i++)
        {
            var r = graph.runs[i];
            r.speed = fixedSpeed;
            r.minSpacing = Mathf.Max(0f, itemSpacing);
            runs.Add(r);
            ejectedPerRun.Add(new List<BeltItem>(16));
            heads.Add(null);
            tails.Add(null);
        }

        if (preserveItems && oldRuns.Count > 0 && oldHeads.Count == oldRuns.Count)
        {
            // Build lookup from old head cell to items
            var map = new Dictionary<Vector2Int, List<BeltItem>>(oldRuns.Count);
            for (int i = 0; i < oldRuns.Count; i++)
            {
                var copy = new List<BeltItem>(oldRuns[i].items.Count);
                for (int k = 0; k < oldRuns[i].items.Count; k++) copy.Add(oldRuns[i].items[k]);
                if (!map.ContainsKey(oldHeads[i])) map[oldHeads[i]] = copy;
            }
            // Remap into new runs by matching head cell
            for (int i = 0; i < graph.headCells.Count && i < runs.Count; i++)
            {
                var headCell = graph.headCells[i];
                if (map.TryGetValue(headCell, out var list))
                {
                    var r = runs[i];
                    r.items.Clear();
                    for (int k = 0; k < list.Count; k++)
                    {
                        var it = list[k];
                        if (it.offset > r.totalLen) it.offset = r.totalLen; // clamp inside new length
                        r.items.Add(it);
                    }
                }
            }
            if (debugLogs) Debug.Log("[BeltTickService] Preserved items across graph rebuild");
        }

        // refresh prevHeads for next rebuild
        prevHeads.Clear();
        for (int i = 0; i < graph.headCells.Count; i++) prevHeads.Add(graph.headCells[i]);

        if (debugLogs) Debug.Log($"[BeltTickService] Graph set: runs={runs.Count}");
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        // 1) advance all runs, collect ejected
        for (int i = 0; i < runs.Count; i++)
        {
            // ensure spacing/speed reflect current inspector values at runtime
            runs[i].speed = fixedSpeed;
            runs[i].minSpacing = Mathf.Max(0f, itemSpacing);

            var ej = ejectedPerRun[i]; ej.Clear();
            bool tailBlocked = tails[i] == null; // when no tail endpoint, jam at end
            runs[i].Advance(dt, tailBlocked, ej);
            if (debugLogs && (i == 0 || ej.Count > 0))
            {
                Debug.Log($"[BeltTickService] Run {i} items={runs[i].items.Count} ejected={ej.Count} tailBlocked={tailBlocked}");
            }
        }
        // 2) deliver ejected to tail endpoints (only when not blocked)
        for (int i = 0; i < runs.Count; i++)
        {
            var ep = tails[i];
            if (ep == null) continue;
            var ej = ejectedPerRun[i];
            for (int k = 0; k < ej.Count; k++) ep.OnInputItem(ej[k].id);
        }
        // 3) try admit from heads; keep pulling until blocked (so bursts are handled in one tick)
        for (int i = 0; i < runs.Count; i++)
        {
            var ep = heads[i]; if (ep == null) continue;
            int before = runs[i].items.Count;
            int guard = 32; // avoid infinite loop in case of misbehaving endpoints
            while (guard-- > 0 && ep.TryOutputTo(runs[i])) { }
            int after = runs[i].items.Count;
            if (debugLogs && after > before)
                Debug.Log($"[BeltTickService] Admitted {after - before} items to run {i}");
        }
    }
}
