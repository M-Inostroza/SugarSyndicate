using System;
using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public class PowerLinkLine : MonoBehaviour
{
    public const int DefaultSagSegments = 10;
    public const float DefaultBaseSag = 0.12f;
    public const float DefaultSagPerUnit = 0.075f;
    public const float DefaultMaxSag = 0.6f;

    [SerializeField] Component startNode;
    [SerializeField] Component endNode;
    [SerializeField] LineRenderer lineRenderer;
    [SerializeField, Min(0.01f)] float lineWidth = 0.08f;
    [SerializeField] Color lineColor = new Color(1f, 0.92f, 0.35f, 0.95f);
    [SerializeField] Material lineMaterial;
    [SerializeField] string lineSortingLayerName = "Default";
    [SerializeField] int lineSortingOrder = 5000;
    [Header("Sag")]
    [SerializeField] bool useSagCurve = true;
    [SerializeField, Min(3)] int sagSegments = 10;
    [SerializeField, Min(0f)] float baseSag = 0.12f;
    [SerializeField, Min(0f)] float sagPerUnit = 0.075f;
    [SerializeField, Min(0f)] float maxSag = 0.6f;

    readonly List<Vector2Int> cableCells = new List<Vector2Int>();
    readonly List<Vector2Int> ownedCableCells = new List<Vector2Int>();
    readonly List<Vector2Int> endpointCells = new List<Vector2Int>();
    readonly List<Vector2Int> passThroughCells = new List<Vector2Int>();
    static readonly HashSet<PowerLinkLine> ActiveLinks = new HashSet<PowerLinkLine>();

    public static event Action ActiveLinksChanged;

    PowerService powerService;
    bool registered;

    public Component StartNode => startNode;
    public Component EndNode => endNode;

    void Awake()
    {
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        EnsureLineRenderer();
    }

    void OnEnable()
    {
        ActiveLinks.Add(this);
        EnsureLineRenderer();
        UpdateVisual();
        if (startNode != null && endNode != null && !registered)
            TryRegisterCableCells();
        NotifyActiveLinksChanged();
    }

    void OnDisable()
    {
        ActiveLinks.Remove(this);
        UnregisterCableCells();
        NotifyActiveLinksChanged();
    }

    void OnDestroy()
    {
        ActiveLinks.Remove(this);
        UnregisterCableCells();
        NotifyActiveLinksChanged();
    }

    void LateUpdate()
    {
        if (!PowerNodeUtil.IsConnectableNode(startNode) || !PowerNodeUtil.IsConnectableNode(endNode))
        {
            Destroy(gameObject);
            return;
        }

        UpdateVisual();
    }

    public bool Initialize(Component fromNode, Component toNode, float width, Color color, Material material, string sortingLayerName, int sortingOrder, List<Vector2Int> precomputedCells = null)
    {
        startNode = fromNode;
        endNode = toNode;
        lineWidth = Mathf.Max(0.01f, width);
        lineColor = color;
        lineMaterial = material;
        lineSortingLayerName = sortingLayerName;
        lineSortingOrder = sortingOrder;

        EnsureLineRenderer();
        UpdateVisual();
        bool initialized = TryRegisterCableCells(precomputedCells);
        NotifyActiveLinksChanged();
        return initialized;
    }

    public bool Connects(Component a, Component b)
    {
        if (a == null || b == null) return false;
        return (startNode == a && endNode == b) || (startNode == b && endNode == a);
    }

    public bool OccupiesCell(Vector2Int cell)
    {
        for (int i = 0; i < cableCells.Count; i++)
        {
            if (cableCells[i] == cell)
                return true;
        }
        return false;
    }

    public static bool HasLinkBetween(Component a, Component b)
    {
        foreach (var link in ActiveLinks)
        {
            if (link == null) continue;
            if (link.Connects(a, b)) return true;
        }
        return false;
    }

    public static bool TryGetAtCell(Vector2Int cell, out PowerLinkLine linkAtCell)
    {
        foreach (var link in ActiveLinks)
        {
            if (link == null) continue;
            if (!link.OccupiesCell(cell)) continue;
            linkAtCell = link;
            return true;
        }

        linkAtCell = null;
        return false;
    }

    public static int CountLinksForNode(Component node)
    {
        if (node == null) return 0;

        int count = 0;
        foreach (var link in ActiveLinks)
        {
            if (link == null) continue;
            if (!link.isActiveAndEnabled) continue;
            if (link.GetComponent<BlueprintTask>() != null) continue;
            if (link.startNode == node || link.endNode == node)
                count++;
        }

        return count;
    }

    bool TryRegisterCableCells(List<Vector2Int> precomputedCells = null)
    {
        UnregisterCableCells();
        if (!PowerNodeUtil.IsConnectableNode(startNode) || !PowerNodeUtil.IsConnectableNode(endNode))
            return false;

        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        if (powerService == null) return false;

        var path = precomputedCells;
        if (path == null)
        {
            if (!PowerNodeUtil.TryBuildCableCells(startNode, endNode, out path))
                return false;
        }

        for (int i = 0; i < path.Count; i++)
        {
            var cell = path[i];
            bool registeredCell = powerService.RegisterCable(cell);
            if (!registeredCell && !powerService.IsCableAt(cell))
            {
                for (int j = 0; j < ownedCableCells.Count; j++)
                    powerService.UnregisterCable(ownedCableCells[j]);
                cableCells.Clear();
                ownedCableCells.Clear();
                endpointCells.Clear();
                passThroughCells.Clear();
                return false;
            }

            if (registeredCell)
                ownedCableCells.Add(cell);
            cableCells.Add(cell);
        }

        RegisterCableRoles(path);
        registered = cableCells.Count > 0;
        return registered;
    }

    void UnregisterCableCells()
    {
        if (!registered
            && cableCells.Count == 0
            && ownedCableCells.Count == 0
            && endpointCells.Count == 0
            && passThroughCells.Count == 0) return;
        if (powerService == null) powerService = PowerService.Instance;
        if (powerService != null)
        {
            for (int i = 0; i < passThroughCells.Count; i++)
                powerService.UnregisterCablePassThrough(passThroughCells[i]);
            for (int i = 0; i < endpointCells.Count; i++)
                powerService.UnregisterCableEndpoint(endpointCells[i]);
            for (int i = 0; i < ownedCableCells.Count; i++)
                powerService.UnregisterCable(ownedCableCells[i]);
        }
        cableCells.Clear();
        ownedCableCells.Clear();
        endpointCells.Clear();
        passThroughCells.Clear();
        registered = false;
    }

    void RegisterCableRoles(List<Vector2Int> path)
    {
        if (powerService == null || path == null || path.Count == 0) return;

        var first = path[0];
        powerService.RegisterCableEndpoint(first);
        endpointCells.Add(first);

        if (path.Count > 1)
        {
            var last = path[path.Count - 1];
            if (last != first)
            {
                powerService.RegisterCableEndpoint(last);
                endpointCells.Add(last);
            }
        }

        for (int i = 1; i < path.Count - 1; i++)
        {
            var cell = path[i];
            powerService.RegisterCablePassThrough(cell);
            passThroughCells.Add(cell);
        }
    }

    void EnsureLineRenderer()
    {
        if (lineRenderer == null)
            lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer == null)
            lineRenderer = gameObject.AddComponent<LineRenderer>();

        ConfigureLineRenderer(lineRenderer, lineWidth, lineColor, lineMaterial, lineSortingLayerName, lineSortingOrder, useSagCurve, sagSegments);
    }

    void UpdateVisual()
    {
        if (lineRenderer == null) return;

        ConfigureLineRenderer(lineRenderer, lineWidth, lineColor, lineMaterial, lineSortingLayerName, lineSortingOrder, useSagCurve, sagSegments);

        float z = transform.position.z;
        var startPos = PowerNodeUtil.GetNodeWorldPosition(startNode, z);
        var endPos = PowerNodeUtil.GetNodeWorldPosition(endNode, z);
        UpdateLinePositions(lineRenderer, startPos, endPos, useSagCurve, sagSegments, baseSag, sagPerUnit, maxSag);
    }

    public static void ConfigureLineRenderer(LineRenderer line, float width, Color color, Material material, string sortingLayerName, int sortingOrder, bool useSagCurve = true, int sagSegments = DefaultSagSegments)
    {
        if (line == null) return;

        line.positionCount = useSagCurve ? Mathf.Max(3, sagSegments) : 2;
        line.useWorldSpace = true;
        line.numCapVertices = 3;
        line.numCornerVertices = 2;
        line.startWidth = width;
        line.endWidth = width;
        line.startColor = color;
        line.endColor = color;
        if (!string.IsNullOrWhiteSpace(sortingLayerName))
            line.sortingLayerName = sortingLayerName;
        line.sortingOrder = sortingOrder;

        if (material != null)
        {
            line.sharedMaterial = material;
        }
        else if (line.sharedMaterial == null)
        {
            var defaultShader = Shader.Find("Sprites/Default");
            if (defaultShader != null)
                line.sharedMaterial = new Material(defaultShader);
        }
    }

    public static void UpdateLinePositions(LineRenderer line, Vector3 startPos, Vector3 endPos, bool useSagCurve = true, int sagSegments = DefaultSagSegments, float baseSag = DefaultBaseSag, float sagPerUnit = DefaultSagPerUnit, float maxSag = DefaultMaxSag)
    {
        if (line == null) return;

        if (!useSagCurve)
        {
            if (line.positionCount != 2)
                line.positionCount = 2;
            line.SetPosition(0, startPos);
            line.SetPosition(1, endPos);
            return;
        }

        int segments = Mathf.Max(3, sagSegments);
        if (line.positionCount != segments)
            line.positionCount = segments;

        float distance = Vector2.Distance(startPos, endPos);
        float sag = Mathf.Min(maxSag, baseSag + distance * sagPerUnit);
        var controlPos = (startPos + endPos) * 0.5f + Vector3.down * sag;

        for (int i = 0; i < segments; i++)
        {
            float t = segments <= 1 ? 0f : (float)i / (segments - 1);
            line.SetPosition(i, EvaluateQuadraticBezier(startPos, controlPos, endPos, t));
        }
    }

    static Vector3 EvaluateQuadraticBezier(Vector3 a, Vector3 b, Vector3 c, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * b + t * t * c;
    }

    static void NotifyActiveLinksChanged()
    {
        ActiveLinksChanged?.Invoke();
    }
}
