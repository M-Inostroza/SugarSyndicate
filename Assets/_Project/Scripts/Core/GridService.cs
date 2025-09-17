using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class GridService : MonoBehaviour, IGridService
{
    public static GridService Instance { get; private set; }

    [SerializeField] Vector2 origin = Vector2.zero;
    [SerializeField, Min(0.01f)] float cellSize = 1f;
    [SerializeField] Vector2Int gridSize = new Vector2Int(20, 12);

    public float CellSize => cellSize;

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

    // Ensure grid dictionary exists/config is sane in editor
    void OnValidate()
    {
        if (gridSize.x < 1) gridSize.x = 1;
        if (gridSize.y < 1) gridSize.y = 1;
        if (cellSize < 0.01f) cellSize = 0.01f;
        if (!Application.isPlaying)
        {
            WarmCells();
        }
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
        var cell = GetCell(c);
        cell.type = CellType.Belt;
        cell.inA = inA; cell.outA = outA; cell.inB = Direction.None; cell.outB = Direction.None;
        // legacy bridge
        cell.hasConveyor = true;
        if (cell.conveyor == null)
        {
            // nothing to attach here; Conveyor placer will handle Unity component
        }
    }

    public void SetJunctionCell(Vector2Int c, Direction inA, Direction inB, Direction outA, Direction outB)
    {
        var cell = GetCell(c);
        cell.type = CellType.Junction;
        cell.inA = inA; cell.inB = inB; cell.outA = outA; cell.outB = outB; cell.junctionToggle = 0;
        // legacy bridge
        cell.hasConveyor = true;
    }

    public void SetMachineCell(Vector2Int c)
    {
        var cell = GetCell(c);
        cell.type = CellType.Machine;
        cell.inA = cell.inB = cell.outA = cell.outB = Direction.None;
        cell.junctionToggle = 0;
        cell.hasConveyor = false;
        cell.conveyor = null;
        cell.hasMachine = true;
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
    }

    // Legacy API used by Conveyor
    public void SetConveyor(Vector2Int c, Conveyor conveyor)
    {
        var cell = GetCell(c);
        cell.conveyor = conveyor;
        cell.hasConveyor = conveyor != null;
        if (conveyor != null)
        {
            // Update new fields from component for compatibility
            cell.type = CellType.Belt;
            cell.inA = DirectionUtil.Opposite(conveyor.direction);
            cell.outA = conveyor.direction;
        }
    }

    public Conveyor GetConveyor(Vector2Int c)
    {
        var cell = GetCell(c);
        return cell != null ? cell.conveyor : null;
    }

    // =====================
    // Editor visualization
    // =====================
    [Header("Gizmos")]
    [SerializeField] bool showGridGizmos = true;
    [SerializeField] bool onlyWhenSelected = false;
    [SerializeField] bool showCellTypes = true;
    [SerializeField] bool showItemMarkers = true;
    [SerializeField] bool showIndices = false;
    [SerializeField] Color gridLineColor = new Color(1f, 1f, 1f, 0.2f);
    [SerializeField] Color boundsColor = new Color(1f, 1f, 1f, 0.6f);
    [SerializeField] Color beltColor = new Color(1f, 0.85f, 0.2f, 0.35f);
    [SerializeField] Color junctionColor = new Color(1f, 0.5f, 0.1f, 0.4f);
    [SerializeField] Color machineColor = new Color(0.2f, 0.7f, 1f, 0.35f);
    [SerializeField] Color itemColor = new Color(0.2f, 1f, 0.4f, 1f);

    void OnDrawGizmos()
    {
        if (!showGridGizmos) return;
#if UNITY_EDITOR
        if (onlyWhenSelected && Selection.activeGameObject != gameObject) return;
#endif
        // Draw grid lines
        Gizmos.color = gridLineColor;
        var cs = Mathf.Max(0.0001f, cellSize);
        var o = (Vector3)origin;
        float z = 0f;
        for (int x = 0; x <= gridSize.x; x++)
        {
            var from = new Vector3(o.x + x * cs, o.y, z);
            var to = new Vector3(o.x + x * cs, o.y + gridSize.y * cs, z);
            Gizmos.DrawLine(from, to);
        }
        for (int y = 0; y <= gridSize.y; y++)
        {
            var from = new Vector3(o.x, o.y + y * cs, z);
            var to = new Vector3(o.x + gridSize.x * cs, o.y + y * cs, z);
            Gizmos.DrawLine(from, to);
        }

        // Draw bounds rectangle thicker by overdrawing
        Gizmos.color = boundsColor;
        var bl = o;
        var br = new Vector3(o.x + gridSize.x * cs, o.y, z);
        var tr = new Vector3(o.x + gridSize.x * cs, o.y + gridSize.y * cs, z);
        var tl = new Vector3(o.x, o.y + gridSize.y * cs, z);
        Gizmos.DrawLine(bl, br);
        Gizmos.DrawLine(br, tr);
        Gizmos.DrawLine(tr, tl);
        Gizmos.DrawLine(tl, bl);

        // Overlay cell content highlights
        if (showCellTypes || showItemMarkers)
        {
            foreach (var kv in cells)
            {
                var c = kv.Key;
                var cell = kv.Value;
                var center = CellToWorld(c, 0f);
                var size = new Vector3(cs * 0.95f, cs * 0.95f, 0.001f);

                if (showCellTypes && cell != null)
                {
                    switch (cell.type)
                    {
                        case CellType.Belt:
                            Gizmos.color = beltColor; Gizmos.DrawCube(center, size); break;
                        case CellType.Junction:
                            Gizmos.color = junctionColor; Gizmos.DrawCube(center, size); break;
                        case CellType.Machine:
                            Gizmos.color = machineColor; Gizmos.DrawCube(center, size); break;
                    }
                }

                if (showItemMarkers && cell != null && cell.hasItem)
                {
                    Gizmos.color = itemColor;
                    Gizmos.DrawSphere(center, cs * 0.15f);
                }

#if UNITY_EDITOR
                if (showIndices)
                {
                    Handles.color = Color.white;
                    var style = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
                    Handles.Label(center, $"{c.x},{c.y}", style);
                }
#endif
            }
        }
    }

    // =====================
    // Adjacency helpers (inputs from neighbors)
    // =====================
    public IEnumerable<Vector2Int> GetNeighbors(Vector2Int c)
    {
        var dirs = new[] { Direction.Up, Direction.Right, Direction.Down, Direction.Left };
        foreach (var d in dirs)
        {
            var n = c + DirectionUtil.DirVec(d);
            if (InBounds(n)) yield return n;
        }
    }

    static bool IsBeltLike(Cell c)
        => c != null && (c.type == CellType.Belt || c.type == CellType.Junction || c.hasConveyor || c.conveyor != null);

    bool NeighborHasOutputTowards(Cell neighbor, Direction toward)
    {
        if (neighbor == null) return false;
        if (neighbor.type == CellType.Belt || neighbor.type == CellType.Junction)
        {
            if (neighbor.outA == toward) return true;
            if (neighbor.outB == toward) return true;
        }
        if (neighbor.conveyor != null)
        {
            return DirectionUtil.DirVec(neighbor.conveyor.direction) == DirectionUtil.DirVec(toward);
        }
        return false;
    }

    public List<Direction> GetIncomingDirections(Vector2Int target)
    {
        var incoming = new List<Direction>(4);
        var dirs = new[] { Direction.Up, Direction.Right, Direction.Down, Direction.Left };
        foreach (var d in dirs)
        {
            var nPos = target + DirectionUtil.DirVec(d);
            if (!InBounds(nPos)) continue;
            var nCell = GetCell(nPos);
            if (!IsBeltLike(nCell)) continue;
            var requiredOut = DirectionUtil.Opposite(d); // neighbor must output toward target
            if (NeighborHasOutputTowards(nCell, requiredOut)) incoming.Add(d);
        }
        return incoming;
    }

    public int GetInputCount(Vector2Int target) => GetIncomingDirections(target).Count;

    public bool HasTwoOrMoreInputs(Vector2Int target) => GetInputCount(target) >= 2;
}
