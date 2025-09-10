using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Central place to build BeltGraph from placed Conveyor tiles and push it into BeltTickService.
public class BeltGraphService : MonoBehaviour
{
    public static BeltGraphService Instance { get; private set; }

    [Header("Runtime")]
    [SerializeField] BeltTickService tickService;
    [SerializeField] bool autoRebuildOnStart = true;

    readonly BeltGraph graph = new BeltGraph();
    readonly List<BeltTile> tiles = new List<BeltTile>(256);

    public event Action<BeltGraph> OnGraphRebuilt;
    public IReadOnlyList<BeltRun> Runs => graph.runs;
    public IReadOnlyList<Vector2Int> HeadCells => graph.headCells;
    public IReadOnlyList<Vector2Int> TailCells => graph.tailCells;

    bool dirty;

    // Minimal endpoints used by default
    class HeadQueueEndpoint : BeltEndpoint { }
    class TailSinkEndpoint : BeltEndpoint { public override void OnInputItem(int itemId) { /* no-op */ } }

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
        if (autoRebuildOnStart) RebuildFromScene();
    }

    void Update()
    {
        if (!dirty) return;
        dirty = false;
        RebuildFromScene();
    }

    public void MarkDirty()
    {
        dirty = true;
    }

    public void RebuildFromScene()
    {
        Debug.Log("[BeltGraphService] RebuildFromScene()");
        var gs = FindGridService(out var miWorldToCell, out var miCellToWorld);
        if (gs == null || miWorldToCell == null)
        {
            Debug.LogWarning("[BeltGraphService] No GridService or WorldToCell method found. Belts cannot be built.");
            return;
        }
        tiles.Clear();
        BuildTilesFromSceneConveyors(gs, miWorldToCell, tiles);
        Debug.Log($"[BeltGraphService] Found {tiles.Count} Conveyor tiles in scene.");
        graph.BuildRuns(tiles);
        // Rebuild the polyline positions using the grid's CellToWorld so visuals align with the grid origin/scale
        if (miCellToWorld != null)
        {
            for (int i = 0; i < graph.runs.Count; i++)
            {
                var cells = GetRunCells(graph, i); // helper to reconstruct cells of the run
                Vector3 Converter(Vector2Int c)
                {
                    var w = miCellToWorld.Invoke(gs, new object[] { c, 0f });
                    return w is Vector3 v ? v : new Vector3(c.x + 0.5f, c.y + 0.5f, 0f);
                }
                graph.runs[i].BuildFromCells(cells, Converter);
            }
        }
        Debug.Log($"[BeltGraphService] Built {graph.runs.Count} runs. Heads={graph.headCells.Count}, Tails={graph.tailCells.Count}");
        if (tickService != null)
        {
            tickService.SetGraph(graph, preserveItems: true);
            // attach default head endpoints so producers can feed immediately
            for (int i = 0; i < graph.runs.Count; i++)
            {
                if (tickService.heads[i] == null) tickService.heads[i] = new HeadQueueEndpoint();
                // do NOT attach default tails; leaving null means tail is blocked -> jam
            }
            Debug.Log($"[BeltGraphService] Tick wired: heads={tickService.heads.Count}, tails={tickService.tails.Count}");
        }
        OnGraphRebuilt?.Invoke(graph);
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

    object FindGridService(out MethodInfo worldToCell, out MethodInfo cellToWorld)
    {
        worldToCell = null; cellToWorld = null;
        var mbs = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in mbs)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name == "GridService")
            {
                worldToCell = t.GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
                cellToWorld = t.GetMethod("CellToWorld", new Type[] { typeof(Vector2Int), typeof(float) });
                if (worldToCell == null) Debug.LogWarning("[BeltGraphService] GridService found but WorldToCell method missing.");
                return mb;
            }
        }
        return null;
    }

    void BuildTilesFromSceneConveyors(object gridServiceInstance, MethodInfo worldToCell, List<BeltTile> outTiles)
    {
        var mbs = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in mbs)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name != "Conveyor") continue;

            // cell via grid
            var cellObj = worldToCell.Invoke(gridServiceInstance, new object[] { mb.transform.position });
            var cell = cellObj is Vector2Int v2 ? v2 : Vector2Int.zero;

            // get dir via Conveyor.DirVec()
            var miDir = t.GetMethod("DirVec", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Vector2Int dv = Vector2Int.right;
            if (miDir != null)
            {
                var res = miDir.Invoke(mb, null);
                if (res is Vector2Int vd) dv = vd;
            }

            Direction2D dir = Direction2D.Right;
            if (dv == Vector2Int.left) dir = Direction2D.Left;
            else if (dv == Vector2Int.up) dir = Direction2D.Up;
            else if (dv == Vector2Int.down) dir = Direction2D.Down;

            outTiles.Add(new BeltTile { cell = cell, dir = dir });
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
        // step along tiles list using Dir2D from tiles map
        var map = new Dictionary<Vector2Int, Direction2D>();
        foreach (var t in tiles) map[t.cell] = t.dir;
        while (cur != tail)
        {
            var dir = map[cur];
            var next = cur + Dir2D.ToVec(dir);
            cells.Add(next);
            cur = next;
        }
        return cells;
    }
}
