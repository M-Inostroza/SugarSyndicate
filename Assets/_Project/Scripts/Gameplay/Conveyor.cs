using System;
using System.Reflection;
using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(Transform))]
public class Conveyor : MonoBehaviour, IConveyor
{
    public Direction direction = Direction.Right;
    [Min(1)] public int ticksPerCell = 4;

    Vector2Int lastCell;

    public Vector2Int DirVec() => DirectionUtil.DirVec(direction);

    void Awake()
    {
        // Cache initial cell; avoid mutating runtime services while not playing
        lastCell = GetCellForPosition(transform.position);
        // Do NOT touch GridService here; its Awake may not have warmed the grid yet when scene loads
        // Registration is done in Start to avoid race with GridService.Awake
    }

    void Start()
    {
        if (Application.isPlaying)
        {
            // First, register with GridService now that all Awakes have run
            RegisterWithGridService();
            // Then register with the belt simulation so it can tick us
            BeltSimulationService.Instance?.RegisterConveyor(this);
        }
    }

    void Update()
    {
        if (!Application.isPlaying)
        {
            // Editor-time grid snapping while moving in Scene view
            var gs = FindGridServiceInstance();
            if (gs == null) return;

            var currentCell = SafeInvokeWorldToCell(gs, transform.position);
            if (currentCell == default) return;

            var cellToWorld = gs.GetType().GetMethod("CellToWorld", new Type[] { typeof(Vector2Int), typeof(float) });
            if (cellToWorld == null) return;

            float z = transform.position.z;
            try
            {
                var worldObj = cellToWorld.Invoke(gs, new object[] { currentCell, z });
                if (worldObj is Vector3 world)
                {
                    if ((world - transform.position).sqrMagnitude > 1e-6f)
                        transform.position = world;
                }
            }
            catch { /* ignore in edit mode */ }
            return;
        }

        // Runtime: track cell changes and update grid + belt graph incrementally
        var rgs = FindGridServiceInstance();
        if (rgs == null) return;

        var current = SafeInvokeWorldToCell(rgs, transform.position);
        if (current != lastCell)
        {
            SetConveyorAtCell(lastCell, null);
            TrySetConveyorSafe(current, this);
            lastCell = current;
            BeltSimulationService.Instance?.RegisterConveyor(this);
        }
    }

    void OnDestroy()
    {
        if (Application.isPlaying)
        {
            SetConveyorAtCell(lastCell, null);
            BeltSimulationService.Instance?.UnregisterConveyor(this);
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
        try
        {
            var gs = FindGridServiceInstance();
            if (gs == null) return;
            var worldToCell = gs.GetType().GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
            var cellToWorld = gs.GetType().GetMethod("CellToWorld", new Type[] { typeof(Vector2Int), typeof(float) });
            if (worldToCell == null || cellToWorld == null) return;
            var cellObj = worldToCell.Invoke(gs, new object[] { transform.position });
            if (cellObj is Vector2Int cell)
            {
                var worldObj = cellToWorld.Invoke(gs, new object[] { cell, transform.position.z });
                if (worldObj is Vector3 world)
                    transform.position = world;
            }
        }
        catch
        {
            // ignore
        }
    }
#endif

    void RegisterWithGridService()
    {
        var gs = FindGridServiceInstance();
        if (gs == null) return;

        var currentCell = SafeInvokeWorldToCell(gs, transform.position);

        TrySetConveyorSafe(currentCell, this);
        lastCell = currentCell;
    }

    void TrySetConveyorSafe(Vector2Int cell, Conveyor conveyor)
    {
        // check for an existing conveyor in the cell
        var gs = FindGridServiceInstance();
        if (gs == null) return;
        var getConv = gs.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
        var setConv = gs.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) });
        if (getConv == null || setConv == null) { SetConveyorAtCell(cell, conveyor); return; }

        var existingObj = getConv.Invoke(gs, new object[] { cell });
        var existing = existingObj as Conveyor;
        if (existing != null && existing != this)
        {
            // if placing opposite direction (head-on), keep the earlier one and destroy the new one to avoid a crashy cycle
            var dvExisting = existing.DirVec();
            var dvNew = this.DirVec();
            if (dvExisting == -dvNew)
            {
                Debug.LogWarning($"Conveyor collision head-on at {cell}, destroying the new one to avoid cycle.");
                Destroy(gameObject);
                return;
            }
            // otherwise, replace existing with this (simple policy) or keep existing; here we keep existing
            Debug.LogWarning($"Conveyor already present at {cell}, keeping existing.");
            return;
        }
        setConv.Invoke(gs, new object[] { cell, conveyor });
    }

    void SetConveyorAtCell(Vector2Int cell, Conveyor conveyor)
    {
        var gs = FindGridServiceInstance();
        if (gs == null) return;

        var setConv = gs.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) });
        if (setConv != null)
        {
            setConv.Invoke(gs, new object[] { cell, conveyor });
            return;
        }
    }

    Vector2Int GetCellForPosition(Vector3 pos)
    {
        var gs = FindGridServiceInstance();
        if (gs == null) return default;
        return SafeInvokeWorldToCell(gs, pos);
    }

    Vector2Int SafeInvokeWorldToCell(object gridService, Vector3 position)
    {
        if (gridService == null) return default;

        try
        {
            var worldToCell = gridService.GetType().GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
            if (worldToCell == null) return default;
            var cellObj = worldToCell.Invoke(gridService, new object[] { position });
            if (cellObj is Vector2Int cell)
                return cell;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed to call WorldToCell: {ex.Message}");
        }

        return default;
    }

    static object FindGridServiceInstance()
    {
        // Prefer the authoritative singleton if available
        if (GridService.Instance != null) return GridService.Instance;
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name == "GridService") return mb;
        }
        return null;
    }
}
