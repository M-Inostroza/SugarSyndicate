using System;
using UnityEngine;

/// <summary>
/// Marks a grid cell as containing a water pipe. Connects adjacent pipes/pumps for supply checks.
/// </summary>
public class WaterPipe : MonoBehaviour
{
    [SerializeField] GridService grid;
    [SerializeField] WaterNetworkService waterNetwork;

    [System.NonSerialized] public bool isGhost = false;

    Vector2Int cell;
    bool registered;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (waterNetwork == null) waterNetwork = WaterNetworkService.EnsureInstance();
    }

    void Start()
    {
        if (isGhost) return;
        TryRegister();
    }

    void OnDestroy()
    {
        if (isGhost) return;
        if (waterNetwork == null) waterNetwork = WaterNetworkService.Instance;
        if (registered) waterNetwork?.UnregisterPipe(cell);
        registered = false;

        try
        {
            if (grid == null) grid = GridService.Instance;
            if (grid != null)
            {
                grid.ClearCell(cell);
                var c = grid.GetCell(cell);
                if (c != null) c.hasMachine = false;
            }
        }
        catch { }
    }

    void TryRegister()
    {
        if (isGhost) return;
        if (grid == null) return;
        if (waterNetwork == null) waterNetwork = WaterNetworkService.Instance;
        cell = grid.WorldToCell(transform.position);
        // Occupy the cell to block other machines/belts
        grid.SetMachineCell(cell);
        transform.position = grid.CellToWorld(cell, transform.position.z);
        waterNetwork?.RegisterPipe(cell);
        registered = true;
    }
}
