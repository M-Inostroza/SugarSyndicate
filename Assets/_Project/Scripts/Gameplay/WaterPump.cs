using System;
using UnityEngine;

/// <summary>
/// Simple emitter that draws from a water tile and outputs water items on a cadence.
/// Registers as an IMachine so belt sim can treat the cell as occupied (but it never accepts input).
/// </summary>
public class WaterPump : MonoBehaviour, IMachine
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] WaterNetworkService waterNetwork;

    [Header("Orientation (legacy, unused for output)")]
    [Tooltip("Output/facing vector. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0);

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    public Vector2Int InputVec => Vector2Int.zero; // does not accept input
    public Vector2Int Cell => cell;

    Vector2Int cell;
    bool registered;

    void DLog(string msg) { if (debugLogging) Debug.Log(msg); }
    void DWarn(string msg) { if (debugLogging) Debug.LogWarning(msg); }

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (waterNetwork == null) waterNetwork = WaterNetworkService.EnsureInstance();
    }

    void Start()
    {
        if (grid == null) return;

        TryRegisterAsMachineAndSnap();
        MachineRegistry.Register(this);
        registered = true;

        if (waterNetwork == null) waterNetwork = WaterNetworkService.Instance;
        waterNetwork?.RegisterPump(cell);

        if (!grid.IsWater(cell))
        {
            Debug.LogWarning($"[WaterPump] Placed off water at {cell}. Pump will not supply water until moved onto water.");
        }
    }

    void OnDestroy()
    {
        if (registered)
        {
            MachineRegistry.Unregister(this);
        }
        if (waterNetwork == null) waterNetwork = WaterNetworkService.Instance;
        waterNetwork?.UnregisterPump(cell);

        if (grid == null) grid = GridService.Instance;
        if (grid != null)
        {
            grid.ClearCell(cell);
            var c = grid.GetCell(cell);
            if (c != null) c.hasMachine = false;
        }
    }

    public bool CanAcceptFrom(Vector2Int approachFromVec) => false; // never accepts input

    public bool TryStartProcess(Item item) => false; // cannot process intake

    void TryRegisterAsMachineAndSnap()
    {
        try
        {
            if (grid == null) grid = GridService.Instance;
            if (grid == null) return;

            cell = grid.WorldToCell(transform.position);
            grid.SetMachineCell(cell);
            var world = grid.CellToWorld(cell, transform.position.z);
            transform.position = world;
        }
        catch (Exception ex) { DWarn($"[WaterPump] Registration failed: {ex.Message}"); }
    }
}
