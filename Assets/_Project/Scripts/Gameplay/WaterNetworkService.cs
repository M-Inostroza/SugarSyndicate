using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks water pumps and pipes and answers "is this cell connected to a pump via pipes?"
/// Uses simple BFS per query; fine for small grids.
/// </summary>
public class WaterNetworkService : MonoBehaviour
{
    public static WaterNetworkService Instance { get; private set; }

    readonly HashSet<Vector2Int> pumps = new();
    readonly HashSet<Vector2Int> pipes = new();

    public event Action OnNetworkChanged;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public static WaterNetworkService EnsureInstance()
    {
        if (Instance != null) return Instance;
        var existing = FindAnyObjectByType<WaterNetworkService>();
        if (existing != null) { Instance = existing; return Instance; }
        var go = new GameObject("WaterNetworkService");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<WaterNetworkService>();
        return Instance;
    }

    public void RegisterPump(Vector2Int cell)
    {
        pumps.Add(cell);
        OnNetworkChanged?.Invoke();
    }

    public void UnregisterPump(Vector2Int cell)
    {
        pumps.Remove(cell);
        OnNetworkChanged?.Invoke();
    }

    public void RegisterPipe(Vector2Int cell)
    {
        pipes.Add(cell);
        OnNetworkChanged?.Invoke();
    }

    public void UnregisterPipe(Vector2Int cell)
    {
        pipes.Remove(cell);
        OnNetworkChanged?.Invoke();
    }

    /// <summary>
    /// Returns true if the given cell is adjacent to a pipe/pump chain that reaches any pump.
    /// </summary>
    public bool HasSupply(Vector2Int cell)
    {
        // Immediate pump on cell
        if (pumps.Contains(cell)) return true;

        var visited = new HashSet<Vector2Int>();
        var queue = new Queue<Vector2Int>();

        // Seed with neighboring pipe/pump cells
        foreach (var n in Neighbors(cell))
        {
            if (IsPipeOrPump(n) && visited.Add(n)) queue.Enqueue(n);
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (pumps.Contains(cur)) return true;
            foreach (var n in Neighbors(cur))
            {
                if (IsPipeOrPump(n) && visited.Add(n)) queue.Enqueue(n);
            }
        }

        return false;
    }

    bool IsPipeOrPump(Vector2Int c) => pipes.Contains(c) || pumps.Contains(c);

    static IEnumerable<Vector2Int> Neighbors(Vector2Int c)
    {
        yield return c + new Vector2Int(1, 0);
        yield return c + new Vector2Int(-1, 0);
        yield return c + new Vector2Int(0, 1);
        yield return c + new Vector2Int(0, -1);
    }

    public bool HasSupplyWorld(Vector3 world, GridService grid = null)
    {
        grid ??= GridService.Instance;
        if (grid == null) return false;
        var cell = grid.WorldToCell(world);
        return HasSupply(cell);
    }
}
