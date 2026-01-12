using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Simple pipe deletion tool: drag a path and remove any WaterPipe instances along it.
/// </summary>
public class PipeDeletionTool : MonoBehaviour
{
    [SerializeField] GameObject deletionGhostPrefab;

    MachineBuilder cachedMachineBuilder;
    bool didSearchMachineBuilder;
    System.Reflection.FieldInfo fiWaterPipeCost;

    object grid;
    System.Reflection.MethodInfo miWorldToCell;
    System.Reflection.MethodInfo miCellToWorld;

    bool dragging;
    Vector2Int startCell;
    List<Vector2Int> path = new();
    readonly List<GameObject> ghosts = new();

    void Awake()
    {
        CacheGrid();
    }

    void CacheGrid()
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                if (mb.GetType().Name == "GridService") { grid = mb; break; }
            }
            if (grid != null)
            {
                var t = grid.GetType();
                miWorldToCell = t.GetMethod("WorldToCell", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null);
                miCellToWorld = t.GetMethod("CellToWorld", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
            }
        }
        catch { }
    }

    MachineBuilder GetMachineBuilderCached()
    {
        if (!didSearchMachineBuilder)
        {
            didSearchMachineBuilder = true;
            cachedMachineBuilder = FindAnyObjectByType<MachineBuilder>();
            if (cachedMachineBuilder != null)
            {
                try
                {
                    fiWaterPipeCost = cachedMachineBuilder.GetType().GetField("waterPipeCost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                }
                catch { }
            }
        }
        return cachedMachineBuilder;
    }

    void Update()
    {
        if (grid == null || miWorldToCell == null || miCellToWorld == null) { CacheGrid(); if (grid == null) return; }
        var cam = Camera.main; if (cam == null) return;
        var world = GetMouseWorldOnPlane(cam);

        if (!dragging)
        {
            if (Input.GetMouseButtonDown(0))
            {
                startCell = WorldToCell(world);
                dragging = true;
                path.Clear();
                path.Add(startCell);
                UpdateGhosts(startCell, startCell);
            }
        }
        else
        {
            var cur = WorldToCell(world);
            UpdateGhosts(startCell, cur);
            if (Input.GetMouseButtonUp(0))
            {
                DeleteAlongPath();
                ClearGhosts();
                dragging = false;
            }
        }
    }

    Vector2Int WorldToCell(Vector3 world)
    {
        try { return (Vector2Int)miWorldToCell.Invoke(grid, new object[] { world }); }
        catch { return default; }
    }

    void UpdateGhosts(Vector2Int a, Vector2Int b)
    {
        var newPath = BuildManhattanPath(a, b);
        path = newPath;
        for (int i = 0; i < path.Count; i++)
        {
            if (i >= ghosts.Count)
            {
                var go = deletionGhostPrefab != null ? Instantiate(deletionGhostPrefab) : new GameObject("DeleteGhost");
                ghosts.Add(go);
            }
            var pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { path[i], 0f });
            ghosts[i].transform.position = pos;
            ghosts[i].SetActive(true);
        }
        for (int i = path.Count; i < ghosts.Count; i++) ghosts[i].SetActive(false);
    }

    void ClearGhosts()
    {
        foreach (var g in ghosts) { if (g != null) Destroy(g); }
        ghosts.Clear();
    }

    void DeleteAlongPath()
    {
        foreach (var cell in path)
        {
            var go = FindPipeAtCell(cell);
            if (go != null)
            {
                try
                {
                    int refund = 0;
                    var tag = go.GetComponentInParent<BuildCostTag>() ?? go.GetComponentInChildren<BuildCostTag>(true);
                    if (tag != null) refund = Mathf.Max(0, tag.Cost);
                    if (refund <= 0)
                    {
                        // Fallback for older pipes without BuildCostTag
                        var mb = GetMachineBuilderCached();
                        if (mb != null)
                        {
                            if (fiWaterPipeCost != null) refund = Mathf.Max(0, (int)fiWaterPipeCost.GetValue(mb));
                        }
                    }
                    if (refund > 0) GameManager.Instance?.AddSweetCredits(refund);
                }
                catch { }

                Destroy(go);
            }
        }
    }

    GameObject FindPipeAtCell(Vector2Int cell)
    {
        try
        {
            var all = FindObjectsByType<WaterPipe>(FindObjectsSortMode.None);
            foreach (var p in all)
            {
                var c = ((GridService)grid).WorldToCell(p.transform.position);
                if (c == cell) return p.gameObject;
            }
        }
        catch { }
        return null;
    }

    List<Vector2Int> BuildManhattanPath(Vector2Int start, Vector2Int end)
    {
        var path = new List<Vector2Int>();
        path.Add(start);
        var cur = start;
        int dx = end.x - cur.x;
        int stepX = System.Math.Sign(dx);
        for (int i = 0; i < Mathf.Abs(dx); i++)
        {
            cur += new Vector2Int(stepX, 0);
            path.Add(cur);
        }
        int dy = end.y - cur.y;
        int stepY = System.Math.Sign(dy);
        for (int i = 0; i < Mathf.Abs(dy); i++)
        {
            cur += new Vector2Int(0, stepY);
            path.Add(cur);
        }
        return path;
    }

    static Vector3 GetMouseWorldOnPlane(Camera cam)
    {
        var mp = Input.mousePosition;
        float planeZ = 0f;
        float camZ = cam.transform.position.z;
        mp.z = planeZ - camZ;
        var world = cam.ScreenToWorldPoint(mp);
        world.z = 0f;
        return world;
    }
}
