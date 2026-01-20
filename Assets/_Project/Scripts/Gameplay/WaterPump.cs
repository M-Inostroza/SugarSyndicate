using System;
using UnityEngine;

/// <summary>
/// Simple emitter that draws from a water tile and outputs water items on a cadence.
/// Registers as an IMachine so belt sim can treat the cell as occupied (but it never accepts input).
/// </summary>
public class WaterPump : MonoBehaviour, IMachine, IPowerConsumer
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] WaterNetworkService waterNetwork;
    [SerializeField] PowerService powerService;

    [Header("Orientation (legacy, unused for output)")]
    [Tooltip("Output/facing vector. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0);

    [Header("Power")]
    [SerializeField, Min(0f)] float powerUsageWatts = 0f;

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    [System.NonSerialized] public bool isGhost = false;

    public Vector2Int InputVec => Vector2Int.zero; // does not accept input
    public Vector2Int Cell => cell;

    Vector2Int cell;
    bool registered;
    bool pumpRegistered;

    void DLog(string msg) { if (debugLogging) Debug.Log(msg); }
    void DWarn(string msg) { if (debugLogging) Debug.LogWarning(msg); }

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (waterNetwork == null) waterNetwork = WaterNetworkService.EnsureInstance();
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
    }

    void Start()
    {
        if (isGhost) return;
        if (grid == null) return;

        TryRegisterAsMachineAndSnap();
        MachineRegistry.Register(this);
        registered = true;

        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        powerService?.RegisterConsumer(this);

        UpdatePowerRegistration(true);

        if (!grid.IsWater(cell))
        {
            Debug.LogWarning($"[WaterPump] Placed off water at {cell}. Pump will not supply water until moved onto water.");
        }
    }

    void OnEnable()
    {
        UndergroundVisibilityRegistry.RegisterOverlay(this);
    }

    void OnDisable()
    {
        UndergroundVisibilityRegistry.UnregisterOverlay(this);
    }

    void Update()
    {
        if (isGhost) return;
        UpdatePowerRegistration(false);
    }

    void OnDestroy()
    {
        if (isGhost) return;
        if (powerService == null) powerService = PowerService.Instance;
        powerService?.UnregisterConsumer(this);
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

    public float GetConsumptionWatts()
    {
        if (isGhost) return 0f;
        return Mathf.Max(0f, powerUsageWatts);
    }

    bool HasPower()
    {
        if (powerUsageWatts <= 0f) return true;
        if (!PowerConsumerUtil.IsMachinePowered(this)) return false;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        return powerService != null && powerService.HasPowerFor(this, powerUsageWatts);
    }

    void UpdatePowerRegistration(bool force)
    {
        bool hasPower = HasPower();
        if (!force && hasPower == pumpRegistered) return;
        pumpRegistered = hasPower;
        if (waterNetwork == null) waterNetwork = WaterNetworkService.Instance;
        if (pumpRegistered) waterNetwork?.RegisterPump(cell);
        else waterNetwork?.UnregisterPump(cell);
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
