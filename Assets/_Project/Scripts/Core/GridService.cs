using System.Collections.Generic;
using UnityEngine;

public class GridService : MonoBehaviour
{
    public static GridService Instance { get; private set; }

    [SerializeField] Vector2 origin = Vector2.zero;
    [SerializeField, Min(0.01f)] float cellSize = 1f;
    [SerializeField] Vector2Int gridSize = new Vector2Int(20, 12);

    readonly Dictionary<Vector2Int, Cell> cells = new();

    public class Cell
    {
        public bool hasFloor;
        public bool hasConveyor;
        public bool hasMachine;
        public Conveyor conveyor;
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

    public void SetConveyor(Vector2Int c, Conveyor conveyor)
    {
        if (!InBounds(c)) return;
        var cell = GetCell(c);
        if (cell == null) return;
        cell.conveyor = conveyor;
        cell.hasConveyor = conveyor != null;
    }

    public Conveyor GetConveyor(Vector2Int c)
    {
        var cell = GetCell(c);
        return cell != null ? cell.conveyor : null;
    }
}
