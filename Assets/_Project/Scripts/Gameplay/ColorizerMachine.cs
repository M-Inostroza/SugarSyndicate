using System;
using UnityEngine;

/// <summary>
/// Machine that takes an input item, applies a tint, and outputs the same item type.
/// Uses the shared IMachine contract so belt logic remains generic.
/// </summary>
public class ColorizerMachine : MonoBehaviour, IMachine, IMachineProgress, IPowerConsumer
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] BeltSimulationService belt;
    [SerializeField] PowerService powerService;

    [Header("Orientation")]
    [Tooltip("Output/facing vector. Input is the opposite side. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0);

    [Header("Visuals")]
    [Tooltip("Color applied to the item's sprite renderer when re-emitted.")]
    [SerializeField] Color outputColor = Color.red;

    [Header("Item Rules")]
    [Tooltip("Logical item types accepted as input. Leave empty to accept any type.")]
    [SerializeField] string[] acceptedItemTypes = new[] { "Sugar", "Cubes" };

    [Header("Processing")]
    [SerializeField, Min(0f)] float processingSeconds = 0.5f;
    [Tooltip("If true, processing advances on GameTick; otherwise it uses frame time.")]
    [SerializeField] bool useGameTickForProcessing = true;

    [Header("Power")]
    [SerializeField, Min(0f)] float powerUsageWatts = 0f;

    [System.NonSerialized] public bool isGhost = false;

    [Header("Maintenance")]
    [SerializeField] MachineMaintenance maintenance = new MachineMaintenance();

    [Header("Debug")]
    [SerializeField] bool enableDebugLogs = false;

    public Vector2Int InputVec => new Vector2Int(-facingVec.x, -facingVec.y);
    public Vector2Int OutputVec => facingVec;
    public Vector2Int Cell => cell;
    public bool IsProcessing => busy && hasInputThisCycle;
    public float Maintenance01 => maintenance != null ? maintenance.Level01 : 1f;
    public bool IsStopped => maintenance != null && maintenance.IsStopped;
    public string[] AcceptedItemTypes => ResolveAcceptedTypes();
    public float Progress01
    {
        get
        {
            if (!IsProcessing) return 0f;
            if (processingSeconds <= 0f) return 1f;
            if (waitingToOutput) return 1f;
            float t = 1f - Mathf.Clamp01(remainingTime / Mathf.Max(0.0001f, processingSeconds));
            return Mathf.Clamp01(t);
        }
    }

    Vector2Int cell;
    bool registered;

    // State
    bool busy;
    bool waitingToOutput;
    float remainingTime;
    bool hasInputThisCycle;
    Item carriedItem;
    Transform carriedView;

    void DLog(string msg) { if (enableDebugLogs) Debug.Log(msg); }
    void DWarn(string msg) { if (enableDebugLogs) Debug.LogWarning(msg); }

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (belt == null) belt = BeltSimulationService.Instance;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
    }

    void Start()
    {
        if (isGhost) return;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        powerService?.RegisterConsumer(this);
        if (grid == null) return;

        EnsureProgressDisplay();
        TryRegisterAsMachineAndSnap();
        MachineRegistry.Register(this);
        registered = true;
    }

    void OnEnable()
    {
        if (isGhost) return;
        if (useGameTickForProcessing)
        {
            try { GameTick.OnTickStart += OnTick; } catch { }
        }
    }

    void OnDisable()
    {
        if (isGhost) return;
        if (useGameTickForProcessing)
        {
            try { GameTick.OnTickStart -= OnTick; } catch { }
        }
    }

    void OnDestroy()
    {
        try
        {
            if (useGameTickForProcessing)
            {
                try { GameTick.OnTickStart -= OnTick; } catch { }
            }
            if (!isGhost)
            {
                if (powerService == null) powerService = PowerService.Instance;
                powerService?.UnregisterConsumer(this);
            }
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

    public bool CanAcceptFrom(Vector2Int approachFromVec)
    {
        if (IsStopped) return false;
        if (approachFromVec != InputVec) return false;
        if (busy) return false;
        return true;
    }

    public bool TryStartProcess(Item item)
    {
        if (busy) return false;
        if (IsStopped) return false;
        if (item == null) return false;

        var accepted = ResolveAcceptedTypes();
        if (!MatchesAcceptedTypes(item, accepted))
        {
            Debug.LogWarning($"[ColorizerMachine] Rejecting input at {cell}: expected {FormatAcceptedTypes(accepted)}, got '{FormatItemType(item.type)}'");
            return false;
        }

        if (maintenance != null && !maintenance.TryConsume(1)) return false;

        busy = true;
        hasInputThisCycle = true;
        waitingToOutput = false;
        remainingTime = Mathf.Max(0f, processingSeconds);
        carriedItem = item;
        carriedView = item.view;
        // prevent the belt sim from destroying the view; we'll reuse it
        item.view = null;
        return true;
    }

    void OnTick()
    {
        if (!useGameTickForProcessing) return;
        if (!busy) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
        if (!HasPower()) return;
        StepProcessing(GetTickDeltaSeconds());
    }

    void Update()
    {
        if (useGameTickForProcessing) return;
        if (GameManager.Instance == null || GameManager.Instance.State != GameState.Play)
            return;
        if (!busy) return;
        if (!HasPower()) return;
        StepProcessing(Time.deltaTime);
    }

    void StepProcessing(float dt)
    {
        if (!waitingToOutput)
        {
            remainingTime -= Mathf.Max(0f, dt);
            if (remainingTime <= 0f)
                waitingToOutput = true;
        }

        if (waitingToOutput)
        {
            if (hasInputThisCycle && TryProduceOutputNow())
            {
                busy = false;
                waitingToOutput = false;
                remainingTime = 0f;
                hasInputThisCycle = false;
            }
        }
    }

    public string GetProcessSummary()
    {
        string inputLabel = FormatTypeList(ResolveAcceptedTypes());
        if (string.IsNullOrWhiteSpace(inputLabel)) inputLabel = "Any";
        return $"1 {inputLabel} -> 1 {inputLabel} (tinted)";
    }

    bool HasPower()
    {
        if (powerUsageWatts <= 0f) return true;
        if (!PowerConsumerUtil.IsMachinePowered(this)) return false;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        return powerService != null && powerService.HasPowerFor(powerUsageWatts);
    }

    public float GetConsumptionWatts()
    {
        if (isGhost) return 0f;
        return Mathf.Max(0f, powerUsageWatts);
    }

    string[] ResolveAcceptedTypes()
    {
        if (acceptedItemTypes == null) return Array.Empty<string>();
        var list = new System.Collections.Generic.List<string>();
        foreach (var t in acceptedItemTypes)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            list.Add(t.Trim());
        }
        return list.ToArray();
    }

    static bool MatchesAcceptedTypes(Item item, string[] acceptedTypes)
    {
        if (item == null) return false;
        if (acceptedTypes == null || acceptedTypes.Length == 0) return true;
        return item.IsAnyType(acceptedTypes);
    }

    static string FormatAcceptedTypes(string[] acceptedTypes)
    {
        if (acceptedTypes == null || acceptedTypes.Length == 0) return "'Any'";
        return "'" + string.Join("', '", acceptedTypes) + "'";
    }

    static string FormatTypeList(string[] types)
    {
        if (types == null || types.Length == 0) return "Any";
        if (types.Length == 1) return types[0];
        return string.Join("/", types);
    }

    static string FormatItemType(string raw) => string.IsNullOrWhiteSpace(raw) ? "Unknown" : raw;

    float GetTickDeltaSeconds()
    {
        int tps = 15;
        try
        {
            var gt = FindFirstObjectByType<GameTick>();
            if (gt != null) tps = Mathf.Clamp(gt.ticksPerSecond, 1, 1000);
        }
        catch { }
        return 1f / Mathf.Max(1, tps);
    }

    bool TryProduceOutputNow()
    {
        if (grid == null) grid = GridService.Instance;
        if (belt == null) belt = BeltSimulationService.Instance;
        if (grid == null || belt == null || carriedItem == null) return false;

        var outCell = cell + OutputVec;

        if (!TryResolveOutputTarget(outCell, out var targetMachine))
            return false;

        // Reattach and tint the preserved view (if any)
        if (carriedView != null)
        {
            var parent = ContainerLocator.GetItemContainer();
            if (parent != null) carriedView.SetParent(parent, true);
            var sr = carriedView.GetComponentInChildren<SpriteRenderer>();
            if (sr != null) sr.color = outputColor;
        }
        carriedItem.view = carriedView;

        if (targetMachine != null)
        {
            bool ok = false;
            try
            {
                if (PowerConsumerUtil.IsMachinePowered(targetMachine))
                    ok = targetMachine.TryStartProcess(carriedItem);
            }
            catch { ok = false; }
            if (!ok)
            {
                carriedItem.view = null;
                return false;
            }

            carriedItem = null;
            carriedView = null;
            return true;
        }

        if (belt.IsVisualNearCell(outCell))
        {
            carriedItem.view = null;
            return false;
        }

        if (!belt.TrySpawnItem(outCell, carriedItem))
        {
            carriedItem.view = null;
            return false;
        }

        // Ensure tint is applied even if the view was positioned by TrySpawnItem
        if (carriedItem.view != null)
        {
            var sr = carriedItem.view.GetComponentInChildren<SpriteRenderer>();
            if (sr != null) sr.color = outputColor;
        }

        carriedItem = null;
        carriedView = null;
        return true;
    }

    bool TryResolveOutputTarget(Vector2Int outCell, out IMachine targetMachine)
    {
        targetMachine = null;
        try
        {
            if (MachineRegistry.TryGet(outCell, out var machine) && machine is IMachineStorageWithCapacity)
            {
                var approachFromVec = cell - outCell;
                bool accepts = false;
                try { accepts = machine.CanAcceptFrom(approachFromVec); } catch { return false; }
                if (!accepts) return false;
                targetMachine = machine;
                return true;
            }
        }
        catch { }

        var cellData = grid.GetCell(outCell);
        if (cellData == null) return false;
        if (cellData.type == GridService.CellType.Machine) return false;
        if (cellData.hasItem) return false;
        return IsBeltLike(cellData);
    }

    static bool IsBeltLike(GridService.Cell c)
        => c != null && !c.isBlueprint && !c.isBroken
           && (c.type == GridService.CellType.Belt || c.type == GridService.CellType.Junction || c.hasConveyor || c.conveyor != null);

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
        catch (Exception ex) { DWarn($"[ColorizerMachine] Registration failed: {ex.Message}"); }
    }

    void EnsureProgressDisplay()
    {
        if (GetComponent<MachineProgressDisplay>() != null) return;
        gameObject.AddComponent<MachineProgressDisplay>();
    }
}
