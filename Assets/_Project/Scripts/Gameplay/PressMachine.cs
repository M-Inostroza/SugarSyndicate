using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

// Simple press machine with processing time and gated input/output.
public class PressMachine : MonoBehaviour
{
    static readonly Dictionary<Vector2Int, PressMachine> s_byCell = new Dictionary<Vector2Int, PressMachine>();

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

    void Awake()
    {
        Debug.Log($"[PressMachine] Awake called for {gameObject.name} at position {transform.position}");

        if (!isGhost && itemPrefab != null)
        {
            TryCallStatic("ItemViewPool", "Ensure", new object[] { itemPrefab, poolPrewarm });
        }

        if (isGhost) return;

        TryRegisterAsMachineAndSnap();

        // Fix: Prevent double registration by checking if already registered
        if (s_byCell.ContainsKey(cell))
        {
            Debug.LogWarning($"[PressMachine] Cell {cell} already registered. Removing old registration and registering this one.");
            s_byCell.Remove(cell);
        }

        s_byCell[cell] = this;
        Debug.Log($"[PressMachine] Registered at cell {cell} facing {OutputVec}");
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
        Debug.Log($"[PressMachine] AcceptsFromVec called for approach vector {approachFromVec} at cell {cell}");

        // Must approach from the input side
        if (approachFromVec != InputVec)
        {
            Debug.LogWarning($"[PressMachine] Rejecting input: approach vector {approachFromVec} does not match InputVec {InputVec}");
            return false;
        }

        // Do not accept if already processing or waiting to output
        if (busy)
        {
            Debug.LogWarning($"[PressMachine] Rejecting input: machine is busy");
            return false;
        }
        return true;
    }

