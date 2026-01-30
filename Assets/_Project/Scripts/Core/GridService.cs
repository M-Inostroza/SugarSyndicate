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

    [System.Serializable]
    public struct WaterRect
    {
        public Vector2Int origin; // bottom-left cell
        public Vector2Int size;   // width/height in cells
    }

    [Header("Water")]
    [SerializeField] Color waterColor = new Color(0.2f, 0.5f, 1f, 0.25f);
    [SerializeField] List<WaterRect> waterRects = new();

    [System.Serializable]
    public struct SugarZone
    {
        public Vector2Int center; // center cell (zone is NxN, odd size)
    }

    [Header("Sugar")]
    [SerializeField, Min(1)] int sugarZoneSize = 3;
    [SerializeField, Min(0f)] float sugarOuterEfficiency = 1f;
    [SerializeField, Min(0f)] float sugarCenterEfficiency = 2f;
    [SerializeField] List<SugarZone> sugarZones = new();
    [Header("Sugar Distribution")]
    [Tooltip("Shape of sugar falloff from center (higher = tighter core).")]
    [SerializeField, Min(0.01f)] float sugarFalloffPower = 1f;
    [Tooltip("Adds per-cell variation to sugar amounts (0 = none).")]
    [SerializeField, Range(0f, 1f)] float sugarNoiseStrength = 0f;
    [Tooltip("Noise scale for sugar variation (higher = more changes between neighboring cells).")]
    [SerializeField, Min(0.001f)] float sugarNoiseScale = 0.5f;
    [Tooltip("Seed for sugar variation.")]
    [SerializeField] int sugarNoiseSeed = 1337;

    public float CellSize => cellSize;
    public Vector2 Origin => origin;
    public Vector2Int GridSize => gridSize;
    public Color WaterColor => waterColor;
    public IReadOnlyList<WaterRect> WaterRects => waterRects;
    public float SugarOuterEfficiency => sugarOuterEfficiency;
    public float SugarCenterEfficiency => sugarCenterEfficiency;
    public int SugarZoneSize => sugarZoneSize;
    public IReadOnlyList<SugarZone> SugarZones => sugarZones;

    readonly Dictionary<Vector2Int, Cell> cells = new();

    public enum CellType : byte { Empty = 0, Belt = 1, Junction = 2, Machine = 3 }

    public class Cell
    {
        public CellType type;
        public Direction outA = Direction.None;
        public Direction outB = Direction.None;
        public Direction inA = Direction.None;
        public Direction inB = Direction.None;
        public Direction inC = Direction.None; // additional input for 3-way merger
        public byte junctionToggle; // 0/1 toggler for split/merge policies
        public bool isWater;
        public bool isSugar;
        public float sugarEfficiency;
        public bool isBlueprint;
        public bool isBroken;

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
        ApplyWaterRects();
        ApplySugarZones();
    }

    // Ensure grid dictionary exists/config is sane in editor
    void OnValidate()
    {
        if (gridSize.x < 1) gridSize.x = 1;
        if (gridSize.y < 1) gridSize.y = 1;
        if (cellSize < 0.01f) cellSize = 0.01f;
        if (sugarZoneSize < 1) sugarZoneSize = 1;
        if (sugarZoneSize % 2 == 0) sugarZoneSize += 1;
        if (sugarOuterEfficiency < 0f) sugarOuterEfficiency = 0f;
        if (sugarCenterEfficiency < 0f) sugarCenterEfficiency = 0f;
        if (sugarCenterEfficiency < sugarOuterEfficiency) sugarCenterEfficiency = sugarOuterEfficiency;
        if (sugarFalloffPower < 0.01f) sugarFalloffPower = 0.01f;
        if (sugarNoiseStrength < 0f) sugarNoiseStrength = 0f;
        if (sugarNoiseStrength > 1f) sugarNoiseStrength = 1f;
        if (sugarNoiseScale < 0.001f) sugarNoiseScale = 0.001f;
        if (!Application.isPlaying)
        {
            WarmCells();
            ApplyWaterRects();
            ApplySugarZones();
        }
    }

    void WarmCells()
    {
        cells.Clear();
        for (int y = 0; y < gridSize.y; y++)
            for (int x = 0; x < gridSize.x; x++)
                cells[new Vector2Int(x, y)] = new Cell();
    }

    void ApplyWaterRects()
    {
        foreach (var kv in cells) kv.Value.isWater = false;
        if (waterRects == null) return;
        foreach (var wr in waterRects)
        {
            var size = new Vector2Int(Mathf.Max(0, wr.size.x), Mathf.Max(0, wr.size.y));
            for (int y = 0; y < size.y; y++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    var c = wr.origin + new Vector2Int(x, y);
                    if (!InBounds(c)) continue;
                    var cell = GetCell(c);
                    if (cell != null) cell.isWater = true;
                }
            }
        }
    }

    void ApplySugarZones()
    {
        foreach (var kv in cells)
        {
            var cell = kv.Value;
            if (cell == null) continue;
            cell.isSugar = false;
            cell.sugarEfficiency = 0f;
        }
        if (sugarZones == null) return;
        int size = Mathf.Max(1, sugarZoneSize);
        if (size % 2 == 0) size += 1;
        int half = size / 2;
        foreach (var zone in sugarZones)
        {
            for (int y = -half; y <= half; y++)
            {
                for (int x = -half; x <= half; x++)
                {
                    var c = zone.center + new Vector2Int(x, y);
                    if (!InBounds(c)) continue;
                    var cell = GetCell(c);
                    if (cell == null) continue;
                    float eff = ComputeSugarEfficiency(c, zone.center, half);
                    cell.isSugar = true;
                    if (eff > cell.sugarEfficiency) cell.sugarEfficiency = eff;
                }
            }
        }
    }

    float ComputeSugarEfficiency(Vector2Int cell, Vector2Int center, int half)
    {
        if (half <= 0) return sugarCenterEfficiency;
        int dx = cell.x - center.x;
        int dy = cell.y - center.y;
        float maxDist = half;
        float dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
        float t = maxDist > 0f ? 1f - (dist / maxDist) : 1f;
        t = Mathf.Clamp01(t);
        if (sugarNoiseStrength > 0f)
        {
            float nx = (cell.x + sugarNoiseSeed) * sugarNoiseScale;
            float ny = (cell.y + sugarNoiseSeed) * sugarNoiseScale;
            float noise = Mathf.PerlinNoise(nx, ny);
            float offset = (noise - 0.5f) * 2f * sugarNoiseStrength;
            t = Mathf.Clamp01(t + offset);
        }
        if (!Mathf.Approximately(sugarFalloffPower, 1f))
        {
            t = Mathf.Pow(t, sugarFalloffPower);
        }
        return Mathf.Lerp(sugarOuterEfficiency, sugarCenterEfficiency, t);
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

    public bool IsWater(Vector2Int c) => InBounds(c) && GetCell(c)?.isWater == true;
    public bool IsSugar(Vector2Int c) => InBounds(c) && GetCell(c)?.isSugar == true;
    public float GetSugarEfficiency(Vector2Int c)
    {
        if (!InBounds(c)) return 0f;
        var cell = GetCell(c);
        return cell != null ? cell.sugarEfficiency : 0f;
    }

    // New API for the cell-based system
    public void SetBeltCell(Vector2Int c, Direction inA, Direction outA)
    {
        var cell = GetCell(c);
        cell.type = CellType.Belt;
        cell.inA = inA; cell.outA = outA; cell.inB = Direction.None; cell.outB = Direction.None;
        cell.isBlueprint = false;
        cell.isBroken = false;
        // legacy bridge
        cell.hasConveyor = true;
        if (cell.conveyor == null)
        {
            // nothing to attach here; Conveyor placer will handle Unity component
        }
    }

    // Updated: supports up to 3 inputs (inA/inB/inC) and 2 outputs (outA/outB).
    // For a 3-to-1 merger, set inA/inB/inC and set outA to forward, outB to None.
    public void SetJunctionCell(Vector2Int c, Direction inA, Direction inB, Direction inC, Direction outA, Direction outB)
    {
        var cell = GetCell(c);
        cell.type = CellType.Junction;
        cell.inA = inA; cell.inB = inB; cell.inC = inC; cell.outA = outA; cell.outB = outB; cell.junctionToggle = 0;
        cell.isBlueprint = false;
        cell.isBroken = false;
        // legacy bridge
        cell.hasConveyor = true;
    }

    public void SetMachineCell(Vector2Int c)
    {
        var cell = GetCell(c);
        cell.type = CellType.Machine;
        cell.inA = cell.inB = cell.inC = cell.outA = cell.outB = Direction.None;
        cell.junctionToggle = 0;
        cell.isBlueprint = false;
        cell.isBroken = false;
        cell.hasConveyor = false;
        cell.conveyor = null;
        cell.hasMachine = true;
    }

    public void SetBlueprintCell(Vector2Int c)
    {
        if (!InBounds(c)) return;
        var cell = GetCell(c);
        if (cell == null) return;
        cell.type = CellType.Empty;
        cell.inA = cell.inB = cell.inC = cell.outA = cell.outB = Direction.None;
        cell.junctionToggle = 0;
        cell.hasConveyor = false;
        cell.conveyor = null;
        cell.hasMachine = false;
        cell.isBlueprint = true;
        cell.isBroken = false;
    }

    public void ClearCell(Vector2Int c)
    {
        if (!InBounds(c)) return;
        var cell = GetCell(c);
        if (cell == null) return;
        cell.type = CellType.Empty;
        cell.inA = cell.inB = cell.inC = cell.outA = cell.outB = Direction.None;
        cell.junctionToggle = 0;
        cell.hasConveyor = false;
        cell.conveyor = null;
        cell.isBlueprint = false;
        cell.isBroken = false;
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
            cell.inB = cell.inC = Direction.None;
            cell.outB = Direction.None;
            cell.isBlueprint = false;
            cell.isBroken = false;
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
    [SerializeField] bool showSugarZones = true;
    [SerializeField] bool showItemMarkers = true;
    [SerializeField] bool showIndices = false;
    [SerializeField] bool showCoordinateLabels = false;
    [SerializeField] Color gridLineColor = new Color(1f, 1f, 1f, 0.2f);
    [SerializeField] Color boundsColor = new Color(1f, 1f, 1f, 0.6f);
    [SerializeField] Color beltColor = new Color(1f, 0.85f, 0.2f, 0.35f);
    [SerializeField] Color junctionColor = new Color(1f, 0.5f, 0.1f, 0.4f);
    [SerializeField] Color machineColor = new Color(0.2f, 0.7f, 1f, 0.35f);
    [SerializeField] Color blueprintColor = new Color(0.35f, 0.75f, 1f, 0.35f);
    [SerializeField] Color itemColor = new Color(0.2f, 1f, 0.4f, 1f);
    [SerializeField] Color sugarColor = new Color(1f, 1f, 1f, 0.35f);
    [SerializeField] Color sugarCenterColor = new Color(1f, 1f, 1f, 0.7f);
    [SerializeField] bool sugarColorByEfficiency = true;

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
        if (showCellTypes || showItemMarkers || showIndices || showCoordinateLabels)
        {
#if UNITY_EDITOR
            GUIStyle labelStyle = null;
            if (showIndices || showCoordinateLabels)
            {
                labelStyle = new GUIStyle(EditorStyles.miniBoldLabel)
                {
                    alignment = TextAnchor.MiddleCenter
                };
                labelStyle.normal.textColor = Color.white;
            }
#endif
            foreach (var kv in cells)
            {
                var c = kv.Key;
                var cell = kv.Value;
                var center = CellToWorld(c, 0f);
                var size = new Vector3(cs * 0.95f, cs * 0.95f, 0.001f);

                if (cell != null && cell.isWater)
                {
                    Gizmos.color = waterColor;
                    Gizmos.DrawCube(center, size);
                }

                if (showSugarZones && cell != null && cell.isSugar)
                {
                    if (sugarColorByEfficiency)
                    {
                        float eff = cell.sugarEfficiency;
                        float t = Mathf.Abs(sugarCenterEfficiency - sugarOuterEfficiency) > 0.0001f
                            ? Mathf.InverseLerp(sugarOuterEfficiency, sugarCenterEfficiency, eff)
                            : 1f;
                        Gizmos.color = Color.Lerp(sugarColor, sugarCenterColor, Mathf.Clamp01(t));
                    }
                    else
                    {
                        Gizmos.color = sugarColor;
                    }
                    Gizmos.DrawCube(center, size);
                }

                if (showCellTypes && cell != null)
                {
                    if (cell.isBlueprint)
                    {
                        Gizmos.color = blueprintColor;
                        Gizmos.DrawCube(center, size);
                    }

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
                if (showIndices || showCoordinateLabels)
                {
                    Handles.color = Color.white;
                    var label = showCoordinateLabels ? FormatCoordinateLabel(c) : $"{c.x},{c.y}";
                    Handles.Label(center, label, labelStyle);
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
        => c != null && !c.isBlueprint && !c.isBroken
           && (c.type == CellType.Belt || c.type == CellType.Junction || c.hasConveyor || c.conveyor != null);

    static string FormatCoordinateLabel(Vector2Int cell)
    {
        return $"{IndexToLetters(cell.x)}{cell.y + 1}";
    }

    static string IndexToLetters(int index)
    {
        if (index < 0) return "?";
        string result = string.Empty;
        int n = index;
        while (n >= 0)
        {
            int rem = n % 26;
            result = (char)('A' + rem) + result;
            n = (n / 26) - 1;
        }
        return result;
    }

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
