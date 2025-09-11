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
    readonly List<List<int>> outgoing = new List<List<int>>();

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

        runs.Clear(); heads.Clear(); tails.Clear(); ejectedPerRun.Clear(); outgoing.Clear();
        for (int i = 0; i < graph.runs.Count; i++)
        {
            var r = graph.runs[i];
            r.speed = fixedSpeed;
            r.minSpacing = Mathf.Max(0f, itemSpacing);
            runs.Add(r);
            ejectedPerRun.Add(new List<BeltItem>(16));
            heads.Add(null);
            tails.Add(null);
            outgoing.Add(new List<int>(graph.outgoing[i]));
        }

        if (preserveItems && oldRuns.Count > 0 && oldHeads.Count == oldRuns.Count)
        {
            // Build lookup from old head cell to items
            var map = new Dictionary<Vector2Int, List<BeltItem>>(oldRuns.Count);
            for (int i = 0; i < oldRuns.Count; i++)
            {
                var copy = new List<BeltItem>(oldRuns[i].items.Count);
                for (var node = oldRuns[i].items.First; node != null; node = node.Next) copy.Add(node.Value);
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
                        r.items.AddLast(it);
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
            runs[i].speed = fixedSpeed;
            runs[i].minSpacing = Mathf.Max(0f, itemSpacing);
            var ej = ejectedPerRun[i]; ej.Clear();
            bool tailBlocked = outgoing[i].Count == 0 && tails[i] == null; // jam only if no connection
            runs[i].Advance(dt, tailBlocked, ej);
            if (debugLogs && (i == 0 || ej.Count > 0))
            {
                Debug.Log($"[BeltTickService] Run {i} items={runs[i].items.Count} ejected={ej.Count} tailBlocked={tailBlocked}");
            }
        }
        // 2) route ejected items
        for (int i = 0; i < runs.Count; i++)
        {
            var ej = ejectedPerRun[i];
            if (ej.Count == 0) continue;
            if (outgoing[i].Count == 0)
            {
                var ep = tails[i]; if (ep != null)
                    for (int k = 0; k < ej.Count; k++) ep.OnInputItem(ej[k].id);
                ej.Clear();
            }
            else if (outgoing[i].Count == 1)
            {
                int headIdx = outgoing[i][0];
                var headEp = heads[headIdx];
                var targetRun = runs[headIdx];
                for (int k = 0; k < ej.Count; k++)
                {
                    var item = ej[k];
                    // If a merger is present, feed it with source info for round-robin fairness
                    if (headEp is MergerEndpoint me)
                    {
                        me.OnInputItemFrom(i, item.id);
                    }
                    else if (headEp != null)
                    {
                        headEp.OnInputItem(item.id);
                    }
                    else if (!targetRun.TryEnqueue(item.id))
                    {
                        // create a merger and enqueue into it for fairness
                        var mep = new MergerEndpoint();
                        heads[headIdx] = mep;
                        mep.OnInputItemFrom(i, item.id);
                    }
                }
                ej.Clear();
            }
            else // splitter
            {
                var sp = tails[i] as SplitterEndpoint;
                if (sp == null) { sp = new SplitterEndpoint(); tails[i] = sp; }
                for (int k = 0; k < ej.Count; k++) sp.OnInputItem(ej[k].id);
                ej.Clear();
            }
        }
        // 3) drive splitters
        for (int i = 0; i < runs.Count; i++)
        {
            if (outgoing[i].Count > 1)
            {
                var sp = tails[i] as SplitterEndpoint; if (sp == null) continue;
                var outsRuns = ListCache<BeltRun>.Get();
                for (int k = 0; k < outgoing[i].Count; k++) outsRuns.Add(runs[outgoing[i][k]]);
                while (sp.TrySplitTo(outsRuns)) { }
                ListCache<BeltRun>.Release(outsRuns);
            }
        }
        // 4) try admit from head endpoints (mergers)
        for (int i = 0; i < runs.Count; i++)
        {
            var ep = heads[i]; if (ep == null) continue;
            int before = runs[i].items.Count;
            int guard = 32;
            while (guard-- > 0 && ep.TryOutputTo(runs[i])) { }
            int after = runs[i].items.Count;
            if (debugLogs && after > before)
                Debug.Log($"[BeltTickService] Admitted {after - before} items to run {i}");
        }
    }
}
