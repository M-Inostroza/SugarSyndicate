using System;
using System.Reflection;
using UnityEngine;

[RequireComponent(typeof(Transform))]
public class Conveyor : MonoBehaviour
{
    public Direction direction = Direction.Right;
    [Min(1)] public int ticksPerCell = 4;

    Vector2Int lastCell;

    public Vector2Int DirVec() => DirectionUtil.DirVec(direction);

    void Start()
    {
        RegisterWithGridService();
        lastCell = GetCellForPosition(transform.position);
    }

    void Update()
    {
        var gs = FindGridServiceInstance();
        if (gs == null) return;
        
        var currentCell = SafeInvokeWorldToCell(gs, transform.position);
        
        if (currentCell != lastCell)
        {
            SetConveyorAtCell(lastCell, null);
            SetConveyorAtCell(currentCell, this);
            lastCell = currentCell;
        }
    }

    void OnDestroy()
    {
        SetConveyorAtCell(lastCell, null);
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
        
        SetConveyorAtCell(currentCell, this);
        lastCell = currentCell;
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
