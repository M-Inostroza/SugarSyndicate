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
    [SerializeField] BeltSimulationService belt;

    [Header("Orientation")]
    [Tooltip("Output/facing vector. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0);

    [Header("Product")]
    [SerializeField] GameObject itemPrefab;
    [SerializeField] string outputItemType = "Water";
    [SerializeField, Min(0)] int poolPrewarm = 8;

    [Header("Timing")]
    [SerializeField, Min(1)] int intervalTicks = 10;
    [SerializeField] bool autoStart = true;

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    public Vector2Int InputVec => Vector2Int.zero; // does not accept input
    public Vector2Int Cell => cell;

    Vector2Int cell;
    bool registered;
    int tickCounter;
    bool running;

    void DLog(string msg) { if (debugLogging) Debug.Log(msg); }
    void DWarn(string msg) { if (debugLogging) Debug.LogWarning(msg); }

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (belt == null) belt = BeltSimulationService.Instance;
    }

    void Start()
    {
        running = autoStart;

        if (itemPrefab != null)
        {
            ItemViewPool.Ensure(itemPrefab, poolPrewarm);
        }

        if (grid == null) return;

        TryRegisterAsMachineAndSnap();
        MachineRegistry.Register(this);
        registered = true;

        if (!grid.IsWater(cell))
        {
            Debug.LogWarning($"[WaterPump] Placed off water at {cell}. Pump disabled until moved.");
            running = false;
        }
    }

    void OnEnable()
    {
        try { GameTick.OnTickStart += OnTick; } catch { }
    }

    void OnDisable()
    {
        try { GameTick.OnTickStart -= OnTick; } catch { }
    }

    void OnDestroy()
    {
        try { GameTick.OnTickStart -= OnTick; } catch { }
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

    public bool CanAcceptFrom(Vector2Int approachFromVec) => false; // never accepts input

    public bool TryStartProcess(Item item) => false; // cannot process intake

    void OnTick()
    {
        if (!running) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
        if (grid == null || belt == null) return;
        if (!grid.IsWater(cell)) return;

        tickCounter++;
        if (tickCounter < intervalTicks) return;
        tickCounter = 0;

        TryEmit();
    }

    void TryEmit()
    {
        var outCell = cell + facingVec;
        var item = new Item { type = ResolveOutputType() };

        if (!belt.TrySpawnItem(outCell, item))
        {
            DWarn($"[WaterPump] Output blocked at {outCell}");
            return;
        }

        // Spawn view using pool if available
        if (itemPrefab != null)
        {
            var parent = ContainerLocator.GetItemContainer();
            var world = grid.CellToWorld(outCell, itemPrefab.transform.position.z);
            var view = ItemViewPool.Get(itemPrefab, world, Quaternion.identity, parent);
            if (view == null)
            {
                var go = parent != null ? Instantiate(itemPrefab, world, Quaternion.identity, parent)
                                        : Instantiate(itemPrefab, world, Quaternion.identity);
                view = go.transform;
            }
            item.view = view;
            if (view != null) view.position = world;
        }
    }

    string ResolveOutputType()
    {
        if (!string.IsNullOrWhiteSpace(outputItemType)) return outputItemType.Trim();
        if (itemPrefab != null) return itemPrefab.name;
        return "Water";
    }

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
