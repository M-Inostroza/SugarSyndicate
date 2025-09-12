using System.Collections.Generic;
using UnityEngine;

public class BeltSimulationService : MonoBehaviour
{
    public static BeltSimulationService Instance { get; private set; }

    readonly HashSet<Vector2Int> active = new HashSet<Vector2Int>();
    GridService grid;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        grid = GridService.Instance;
        DontDestroyOnLoad(gameObject);
    }

    void Update()
    {
        if (grid == null) return;
        var step = new List<Vector2Int>(active);
        active.Clear();
        foreach (var cell in step)
            StepCell(cell);
    }

    void StepCell(Vector2Int cellPos)
    {
        var cell = grid.GetCell(cellPos);
        if (cell == null || !cell.hasItem) return;

        if (cell.conveyor is Junction j)
        {
            HandleJunction(cellPos, cell, j);
            return;
        }

        var destPos = cellPos + cell.conveyor.DirVec();
        var dest = grid.GetCell(destPos);
        if (dest == null || dest.hasItem || !dest.hasConveyor)
        {
            active.Add(cellPos);
            return;
        }

        dest.item = cell.item;
        dest.hasItem = true;
        cell.hasItem = false;
        active.Add(destPos);
    }

    void HandleJunction(Vector2Int cellPos, GridService.Cell cell, Junction j)
    {
        if (!cell.hasItem)
        {
            TryPullFrom(cellPos, j.inA);
            TryPullFrom(cellPos, j.inB);
            if (grid.GetCell(cellPos).hasItem)
                active.Add(cellPos);
            return;
        }

        var outDir = j.SelectOutput();
        var destPos = cellPos + DirectionUtil.DirVec(outDir);
        var dest = grid.GetCell(destPos);
        if (dest == null || dest.hasItem || !dest.hasConveyor)
        {
            active.Add(cellPos);
            return;
        }

        dest.item = cell.item;
        dest.hasItem = true;
        cell.hasItem = false;
        active.Add(destPos);
    }

    void TryPullFrom(Vector2Int target, Direction dir)
    {
        var fromPos = target + DirectionUtil.DirVec(dir);
        var from = grid.GetCell(fromPos);
        var dest = grid.GetCell(target);
        if (from == null || dest == null) return;
        if (!from.hasItem || from.conveyor == null)
            return;
        if (from.conveyor is IConveyor conv && conv.DirVec() != -DirectionUtil.DirVec(dir))
            return;
        if (dest.hasItem) return;

        dest.item = from.item;
        dest.hasItem = true;
        from.hasItem = false;
        active.Add(target);
    }

    public bool TrySpawnItem(Vector2Int cellPos, Item item)
    {
        var cell = grid.GetCell(cellPos);
        if (cell == null || cell.hasItem || !cell.hasConveyor)
            return false;
        cell.item = item;
        cell.hasItem = true;
        active.Add(cellPos);
        return true;
    }

    public void RegisterConveyor(Conveyor c)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null || c == null) return;
        var cellPos = grid.WorldToCell(c.transform.position);
        active.Add(cellPos);
    }

    public void UnregisterConveyor(Conveyor c)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null || c == null) return;
        var cellPos = grid.WorldToCell(c.transform.position);
        active.Remove(cellPos);
    }
}
