using System;
using System.Collections.Generic;
using UnityEngine;

// Central place to build BeltGraph from placed Conveyor tiles and push it into BeltTickService.
public class BeltGraphService : MonoBehaviour
{
    public static BeltGraphService Instance { get; private set; }

    [Header("Runtime")]
    [SerializeField] BeltTickService tickService;
    [SerializeField] bool autoRebuildOnStart = true;
    [SerializeField] MonoBehaviour gridServiceBehaviour;

    readonly BeltGraph graph = new BeltGraph();
    // tiles are maintained incrementally as conveyors register with the service
    readonly Dictionary<IConveyor, Vector2Int> conveyorCells = new Dictionary<IConveyor, Vector2Int>();
    readonly Dictionary<Vector2Int, BeltTile> tileMap = new Dictionary<Vector2Int, BeltTile>();
    readonly List<BeltTile> tiles = new List<BeltTile>(256);

    IGridService gridService;

    public event Action<BeltGraph> OnGraphRebuilt;
    public IReadOnlyList<BeltRun> Runs => graph.runs;
    public IReadOnlyList<Vector2Int> HeadCells => graph.headCells;
    public IReadOnlyList<Vector2Int> TailCells => graph.tailCells;

    bool dirty;

    // Minimal endpoints used by default
    class HeadQueueEndpoint : BeltEndpoint { }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        Debug.Log("[BeltGraphService] Awake");
    }

    void Start()
    {
        if (tickService == null)
        {
#if UNITY_2023_1_OR_NEWER
            tickService = UnityEngine.Object.FindFirstObjectByType<BeltTickService>();
#else
#pragma warning disable 0618
            tickService = UnityEngine.Object.FindObjectOfType<BeltTickService>();
#pragma warning restore 0618
#endif
        }
        // Ensure there is a tick service in the scene so items can move
        if (tickService == null)
        {
            var go = new GameObject("BeltTickService");
            tickService = go.AddComponent<BeltTickService>();
            Debug.Log("[BeltGraphService] Created runtime BeltTickService because none was found in the scene.");
        }
        if (gridServiceBehaviour != null)
            gridService = gridServiceBehaviour as IGridService;

        // discover any conveyors already present
        ScanAndRegisterExistingConveyors();

        if (autoRebuildOnStart) RebuildGraph();
    }

    void Update()
    {
        if (!dirty) return;
        dirty = false;
        RebuildGraph();
    }

    public void MarkDirty()
    {
        dirty = true;
    }

    public void RegisterConveyor(IConveyor conveyor)
    {
        if (conveyor == null) return;
        if (gridService == null) TryFindGridService();
        var mb = conveyor as MonoBehaviour;
        if (mb == null || gridService == null) return;
        var cell = gridService.WorldToCell(mb.transform.position);
        var dir = conveyor.DirVec();
        Direction2D d = Direction2D.Right;
        if (dir == Vector2Int.left) d = Direction2D.Left;
        else if (dir == Vector2Int.up) d = Direction2D.Up;
        else if (dir == Vector2Int.down) d = Direction2D.Down;
        int tunnelId = 0;
        if (mb.TryGetComponent<TunnelTag>(out var tunnel)) tunnelId = tunnel.tunnelId;

        if (conveyorCells.TryGetValue(conveyor, out var oldCell) && oldCell != cell)
            tileMap.Remove(oldCell);
        tileMap[cell] = new BeltTile { cell = cell, dir = d, tunnelId = tunnelId };
        conveyorCells[conveyor] = cell;
        MarkDirty();
    }

    public void UnregisterConveyor(IConveyor conveyor)
    {
        if (conveyor == null) return;
        if (conveyorCells.TryGetValue(conveyor, out var cell))
        {
            tileMap.Remove(cell);
            conveyorCells.Remove(conveyor);
            MarkDirty();
        }
    }

    public void RebuildGraph()
    {
        Debug.Log("[BeltGraphService] RebuildGraph()");
        TryFindGridService();
        tiles.Clear();
        foreach (var kv in tileMap) tiles.Add(kv.Value);
        graph.BuildRuns(tiles);

        // rebuild run positions using grid service if available
        if (gridService != null)
        {
            for (int i = 0; i < graph.runs.Count; i++)
            {
                var cells = GetRunCells(graph, i);
                Vector3 Converter(Vector2Int c) => gridService.CellToWorld(c, 0f);
                graph.runs[i].BuildFromCells(cells, Converter);
            }
        }
        Debug.Log($"[BeltGraphService] Built {graph.runs.Count} runs. Heads={graph.headCells.Count}, Tails={graph.tailCells.Count}");
        if (tickService != null)
        {
            tickService.SetGraph(graph, preserveItems: true);
            AutoWireEndpoints();
            Debug.Log($"[BeltGraphService] Tick wired: heads={tickService.heads.Count}, tails={tickService.tails.Count}");
        }
        OnGraphRebuilt?.Invoke(graph);
    }

    void AutoWireEndpoints()
    {
        var inc = graph.incoming;
        var outg = graph.outgoing;

        while (tickService.heads.Count < graph.runs.Count) tickService.heads.Add(null);
        while (tickService.tails.Count < graph.runs.Count) tickService.tails.Add(null);

        for (int i = 0; i < inc.Count; i++)
        {
            bool isSelfLoop = inc[i].Count == 1 && inc[i][0] == i;
            if (inc[i].Count > 1 || isSelfLoop)
            {
                if (!(tickService.heads[i] is MergerEndpoint)) tickService.heads[i] = new MergerEndpoint();
            }
            else
            {
                if (tickService.heads[i] == null) tickService.heads[i] = new HeadQueueEndpoint();
            }
        }

        for (int i = 0; i < outg.Count; i++)
        {
            if (outg[i].Count > 1)
            {
                if (!(tickService.tails[i] is SplitterEndpoint)) tickService.tails[i] = new SplitterEndpoint();
            }
        }

        // Automatically pair tunnels
        var headTunnel = new Dictionary<int, int>();
        var tailTunnel = new Dictionary<int, int>();
        foreach (var kv in tileMap)
        {
            var tile = kv.Value;
            if (tile.tunnelId <= 0) continue;
            for (int i = 0; i < graph.headCells.Count; i++) if (graph.headCells[i] == tile.cell) headTunnel[tile.tunnelId] = i;
            for (int i = 0; i < graph.tailCells.Count; i++) if (graph.tailCells[i] == tile.cell) tailTunnel[tile.tunnelId] = i;
        }
        foreach (var kv in headTunnel)
        {
            if (!tailTunnel.TryGetValue(kv.Key, out var tailIdx)) continue;
            var headIdx = kv.Value;
            var tailEp = tickService.tails[tailIdx] as TunnelEndpoint;
            if (tailEp == null) { tailEp = new TunnelEndpoint(); tickService.tails[tailIdx] = tailEp; }
            var headEp = tickService.heads[headIdx] as TunnelEndpoint;
            if (headEp == null) { headEp = new TunnelEndpoint(); tickService.heads[headIdx] = headEp; }
            tailEp.peer = headEp;
        }
    }

    // External API: produce an item at the run whose head cell matches 'headCell'. Returns true if admitted or queued.
    public bool TryProduceAtHead(Vector2Int headCell, int itemId)
    {
        if (tickService == null) { Debug.LogWarning("[BeltGraphService] TryProduceAtHead called but no BeltTickService."); return false; }
        for (int i = 0; i < graph.headCells.Count; i++)
        {
            if (graph.headCells[i] == headCell)
            {
                var ep = tickService.heads[i];
                if (ep == null) { ep = tickService.heads[i] = new HeadQueueEndpoint(); }
                ep.Produce(itemId);
                Debug.Log($"[BeltGraphService] Queued item {itemId} at head cell {headCell} (run {i}).");
                return true;
            }
        }
        Debug.LogWarning($"[BeltGraphService] No run head at cell {headCell}; item {itemId} not queued.");
        return false;
    }

    void TryFindGridService()
    {
        if (gridService != null) return;
        if (gridServiceBehaviour != null)
        {
            gridService = gridServiceBehaviour as IGridService;
            if (gridService != null) return;
        }
        var mbs = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in mbs)
        {
            if (mb is IGridService gs)
            {
                gridService = gs;
                gridServiceBehaviour = mb;
                return;
            }
        }
    }

    // NEW: scan scene to pick up conveyors that may have awoken before this service
    void ScanAndRegisterExistingConveyors()
    {
        TryFindGridService();
        var mbs = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in mbs)
        {
            if (mb is IConveyor conv)
            {
                RegisterConveyor(conv);
            }
        }
    }

    // Reconstruct the cell sequence for a run using head/tail info and tiles index
    List<Vector2Int> GetRunCells(BeltGraph g, int runIndex)
    {
        var cells = new List<Vector2Int>();
        var head = g.headCells[runIndex];
        var tail = g.tailCells[runIndex];
        cells.Add(head);
        var cur = head;
        // step along tiles list using current tileMap directions
        while (cur != tail)
        {
            var dir = tileMap[cur].dir;
            var next = cur + Dir2D.ToVec(dir);
            cells.Add(next);
            cur = next;
        }
        return cells;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (graph == null) return;
        Gizmos.color = Color.yellow;
        foreach (var run in graph.runs)
        {
            for (int i = 0; i < run.points.Count - 1; i++)
                Gizmos.DrawLine(run.points[i], run.points[i + 1]);
            foreach (var item in run.items)
            {
                run.PositionAt(item.offset, out var p, out var _);
                Gizmos.DrawSphere(p, 0.05f);
            }
        }
    }
#endif
}
