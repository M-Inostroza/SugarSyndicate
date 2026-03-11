using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class BlueprintTask : DroneTaskTarget
{
    public enum BlueprintType { Belt, Machine, Junction, Pipe, DroneHQ, Cable, Pole }

    static int hqBlueprintCount;
    public static event Action<BlueprintType, BlueprintTask> BlueprintPlaced;
    public static event Action<BlueprintType, BlueprintTask> BlueprintCompleted;

    [Header("Setup")]
    [SerializeField] BlueprintType blueprintType = BlueprintType.Belt;
    [SerializeField] GameObject buildPrefab;
    [SerializeField] Vector2Int[] footprintCells = new Vector2Int[0];
    [SerializeField] Direction beltDirection = Direction.Right;
    [SerializeField] bool beltIsCurve = false;
    [SerializeField] Direction beltCurveFrom = Direction.Right;
    [SerializeField] Direction beltCurveTo = Direction.Right;
    [SerializeField] Direction cableDirection = Direction.Right;
    [SerializeField] bool cableIsCurve = false;
    [SerializeField] Direction cableCurveFrom = Direction.Right;
    [SerializeField] Direction cableCurveTo = Direction.Right;
    [SerializeField] Direction inA = Direction.None;
    [SerializeField] Direction inB = Direction.None;
    [SerializeField] Direction inC = Direction.None;
    [SerializeField] Direction outA = Direction.None;
    [SerializeField] Direction outB = Direction.None;
    [SerializeField] Vector2Int facingVec = new Vector2Int(1, 0);
    [SerializeField] Quaternion buildRotation = Quaternion.identity;
    [SerializeField, Min(0)] int buildCost = 0;
    [SerializeField] bool keepVisualOnComplete = false;
    [SerializeField] int sortingOrderOverride = int.MinValue;
    [SerializeField] bool nodeLinkCable = false;
    [SerializeField] Component cableStartNode;
    [SerializeField] Component cableEndNode;
    [SerializeField] float linkLineWidth = 0.09f;
    [SerializeField] Color linkLineColor = new Color(1f, 0.92f, 0.35f, 0.95f);
    [SerializeField] Material linkLineMaterial;
    [SerializeField] string linkSortingLayerName = "Default";
    [SerializeField] int linkSortingOrder = 5000;
    [SerializeField, Range(0.05f, 0.95f)] float nodeLinkCarryProgressThreshold = 0.35f;
    [SerializeField] bool blueprintLinkUsesSagCurve = true;
    [SerializeField, Min(3)] int blueprintLinkSagSegments = PowerLinkLine.DefaultSagSegments;
    [SerializeField, Min(0f)] float blueprintLinkBaseSag = PowerLinkLine.DefaultBaseSag;
    [SerializeField, Min(0f)] float blueprintLinkSagPerUnit = PowerLinkLine.DefaultSagPerUnit;
    [SerializeField, Min(0f)] float blueprintLinkMaxSag = PowerLinkLine.DefaultMaxSag;

    [Header("Visuals")]
    [SerializeField] Color blueprintTint = new Color(0.35f, 0.75f, 1f, 0.6f);

    bool countedAsHq;
    bool nodeLinkCarryStarted;
    LineRenderer blueprintLinkRenderer;
    List<Color> cachedColors;
    List<SpriteRenderer> cachedRenderers;

    public static bool HasHqBlueprint => hqBlueprintCount > 0;
    public BlueprintType Type => blueprintType;
    public bool IsHqBlueprint => blueprintType == BlueprintType.DroneHQ;
    public GameObject BuildPrefab => buildPrefab;
    public IReadOnlyList<Vector2Int> FootprintCells => footprintCells;
    public bool IsNodeLinkCableBlueprint => blueprintType == BlueprintType.Cable && nodeLinkCable;

    public void InitializeBelt(Vector2Int cell, Direction outDir, Quaternion rotation, GameObject prefab, int cost, float buildSeconds)
    {
        blueprintType = BlueprintType.Belt;
        footprintCells = new[] { cell };
        beltDirection = outDir;
        beltIsCurve = false;
        beltCurveFrom = outDir;
        beltCurveTo = outDir;
        buildRotation = rotation;
        buildPrefab = prefab;
        buildCost = cost;
        keepVisualOnComplete = false;
        RegisterBlueprint(buildSeconds);
    }

    public void InitializeCable(Vector2Int cell, Direction dir, Quaternion rotation, GameObject prefab, int cost, float buildSeconds)
    {
        blueprintType = BlueprintType.Cable;
        nodeLinkCable = false;
        nodeLinkCarryStarted = false;
        cableStartNode = null;
        cableEndNode = null;
        footprintCells = new[] { cell };
        cableDirection = dir;
        cableIsCurve = false;
        buildRotation = rotation;
        buildPrefab = prefab;
        buildCost = cost;
        keepVisualOnComplete = true;
        RegisterBlueprint(buildSeconds);
    }

    public void InitializeNodeLinkCable(Component fromNode, Component toNode, IReadOnlyList<Vector2Int> cableCells, float width, Color color, Material material, string sortingLayerName, int sortingOrder, int cost, float buildSeconds)
    {
        blueprintType = BlueprintType.Cable;
        nodeLinkCable = true;
        nodeLinkCarryStarted = false;
        cableStartNode = fromNode;
        cableEndNode = toNode;
        linkLineWidth = Mathf.Max(0.01f, width);
        linkLineColor = color;
        linkLineMaterial = material;
        linkSortingLayerName = sortingLayerName;
        linkSortingOrder = sortingOrder;
        footprintCells = cableCells != null ? new List<Vector2Int>(cableCells).ToArray() : new Vector2Int[0];
        buildRotation = Quaternion.identity;
        buildPrefab = null;
        buildCost = cost;
        keepVisualOnComplete = false;
        RegisterBlueprint(buildSeconds);
        SetWorkPosition(ResolveNodeWorldPosition(cableStartNode, transform.position.z));
    }

    public void InitializePole(Vector2Int cell, Quaternion rotation, GameObject prefab, int cost, float buildSeconds)
    {
        blueprintType = BlueprintType.Pole;
        footprintCells = new[] { cell };
        buildRotation = rotation;
        buildPrefab = prefab;
        buildCost = cost;
        keepVisualOnComplete = true;
        RegisterBlueprint(buildSeconds);
    }

    public void InitializeMachine(Vector2Int[] footprint, Vector2Int facing, Quaternion rotation, GameObject prefab, int cost, float buildSeconds, bool isHq, int sortingOrder)
    {
        blueprintType = isHq ? BlueprintType.DroneHQ : BlueprintType.Machine;
        footprintCells = footprint;
        facingVec = facing;
        buildRotation = rotation;
        buildPrefab = prefab;
        buildCost = cost;
        sortingOrderOverride = sortingOrder;
        keepVisualOnComplete = false;
        RegisterBlueprint(buildSeconds);
    }

    public void InitializePipe(Vector2Int cell, Quaternion rotation, GameObject prefab, int cost, float buildSeconds)
    {
        blueprintType = BlueprintType.Pipe;
        footprintCells = new[] { cell };
        buildRotation = rotation;
        buildPrefab = prefab;
        buildCost = cost;
        keepVisualOnComplete = false;
        RegisterBlueprint(buildSeconds);
    }

    public void InitializeJunction(Vector2Int cell, Direction inDirA, Direction inDirB, Direction inDirC, Direction outDirA, Direction outDirB, Quaternion rotation, GameObject prefab, int cost, float buildSeconds, bool keepVisual)
    {
        blueprintType = BlueprintType.Junction;
        footprintCells = new[] { cell };
        inA = inDirA;
        inB = inDirB;
        inC = inDirC;
        outA = outDirA;
        outB = outDirB;
        buildRotation = rotation;
        buildPrefab = prefab;
        buildCost = cost;
        keepVisualOnComplete = keepVisual;
        RegisterBlueprint(buildSeconds);
    }

    public void SetBlueprintTint(Color tint)
    {
        blueprintTint = tint;
        if (cachedRenderers == null || cachedColors == null)
            CacheOriginalColors();
        ApplyBlueprintTint();
    }

    public bool ContainsCell(Vector2Int cell)
    {
        if (footprintCells == null) return false;
        for (int i = 0; i < footprintCells.Length; i++)
        {
            if (footprintCells[i] == cell) return true;
        }
        return false;
    }

    public void CancelFromDelete()
    {
        CancelBlueprint();
    }

    void RegisterBlueprint(float buildSeconds)
    {
        if (blueprintType == BlueprintType.DroneHQ && !countedAsHq)
        {
            countedAsHq = true;
            hqBlueprintCount++;
        }

        MarkBlueprintCells(true);
        CacheOriginalColors();
        ApplyBlueprintTint();
        try { BlueprintPlaced?.Invoke(blueprintType, this); } catch { }

        var workPos = ComputeFootprintCenter();
        BeginTask(DroneTaskType.Build, buildSeconds, DroneTaskPriority.Normal, workPos);
        EnsureNodeLinkBlueprintVisual();
    }

    public void UpdateBeltDirection(Direction newDirection, Quaternion rotation)
    {
        if (blueprintType != BlueprintType.Belt) return;
        beltDirection = newDirection;
        beltIsCurve = false;
        buildRotation = rotation;
    }

    public void UpdateBeltCurve(Direction fromDirection, Direction toDirection, Quaternion rotation)
    {
        if (blueprintType != BlueprintType.Belt) return;
        beltIsCurve = true;
        beltCurveFrom = fromDirection;
        beltCurveTo = toDirection;
        beltDirection = toDirection;
        buildRotation = rotation;
    }

    public void UpdateCableDirection(Direction newDirection, Quaternion rotation)
    {
        if (blueprintType != BlueprintType.Cable) return;
        cableDirection = newDirection;
        cableIsCurve = false;
        buildRotation = rotation;
    }

    public void UpdateCableCurve(Direction fromDirection, Direction toDirection, Quaternion rotation)
    {
        if (blueprintType != BlueprintType.Cable) return;
        cableIsCurve = true;
        cableCurveFrom = fromDirection;
        cableCurveTo = toDirection;
        cableDirection = toDirection;
        buildRotation = rotation;
    }

    public bool TryGetDroneCableCarryVisual(out Vector3 anchorWorld, out float width, out Color color, out Material material, out string sortingLayerName, out int sortingOrder)
    {
        anchorWorld = Vector3.zero;
        width = 0f;
        color = Color.white;
        material = null;
        sortingLayerName = linkSortingLayerName;
        sortingOrder = linkSortingOrder;

        if (!IsNodeLinkCableBlueprint || !nodeLinkCarryStarted || IsComplete)
            return false;

        anchorWorld = ResolveNodeWorldPosition(cableStartNode, transform.position.z);
        width = linkLineWidth;
        color = linkLineColor;
        material = linkLineMaterial;
        return true;
    }

    void LateUpdate()
    {
        UpdateNodeLinkBlueprintVisual();
    }

    void MarkBlueprintCells(bool enabled)
    {
        if (blueprintType == BlueprintType.Cable || blueprintType == BlueprintType.Pole)
            return;
        var grid = GridService.Instance;
        if (grid == null || footprintCells == null) return;
        foreach (var c in footprintCells)
        {
            if (!grid.InBounds(c)) continue;
            if (enabled) grid.SetBlueprintCell(c);
            else
            {
                var cell = grid.GetCell(c);
                if (cell != null) cell.isBlueprint = false;
            }
        }
    }

    Vector3 ComputeFootprintCenter()
    {
        var grid = GridService.Instance;
        if (grid == null || footprintCells == null || footprintCells.Length == 0)
            return transform.position;

        Vector3 sum = Vector3.zero;
        foreach (var c in footprintCells)
            sum += grid.CellToWorld(c, transform.position.z);
        return sum / Mathf.Max(1, footprintCells.Length);
    }

    void CacheOriginalColors()
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        if (srs == null || srs.Length == 0) return;
        cachedRenderers = new List<SpriteRenderer>(srs.Length);
        cachedColors = new List<Color>(srs.Length);
        foreach (var sr in srs)
        {
            if (sr == null) continue;
            cachedRenderers.Add(sr);
            cachedColors.Add(sr.color);
        }
    }

    void ApplyBlueprintTint()
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        if (srs == null || srs.Length == 0) return;
        for (int i = 0; i < srs.Length; i++)
        {
            var sr = srs[i];
            if (sr == null) continue;
            var baseCol = (cachedRenderers != null && cachedColors != null && i < cachedRenderers.Count && i < cachedColors.Count && cachedRenderers[i] == sr)
                ? cachedColors[i]
                : sr.color;
            sr.color = new Color(baseCol.r * blueprintTint.r, baseCol.g * blueprintTint.g, baseCol.b * blueprintTint.b, Mathf.Clamp01(blueprintTint.a));
        }
    }

    void RestoreOriginalColors()
    {
        if (cachedRenderers == null || cachedColors == null) return;
        for (int i = 0; i < cachedRenderers.Count && i < cachedColors.Count; i++)
        {
            var sr = cachedRenderers[i];
            if (sr == null) continue;
            sr.color = cachedColors[i];
        }
    }

    void OnMouseDown()
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        if (Input.GetMouseButtonDown(1))
        {
            CancelBlueprint();
            return;
        }

        TogglePriority();
    }

    void CancelBlueprint()
    {
        CancelTask();
        MarkBlueprintCells(false);
        if (blueprintLinkRenderer != null)
            blueprintLinkRenderer.enabled = false;
        if (buildCost > 0) GameManager.Instance?.AddSweetCredits(buildCost);
        Destroy(gameObject);
    }

    public override void ApplyWork(float deltaSeconds)
    {
        base.ApplyWork(deltaSeconds);

        if (!IsNodeLinkCableBlueprint || nodeLinkCarryStarted || IsComplete)
            return;

        if (Progress01 < Mathf.Clamp01(nodeLinkCarryProgressThreshold))
            return;

        nodeLinkCarryStarted = true;
        SetWorkPosition(ResolveNodeWorldPosition(cableEndNode, transform.position.z));
    }

    protected override void OnTaskCompleted()
    {
        MarkBlueprintCells(false);

        switch (blueprintType)
        {
            case BlueprintType.Belt:
                CompleteBelt();
                break;
            case BlueprintType.Machine:
            case BlueprintType.DroneHQ:
                CompleteMachine(blueprintType == BlueprintType.DroneHQ);
                break;
            case BlueprintType.Junction:
                CompleteJunction();
                break;
            case BlueprintType.Pipe:
                CompletePipe();
                break;
            case BlueprintType.Cable:
                CompleteCable();
                break;
            case BlueprintType.Pole:
                CompletePole();
                break;
        }

        try { BlueprintCompleted?.Invoke(blueprintType, this); } catch { }

        if (keepVisualOnComplete)
        {
            RestoreOriginalColors();
            Destroy(this);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void CompleteBelt()
    {
        var grid = GridService.Instance;
        if (grid == null || footprintCells == null || footprintCells.Length == 0)
            return;

        var cell = footprintCells[0];
        if (buildPrefab == null)
        {
            grid.SetBeltCell(cell, DirectionUtil.Opposite(beltDirection), beltDirection);
            BeltSimulationService.Instance?.RegisterCell(cell);
            return;
        }

        var pos = grid.CellToWorld(cell, transform.position.z);
        var parent = ContainerLocator.GetBeltContainer();
        var go = parent != null ? Instantiate(buildPrefab, pos, buildRotation, parent)
                                : Instantiate(buildPrefab, pos, buildRotation);
        var conv = go.GetComponent<Conveyor>();
        if (conv != null)
        {
            if (beltIsCurve)
                conv.ApplyCurveSprite(beltCurveFrom, beltCurveTo);
            else
                conv.ApplyStraightSprite(beltDirection);
            conv.isGhost = false;
        }

        BeltSimulationService.Instance?.RegisterCell(cell);

        var repairable = go.GetComponent<Repairable>();
        if (repairable == null) repairable = go.AddComponent<Repairable>();
        repairable.Initialize(new[] { cell });
    }

    void CompleteCable()
    {
        if (IsNodeLinkCableBlueprint)
        {
            CompleteNodeLinkCable();
            return;
        }

        if (footprintCells == null || footprintCells.Length == 0)
            return;

        var cable = GetComponent<PowerCable>();
        if (cable != null)
        {
            if (cableIsCurve)
                cable.SetCurve(cableCurveFrom, cableCurveTo);
            else
                cable.SetDirection(cableDirection);
            cable.ActivateFromBlueprint();
            return;
        }

        if (buildPrefab == null)
            return;

        var grid = GridService.Instance;
        if (grid == null)
            return;

        var cell = footprintCells[0];
        var pos = grid.CellToWorld(cell, transform.position.z);
        var go = Instantiate(buildPrefab, pos, buildRotation);
        var powerCable = go.GetComponent<PowerCable>();
        if (powerCable != null)
        {
            if (cableIsCurve)
                powerCable.SetCurve(cableCurveFrom, cableCurveTo);
            else
                powerCable.SetDirection(cableDirection);
        }
    }

    void CompleteNodeLinkCable()
    {
        if (!PowerNodeUtil.IsConnectableNode(cableStartNode) || !PowerNodeUtil.IsConnectableNode(cableEndNode))
            return;

        if (PowerLinkLine.HasLinkBetween(cableStartNode, cableEndNode))
            return;

        List<Vector2Int> path = null;
        if (footprintCells != null && footprintCells.Length > 0)
            path = new List<Vector2Int>(footprintCells);
        if ((path == null || path.Count == 0) && !PowerNodeUtil.TryBuildCableCells(cableStartNode, cableEndNode, out path))
            return;

        var go = new GameObject($"PowerLink_{cableStartNode.name}_{cableEndNode.name}");
        if (transform.parent != null)
            go.transform.SetParent(transform.parent, true);

        float z = Mathf.Min(cableStartNode.transform.position.z, cableEndNode.transform.position.z);
        go.transform.position = new Vector3(0f, 0f, z);

        var tag = go.AddComponent<BuildCostTag>();
        tag.Cost = buildCost;

        var link = go.AddComponent<PowerLinkLine>();
        if (!link.Initialize(cableStartNode, cableEndNode, linkLineWidth, linkLineColor, linkLineMaterial, linkSortingLayerName, linkSortingOrder, path))
            Destroy(go);
    }

    void CompletePole()
    {
        if (footprintCells == null || footprintCells.Length == 0)
            return;

        var pole = GetComponent<PowerPole>();
        if (pole != null)
        {
            pole.ActivateFromBlueprint();
            return;
        }

        if (buildPrefab == null)
            return;

        var grid = GridService.Instance;
        if (grid == null)
            return;

        var cell = footprintCells[0];
        var pos = grid.CellToWorld(cell, transform.position.z);
        Instantiate(buildPrefab, pos, buildRotation);
    }

    void CompleteMachine(bool isHq)
    {
        var grid = GridService.Instance;
        if (grid == null || footprintCells == null || footprintCells.Length == 0)
            return;

        foreach (var c in footprintCells)
        {
            grid.SetMachineCell(c);
        }

        if (buildPrefab == null)
            return;

        var pos = ComputeFootprintCenter();
        var go = Instantiate(buildPrefab, pos, buildRotation);
        var tag = go.GetComponent<BuildCostTag>();
        if (tag == null) tag = go.AddComponent<BuildCostTag>();
        tag.Cost = buildCost;

        if (sortingOrderOverride != int.MinValue)
        {
            var group = go.GetComponentInChildren<UnityEngine.Rendering.SortingGroup>(true);
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            if (group != null)
            {
                // Keep child sprite relative ordering (e.g. overlays/attachments), only move the group.
                group.sortingOrder = sortingOrderOverride;
            }
            else if (srs != null && srs.Length > 0)
            {
                int minOrder = int.MaxValue;
                for (int i = 0; i < srs.Length; i++)
                {
                    if (srs[i] == null) continue;
                    if (srs[i].sortingOrder < minOrder) minOrder = srs[i].sortingOrder;
                }
                if (minOrder == int.MaxValue) minOrder = 0;
                int delta = sortingOrderOverride - minOrder;
                for (int i = 0; i < srs.Length; i++)
                {
                    if (srs[i] == null) continue;
                    srs[i].sortingOrder += delta;
                }
            }
        }

        var press = go.GetComponent<PressMachine>();
        if (press != null) press.facingVec = facingVec;
        var colorizer = go.GetComponent<ColorizerMachine>();
        if (colorizer != null) colorizer.facingVec = facingVec;
        var pump = go.GetComponent<WaterPump>();
        if (pump != null) pump.facingVec = facingVec;
        var mine = go.GetComponent<SugarMine>();
        if (mine != null) mine.SetFacing(facingVec);
        var storage = go.GetComponent<StorageContainerMachine>();
        if (storage != null) storage.facingVec = facingVec;
        var solar = go.GetComponent<SolarPanelMachine>();
        if (solar != null) solar.facingVec = facingVec;

        var repairable = go.GetComponent<Repairable>();
        if (repairable == null) repairable = go.AddComponent<Repairable>();
        repairable.Initialize(footprintCells);
    }

    void CompletePipe()
    {
        var grid = GridService.Instance;
        if (grid == null || footprintCells == null || footprintCells.Length == 0)
            return;

        var cell = footprintCells[0];
        grid.SetMachineCell(cell);

        if (buildPrefab == null)
            return;

        var pos = grid.CellToWorld(cell, transform.position.z);
        var go = Instantiate(buildPrefab, pos, buildRotation);
        var tag = go.GetComponent<BuildCostTag>();
        if (tag == null) tag = go.AddComponent<BuildCostTag>();
        tag.Cost = buildCost;
    }

    void CompleteJunction()
    {
        var grid = GridService.Instance;
        if (grid == null || footprintCells == null || footprintCells.Length == 0)
            return;

        var cell = footprintCells[0];
        grid.SetJunctionCell(cell, inA, inB, inC, outA, outB);

        var belt = BeltSimulationService.Instance;
        belt?.RegisterCell(cell);

        if (buildPrefab != null)
        {
            var pos = grid.CellToWorld(cell, transform.position.z);
            var parent = ContainerLocator.GetBeltContainer();
            var go = parent != null ? Instantiate(buildPrefab, pos, buildRotation, parent)
                                    : Instantiate(buildPrefab, pos, buildRotation);
            var tag = go.GetComponent<BuildCostTag>();
            if (tag == null) tag = go.AddComponent<BuildCostTag>();
            tag.Cost = buildCost;

            var repairable = go.GetComponent<Repairable>();
            if (repairable == null) repairable = go.AddComponent<Repairable>();
            repairable.Initialize(new[] { cell });
        }
    }

    protected override void OnDestroy()
    {
        if (blueprintLinkRenderer != null)
            Destroy(blueprintLinkRenderer.gameObject);
        base.OnDestroy();
        if (countedAsHq) hqBlueprintCount = Mathf.Max(0, hqBlueprintCount - 1);
    }

    Vector3 ResolveNodeWorldPosition(Component node, float fallbackZ)
    {
        if (node == null)
            return transform.position;

        float z = node.transform.position.z;
        if (Mathf.Approximately(z, 0f))
            z = fallbackZ;
        return PowerNodeUtil.GetNodeWorldPosition(node, z);
    }

    void EnsureNodeLinkBlueprintVisual()
    {
        if (!IsNodeLinkCableBlueprint || blueprintLinkRenderer != null)
            return;

        var lineObject = new GameObject("NodeLinkBlueprintVisual");
        lineObject.transform.SetParent(transform, false);
        blueprintLinkRenderer = lineObject.AddComponent<LineRenderer>();
        PowerLinkLine.ConfigureLineRenderer(blueprintLinkRenderer, linkLineWidth, blueprintTint, linkLineMaterial, linkSortingLayerName, linkSortingOrder, blueprintLinkUsesSagCurve, blueprintLinkSagSegments);
        blueprintLinkRenderer.enabled = true;
    }

    void UpdateNodeLinkBlueprintVisual()
    {
        if (!IsNodeLinkCableBlueprint || IsComplete)
        {
            if (blueprintLinkRenderer != null)
                blueprintLinkRenderer.enabled = false;
            return;
        }

        EnsureNodeLinkBlueprintVisual();
        if (blueprintLinkRenderer == null || !PowerNodeUtil.IsConnectableNode(cableStartNode) || !PowerNodeUtil.IsConnectableNode(cableEndNode))
            return;

        float z = transform.position.z;
        var startPos = ResolveNodeWorldPosition(cableStartNode, z);
        var endPos = ResolveNodeWorldPosition(cableEndNode, z);
        PowerLinkLine.ConfigureLineRenderer(blueprintLinkRenderer, linkLineWidth, blueprintTint, linkLineMaterial, linkSortingLayerName, linkSortingOrder, blueprintLinkUsesSagCurve, blueprintLinkSagSegments);
        PowerLinkLine.UpdateLinePositions(blueprintLinkRenderer, startPos, endPos, blueprintLinkUsesSagCurve, blueprintLinkSagSegments, blueprintLinkBaseSag, blueprintLinkSagPerUnit, blueprintLinkMaxSag);
        blueprintLinkRenderer.enabled = true;
    }
}
