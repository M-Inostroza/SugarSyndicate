using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Simple press machine with processing time and gated input/output.
public class PressMachine : MonoBehaviour
{
    static readonly Dictionary<Vector2Int, PressMachine> s_byCell = new Dictionary<Vector2Int, PressMachine>();

    // Cached refs to avoid reflection per cycle
    static object s_beltInstance;
    static MethodInfo s_trySpawnMI;
    static Type s_itemType;
    static FieldInfo s_itemViewField;
    static object s_gridInstance;
    static MethodInfo s_cellToWorldMI;
    static MethodInfo s_poolGetWithPrefab;
    static MethodInfo s_poolGetLegacy;

    [Header("Orientation")]
    [Tooltip("Output/facing vector. Input is the opposite side. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0); // output side; default Right

    [Header("Product")]
    [Tooltip("Item view prefab to spawn as the press output product.")]
    [SerializeField] GameObject itemPrefab;
    [SerializeField, Min(0)] int poolPrewarm = 8;

    [Header("Processing")]
    [Tooltip("How many seconds are required to process one input into an output.")]
    [SerializeField, Min(0f)] float processingSeconds = 1.0f;
    [Tooltip("If true, the press advances processing on GameTick; otherwise it uses frame time (Update).")]
    [SerializeField] bool useGameTickForProcessing = true;

    [Header("Debug")]
    [Tooltip("Enable verbose debug logs for PressMachine.")]
    [SerializeField] bool enableDebugLogs = false;

    [NonSerialized] public bool isGhost = false; // builder sets this before enabling

    public Vector2Int InputVec => new Vector2Int(-facingVec.x, -facingVec.y);
    public Vector2Int OutputVec => facingVec;

    Vector2Int cell;

    // State
    bool busy;               // true after accepting an input until output successfully spawns
    float remainingTime;     // countdown while processing (seconds)
    bool waitingToOutput;    // processing done; waiting for free output cell to spawn

    // NEW: track if an input was actually accepted for this cycle
    bool hasInputThisCycle;

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

        if (!isGhost && itemPrefab != null)
        {
            TryCallStatic("ItemViewPool", "Ensure", new object[] { itemPrefab, poolPrewarm });
        }

        if (isGhost) return;

        TryRegisterAsMachineAndSnap();

        // Fix: Prevent double registration by checking if already registered
        if (s_byCell.ContainsKey(cell))
        {
            DWarn($"[PressMachine] Cell {cell} already registered. Removing old registration and registering this one.");
            s_byCell.Remove(cell);
        }

        s_byCell[cell] = this;
        DLog($"[PressMachine] Registered at cell {cell} facing {OutputVec}");

        // Resolve and cache heavy reflection once
        WarmupCaches();
    }

    void OnEnable()
    {
        // Optionally drive processing with GameTick so it lines up with belt steps
        if (useGameTickForProcessing)
        {
            try { GameTick.OnTickStart += OnTick; } catch { }
        }
    }

    void OnDisable()
    {
        if (useGameTickForProcessing)
        {
            try { GameTick.OnTickStart -= OnTick; } catch { }
        }
    }

    void OnDestroy()
    {
        try { if (s_byCell.ContainsKey(cell) && s_byCell[cell] == this) s_byCell.Remove(cell); } catch { }
        // Clear grid logical cell if this press is being deleted
        try
        {
            var grid = FindGridService();
            if (grid != null)
            {
                var t = grid.GetType();
                var miClear = t.GetMethod("ClearCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
                if (miClear != null) miClear.Invoke(grid, new object[] { cell });
                var miGetCell = t.GetMethod("GetCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
                if (miGetCell != null)
                {
                    var cellObj = miGetCell.Invoke(grid, new object[] { cell });
                    if (cellObj != null)
                    {
                        var fiHasMachine = cellObj.GetType().GetField("hasMachine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fiHasMachine != null) fiHasMachine.SetValue(cellObj, false);
                    }
                }
            }
        }
        catch { }
    }

    public static bool TryGetAt(Vector2Int c, out PressMachine press)
        => s_byCell.TryGetValue(c, out press);

    public bool AcceptsFromVec(Vector2Int approachFromVec)
    {
        DLog($"[PressMachine] AcceptsFromVec called for approach vector {approachFromVec} at cell {cell}");

        // Must approach from the input side
        if (approachFromVec != InputVec)
        {
            DWarn($"[PressMachine] Rejecting input: approach vector {approachFromVec} does not match InputVec {InputVec}");
            return false;
        }

        // Do not accept if already processing or waiting to output
        if (busy)
        {
            DWarn($"[PressMachine] Rejecting input: machine is busy");
            return false;
        }
        return true;
    }

    void TryRegisterAsMachineAndSnap()
    {
        try
        {
            var grid = FindGridService();
            if (grid == null) { DWarn("[PressMachine] GridService not found"); return; }
            var t = grid.GetType();
            var miWorldToCell = t.GetMethod("WorldToCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null);
            var miCellToWorld = t.GetMethod("CellToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
            var miSetMachine = t.GetMethod("SetMachineCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            if (miWorldToCell == null || miCellToWorld == null || miSetMachine == null) { DWarn("[PressMachine] GridService methods not found"); return; }

            var v = (Vector2Int)miWorldToCell.Invoke(grid, new object[] { transform.position });
            cell = v;
            // register as Machine
            miSetMachine.Invoke(grid, new object[] { v });
            // snap to center
            var world = (Vector3)miCellToWorld.Invoke(grid, new object[] { v, transform.position.z });
            transform.position = world;
        }
        catch (Exception ex) { DWarn($"[PressMachine] Registration failed: {ex.Message}"); }
    }

    object FindGridService()
    {
        // 1) Prefer GridService.Instance to match BeltSimulationService
        try
        {
            var type = FindTypeByName("GridService");
            if (type != null)
            {
                var prop = type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    var inst = prop.GetValue(null, null) as MonoBehaviour;
                    if (inst != null) return inst;
                }
            }
        }
        catch { }

        // 2) Fallback: search scene objects
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all) { if (mb != null && mb.GetType().Name == "GridService") return mb; }
        }
        catch { }
        return null;
    }

    void WarmupCaches()
    {
        try
        {
            // Grid and helpers
            if (s_gridInstance == null)
            {
                s_gridInstance = FindGridService();
                if (s_gridInstance != null)
                {
                    var gt = s_gridInstance.GetType();
                    s_cellToWorldMI = s_cellToWorldMI ?? gt.GetMethod("CellToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
                }
            }

            // Belt singleton and TrySpawnItem
            if (s_beltInstance == null)
            {
                s_beltInstance = FindSingletonByTypeName("BeltSimulationService");
                if (s_beltInstance != null)
                {
                    var bt = s_beltInstance.GetType();
                    s_trySpawnMI = s_trySpawnMI ?? bt.GetMethod("TrySpawnItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }

            // Item type + view field
            if (s_itemType == null)
            {
                s_itemType = FindTypeByName("BeltSimulationService+Item") ?? FindTypeByName("GridService+Item") ?? FindTypeByName("Item");
                if (s_itemType != null)
                {
                    s_itemViewField = s_itemViewField ?? s_itemType.GetField("view", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }

            // ItemViewPool Get methods
            if (s_poolGetWithPrefab == null || s_poolGetLegacy == null)
            {
                var poolType = FindTypeByName("ItemViewPool");
                if (poolType != null)
                {
                    if (s_poolGetWithPrefab == null)
                        s_poolGetWithPrefab = poolType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(Transform) }, null);
                    if (s_poolGetLegacy == null)
                        s_poolGetLegacy = poolType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(Vector3), typeof(Quaternion), typeof(Transform) }, null);
                }
            }
        }
        catch { }
    }

    // Return true if the item was accepted and processing started
    public bool OnItemArrived()
    {
        DLog($"[PressMachine] OnItemArrived called for cell {cell}");

        if (busy)
        {
            DWarn($"[PressMachine] OnItemArrived ignored because machine is busy.");
            return false;
        }

        busy = true;
        hasInputThisCycle = true;
        waitingToOutput = false;
        remainingTime = Mathf.Max(0f, processingSeconds);
        DLog($"[PressMachine] Input accepted at {cell}. Processing for {remainingTime:0.###} sec... busy={busy}, hasInputThisCycle={hasInputThisCycle}");
        return true;
    }

    void OnTick()
    {
        if (!useGameTickForProcessing) return;
        if (!busy) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
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
                DLog($"[PressMachine] Cycle complete at {cell}");
            }
        }
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

        // Ensure caches are warm
        WarmupCaches();

        if (s_gridInstance == null || s_beltInstance == null || s_trySpawnMI == null || s_itemType == null)
        {
            DWarn("[PressMachine] Missing cached refs (grid/belt/item).");
            return false;
        }

        // Create Item instance
        object itemObj = null;
        try { itemObj = Activator.CreateInstance(s_itemType); } catch { itemObj = null; }
        if (itemObj == null) { DWarn("[PressMachine] Failed to allocate item instance."); return false; }

        // Compute out cell
        var outCell = cell + OutputVec;

        // Call TrySpawnItem(outCell, item) via cached MI
        bool spawned = false;
        try { spawned = (bool)s_trySpawnMI.Invoke(s_beltInstance, new object[] { outCell, itemObj }); }
        catch (Exception ex) { DWarn($"[PressMachine] TrySpawnItem threw: {ex.Message}"); spawned = false; }

        if (!spawned)
        {
            // Keep waitingToOutput=true; try again on a later tick
            return false;
        }

        // Attach view to item using pool if available
        Transform view = null;
        var parent = TryCallStatic("ContainerLocator", "GetItemContainer", null) as Transform;
        Vector3 world = transform.position;
        try
        {
            if (s_cellToWorldMI != null)
                world = (Vector3)s_cellToWorldMI.Invoke(s_gridInstance, new object[] { outCell, itemPrefab.transform.position.z });
        }
        catch { }

        try
        {
            if (s_poolGetWithPrefab != null)
                view = s_poolGetWithPrefab.Invoke(null, new object[] { itemPrefab, world, Quaternion.identity, parent }) as Transform;
            if (view == null && s_poolGetLegacy != null)
                view = s_poolGetLegacy.Invoke(null, new object[] { world, Quaternion.identity, parent }) as Transform;
        }
        catch { }

        if (view == null)
        {
            var go = parent != null ? Instantiate(itemPrefab, world, Quaternion.identity, parent) : Instantiate(itemPrefab, world, Quaternion.identity);
            view = go.transform;
        }

        if (s_itemViewField != null)
        {
            try { s_itemViewField.SetValue(itemObj, view); } catch { }
        }
        if (view != null) view.position = world;
        return true;
    }

    static Type FindTypeByName(string name)
    {
        // Prefer the main game assembly to avoid conflicts with package types
        try
        {
            var asm = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a != null && a.GetName().Name == "Assembly-CSharp");
            if (asm != null)
            {
                try
                {
                    var tExact = Array.Find(asm.GetTypes(), tt => tt != null && tt.Name == name);
                    if (tExact != null) return tExact;
                }
                catch { }
            }
        }
        catch { }

        // Fallback: first match across all assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            try { t = Array.Find(asm.GetTypes(), tt => tt.Name == name); } catch { }
            if (t != null) return t;
        }
        return null;
    }

    static object FindSingletonByTypeName(string name)
    {
        var type = FindTypeByName(name);
        if (type == null) return null;
        var prop = type.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null)
        {
            try { return prop.GetValue(null, null); } catch { }
        }
        // Fallback: find any object of that type in scene
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all) { if (mb != null && mb.GetType().Name == name) return mb; }
        }
        catch { }
        return null;
    }

    static MethodInfo FindStaticMethod(string typeName, string methodName, Type[] sig)
    {
        var type = FindTypeByName(typeName); if (type == null) return null;
        try { return type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, sig ?? Type.EmptyTypes, null); } catch { return null; }
    }

    static object TryCallStatic(string typeName, string methodName, object[] args)
    {
        var method = FindStaticMethod(typeName, methodName, args == null ? Type.EmptyTypes : Array.ConvertAll(args, a => a?.GetType() ?? typeof(object)));
        if (method == null) return null;
        try { return method.Invoke(null, args); } catch { return null; }
    }
}
