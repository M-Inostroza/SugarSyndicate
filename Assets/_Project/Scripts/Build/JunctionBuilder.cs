using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

// Builder for grid junction machines: Splitter (1 input -> 2 outputs) and Merger (2 inputs -> 1 output).
// Usage: Hook BuildSplitterJunction() and BuildMergerJunction() to UI Buttons.
// Optional: assign prefabs for visuals; otherwise this only updates the logical grid.
public class JunctionBuilder : MonoBehaviour
{
    [Header("Optional visuals")]
    [Tooltip("Prefab to spawn for a Splitter junction (1 input, 2 outputs)")]
    [SerializeField] GameObject splitterPrefab;
    [Tooltip("Prefab to spawn for a Merger junction (2 inputs, 1 output)")]
    [SerializeField] GameObject mergerPrefab;

    [Header("Ghost visuals")]
    [SerializeField] [Range(0f, 1f)] float ghostAlpha = 0.6f;

    enum Mode { None, Splitter, Merger }
    Mode mode = Mode.None;

    // Cached GridService reflection
    object grid;
    MethodInfo miWorldToCell;
    MethodInfo miCellToWorld;
    MethodInfo miSetJunctionCell; // void SetJunctionCell(Vector2Int, Direction, Direction, Direction, Direction)
    MethodInfo miGetCell;

    // Direction enum type (resolve at runtime to avoid compile-time dependency/ambiguity)
    Type directionType;

    // Flow state
    bool awaitingWorldClick;
    bool waitRelease; // ignore the click that triggered the button until released
    int activationFrame;

    // Ghost placement state
    bool placing;
    Vector2Int baseCell;
    GameObject ghostGO;
    Vector2Int facingVec = new Vector2Int(1, 0); // orientation chosen by drag

    void Awake()
    {
        CacheGrid();
    }

