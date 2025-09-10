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
    }

    void Start()
    {
        if (tickService == null) tickService = FindObjectOfType<BeltTickService>();
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
        var gs = FindGridService(out var miWorldToCell);
        if (gs == null || miWorldToCell == null)
        {
            // No grid yet, skip
            return;
        }
        tiles.Clear();
        BuildTilesFromSceneConveyors(gs, miWorldToCell, tiles);
        graph.BuildRuns(tiles);
        if (tickService != null)
        {
            tickService.SetGraph(graph);
            // attach default endpoints if missing so producers can feed immediately
            for (int i = 0; i < graph.runs.Count; i++)
            {
                if (tickService.heads[i] == null) tickService.heads[i] = new HeadQueueEndpoint();
                if (tickService.tails[i] == null) tickService.tails[i] = new TailSinkEndpoint();
            }
        }
        OnGraphRebuilt?.Invoke(graph);
    }

    // External API: produce an item at the run whose head cell matches 'headCell'. Returns true if admitted or queued.
    public bool TryProduceAtHead(Vector2Int headCell, int itemId)
    {
        if (tickService == null) return false;
        for (int i = 0; i < graph.headCells.Count; i++)
        {
            if (graph.headCells[i] == headCell)
            {
                var ep = tickService.heads[i];
                if (ep == null) { ep = tickService.heads[i] = new HeadQueueEndpoint(); }
                ep.Produce(itemId);
                return true;
            }
        }
        return false;
    }

    object FindGridService(out MethodInfo worldToCell)
    {
        worldToCell = null;
        var mbs = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in mbs)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name == "GridService")
            {
                worldToCell = t.GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
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
}
