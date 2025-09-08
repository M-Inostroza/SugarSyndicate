using System.Collections.Generic;
using UnityEngine;

public class GridService : MonoBehaviour
{
    public static GridService Instance { get; private set; }

    [SerializeField] Vector2 origin = Vector2.zero;
    [SerializeField, Min(0.01f)] float cellSize = 1f;
    [SerializeField] Vector2Int gridSize = new Vector2Int(20, 12);

    // Runtime toggle to show grid to player
    [Header("Runtime Debug")]
    public bool showRuntimeGrid = false;
    public Color runtimeGridColor = new Color(1f, 1f, 1f, 0.08f);

    readonly Dictionary<Vector2Int, Cell> cells = new();

    Material runtimeGridMat;

    public class Cell
    {
        // Simple occupancy flags; expand later (belts, machines, items)
        public bool hasFloor;
        public bool hasConveyor;
        public bool hasMachine;
        public int itemCount; // items in transit/center

        // cached conveyor component if any
        public Conveyor conveyor;
    }

    void Awake()
    {
        if (Instance) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        WarmCells();

        // Show grid by default in Play mode to aid debugging/placement
        if (Application.isPlaying)
            showRuntimeGrid = true;
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

    public bool InBounds(Vector2Int c) =>
        c.x >= 0 && c.y >= 0 && c.x < gridSize.x && c.y < gridSize.y;

    public Cell GetCell(Vector2Int c) =>
        cells.TryGetValue(c, out var cell) ? cell : null;

    // Conveyor registration helpers
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

    // Create a simple material for GL rendering
    void EnsureRuntimeMaterial()
    {
        if (runtimeGridMat != null) return;
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null) return;
        runtimeGridMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        // enable alpha blending
        runtimeGridMat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        runtimeGridMat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        runtimeGridMat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        runtimeGridMat.SetInt("_ZWrite", 0);
    }

    // Draw grid in play mode using GL lines when showRuntimeGrid is enabled
    void OnRenderObject()
    {
        if (!showRuntimeGrid) return;
        EnsureRuntimeMaterial();
        if (runtimeGridMat == null) return;

        runtimeGridMat.SetPass(0);
        GL.PushMatrix();
        // match transform identity
        GL.MultMatrix(Matrix4x4.identity);
        GL.Begin(GL.LINES);
        GL.Color(runtimeGridColor);

        float left = origin.x;
        float bottom = origin.y;
        float width = gridSize.x * cellSize;
        float height = gridSize.y * cellSize;

        // vertical lines
        for (int x = 0; x <= gridSize.x; x++)
        {
            float px = left + x * cellSize;
            GL.Vertex(new Vector3(px, bottom, 0));
            GL.Vertex(new Vector3(px, bottom + height, 0));
        }

        // horizontal lines
        for (int y = 0; y <= gridSize.y; y++)
        {
            float py = bottom + y * cellSize;
            GL.Vertex(new Vector3(left, py, 0));
            GL.Vertex(new Vector3(left + width, py, 0));
        }

        GL.End();
        GL.PopMatrix();
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        // Draw grid in editor for alignment
        Gizmos.matrix = Matrix4x4.identity;
        Gizmos.color = new Color(1, 1, 1, 0.15f);
        for (int y = 0; y < gridSize.y; y++)
            for (int x = 0; x < gridSize.x; x++)
            {
                Vector3 center = CellToWorld(new Vector2Int(x, y));
                Gizmos.DrawWireCube(center, new Vector3(cellSize, cellSize, 0));
            }
    }
#endif
}
