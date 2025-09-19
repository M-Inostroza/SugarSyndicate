using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;

public class MachineBuilder : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GameObject pressMachinePrefab;

    object grid;
    MethodInfo miWorldToCell;
    MethodInfo miCellToWorld;
    MethodInfo miGetCell;

    bool awaitingWorldClick;
    bool waitRelease; // ignore the click that triggered the button until released
    int activationFrame;

    // Ghost placement state
    bool placing;
    Vector2Int baseCell;
    GameObject ghostGO;
    PressMachine ghostPress;

    void Awake()
    {
        CacheGrid();
    }

    void Update()
    {
        // Enforce global Build state: if not in Build, cancel any pending placement and ignore input
        if (GameManager.Instance == null || GameManager.Instance.State != GameState.Build)
        {
            if (awaitingWorldClick || placing)
            {
                CancelPreview();
                awaitingWorldClick = false;
                placing = false;
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

        // Cancel flow -> end only local preview; DO NOT change GameManager state
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
        {
            CancelPreview();
            awaitingWorldClick = false;
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
                SpawnGhost(baseCell);
                placing = true;
            }
        }
        else
        {
            // Drag to set orientation, release to commit
            if (!TryCellFromWorld(world, out var curCell)) return;
            var dir = curCell - baseCell;
            dir = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y) ? new Vector2Int(Mathf.Clamp(dir.x, -1, 1), 0) : new Vector2Int(0, Mathf.Clamp(dir.y, -1, 1));
            if (dir == Vector2Int.zero) dir = new Vector2Int(1, 0); // default facing right
            UpdateGhost(baseCell, dir);

            if (Input.GetMouseButtonUp(0))
            {
                // Commit the press but STAY in Build mode so user can place another
                Commit(baseCell, dir);
                placing = false; // Clear for next placement
                // Prepare for next placement: require mouse release + new click
                waitRelease = true;
                activationFrame = Time.frameCount;
                awaitingWorldClick = true; // remain in build loop
            }
        }
    }

    // Public API: stop any active press placement session (called when switching tools)
    public void StopBuilding()
    {
        awaitingWorldClick = false;
        placing = false;
        // Clear any ghost preview safely
        CancelPreview();
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
                miGetCell = t.GetMethod("GetCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            }
        }
        catch { }
    }

    // UI Button-friendly helper: drag this component into a Button.onClick and pick BuildPress
    public void BuildPress()
    {
        // End any active conveyor preview WITHOUT changing global state
        TryEndConveyorPreviewWithoutState();

        // Only arm placement if currently in Build mode
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Build)
            return;

        // Wait for the next world click to place
        awaitingWorldClick = true;
        waitRelease = true;
        activationFrame = Time.frameCount;
    }

    // Back-compatible alias
    public void BuildPressAtMouse() => BuildPress();

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
                    // Prefer calling the private EndCurrentPreview() helper we added earlier
                    var miEnd = t.GetMethod("EndCurrentPreview", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (miEnd != null)
                    {
                        miEnd.Invoke(mb, null);
                        break;
                    }

                    // Fallback: try to grab conveyorPlacer field and call EndPreview directly
                    var fiPlacer = t.GetField("conveyorPlacer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var placer = fiPlacer != null ? fiPlacer.GetValue(mb) : null;
                    if (placer != null)
                    {
                        var mi = placer.GetType().GetMethod("EndPreview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mi != null) mi.Invoke(placer, null);
                    }
                    // Also attempt to set current = BuildableType.None so Update() doesn't keep driving the placer
                    var fiCurrent = t.GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fiCurrent != null)
                    {
                        var enumType = fiCurrent.FieldType; // BuildableType
                        object noneVal = null;
                        try { noneVal = System.Enum.Parse(enumType, "None"); } catch { }
                        if (noneVal != null) fiCurrent.SetValue(mb, noneVal);
                    }
                    break;
                }
            }
        }
        catch { }
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
                var fiHasConv = ct.GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fiType = ct.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                bool hasConv = fiHasConv != null && (bool)fiHasConv.GetValue(cellObj);
                string typeName = fiType != null ? fiType.GetValue(cellObj)?.ToString() : string.Empty;
                if (hasConv || typeName == "Machine") return true;
            }
        }
        catch { }
        return false;
    }

    void SpawnGhost(Vector2Int cell)
    {
        if (pressMachinePrefab == null || grid == null || miCellToWorld == null) return;
        var pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
        ghostGO = Instantiate(pressMachinePrefab, pos, Quaternion.identity);
        ghostPress = ghostGO.GetComponent<PressMachine>();
        if (ghostPress != null)
        {
            ghostPress.isGhost = true;
            // tint ghost
            var srs = ghostGO.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs) { var c = sr.color; c.a = 0.6f; sr.color = c; }
        }
    }

    void UpdateGhost(Vector2Int cell, Vector2Int outputDir)
    {
        if (ghostGO == null) return;
        // Rotate ghost to face outputDir (Right=0, Up=90, Left=180, Down=270)
        float z = 0f;
        if (outputDir == new Vector2Int(1, 0)) z = 0f;
        else if (outputDir == new Vector2Int(0, 1)) z = 90f;
        else if (outputDir == new Vector2Int(-1, 0)) z = 180f;
        else if (outputDir == new Vector2Int(0, -1)) z = 270f;
        ghostGO.transform.rotation = Quaternion.Euler(0, 0, z);
        if (ghostPress != null) ghostPress.facingVec = outputDir;
    }

    void Commit(Vector2Int cell, Vector2Int outputDir)
    {
        Debug.Log($"[MachineBuilder] Committing PressMachine at cell {cell} facing {outputDir}");

        // Destroy ghost and place real prefab with same orientation
        Vector3 pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
        if (ghostGO != null) Destroy(ghostGO);

        // Ensure the target cell is not a belt anymore (remove any conveyor and clear logical belt)
        TryRemoveBeltAtCell(cell);

        var go = Instantiate(pressMachinePrefab, pos, Quaternion.Euler(0, 0, DirToZ(outputDir)));
        Debug.Log($"[MachineBuilder] Instantiated PressMachine at position {pos}");

        var press = go.GetComponent<PressMachine>();
        if (press != null)
        {
            press.facingVec = outputDir; // Awake will register
            Debug.Log($"[MachineBuilder] PressMachine facing set to {outputDir}");
        }

        ClearPreviewState();
    }

    void TryRemoveBeltAtCell(Vector2Int cell)
    {
        if (grid == null) { CacheGrid(); if (grid == null) return; }
        try
        {
            var t = grid.GetType();
            var miGetConv = t.GetMethod("GetConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            var miSetConv = t.GetMethod("SetConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(Conveyor) }, null);
            var miClearCell = t.GetMethod("ClearCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);

            Conveyor existing = null;
            if (miGetConv != null)
            {
                try { existing = miGetConv.Invoke(grid, new object[] { cell }) as Conveyor; } catch { existing = null; }
            }
            if (existing != null)
            {
                try { Destroy(existing.gameObject); } catch { }
            }
            if (miSetConv != null)
            {
                try { miSetConv.Invoke(grid, new object[] { cell, null }); } catch { }
            }
            if (miClearCell != null)
            {
                try { miClearCell.Invoke(grid, new object[] { cell }); } catch { }
            }
        }
        catch { }
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
        ghostPress = null;
    }

    static float DirToZ(Vector2Int d)
    {
        if (d == new Vector2Int(1, 0)) return 0f;
        if (d == new Vector2Int(0, 1)) return 90f;
        if (d == new Vector2Int(-1, 0)) return 180f;
        if (d == new Vector2Int(0, -1)) return 270f;
        return 0f;
    }

    static void TrySetGameState(string name)
    {
        // Deprecated: no-op (kept for binary compatibility if referenced elsewhere)
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
