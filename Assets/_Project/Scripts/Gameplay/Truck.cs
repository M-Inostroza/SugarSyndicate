using System;
using UnityEngine;

// Simple sink that consumes any item. Counts SugarBlock deliveries.
public class Truck : MonoBehaviour, IMachine
{
    [Header("Services")]
    [SerializeField] GridService grid;

    [Header("State")]
    [SerializeField] bool activeOnStart = false;

    public static event Action<string> OnItemDelivered;
    public static event Action<int> OnSugarBlockDelivered;

    public Vector2Int Cell => cell;
    public Vector2Int InputVec => Vector2Int.zero;

    Vector2Int cell;
    bool registered;
    int sugarBlocksDelivered;
    bool isActive;
    const string SugarBlockType = "SugarBlock";

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        isActive = activeOnStart;
    }

    void Start()
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;
        TryRegisterAsMachineAndSnap();
        MachineRegistry.Register(this);
        registered = true;
    }

    void OnDestroy()
    {
        try
        {
            if (!registered) return;
            MachineRegistry.Unregister(this);
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

    public void Activate() => isActive = true;
    public void Deactivate() => isActive = false;

    public bool CanAcceptFrom(Vector2Int approachFromVec)
    {
        return isActive;
    }

    public bool TryStartProcess(Item item)
    {
        if (!isActive) return false;
        if (item == null) return false;

        var type = string.IsNullOrWhiteSpace(item.type) ? "Unknown" : item.type.Trim();
        Debug.Log($"[Truck] Accepted item '{type}' at {cell}");
        OnItemDelivered?.Invoke(type);

        if (IsSugarBlock(type))
        {
            sugarBlocksDelivered++;
            OnSugarBlockDelivered?.Invoke(sugarBlocksDelivered);
        }
        return true; // consume any item
    }

    void TryRegisterAsMachineAndSnap()
    {
        try
        {
            if (grid == null) grid = GridService.Instance;
            if (grid == null) { Debug.LogWarning("[Truck] GridService not found"); return; }

            cell = grid.WorldToCell(transform.position);
            grid.SetMachineCell(cell);
            var world = grid.CellToWorld(cell, transform.position.z);
            transform.position = world;
        }
        catch { }
    }

    bool IsSugarBlock(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return string.Equals(type.Trim(), SugarBlockType, StringComparison.OrdinalIgnoreCase);
    }
}
