using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class BlueprintTask : DroneTaskTarget
{
    public enum BlueprintType { Belt, Machine, Junction, Pipe, DroneHQ }

    static int hqBlueprintCount;

    [Header("Setup")]
    [SerializeField] BlueprintType blueprintType = BlueprintType.Belt;
    [SerializeField] GameObject buildPrefab;
    [SerializeField] Vector2Int[] footprintCells = new Vector2Int[0];
    [SerializeField] Direction beltDirection = Direction.Right;
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

    [Header("Visuals")]
    [SerializeField] Color blueprintTint = new Color(0.35f, 0.75f, 1f, 0.6f);

    bool countedAsHq;
    List<Color> cachedColors;
    List<SpriteRenderer> cachedRenderers;

    public static bool HasHqBlueprint => hqBlueprintCount > 0;

    public void InitializeBelt(Vector2Int cell, Direction outDir, Quaternion rotation, GameObject prefab, int cost, float buildSeconds)
    {
        blueprintType = BlueprintType.Belt;
        footprintCells = new[] { cell };
        beltDirection = outDir;
        buildRotation = rotation;
        buildPrefab = prefab;
        buildCost = cost;
        keepVisualOnComplete = false;
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

        var workPos = ComputeFootprintCenter();
        BeginTask(DroneTaskType.Build, buildSeconds, DroneTaskPriority.Normal, workPos);
    }

    public void UpdateBeltDirection(Direction newDirection, Quaternion rotation)
    {
        if (blueprintType != BlueprintType.Belt) return;
        beltDirection = newDirection;
        buildRotation = rotation;
    }

    void MarkBlueprintCells(bool enabled)
    {
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
        foreach (var sr in srs)
        {
            if (sr == null) continue;
            var baseCol = sr.color;
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
        if (buildCost > 0) GameManager.Instance?.AddSweetCredits(buildCost);
        Destroy(gameObject);
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
        }

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
            conv.direction = beltDirection;
            conv.isGhost = false;
        }

        BeltSimulationService.Instance?.RegisterCell(cell);

        var repairable = go.GetComponent<Repairable>();
        if (repairable == null) repairable = go.AddComponent<Repairable>();
        repairable.Initialize(new[] { cell });
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
            if (group != null) group.sortingOrder = sortingOrderOverride;
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs) sr.sortingOrder = sortingOrderOverride;
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
        base.OnDestroy();
        if (countedAsHq) hqBlueprintCount = Mathf.Max(0, hqBlueprintCount - 1);
    }
}
