using System.Collections.Generic;
using UnityEngine;

// Drives mergers and splitters each FixedUpdate for out-of-the-box routing.
public class BeltRoutingDriver : MonoBehaviour
{
    [SerializeField] BeltTickService tick;

    // Map tail cell -> merger endpoint
    readonly Dictionary<Vector2Int, MergerEndpoint> mergers = new Dictionary<Vector2Int, MergerEndpoint>();
    // Map head cell -> splitter endpoint and its outputs
    readonly Dictionary<Vector2Int, (SplitterEndpoint sp, List<int> outRunIdx)> splitters = new Dictionary<Vector2Int, (SplitterEndpoint, List<int>)>();

    void Awake()
    {
        if (tick == null) tick = FindObjectOfType<BeltTickService>();
        if (BeltGraphService.Instance != null)
            BeltGraphService.Instance.OnGraphRebuilt += OnGraphRebuilt;
    }

    void OnDestroy()
    {
        if (BeltGraphService.Instance != null)
            BeltGraphService.Instance.OnGraphRebuilt -= OnGraphRebuilt;
    }

    void OnGraphRebuilt(BeltGraph g)
    {
        // clear existing wiring; user code can add their own via public API if needed
        mergers.Clear(); splitters.Clear();
        // Example default: no automatic creation; keep API for user to register
    }

    // Public API to register a merger at a given tail cell (N upstreams feed same consumer). Tail is where items eject.
    public void RegisterMergerAtTail(Vector2Int tailCell, MergerEndpoint merger)
    {
        mergers[tailCell] = merger;
    }

    // Public API to register a splitter at a given head cell (produces to N downstream runs)
    public void RegisterSplitterAtHead(Vector2Int headCell, SplitterEndpoint splitter, List<int> outRunIndices)
    {
        splitters[headCell] = (splitter, outRunIndices);
    }

    void FixedUpdate()
    {
        if (tick == null) return;
        var svc = BeltGraphService.Instance; if (svc == null) return;
        var runs = svc.Runs;
        // Drive mergers: try output into the run whose head is at tail cell
        foreach (var kv in mergers)
        {
            var tailCell = kv.Key; var merger = kv.Value;
            int headIdx = IndexOfHead(svc, tailCell);
            if (headIdx >= 0) merger.TryOutputTo(runs[headIdx]);
        }
        // Drive splitters: alternate into configured outputs
        foreach (var kv in splitters)
        {
            var sp = kv.Value.sp; var outs = kv.Value.outRunIdx;
            var outsRuns = ListCache<BeltRun>.Get();
            for (int i = 0; i < outs.Count; i++) outsRuns.Add(runs[outs[i]]);
            sp.TrySplitTo(outsRuns);
            ListCache<BeltRun>.Release(outsRuns);
        }
    }

    int IndexOfHead(BeltGraphService svc, Vector2Int headCell)
    {
        var heads = svc.HeadCells;
        for (int i = 0; i < heads.Count; i++) if (heads[i] == headCell) return i;
        return -1;
    }
}
