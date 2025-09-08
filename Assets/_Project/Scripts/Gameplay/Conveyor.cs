using System;
using System.Reflection;
using UnityEngine;

[RequireComponent(typeof(Transform))]
public class Conveyor : MonoBehaviour
{
    public Direction direction = Direction.Right;
    [Min(1)] public int ticksPerCell = 4; // how many ticks to move one cell

    Vector2Int lastCell;
    bool registered;

    public Vector2Int DirVec() => DirectionUtil.DirVec(direction);

    void Start()
    {
        RegisterWithGridService();
        lastCell = GetCellForPosition(transform.position);
    }

    void Update()
    {
        // runtime: detect cell changes via reflection and move registration
        var gs = FindGridServiceInstance();
        if (gs == null) return;
        var worldToCell = gs.GetType().GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
        if (worldToCell == null) return;
        var curObj = worldToCell.Invoke(gs, new object[] { transform.position });
        if (!(curObj is Vector2Int cur)) return;
        if (cur != lastCell)
        {
            // unregister old and register new
            SetConveyorAtCell(lastCell, null);
            SetConveyorAtCell(cur, this);

            // find ItemAgent-like objects in old cell and call ReleaseToPool via reflection
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name != "ItemAgent") continue;
                // compute its cell
                var pos = mb.transform.position;
                var cellObj = worldToCell.Invoke(gs, new object[] { pos });
                if (cellObj is Vector2Int itemCell && itemCell == lastCell)
                {
                    var method = t.GetMethod("ReleaseToPool", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    method?.Invoke(mb, null);
                }
            }

            lastCell = cur;
        }
    }

    void OnDestroy()
    {
        // unregister when destroyed
        SetConveyorAtCell(lastCell, null);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Snap to cell centers in editor when not playing
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
        var worldToCell = gs.GetType().GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
        if (worldToCell == null) return;
        var cellObj = worldToCell.Invoke(gs, new object[] { transform.position });
        if (cellObj is Vector2Int cell)
        {
            SetConveyorAtCell(cell, this);
            lastCell = cell;
            registered = true;
        }
    }

    void SetConveyorAtCell(Vector2Int cell, Conveyor conveyor)
    {
        var gs = FindGridServiceInstance();
        if (gs == null) return;

        // Prefer SetConveyor method if available
        var setConv = gs.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) });
        if (setConv != null)
        {
            setConv.Invoke(gs, new object[] { cell, conveyor });
            return;
        }

        // Fallback: set fields on the cell data
        var getCell = gs.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
        if (getCell == null) return;
        var cellData = getCell.Invoke(gs, new object[] { cell });
        if (cellData == null) return;
        var fHas = cellData.GetType().GetField("hasConveyor");
        var fConv = cellData.GetType().GetField("conveyor");
        fHas?.SetValue(cellData, conveyor != null);
        if (fConv != null)
            fConv.SetValue(cellData, conveyor);
    }

    Vector2Int GetCellForPosition(Vector3 pos)
    {
        var gs = FindGridServiceInstance();
        if (gs == null) return default;
        var worldToCell = gs.GetType().GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
        if (worldToCell == null) return default;
        var cellObj = worldToCell.Invoke(gs, new object[] { pos });
        if (cellObj is Vector2Int cell) return cell;
        return default;
    }

    static object FindGridServiceInstance()
    {
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
