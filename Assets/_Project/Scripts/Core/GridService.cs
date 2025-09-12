using System.Collections.Generic;
using UnityEngine;

public class GridService : MonoBehaviour, IGridService
{
    public static GridService Instance { get; private set; }

    [SerializeField] Vector2 origin = Vector2.zero;
    [SerializeField, Min(0.01f)] float cellSize = 1f;
    [SerializeField] Vector2Int gridSize = new Vector2Int(20, 12);

    readonly Dictionary<Vector2Int, Cell> cells = new();

    public enum CellType : byte { Empty = 0, Belt = 1, Junction = 2, Machine = 3 }

    public class Cell
    {
        public CellType type;
        public Direction outA = Direction.None;
        public Direction outB = Direction.None;
        public Direction inA = Direction.None;
        public Direction inB = Direction.None;
        public byte junctionToggle; // 0/1 toggler for split/merge policies

        // legacy support
        public bool hasFloor;
        public bool hasConveyor;
        public bool hasMachine;
        public Conveyor conveyor;
        public bool hasItem;
        public Item item;
    }

    void Awake()
    {
        if (Instance) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        WarmCells();
    }

    void WarmCells()
    {
        cells.Clear();
        for (int y = 0; y < gridSize.y; y++)
            for (int x = 0; x < gridSize.x; x++)
                cells[new Vector2Int(x, y)] = new Cell();
    }

    public Vector2Int WorldToCell(Vector3 world)
    {
        Vector2 p = (Vector2)world - origin;
        return new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.y / cellSize));
    }

    public Vector3 CellToWorld(Vector2Int cell, float z = 0)
    {
        Vector2 w = origin + (Vector2)cell * cellSize + new Vector2(cellSize * 0.5f, cellSize * 0.5f);
        return new Vector3(w.x, w.y, z);
    }

    public bool InBounds(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < gridSize.x && c.y < gridSize.y;

    public Cell GetCell(Vector2Int c) => cells.TryGetValue(c, out var cell) ? cell : null;

    // New API for the cell-based system
    public void SetBeltCell(Vector2Int c, Direction inA, Direction outA)
    {
        if (!InBounds(c)) { Debug.LogWarning($"[Grid] SetBeltCell ignored: {c} out of bounds"); return; }
        var cell = GetCell(c);
        if (cell == null) { Debug.LogWarning($"[Grid] SetBeltCell ignored: cell {c} is null"); return; }
        cell.type = CellType.Belt;
        cell.inA = inA; cell.outA = outA; cell.inB = Direction.None; cell.outB = Direction.None;
        // legacy bridge
        cell.hasConveyor = true;
        if (cell.conveyor == null)
        {
            // nothing to attach here; Conveyor placer will handle Unity component
        }
        Debug.Log($"[Grid] SetBeltCell {c}: inA={inA} outA={outA}");
    }

    public void SetJunctionCell(Vector2Int c, Direction inA, Direction inB, Direction outA, Direction outB)
    {
        if (!InBounds(c)) { Debug.LogWarning($"[Grid] SetJunctionCell ignored: {c} out of bounds"); return; }
        var cell = GetCell(c);
        if (cell == null) { Debug.LogWarning($"[Grid] SetJunctionCell ignored: cell {c} is null"); return; }
        cell.type = CellType.Junction;
        cell.inA = inA; cell.inB = inB; cell.outA = outA; cell.outB = outB; cell.junctionToggle = 0;
        // legacy bridge
        cell.hasConveyor = true;
        Debug.Log($"[Grid] SetJunctionCell {c}: inA={inA} inB={inB} outA={outA} outB={outB}");
    }

    public void ClearCell(Vector2Int c)
    {
        if (!InBounds(c)) return;
        var cell = GetCell(c);
        if (cell == null) return;
        cell.type = CellType.Empty;
        cell.inA = cell.inB = cell.outA = cell.outB = Direction.None;
        cell.junctionToggle = 0;
        cell.hasConveyor = false;
        cell.conveyor = null;
        Debug.Log($"[Grid] Cleared {c}");
    }

    // Legacy API used by Conveyor
    public void SetConveyor(Vector2Int c, Conveyor conveyor)
    {
        if (!InBounds(c)) { Debug.LogWarning($"[Grid] SetConveyor ignored: {c} out of bounds"); return; }
        var cell = GetCell(c);
        if (cell == null) { Debug.LogWarning($"[Grid] SetConveyor ignored: cell {c} is null"); return; }
        cell.conveyor = conveyor;
        cell.hasConveyor = conveyor != null;
        if (conveyor != null)
        {
            // Update new fields from component for compatibility
            cell.type = CellType.Belt;
            cell.inA = DirectionUtil.Opposite(conveyor.direction);
            cell.outA = conveyor.direction;
        }
        Debug.Log($"[Grid] SetConveyor {c} => {(conveyor != null ? "SET" : "CLEARED")} {(conveyor != null ? "dir=" + conveyor.direction.ToString() : string.Empty)}");
    }

    public Conveyor GetConveyor(Vector2Int c)
    {
        var cell = GetCell(c);
        return cell != null ? cell.conveyor : null;
    }
}
