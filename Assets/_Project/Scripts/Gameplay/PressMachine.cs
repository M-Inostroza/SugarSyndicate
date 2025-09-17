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

    [NonSerialized] public bool isGhost = false; // builder sets this before enabling

    public Vector2Int InputVec => new Vector2Int(-facingVec.x, -facingVec.y);
    public Vector2Int OutputVec => facingVec;

    Vector2Int cell;

    // State
    bool busy;               // true after accepting an input until output successfully spawns
    float remainingTime;     // countdown while processing
    bool waitingToOutput;    // processing done; waiting for free output cell to spawn

    void Awake()
    {
        if (!isGhost && itemPrefab != null)
        {
            // Try to prewarm pool if present (reflection)
            TryCallStatic("ItemViewPool", "Ensure", new object[] { itemPrefab, poolPrewarm });
        }

        if (isGhost) return; // skip grid registration for ghost previews
        TryRegisterAsMachineAndSnap();
        s_byCell[cell] = this;
        Debug.Log($"[PressMachine] Registered at cell {cell} facing {OutputVec}");
    }

    void OnDestroy()
    {
        try { if (s_byCell.ContainsKey(cell) && s_byCell[cell] == this) s_byCell.Remove(cell); } catch { }
    }

    public static bool TryGetAt(Vector2Int c, out PressMachine press)
        => s_byCell.TryGetValue(c, out press);

    public bool AcceptsFromVec(Vector2Int approachFromVec)
    {
        if (approachFromVec != InputVec) return false;
        if (busy) return false; // do not accept new input while working or waiting to output

        // Optional: only accept if output cell currently looks free and usable
        var grid = FindGridService();
        if (grid == null) return true; // be permissive if grid not found
        var outCellPos = cell + OutputVec;

        try
        {
            var t = grid.GetType();
            var miInBounds = t.GetMethod("InBounds", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            var miGetCell = t.GetMethod("GetCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            if (miInBounds != null && miGetCell != null)
            {
                bool inBounds = (bool)miInBounds.Invoke(grid, new object[] { outCellPos });
                if (!inBounds) return false;
                var cellObj = miGetCell.Invoke(grid, new object[] { outCellPos });
                if (cellObj == null) return false;
                // reflect fields: hasItem, type, hasConveyor, conveyor
                var typeField = cellObj.GetType().GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var hasItemField = cellObj.GetType().GetField("hasItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var hasConveyorField = cellObj.GetType().GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var conveyorField = cellObj.GetType().GetField("conveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                bool hasItem = hasItemField != null && (bool)hasItemField.GetValue(cellObj);
                if (hasItem) return false; // must be empty now

                // Belt-like check to ensure it's an output-capable cell
                bool beltLike = false;
                if (typeField != null)
                {
                    var typeVal = typeField.GetValue(cellObj);
                    var enumType = typeVal.GetType();
                    string name = Enum.GetName(enumType, typeVal);
                    // enum names: Empty, Belt, Junction, Machine
                    beltLike = name == "Belt" || name == "Junction";
                }
                if (!beltLike && hasConveyorField != null)
                {
                    beltLike = (bool)hasConveyorField.GetValue(cellObj);
                }
                if (!beltLike && conveyorField != null)
                {
                    beltLike = conveyorField.GetValue(cellObj) != null;
                }
                return beltLike;
            }
        }
        catch { }
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
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all) { if (mb != null && mb.GetType().Name == "GridService") return mb; }
        }
        catch { }
        return null;
    }

    // Called by the belt sim when an item finishes moving toward this machine from its input side
    public void OnItemArrived()
    {
        if (busy)
        {
            // Shouldn't happen as AcceptsFromVec prevents it, but guard anyway
            return;
        }
        // Start processing
        busy = true;
        waitingToOutput = false;
        remainingTime = Mathf.Max(0f, processingSeconds);
        Debug.Log($"[PressMachine] Input accepted at {cell}. Processing for {remainingTime:0.###} sec...");
    }

    void Update()
    {
        if (!busy) return;

        if (!waitingToOutput)
        {
            remainingTime -= Time.deltaTime;
            if (remainingTime <= 0f)
            {
                waitingToOutput = true; // finished working time; now try to spawn
            }
        }

        if (waitingToOutput)
        {
            // Try to produce the output now; if it fails (blocked), keep trying each frame
            if (TryProduceOutputNow())
            {
                busy = false;
                waitingToOutput = false;
                remainingTime = 0f;
                Debug.Log($"[PressMachine] Cycle complete at {cell}");
            }
        }
    }

    bool TryProduceOutputNow()
    {
        if (itemPrefab == null) { Debug.LogWarning("[PressMachine] No itemPrefab set; cannot produce."); return false; }
        // Resolve GridService and BeltSimulationService via reflection
        var grid = FindGridService(); if (grid == null) { Debug.LogWarning("[PressMachine] GridService not found for output."); return false; }
        var belt = FindSingletonByTypeName("BeltSimulationService"); if (belt == null) { Debug.LogWarning("[PressMachine] BeltSimulationService not found."); return false; }

        var beltType = belt.GetType();
        var trySpawn = beltType.GetMethod("TrySpawnItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (trySpawn == null) { Debug.LogWarning("[PressMachine] TrySpawnItem not found on BeltSimulationService."); return false; }

        // Create Item instance via reflection
        var itemType = FindTypeByName("Item");
        if (itemType == null) { Debug.LogWarning("[PressMachine] Item type not found."); return false; }
        var itemObj = Activator.CreateInstance(itemType);

        // Compute out cell
        var outCell = cell + OutputVec;

        // Call TrySpawnItem(outCell, item)
        bool spawned = false;
        try { spawned = (bool)trySpawn.Invoke(belt, new object[] { outCell, itemObj }); } catch (Exception ex) { Debug.LogWarning($"[PressMachine] TrySpawnItem threw: {ex.Message}"); spawned = false; }
        if (!spawned) { return false; }

        // Attach view to item using pool if available
        Transform view = null;
        var parent = TryCallStatic("ContainerLocator", "GetItemContainer", null) as Transform;
        var gridType = grid.GetType();
        var miCellToWorld = gridType.GetMethod("CellToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
        var world = miCellToWorld != null ? (Vector3)miCellToWorld.Invoke(grid, new object[] { outCell, itemPrefab.transform.position.z }) : transform.position;

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
                try { view = poolGetLegacy.Invoke(null, new object[] { world, Quaternion.identity, parent }) as Transform; } catch { view = null; }
            }
        }
        if (view == null)
        {
            var go = parent != null ? Instantiate(itemPrefab, world, Quaternion.identity, parent) : Instantiate(itemPrefab, world, Quaternion.identity);
            view = go.transform;
        }

        // Assign item.view
        var fiView = itemType.GetField("view", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fiView != null)
        {
            try { fiView.SetValue(itemObj, view); } catch { }
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
