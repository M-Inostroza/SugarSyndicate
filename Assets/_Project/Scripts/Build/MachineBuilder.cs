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
        if (!awaitingWorldClick) return;

        // Ensure we don't accept the same click that triggered the button: wait for release and a new frame
        if (waitRelease)
        {
            if (Input.GetMouseButton(0)) return;
            if (Time.frameCount == activationFrame) return;
            waitRelease = false;
            return;
        }

        // Cancel flow
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
        {
            CancelPreview();
            awaitingWorldClick = false;
            TrySetGameState("Play");
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
                Commit(baseCell, dir);
                placing = false;
                awaitingWorldClick = false;
                TrySetGameState("Play");
            }
        }
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
        // Stop any active conveyor building to avoid overlap
        TryCancelConveyorBuildMode();

        // Enter build mode and wait for the next world click to place
        TrySetGameState("Build");
        awaitingWorldClick = true;
        waitRelease = true;
        activationFrame = Time.frameCount;
    }

    // Back-compatible alias
    public void BuildPressAtMouse() => BuildPress();

    void TryCancelConveyorBuildMode()
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
                    var mi = t.GetMethod("CancelBuildMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null) mi.Invoke(mb, null);
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
        // Destroy ghost and place real prefab with same orientation
        Vector3 pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
        if (ghostGO != null) Destroy(ghostGO);
        var go = Instantiate(pressMachinePrefab, pos, Quaternion.Euler(0, 0, DirToZ(outputDir)));
        var press = go.GetComponent<PressMachine>();
        if (press != null) press.facingVec = outputDir; // Awake will register
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
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb != null && mb.GetType().Name == "GameManager")
                {
                    var t = mb.GetType();
                    var mi = t.GetMethod("SetState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mi != null)
                    {
                        var gsType = t.Assembly.GetType("GameState");
                        object value = gsType != null ? System.Enum.Parse(gsType, name) : null;
                        mi.Invoke(mb, new object[] { value });
                    }
                    break;
                }
            }
        }
        catch { }
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
