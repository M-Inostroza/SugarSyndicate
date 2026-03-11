using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Simple press machine with processing time and gated input/output.
public class PressMachine : MonoBehaviour, IMachine, IMachineStorage, IMachineProgress, IPowerConsumer, IMachineJammed, IMachineStoppable, IGhostState
{
    public event Action<int> InputBuffered;
    public event Action<float> ProcessingStarted;
    public event Action OutputProduced;

    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] BeltSimulationService belt;
    [SerializeField] PowerService powerService;

    [Header("Orientation")]
    [Tooltip("Output/facing vector. Input is the opposite side. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0); // output side; default Right

    [Header("Product")]
    [Tooltip("Item view prefab to spawn as the press output product.")]
    [SerializeField] GameObject itemPrefab;
    [SerializeField, Min(0)] int poolPrewarm = 8;

    [Header("Item Rules")]
    [Tooltip("Logical item type accepted as input. Leave empty to accept any type.")]
    [SerializeField] string acceptedItemType = "Sugar";
    [Tooltip("Additional logical item types accepted as input. Useful when multiple variants should be pressed.")]
    [SerializeField] string[] acceptedItemTypes;
    [Tooltip("Logical item type produced by this press. Defaults to prefab name when empty.")]
    [SerializeField] string outputItemType = "Cubes";

    [Header("Processing")]
    [Tooltip("How many seconds are required to process one input into an output.")]
    [SerializeField, Min(0f)] float processingSeconds = 1.0f;
    [Tooltip("If true, the press advances processing on GameTick; otherwise it uses frame time (Update).")]
    [SerializeField] bool useGameTickForProcessing = true;

    [Header("Input Buffer")]
    [Tooltip("How many input items are required to start one processing cycle.")]
    [SerializeField, Min(1)] int inputsPerProcess = 3;
    [Tooltip("Max items that can queue inside the press before belts block.")]
    [SerializeField, Min(1)] int maxBufferedInputs = 9;

    [Header("Power")]
    [SerializeField, Min(0f)] float powerUsageWatts = 0f;

    [Header("Maintenance")]
    [SerializeField] MachineMaintenance maintenance = new MachineMaintenance();

    [Header("Debug")]
    [Tooltip("Enable verbose debug logs for PressMachine.")]
    [SerializeField] bool enableDebugLogs = false;
    [SerializeField] int itemOcclusionSortingBoost = 8;

    [NonSerialized] public bool isGhost = false; // builder sets this before enabling
    public bool IsGhost => isGhost;

    public Vector2Int InputVec => new Vector2Int(-facingVec.x, -facingVec.y);
    public Vector2Int OutputVec => facingVec;
    public Vector2Int Cell => cell;
    public Vector2Int InputPortCell => cell + InputVec;
    public Vector2Int OutputSourceCell => cell + OutputVec;
    public Vector2Int OutputPortCell => cell + OutputVec * 2;
    public float Maintenance01 => maintenance != null ? maintenance.Level01 : 1f;
    public bool IsStopped => maintenance != null && maintenance.IsStopped;
    public bool IsJammed => waitingToOutput;
    public int InputsPerProcess => Mathf.Max(1, inputsPerProcess);
    public string OutputItemType => ResolveOutputItemType();
    public string[] AcceptedItemTypes => ResolveAcceptedTypes();
    public int StoredItemCount
    {
        get
        {
            int count = Mathf.Max(0, bufferedInputs);
            if (busy && hasInputThisCycle)
                count += Mathf.Max(1, inputsPerProcess);
            return count;
        }
    }
    public bool IsProcessing => busy && hasInputThisCycle;
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

    class FootprintBlocker : IMachine
    {
        readonly Vector2Int cellPos;

        public FootprintBlocker(Vector2Int cell)
        {
            cellPos = cell;
        }

        public Vector2Int Cell => cellPos;
        public Vector2Int InputVec => Vector2Int.zero;
        public bool CanAcceptFrom(Vector2Int approachFromVec) => false;
        public bool TryStartProcess(Item item) => false;
    }
	
    Vector2Int cell;
    Vector2Int outputFootprintCell;
    Vector2Int sideCellA;
    Vector2Int sideCellB;
    bool hasSideCellB;
    bool registered;
    readonly List<FootprintBlocker> footprintBlockers = new();

    // State
    bool busy;               // true after accepting an input until output successfully spawns
    float remainingTime;     // countdown while processing (seconds)
    bool waitingToOutput;    // processing done; waiting for free output cell to spawn

    // NEW: track if an input was actually accepted for this cycle
    bool hasInputThisCycle;
    int bufferedInputs;

    // --- Debug helpers (no-op unless enableDebugLogs is true) ---
    void DLog(string msg)
    {
        if (enableDebugLogs) Debug.Log(msg);
    }
    void DWarn(string msg)
    {
        if (enableDebugLogs) Debug.LogWarning(msg);
    }

    void Awake()
    {
        DLog($"[PressMachine] Awake called for {gameObject.name} at position {transform.position}");

        if (grid == null) grid = GridService.Instance;
        if (belt == null) belt = BeltSimulationService.Instance;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
    }

    void Start()
    {
        if (!isGhost && itemPrefab != null)
        {
            ItemViewPool.Ensure(itemPrefab, poolPrewarm);
        }

        if (!isGhost)
            EnsureStorageDisplay();
        if (!isGhost)
            EnsureProgressDisplay();

        if (isGhost || grid == null) return;

        TryRegisterAsMachineAndSnap();
        DLog($"[PressMachine] Registered at cell {cell} facing {OutputVec}");
        EnsureRendersAboveItems();

        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        powerService?.RegisterConsumer(this);
    }

    void OnEnable()
    {
        UndergroundVisibilityRegistry.RegisterOverlay(this);
        // Optionally drive processing with GameTick so it lines up with belt steps
        if (useGameTickForProcessing)
        {
            try { GameTick.OnTickStart += OnTick; } catch { }
        }
    }

    void OnDisable()
    {
        UndergroundVisibilityRegistry.UnregisterOverlay(this);
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
            for (int i = 0; i < footprintBlockers.Count; i++)
            {
                if (footprintBlockers[i] == null) continue;
                MachineRegistry.Unregister(footprintBlockers[i]);
            }
            footprintBlockers.Clear();
            if (grid == null) grid = GridService.Instance;
            if (grid != null)
            {
                ClearMachineCell(cell);
                ClearMachineCell(outputFootprintCell);
                ClearMachineCell(sideCellA);
                if (hasSideCellB)
                    ClearMachineCell(sideCellB);
            }
        }
        catch { }
    }

    public static bool TryGetAt(Vector2Int c, out PressMachine press)
    {
        press = null;
        if (MachineRegistry.TryGet(c, out var machine) && machine is PressMachine pm)
        {
            press = pm;
            return true;
        }

        var all = UnityEngine.Object.FindObjectsByType<PressMachine>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            var candidate = all[i];
            if (candidate == null || candidate.isGhost) continue;
            if (!candidate.OccupiesCell(c)) continue;
            press = candidate;
            return true;
        }
        return false;
    }

    public bool OccupiesCell(Vector2Int queryCell)
    {
        return queryCell == cell
            || queryCell == outputFootprintCell
            || queryCell == sideCellA
            || (hasSideCellB && queryCell == sideCellB);
    }

    public bool CanAcceptFrom(Vector2Int approachFromVec)
    {
        DLog($"[PressMachine] CanAcceptFrom called for approach vector {approachFromVec} at cell {cell}");

        if (IsStopped) return false;

        // Must approach from the input side
        if (approachFromVec != InputVec)
        {
            DWarn($"[PressMachine] Rejecting input: approach vector {approachFromVec} does not match InputVec {InputVec}");
            return false;
        }

        if (inputsPerProcess < 1) inputsPerProcess = 1;
        int maxBuffer = GetMaxBufferedInputs();
        if (bufferedInputs >= maxBuffer)
        {
            DWarn($"[PressMachine] Rejecting input: buffer full ({bufferedInputs}/{maxBuffer})");
            return false;
        }
        return true;
    }

    void TryRegisterAsMachineAndSnap()
    {
        try
        {
            if (grid == null) grid = GridService.Instance;
            if (grid == null) { Debug.LogWarning("[PressMachine] GridService not found"); return; }

            facingVec = NormalizeFacing(facingVec);
            cell = ComputeCenterCellFromTransform();

            var sideDir = PerpendicularCounterClockwise(OutputVec);
            outputFootprintCell = cell + OutputVec;
            sideCellA = cell + sideDir;
            sideCellB = default;
            hasSideCellB = false;

            grid.SetMachineCell(cell);
            grid.SetMachineCell(outputFootprintCell);
            grid.SetMachineCell(sideCellA);

            transform.position = GetFootprintCenterWorld(transform.position.z);

            MachineRegistry.Register(this);
            RegisterFootprintBlocker(outputFootprintCell);
            RegisterFootprintBlocker(sideCellA);
            registered = true;
        }
        catch (Exception ex) { DWarn($"[PressMachine] Registration failed: {ex.Message}"); }
    }

    Vector2Int ComputeCenterCellFromTransform()
    {
        return grid.WorldToCell(transform.position);
    }

    static Vector2Int NormalizeFacing(Vector2Int dir)
    {
        if (Mathf.Abs(dir.x) >= Mathf.Abs(dir.y))
            return dir.x < 0 ? Vector2Int.left : Vector2Int.right;
        return dir.y < 0 ? Vector2Int.down : Vector2Int.up;
    }

    static Vector2Int PerpendicularCounterClockwise(Vector2Int dir)
    {
        return new Vector2Int(-dir.y, dir.x);
    }

    Vector3 GetFootprintCenterWorld(float z)
    {
        return grid.CellToWorld(cell, z);
    }

    void RegisterFootprintBlocker(Vector2Int blockerCell)
    {
        var blocker = new FootprintBlocker(blockerCell);
        footprintBlockers.Add(blocker);
        MachineRegistry.Register(blocker);
    }

    void ClearMachineCell(Vector2Int targetCell)
    {
        grid.ClearCell(targetCell);
        var c = grid.GetCell(targetCell);
        if (c != null) c.hasMachine = false;
    }

    void EnsureRendersAboveItems()
    {
        if (itemOcclusionSortingBoost == 0)
            return;

        var groups = GetComponentsInChildren<SortingGroup>(true);
        if (groups != null && groups.Length > 0)
        {
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (group == null) continue;
                group.sortingOrder += itemOcclusionSortingBoost;
            }
            return;
        }

        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < srs.Length; i++)
        {
            var sr = srs[i];
            if (sr == null) continue;
            sr.sortingOrder += itemOcclusionSortingBoost;
        }
    }

    void EnsureStorageDisplay()
    {
        if (GetComponent<MachineStorageDisplay>() != null) return;
        gameObject.AddComponent<MachineStorageDisplay>();
    }

    void EnsureProgressDisplay()
    {
        if (GetComponent<MachineProgressDisplay>() != null) return;
        gameObject.AddComponent<MachineProgressDisplay>();
    }

    // Return true if the item was accepted and processing started
    public bool TryStartProcess(Item item)
    {
        DLog($"[PressMachine] TryStartProcess called for cell {cell}");

        if (IsStopped) return false;
        if (item == null)
        {
            Debug.LogWarning($"[PressMachine] Rejecting input at {cell}: item was null");
            return false;
        }

        var acceptedTypes = ResolveAcceptedTypes();
        if (!MatchesAcceptedTypes(item, acceptedTypes))
        {
            Debug.LogWarning($"[PressMachine] Rejecting input at {cell}: expected {FormatAcceptedTypes(acceptedTypes)}, got '{FormatItemType(item.type)}'");
            return false;
        }

        if (inputsPerProcess < 1) inputsPerProcess = 1;
        int maxBuffer = GetMaxBufferedInputs();
        if (bufferedInputs >= maxBuffer)
        {
            DWarn($"[PressMachine] TryStartProcess rejected: buffer full ({bufferedInputs}/{maxBuffer})");
            return false;
        }

        if (maintenance != null && !maintenance.TryConsume(1)) return false;
        bufferedInputs++;
        DLog($"[PressMachine] Input buffered at {cell}: {bufferedInputs}/{inputsPerProcess}");
        int slotIndex = ((Mathf.Max(1, bufferedInputs) - 1) % Mathf.Max(1, inputsPerProcess)) + 1;
        InputBuffered?.Invoke(slotIndex);
        TryBeginProcessingFromBuffer();
        return true;
    }

    // Back-compat helper for any legacy callers
    public bool OnItemArrived() => TryStartProcess(null);

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
        // Tick-only path: avoid per-frame spawning attempts when tick mode is enabled
        if (useGameTickForProcessing) return;

        // Frame-time processing path
        if (GameManager.Instance == null || GameManager.Instance.State != GameState.Play)
        {
            if (busy) DLog($"[PressMachine] Skipping update - not in Play mode. Current state: {GameManager.Instance?.State}");
            return;
        }

        if (!busy) return;
        if (!HasPower()) return;

        StepProcessing(Time.deltaTime);
    }

    void StepProcessing(float dt)
    {
        DLog($"[PressMachine] StepProcessing dt={dt:0.###} - busy={busy}, waitingToOutput={waitingToOutput}, remainingTime={remainingTime:0.###}, hasInputThisCycle={hasInputThisCycle}");

        if (!waitingToOutput)
        {
            remainingTime -= Mathf.Max(0f, dt);
            if (remainingTime <= 0f)
            {
                waitingToOutput = true; // finished working time; now try to spawn
                DLog($"[PressMachine] Processing complete, now waiting to output at cell {cell}");
            }
        }

        if (waitingToOutput)
        {
            // Only produce output if we actually accepted an input this cycle
            if (hasInputThisCycle && TryProduceOutputNow())
            {
                busy = false;
                waitingToOutput = false;
                remainingTime = 0f;
                hasInputThisCycle = false;
                OutputProduced?.Invoke();
                DLog($"[PressMachine] Cycle complete at {cell}");
                TryBeginProcessingFromBuffer();
            }
        }
    }

    void TryBeginProcessingFromBuffer()
    {
        if (busy) return;
        int required = Mathf.Max(1, inputsPerProcess);
        if (bufferedInputs < required) return;

        bufferedInputs = Mathf.Max(0, bufferedInputs - required);
        busy = true;
        hasInputThisCycle = true;
        waitingToOutput = false;
        remainingTime = Mathf.Max(0f, processingSeconds);
        ProcessingStarted?.Invoke(remainingTime);
        DLog($"[PressMachine] Processing started at {cell}. Processing for {remainingTime:0.###} sec... busy={busy}, buffered={bufferedInputs}");
    }

    int GetMaxBufferedInputs()
    {
        int required = Mathf.Max(1, inputsPerProcess);
        if (maxBufferedInputs < required) maxBufferedInputs = required;
        return maxBufferedInputs;
    }

    public string GetProcessSummary()
    {
        string inputLabel = FormatTypeList(ResolveAcceptedTypes());
        if (string.IsNullOrWhiteSpace(inputLabel)) inputLabel = "Any";
        string outputLabel = ResolveOutputItemType();
        if (string.IsNullOrWhiteSpace(outputLabel)) outputLabel = "Output";
        return $"{InputsPerProcess} {inputLabel} -> 1 {outputLabel}";
    }

    bool HasPower()
    {
        if (powerUsageWatts <= 0f) return true;
        if (!PowerConsumerUtil.IsMachinePowered(this)) return false;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        return powerService != null && powerService.HasPowerFor(this, powerUsageWatts);
    }

    public float GetConsumptionWatts()
    {
        if (isGhost) return 0f;
        return Mathf.Max(0f, powerUsageWatts);
    }

    string[] ResolveAcceptedTypes()
    {
        // Combine legacy single entry with the array for flexible configuration.
        var list = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(acceptedItemType)) list.Add(acceptedItemType.Trim());
        if (acceptedItemTypes != null)
        {
            foreach (var t in acceptedItemTypes)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                list.Add(t.Trim());
            }
        }
        return list.ToArray();
    }

    string ResolveOutputItemType()
    {
        if (!string.IsNullOrWhiteSpace(outputItemType)) return outputItemType.Trim();
        if (itemPrefab != null) return itemPrefab.name;
        return string.Empty;
    }

    static bool MatchesAcceptedTypes(Item item, string[] acceptedTypes)
    {
        // Empty accepted list means accept any type
        if (item == null) return false;
        if (acceptedTypes == null || acceptedTypes.Length == 0) return true;
        return item.IsAnyType(acceptedTypes);
    }

    static string FormatItemType(string raw) => string.IsNullOrWhiteSpace(raw) ? "Unknown" : raw;

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

    float GetTickDeltaSeconds()
    {
        // Compute 1 / ticksPerSecond (fallback to 1/15)
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
        if (itemPrefab == null) 
        { 
            DWarn("[PressMachine] No itemPrefab set; cannot produce."); 
            return false; 
        }

        if (grid == null) grid = GridService.Instance;
        if (belt == null) belt = BeltSimulationService.Instance;
        if (grid == null || belt == null)
        {
            Debug.LogWarning("[PressMachine] Missing grid or belt service; cannot produce output.");
            return false;
        }

        var outputSourceCell = OutputSourceCell;
        var outCell = OutputPortCell;

        if (!TryResolveOutputTarget(outCell, outputSourceCell, out var targetMachine))
        {
            // Keep waitingToOutput=true; try again on a later tick
            return false;
        }
        if (targetMachine == null && belt.IsVisualNearCell(outCell))
        {
            return false;
        }

        var item = new Item { type = ResolveOutputItemType() };

        // Attach view to item using pool if available
        var parent = ContainerLocator.GetItemContainer();
        var world = grid.CellToWorld(outCell, itemPrefab.transform.position.z);
        Transform view = ItemViewPool.Get(itemPrefab, world, Quaternion.identity, parent);
        if (view == null)
        {
            var go = parent != null ? Instantiate(itemPrefab, world, Quaternion.identity, parent) : Instantiate(itemPrefab, world, Quaternion.identity);
            view = go.transform;
        }

        item.view = view;
        if (view != null) view.position = world;

        if (targetMachine != null)
        {
            bool ok = false;
            try
            {
                if (PowerConsumerUtil.IsMachinePowered(targetMachine))
                    ok = targetMachine.TryStartProcess(item);
            }
            catch { ok = false; }
            if (!ok)
            {
                if (view != null) ItemViewPool.Return(view);
                return false;
            }
            return true;
        }

        if (!belt.TrySpawnItem(outCell, item))
        {
            if (view != null) ItemViewPool.Return(view);
            return false;
        }

        belt.TryAdvanceSpawnedItem(outCell);
        return true;
    }

    bool TryResolveOutputTarget(Vector2Int outCell, Vector2Int sourceCell, out IMachine targetMachine)
    {
        targetMachine = null;
        try
        {
            if (MachineRegistry.TryGet(outCell, out var machine) && machine is IMachineStorageWithCapacity)
            {
                var approachFromVec = sourceCell - outCell;
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
}