    void TryRegisterAsMachineAndSnap()
    {
        try
        {
            var grid = FindGridService();
            if (grid == null) { Debug.LogWarning("[PressMachine] GridService not found"); return; }
            var t = grid.GetType();
            var miWorldToCell = t.GetMethod("WorldToCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null);
            var miCellToWorld = t.GetMethod("CellToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
            var miSetMachine = t.GetMethod("SetMachineCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            if (miWorldToCell == null || miCellToWorld == null || miSetMachine == null) { Debug.LogWarning("[PressMachine] GridService methods not found"); return; }

            var v = (Vector2Int)miWorldToCell.Invoke(grid, new object[] { transform.position });
            cell = v;
            // register as Machine
            miSetMachine.Invoke(grid, new object[] { v });
            // snap to center
            var world = (Vector3)miCellToWorld.Invoke(grid, new object[] { v, transform.position.z });
            transform.position = world;
        }
        catch (Exception ex) { Debug.LogWarning($"[PressMachine] Registration failed: {ex.Message}"); }
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

    // Return true if the item was accepted and processing started
    public bool OnItemArrived()
    {
        Debug.Log($"[PressMachine] OnItemArrived called for cell {cell}");

        if (busy)
        {
            Debug.LogWarning($"[PressMachine] OnItemArrived ignored because machine is busy.");
            return false;
        }

        busy = true;
        hasInputThisCycle = true;
        waitingToOutput = false;
        remainingTime = Mathf.Max(0f, processingSeconds);
        Debug.Log($"[PressMachine] Input accepted at {cell}. Processing for {remainingTime:0.###} sec... busy={busy}, hasInputThisCycle={hasInputThisCycle}");
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
        // If we drive processing from GameTick, only try to spawn when waiting to output
        if (useGameTickForProcessing)
        {
            if (!busy) return;
            if (GameManager.Instance == null || GameManager.Instance.State != GameState.Play) return;
            if (waitingToOutput)
            {
                // Only produce output if we actually accepted an input this cycle
                if (hasInputThisCycle && TryProduceOutputNow())
                {
                    busy = false;
                    waitingToOutput = false;
                    remainingTime = 0f;
                    hasInputThisCycle = false;
                    Debug.Log($"[PressMachine] Cycle complete at {cell}");
                }
            }
            return;
        }

        // Frame-time processing path
        if (GameManager.Instance == null || GameManager.Instance.State != GameState.Play)
        {
            if (busy) Debug.Log($"[PressMachine] Skipping update - not in Play mode. Current state: {GameManager.Instance?.State}");
            return;
        }

        if (!busy) return;

        StepProcessing(Time.deltaTime);
    }

    void StepProcessing(float dt)
    {
        Debug.Log($"[PressMachine] StepProcessing dt={dt:0.###} - busy={busy}, waitingToOutput={waitingToOutput}, remainingTime={remainingTime:0.###}, hasInputThisCycle={hasInputThisCycle}");

        if (!waitingToOutput)
        {
            remainingTime -= Mathf.Max(0f, dt);
            if (remainingTime <= 0f)
            {
                waitingToOutput = true; // finished working time; now try to spawn
                Debug.Log($"[PressMachine] Processing complete, now waiting to output at cell {cell}");
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
                Debug.Log($"[PressMachine] Cycle complete at {cell}");
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
            Debug.LogWarning("[PressMachine] No itemPrefab set; cannot produce."); 
            return false; 
        }

        // Resolve GridService and BeltSimulationService via reflection
        var grid = FindGridService(); 
        if (grid == null) 
        { 
            Debug.LogWarning("[PressMachine] GridService not found for output."); 
            return false; 
        }

        var belt = FindSingletonByTypeName("BeltSimulationService"); 
        if (belt == null) 
        { 
            Debug.LogWarning("[PressMachine] BeltSimulationService not found."); 
            return false; 
        }

        var beltType = belt.GetType();
        var trySpawn = beltType.GetMethod("TrySpawnItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (trySpawn == null) 
        { 
            Debug.LogWarning("[PressMachine] TrySpawnItem not found on BeltSimulationService."); 
            return false; 
        }

        // Create Item instance via reflection
        var itemType = FindTypeByName("BeltSimulationService+Item") ?? FindTypeByName("GridService+Item") ?? FindTypeByName("Item");
        if (itemType == null) 
        { 
            Debug.LogWarning("[PressMachine] Item type not found."); 
            return false; 
        }

        var itemObj = Activator.CreateInstance(itemType);
        Debug.Log($"[PressMachine] Created item of type {itemType.Name}.");

        // Compute out cell
        var outCell = cell + OutputVec;
        Debug.Log($"[PressMachine] Output cell calculated as {outCell}.");

        // Call TrySpawnItem(outCell, item)
        bool spawned = false;
        try 
        { 
            spawned = (bool)trySpawn.Invoke(belt, new object[] { outCell, itemObj }); 
            Debug.Log($"[PressMachine] TrySpawnItem result: {spawned} for cell {outCell}."); 
        } 
        catch (Exception ex) 
        { 
            Debug.LogWarning($"[PressMachine] TrySpawnItem threw: {ex.Message}"); 
            spawned = false; 
        }

        if (!spawned) 
        { 
            Debug.LogWarning($"[PressMachine] Failed to spawn item at {outCell}."); 
            return false; 
        }

        // Attach view to item using pool if available
        Transform view = null;
        var parent = TryCallStatic("ContainerLocator", "GetItemContainer", null) as Transform;
        var gridType = grid.GetType();
        var miCellToWorld = gridType.GetMethod("CellToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
        var world = miCellToWorld != null ? (Vector3)miCellToWorld.Invoke(grid, new object[] { outCell, itemPrefab.transform.position.z }) : transform.position;

        Debug.Log($"[PressMachine] World position for output cell: {world}.");

        // Prefer new overload ItemViewPool.Get(GameObject, Vector3, Quaternion, Transform)
        Transform TryGetFromPoolWithPrefab()
        {
            var poolType = FindTypeByName("ItemViewPool");
            if (poolType == null) return null;
            try
            {
                var mi = poolType.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new Type[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion), typeof(Transform) }, null);
                if (mi != null)
                {
                    var t = mi.Invoke(null, new object[] { itemPrefab, world, Quaternion.identity, parent }) as Transform;
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        view = TryGetFromPoolWithPrefab();
        if (view == null)
        {
            // Fallback to legacy Get(Vector3, Quaternion, Transform)
            var poolGetLegacy = FindStaticMethod("ItemViewPool", "Get", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(Transform) });
            if (poolGetLegacy != null)
            {
                try 
                { 
                    view = poolGetLegacy.Invoke(null, new object[] { world, Quaternion.identity, parent }) as Transform; 
                } 
                catch 
                { 
                    view = null; 
                }
            }
        }
        if (view == null)
        {
            var go = parent != null ? Instantiate(itemPrefab, world, Quaternion.identity, parent) : Instantiate(itemPrefab, world, Quaternion.identity);
            view = go.transform;
        }

        Debug.Log($"[PressMachine] View attached to item at position {view?.position}.");

        // Assign item.view
        var fiView = itemType.GetField("view", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fiView != null)
        {
            try 
            { 
                fiView.SetValue(itemObj, view); 
                Debug.Log($"[PressMachine] View assigned to item."); 
            } 
            catch 
            { 
                Debug.LogWarning($"[PressMachine] Failed to assign view to item."); 
            }
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
