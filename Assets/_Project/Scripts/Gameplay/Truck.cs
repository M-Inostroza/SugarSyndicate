using System;
using UnityEngine;

// Simple sink that consumes any item. Counts SugarBlock deliveries.
public class Truck : MonoBehaviour, IMachine, IMachineStorageWithCapacity, IPowerConsumer
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;

    [Header("State")]
    [SerializeField] bool activeOnStart = false;

    [Header("Capacity")]
    [SerializeField, Min(0)] int capacity = 30;
    [SerializeField] int currentLoad;

    [Header("Power")]
    [SerializeField, Min(0f)] float powerUsageWatts = 0f;

    public int StoredItemCount => Mathf.Max(0, currentLoad);
    public int Capacity => Mathf.Max(0, capacity);

    public static event Action<string> OnItemDelivered;
    public static event Action<int> OnSugarBlockDelivered;
    public static event Action<bool> OnTrucksCalledChanged;

    public Vector2Int Cell => cell;
    public Vector2Int InputVec => Vector2Int.zero;

    Vector2Int cell;
    bool registered;
    int sugarBlocksDelivered;
    bool isActive;
    const string SugarBlockType = "SugarBlock";

    static bool trucksCalled;
    public static bool TrucksCalled => trucksCalled;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        isActive = trucksCalled || activeOnStart;
    }

    void OnEnable()
    {
        OnTrucksCalledChanged += HandleTrucksCalledChanged;
    }

    void OnDisable()
    {
        OnTrucksCalledChanged -= HandleTrucksCalledChanged;
    }

    void Start()
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;

        EnsureStorageDisplay();

        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        powerService?.RegisterConsumer(this);

        TryRegisterAsMachineAndSnap();
        MachineRegistry.Register(this);
        registered = true;
    }

    void OnDestroy()
    {
        try
        {
            if (powerService == null) powerService = PowerService.Instance;
            powerService?.UnregisterConsumer(this);
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

    void HandleTrucksCalledChanged(bool called)
    {
        if (called) Activate();
        else Deactivate();
    }

    public static void SetTrucksCalled(bool called)
    {
        if (trucksCalled == called) return;
        trucksCalled = called;
        try { OnTrucksCalledChanged?.Invoke(trucksCalled); } catch { }
    }

    public bool CanAcceptFrom(Vector2Int approachFromVec)
    {
        return isActive;
    }

    public bool TryStartProcess(Item item)
    {
        if (!isActive) return false;
        if (!HasPower()) return false;
        if (item == null) return false;

        if (currentLoad >= capacity)
        {
            Debug.Log($"[Truck] Rejecting item; capacity full ({currentLoad}/{capacity}) at {cell}");
            return false;
        }

        var type = string.IsNullOrWhiteSpace(item.type) ? "Unknown" : item.type.Trim();
        Debug.Log($"[Truck] Accepted item '{type}' at {cell}");

        currentLoad++;
        LevelStats.RecordShipped(type);
        OnItemDelivered?.Invoke(type);

        if (IsSugarBlock(type))
        {
            sugarBlocksDelivered++;
            OnSugarBlockDelivered?.Invoke(sugarBlocksDelivered);
        }
        return true; // consume any item
    }

    void EnsureStorageDisplay()
    {
        if (GetComponent<MachineStorageDisplay>() != null) return;
        gameObject.AddComponent<MachineStorageDisplay>();
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

    public float GetConsumptionWatts()
    {
        return Mathf.Max(0f, powerUsageWatts);
    }

    bool HasPower()
    {
        if (powerUsageWatts <= 0f) return true;
        if (!PowerConsumerUtil.IsMachinePowered(this)) return false;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        return powerService != null && powerService.HasPowerFor(powerUsageWatts);
    }
}
