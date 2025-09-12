using System;
using System.Reflection;
using UnityEngine;

public class ConveyorPlacer : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GameObject conveyorPrefab;
    [Tooltip("Optional: provide a GameObject that hosts GridService (if you want to assign manually)")]
    [SerializeField] GameObject gridServiceObject;
    [SerializeField] LayerMask blockingMask;

    [Header("Placement")]
    [SerializeField] Color okColor = new Color(1f,1f,1f,0.5f);
    [SerializeField] Color blockedColor = new Color(1f,0f,0f,0.5f);

    Quaternion rotation = Quaternion.identity;

    // reflection cache for GridService methods
    object gridServiceInstance;
    MethodInfo miWorldToCell; // Vector2Int WorldToCell(Vector3)
    MethodInfo miCellToWorld; // Vector3 CellToWorld(Vector2Int, float)

    void Reset()
    {
        // try to auto-find an instance by name if not assigned
        if (gridServiceObject == null)
        {
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "GridService")
                {
                    gridServiceObject = mb.gameObject;
                    break;
                }
            }
        }

        if (gridServiceObject != null)
            CacheGridServiceReflection(gridServiceObject);
    }

    void Awake()
    {
        if (gridServiceObject != null && gridServiceInstance == null)
            CacheGridServiceReflection(gridServiceObject);
    }

    void CacheGridServiceReflection(GameObject go)
    {
        gridServiceInstance = null;
        miWorldToCell = null;
        miCellToWorld = null;
        if (go == null) return;
        var mbs = go.GetComponents<MonoBehaviour>();
        foreach (var mb in mbs)
        {
            if (mb == null) continue;
            var type = mb.GetType();
            if (type.Name != "GridService") continue;
            gridServiceInstance = mb;
            miWorldToCell = type.GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
            miCellToWorld = type.GetMethod("CellToWorld", new Type[] { typeof(Vector2Int), typeof(float) });
            break;
        }
    }

    // Called by BuildModeController when entering build mode
    public void BeginPreview()
    {
        rotation = Quaternion.identity;
        // ensure GridService is available early
        EnsureGridServiceCached();
    }

    // Called by BuildModeController when exiting build mode
    public void EndPreview()
    {
        // no-op for tap-to-place workflow
    }

    public void RotatePreview()
    {
        rotation = Quaternion.Euler(0,0, Mathf.Round((rotation.eulerAngles.z + 90f) % 360f));
    }

    // Preview update is intentionally NO-OP for tap-to-place workflow on mobile
    public void UpdatePreviewPosition() { }

    public bool TryPlaceAtMouse()
    {
        if (conveyorPrefab == null) return false;
        var world = GetMouseWorld();
        Vector2Int cell2;
        if (EnsureGridServiceCached())
        {
            if (miWorldToCell == null) return false;
            var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
            cell2 = res is Vector2Int v ? v : new Vector2Int(0,0);
        }
        else return false;

        if (IsBlocked(cell2)) return false;

        if (miCellToWorld == null) return false;
        var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f });
        var center = worldObj is Vector3 vv ? vv : Vector3.zero;

        // Instantiate the prefab exactly as authored
        Instantiate(conveyorPrefab, center, rotation);

        // Inform the belt graph system that tiles changed
        MarkGraphDirtyIfPresent();
        return true;
    }

    public void RefreshPreviewAfterPlace()
    {
        // no-op for tap-to-place
    }

    void MarkGraphDirtyIfPresent()
    {
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
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

    bool IsBlocked(Vector2Int cell2)
    {
        if (miCellToWorld == null) return false;
        var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f });
        var center = worldObj is Vector3 vv ? vv : Vector3.zero;

        var hit = Physics2D.OverlapBox((Vector2)center, Vector2.one * 0.9f, 0f, blockingMask);
        return hit != null;
    }

    Vector3 GetMouseWorld()
    {
        var cam = Camera.main;
        var pos = Input.mousePosition;
        var world = cam != null ? cam.ScreenToWorldPoint(pos) : new Vector3(pos.x, pos.y, 0f);
        world.z = 0f;
        return world;
    }

    bool EnsureGridServiceCached()
    {
        if (gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null) return true;
        if (gridServiceObject != null)
        {
            CacheGridServiceReflection(gridServiceObject);
            return gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null;
        }

        // try to find a MonoBehaviour named GridService in scene
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name == "GridService")
            {
                gridServiceObject = mb.gameObject;
                CacheGridServiceReflection(gridServiceObject);
                break;
            }
        }
        return gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null;
    }
}