    void CacheGrid()
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                if (mb.GetType().Name == "GridService") { grid = mb; break; }
            }
            if (grid != null)
            {
                var t = grid.GetType();
                miWorldToCell = t.GetMethod("WorldToCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null);
                miCellToWorld = t.GetMethod("CellToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
                // Find SetJunctionCell by name & arity to avoid compile-time Direction dependency
                miSetJunctionCell = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "SetJunctionCell"
                        && m.GetParameters().Length == 5
                        && m.GetParameters()[0].ParameterType == typeof(Vector2Int));
                miGetCell = t.GetMethod("GetCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            }
            directionType = FindDirectionType();
        }
        catch { }
    }

    public void BuildSplitterJunction()
    {
        StartBuild(Mode.Splitter);
    }

    public void BuildMergerJunction()
    {
        StartBuild(Mode.Merger);
    }

    void StartBuild(Mode m)
    {
        // Only arm placement in Build mode
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Build)
            return;

        // Stop any active conveyor building to avoid overlap with drag handlers, without changing global state
        TryEndConveyorPreviewWithoutState();
        // Also stop any active press placement to ensure single-tool behavior
        TryStopPressBuilder();

        mode = m;
        awaitingWorldClick = true;
        waitRelease = true;
        activationFrame = Time.frameCount;
        ClearPreviewState();
    }

    void Update()
    {
        // Enforce global Build mode
        if (GameManager.Instance == null || GameManager.Instance.State != GameState.Build)
        {
            if (awaitingWorldClick || placing)
            {
                CancelPreview();
                awaitingWorldClick = false;
                placing = false;
                mode = Mode.None;
            }
            return;
        }

        if (!awaitingWorldClick) return;

        // Ensure we don't accept the same click that triggered the button: wait for release and a new frame
        if (waitRelease)
        {
            if (Input.GetMouseButton(0)) return;
            if (Time.frameCount == activationFrame) return;
            waitRelease = false;
            return;
        }

        // Cancel flow: only end local preview; do not touch global state
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
        {
            CancelPreview();
            awaitingWorldClick = false;
            mode = Mode.None;
            return;
        }

        var cam = Camera.main; if (cam == null) return;
        var world = GetMouseWorldOnPlane(cam);

        if (!placing)
        {
            // First world click: choose base cell and spawn ghost
            if (Input.GetMouseButtonDown(0))
            {
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

                if (!TryCellFromWorld(world, out baseCell)) return;
                if (IsBlocked(baseCell)) return;
                facingVec = new Vector2Int(1, 0);
                SpawnGhost(baseCell);
                placing = true;
            }
        }
        else
        {
            // Drag to set orientation, release to commit
            if (!TryCellFromWorld(world, out var curCell)) return;
            var dir = curCell - baseCell;
            dir = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y) ? new Vector2Int(Math.Sign(dir.x), 0) : new Vector2Int(0, Math.Sign(dir.y));
            if (dir == Vector2Int.zero) dir = new Vector2Int(1, 0); // default facing right
            facingVec = dir;
            UpdateGhost(baseCell, dir);

            if (Input.GetMouseButtonUp(0))
            {
                Commit(baseCell, dir);
                placing = false;
                awaitingWorldClick = false;
                mode = Mode.None;
            }
        }
    }

    // Public API: stop any active junction placement session (called when switching tools)
    public void StopBuilding()
    {
        awaitingWorldClick = false;
        CancelPreview();
        mode = Mode.None;
    }

    bool TryCellFromWorld(Vector3 world, out Vector2Int cell)
    {
        cell = default;
        if (grid == null || miWorldToCell == null) { CacheGrid(); if (grid == null || miWorldToCell == null) return false; }
        cell = (Vector2Int)miWorldToCell.Invoke(grid, new object[] { world });
        return true;
    }

    bool IsBlocked(Vector2Int cell)
    {
        try
        {
            var cellObj = miGetCell != null ? miGetCell.Invoke(grid, new object[] { cell }) : null;
            if (cellObj != null)
            {
                var ct = cellObj.GetType();
                var fiType = ct.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string typeName = fiType != null ? fiType.GetValue(cellObj)?.ToString() : string.Empty;
                // Block only if existing Machine occupies the cell; belts/junctions can be overwritten via delete tools
                if (typeName == "Machine") return true;
            }
        }
        catch { }
        return false;
    }

    void SpawnGhost(Vector2Int cell)
    {
        if (grid == null || miCellToWorld == null) return;
        var pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
        GameObject prefab = mode == Mode.Splitter ? splitterPrefab : mergerPrefab;
        if (prefab != null)
        {
            ghostGO = Instantiate(prefab, pos, Quaternion.identity);
            // tint ghost
            var srs = ghostGO.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs) { var c = sr.color; c.a *= ghostAlpha; sr.color = c; }
        }
        else
        {
            // No prefab assigned: create a simple placeholder so player sees something while dragging
            ghostGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ghostGO.name = mode == Mode.Splitter ? "SplitterGhost" : "MergerGhost";
            ghostGO.transform.position = pos;
            var mr = ghostGO.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                try { mr.material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); } catch { }
                var col = new Color(1f, 1f, 1f, ghostAlpha);
                if (mr.material != null && mr.material.HasProperty("_BaseColor")) mr.material.SetColor("_BaseColor", col);
                else if (mr.material != null && mr.material.HasProperty("_Color")) mr.material.SetColor("_Color", col);
            }
        }
        UpdateGhost(cell, facingVec);
    }

    void UpdateGhost(Vector2Int cell, Vector2Int forward)
    {
        if (ghostGO == null || grid == null || miCellToWorld == null) return;
        // Snap to center and orient to forward
        var pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
        ghostGO.transform.position = pos;
        ghostGO.transform.rotation = Quaternion.Euler(0, 0, DirToZ(forward));
    }

    void Commit(Vector2Int cell, Vector2Int forward)
    {
        // Destroy ghost
        if (ghostGO != null) Destroy(ghostGO);

        // Compute junction IOs based on mode and forward orientation (Right=0,Up=90,Left=180,Down=270)
        var outForward = forward;
        var right = new Vector2Int(forward.y, -forward.x); // rotate cw
        var left = new Vector2Int(-forward.y, forward.x);  // rotate ccw
        var back = new Vector2Int(-forward.x, -forward.y);

        object V2DirObj(Vector2Int v)
        {
            // Prefer parsing by enum name if available
            EnsureDirectionType();
            if (directionType == null) return null;
            string name = VecToDirName(v);
            try { return Enum.Parse(directionType, name); } catch { }
            // Fallback by underlying int mapping like DirectionUtil (Up=0, Right=1, Down=2, Left=3, None=4)
            int idx = name == "Up" ? 0 : name == "Right" ? 1 : name == "Down" ? 2 : name == "Left" ? 3 : 4;
            try { return Enum.ToObject(directionType, idx); } catch { return null; }
        }

        object inA = null, inB = null, outA = null, outB = null;
        if (mode == Mode.Splitter)
        {
            // 1 input (from back) -> 2 outputs (left & right)
            inA = V2DirObj(back);
            outA = V2DirObj(left);
            outB = V2DirObj(right);
        }
        else if (mode == Mode.Merger)
        {
            // 2 inputs (left & right) -> 1 output (forward)
            inA = V2DirObj(left);
            inB = V2DirObj(right);
            outA = V2DirObj(outForward);
            outB = V2DirObj(new Vector2Int(0, 0)); // None
        }

        // Set logical grid cell
        try
        {
            if (grid == null || miSetJunctionCell == null || miCellToWorld == null)
            {
                CacheGrid();
            }
            if (miSetJunctionCell != null)
            {
                // Ensure non-null values: use None where missing
                var noneObj = V2DirObj(new Vector2Int(0, 0));
                inA = inA ?? noneObj; inB = inB ?? noneObj; outA = outA ?? noneObj; outB = outB ?? noneObj;
                miSetJunctionCell.Invoke(grid, new object[] { cell, inA, inB, outA, outB });
            }
        }
        catch { }

        // Register with belt simulation and mark graph dirty
        TryRegisterCellInBeltSim(cell);
        MarkGraphDirtyIfPresent();

        // Spawn real prefab for visuals if provided
        GameObject prefab = mode == Mode.Splitter ? splitterPrefab : mergerPrefab;
        if (prefab != null && grid != null && miCellToWorld != null)
        {
            Vector3 pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
            var parent = TryCallStatic("ContainerLocator", "GetBeltContainer", null) as Transform;
            var go = parent != null ? Instantiate(prefab, pos, Quaternion.Euler(0, 0, DirToZ(forward)), parent)
                                    : Instantiate(prefab, pos, Quaternion.Euler(0, 0, DirToZ(forward)));
            go.name = mode == Mode.Splitter ? "SplitterJunction" : "MergerJunction";
        }

        // Prevent immediate pulls next step to let graph settle
        TrySuppressNextStepPulls();

        ClearPreviewState();
    }

    void CancelPreview()
    {
        if (ghostGO != null) Destroy(ghostGO);
        ClearPreviewState();
    }

    void ClearPreviewState()
    {
        placing = false;
        baseCell = default;
        ghostGO = null;
        facingVec = new Vector2Int(1, 0);
    }

    // End conveyor tool without altering global state (replaces old TryCancelConveyorBuildMode)
    void TryEndConveyorPreviewWithoutState()
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "BuildModeController")
                {
                    // Prefer the private EndCurrentPreview helper
                    var miEnd = t.GetMethod("EndCurrentPreview", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (miEnd != null)
                    {
                        miEnd.Invoke(mb, null);
                        break;
                    }
                    // Fallback: end placer preview and clear current
                    var fiPlacer = t.GetField("conveyorPlacer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var placer = fiPlacer != null ? fiPlacer.GetValue(mb) : null;
                    if (placer != null)
                    {
                        var mi = placer.GetType().GetMethod("EndPreview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mi != null) mi.Invoke(placer, null);
                    }
                    var fiCurrent = t.GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fiCurrent != null)
                    {
                        var enumType = fiCurrent.FieldType; // BuildableType
                        object noneVal = null;
                        try { noneVal = Enum.Parse(enumType, "None"); } catch { }
                        if (noneVal != null) fiCurrent.SetValue(mb, noneVal);
                    }
                    break;
                }
            }
        }
        catch { }
    }

    void TryStopPressBuilder()
    {
        try
        {
            var pressBuilder = FindAnyObjectByType<MachineBuilder>();
            if (pressBuilder != null)
            {
                var mi = typeof(MachineBuilder).GetMethod("StopBuilding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(pressBuilder, null);
            }
        }
        catch { }
    }

    void TryRegisterCellInBeltSim(Vector2Int cell)
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "BeltSimulationService")
                {
                    var mi = t.GetMethod("RegisterCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null) mi.Invoke(mb, new object[] { cell });
                    break;
                }
            }
        }
        catch { }
    }

    void TrySuppressNextStepPulls()
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "BeltSimulationService")
                {
                    var mi = t.GetMethod("SuppressNextStepPulls", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null) mi.Invoke(mb, null);
                    break;
                }
            }
        }
        catch { }
    }

    void MarkGraphDirtyIfPresent()
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "BeltGraphService")
                {
                    var mi = t.GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null) mi.Invoke(mb, null);
                    break;
                }
            }
        }
        catch { }
    }

    void EnsureDirectionType()
    {
        if (directionType != null) return;
        directionType = FindDirectionType();
    }

    static Type FindDirectionType()
    {
        // Prefer the main game assembly to avoid conflicts with package enums of the same name
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a != null && a.GetName().Name == "Assembly-CSharp");
            if (asm != null)
            {
                try
                {
                    var tExact = asm.GetTypes().FirstOrDefault(tt => tt != null && tt.IsEnum && tt.Name == "Direction");
                    if (tExact != null) return tExact;
                }
                catch { }
            }
        }
        catch { }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetTypes().FirstOrDefault(tt => tt != null && tt.IsEnum && tt.Name == "Direction");
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    static string VecToDirName(Vector2Int v)
    {
        if (v == new Vector2Int(1, 0)) return "Right";
        if (v == new Vector2Int(-1, 0)) return "Left";
        if (v == new Vector2Int(0, 1)) return "Up";
        if (v == new Vector2Int(0, -1)) return "Down";
        return "None";
    }

    static float DirToZ(Vector2Int d)
    {
        if (d == new Vector2Int(1, 0)) return 0f;
        if (d == new Vector2Int(0, 1)) return 90f;
        if (d == new Vector2Int(-1, 0)) return 180f;
        if (d == new Vector2Int(0, -1)) return 270f;
        return 0f;
    }

    static object TryCallStatic(string typeName, string methodName, object[] args)
    {
        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a != null && a.GetName().Name == "Assembly-CSharp");
            Type type = null;
            if (asm != null)
            {
                try { type = asm.GetTypes().FirstOrDefault(tt => tt.Name == typeName); } catch { type = null; }
            }
            if (type == null)
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { type = a.GetTypes().FirstOrDefault(tt => tt.Name == typeName); } catch { }
                    if (type != null) break;
                }
            }
            if (type == null) return null;
            var mi = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi == null) return null;
            return mi.Invoke(null, args);
        }
        catch { return null; }
    }

    static Vector3 GetMouseWorldOnPlane(Camera cam)
    {
        var mp = Input.mousePosition;
        float planeZ = 0f;
        float camZ = cam.transform.position.z;
        mp.z = planeZ - camZ;
        var world = cam.ScreenToWorldPoint(mp);
        world.z = 0f;
        return world;
    }
}
