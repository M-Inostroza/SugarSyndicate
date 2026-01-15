using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class PowerBuildManager : MonoBehaviour
{
    [Header("Prefabs & Parents")]
    [SerializeField] GameObject cablePrefab;
    [SerializeField] GameObject polePrefab;
    [SerializeField] Transform placeParent;
    [SerializeField] UndergroundViewController undergroundView;

    [Header("Placement")]
    [SerializeField] bool allowCableDrag = true;
    [SerializeField, Min(0.01f)] float cableBuildSeconds = 0.4f;
    [SerializeField, Min(0.01f)] float poleBuildSeconds = 0.6f;
    [SerializeField, Min(0)] int cableCost = 0;
    [SerializeField, Min(0)] int poleCost = 0;

    enum Mode { None, Cable, Pole }
    Mode mode = Mode.None;

    Camera cam;
    bool isMouseDown;
    Vector2Int lastPlacedCell;
    PowerCable lastPlacedCable;
    readonly List<Vector2Int> dragPath = new();
    readonly Dictionary<Vector2Int, PowerCable> dragCables = new();
    readonly Dictionary<Vector2Int, int> dragDistances = new();

    static readonly Vector2Int[] NeighborDirs =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0),
    };

    void Awake()
    {
        cam = Camera.main;
        if (cam == null) cam = Camera.current;
    }

    void OnEnable()
    {
        BuildSelectionNotifier.OnSelectionChanged += HandleSelectionChanged;
    }

    void OnDisable()
    {
        BuildSelectionNotifier.OnSelectionChanged -= HandleSelectionChanged;
    }

    void Update()
    {
        if (mode == Mode.None) return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            StopPlacing();
            return;
        }

        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        if (Input.GetMouseButtonDown(0))
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            var cell = GetMouseCell();
            if (!cell.HasValue) return;

            isMouseDown = true;
            ResetDragPath();

            if (mode == Mode.Pole)
            {
                if (!TryPlacePole(cell.Value))
                    isMouseDown = false;
                return;
            }

            if (!TryGetStartDragDistance(cell.Value, out var startDistance) || !IsWithinLengthLimit(startDistance))
            {
                isMouseDown = false;
                return;
            }

            if (TryPlaceCable(cell.Value, null, out lastPlacedCable))
            {
                lastPlacedCell = cell.Value;
                AddDragCable(cell.Value, lastPlacedCable, startDistance);
                PinUndergroundView();
            }
            else
                isMouseDown = false;
        }

        if (mode == Mode.Cable && allowCableDrag && isMouseDown && Input.GetMouseButton(0))
        {
            var cell = GetMouseCell();
            if (!cell.HasValue) return;
            if (cell.Value == lastPlacedCell) return;

            if (TryBacktrack(cell.Value))
                return;

            Vector2Int from = lastPlacedCell;
            Vector2Int to = cell.Value;
            while (from != to)
            {
                var delta = to - from;
                Vector2Int step;
                if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                    step = new Vector2Int(Math.Sign(delta.x), 0);
                else
                    step = new Vector2Int(0, Math.Sign(delta.y));

                var next = from + step;
                if (Mathf.Abs(next.x - from.x) + Mathf.Abs(next.y - from.y) != 1) break;

                var dir = DeltaToDirection(step);
                if (!TryGetNextDragDistance(next, from, out var nextDistance) || !IsWithinLengthLimit(nextDistance))
                    break;
                if (!TryPlaceCable(next, dir, out var newCable))
                    break;

                if (lastPlacedCable != null)
                    UpdateCableVisualForTurn(lastPlacedCable, dir);
                lastPlacedCable = newCable;
                AddDragCable(next, newCable, nextDistance);
                PinUndergroundView();

                from = next;
                lastPlacedCell = from;

                if (from == to) break;
            }
        }

        if (isMouseDown && Input.GetMouseButtonUp(0))
        {
            isMouseDown = false;
            lastPlacedCable = null;
            ResetDragPath();
        }
    }

    public void StartPlacingCable()
    {
        TryStopOtherTools();
        mode = Mode.Cable;
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        try { BuildModeController.SetToolActive(true); } catch { }
        BuildSelectionNotifier.Notify("PowerCable");
    }

    public void StartPlacingPole()
    {
        TryStopOtherTools();
        mode = Mode.Pole;
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        try { BuildModeController.SetToolActive(true); } catch { }
        BuildSelectionNotifier.Notify("PowerPole");
    }

    public void StopPlacing()
    {
        StopPlacingInternal(setGameState: true, setToolActive: true, notifySelection: true);
    }

    void StopPlacingInternal(bool setGameState, bool setToolActive, bool notifySelection)
    {
        mode = Mode.None;
        isMouseDown = false;
        lastPlacedCable = null;
        ResetDragPath();
        if (setGameState && GameManager.Instance != null) GameManager.Instance.SetState(GameState.Play);
        if (setToolActive)
        {
            try { BuildModeController.SetToolActive(false); } catch { }
        }
        if (notifySelection)
            BuildSelectionNotifier.Notify(null);
    }

    void HandleSelectionChanged(string selectionName)
    {
        if (string.Equals(selectionName, "PowerCable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectionName, "PowerPole", StringComparison.OrdinalIgnoreCase))
            return;

        if (mode != Mode.None)
            StopPlacingInternal(setGameState: false, setToolActive: false, notifySelection: false);
    }

    void TryStopOtherTools()
    {
        try
        {
            var bmc = FindAnyObjectByType<BuildModeController>();
            if (bmc != null)
                bmc.ClearActiveTool();
        }
        catch { }
    }

    Vector2Int? GetMouseCell()
    {
        var gs = GridService.Instance;
        if (gs == null) return null;
        var worldPoint = cam.ScreenToWorldPoint(Input.mousePosition);
        worldPoint.z = 0f;
        var cell = gs.WorldToCell(worldPoint);
        if (!gs.InBounds(cell)) return null;
        return cell;
    }

    bool TryPlaceCable(Vector2Int cell, Direction? direction, out PowerCable placedCable)
    {
        placedCable = null;
        if (!CanPlaceCableAt(cell)) return false;
        if (cablePrefab == null)
        {
            Debug.LogWarning("[PowerBuildManager] Cable prefab not assigned.");
            return false;
        }

        if (!TrySpendCost(cableCost))
            return false;

        var go = SpawnAt(cell, cablePrefab);
        if (go == null)
        {
            RefundCost(cableCost);
            return false;
        }
        var cable = go.GetComponent<PowerCable>();
        if (cable == null)
        {
            Debug.LogWarning("[PowerBuildManager] Cable prefab missing PowerCable component.");
            RefundCost(cableCost);
            Destroy(go);
            return false;
        }

        cable.SetGhost(true);
        if (direction.HasValue)
            cable.SetDirection(direction.Value);

        var task = cable.GetComponent<BlueprintTask>();
        if (task == null) task = cable.gameObject.AddComponent<BlueprintTask>();
        var dir = direction ?? Direction.Right;
        task.InitializeCable(cell, dir, cable.transform.rotation, cablePrefab, cableCost, cableBuildSeconds);
        placedCable = cable;
        return true;
    }

    bool TryPlacePole(Vector2Int cell)
    {
        if (!CanPlacePoleAt(cell)) return false;
        if (polePrefab == null)
        {
            Debug.LogWarning("[PowerBuildManager] Pole prefab not assigned.");
            return false;
        }

        if (!TrySpendCost(poleCost))
            return false;

        var go = SpawnAt(cell, polePrefab);
        if (go == null)
        {
            RefundCost(poleCost);
            return false;
        }

        var pole = go.GetComponent<PowerPole>();
        if (pole == null)
        {
            Debug.LogWarning("[PowerBuildManager] Pole prefab missing PowerPole component.");
            RefundCost(poleCost);
            Destroy(go);
            return false;
        }
        pole.SetGhost(true);

        var task = go.GetComponent<BlueprintTask>();
        if (task == null) task = go.AddComponent<BlueprintTask>();
        task.InitializePole(cell, go.transform.rotation, polePrefab, poleCost, poleBuildSeconds);
        PinUndergroundView();
        return true;
    }

    bool CanPlaceCableAt(Vector2Int cell)
    {
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null) return true;
        return power.CanPlaceCableAt(cell) && !IsMachineCell(cell);
    }

    bool CanPlacePoleAt(Vector2Int cell)
    {
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null) return true;
        return power.CanPlacePoleAt(cell) && !IsMachineCell(cell);
    }

    GameObject SpawnAt(Vector2Int cell, GameObject prefab)
    {
        var gs = GridService.Instance;
        if (gs == null) return null;
        float z = prefab.transform.position.z;
        var pos = gs.CellToWorld(cell, z);
        var parent = placeParent != null ? placeParent : null;
        return Instantiate(prefab, pos, Quaternion.identity, parent);
    }

    void UpdateCableDirection(PowerCable cable, Direction dir)
    {
        if (cable == null) return;
        cable.SetDirection(dir);
        var task = cable.GetComponent<BlueprintTask>();
        if (task != null)
            task.UpdateCableDirection(dir, cable.transform.rotation);
    }

    void UpdateCableVisualForTurn(PowerCable cable, Direction outgoing)
    {
        if (cable == null) return;
        Direction travelIn = outgoing;
        if (dragPath.Count >= 2)
        {
            var prevCell = dragPath[dragPath.Count - 2];
            var curCell = dragPath[dragPath.Count - 1];
            travelIn = DeltaToDirection(curCell - prevCell);
        }

        if (travelIn != outgoing)
        {
            var fromSide = DirectionUtil.Opposite(travelIn);
            cable.SetCurve(fromSide, outgoing);
            var task = cable.GetComponent<BlueprintTask>();
            if (task != null)
                task.UpdateCableCurve(fromSide, outgoing, cable.transform.rotation);
        }
        else
        {
            UpdateCableDirection(cable, outgoing);
        }
    }

    bool TryBacktrack(Vector2Int cell)
    {
        int index = dragPath.IndexOf(cell);
        if (index < 0 || index == dragPath.Count - 1) return false;

        for (int i = dragPath.Count - 1; i > index; i--)
        {
            var removeCell = dragPath[i];
            RemoveDragCable(removeCell);
            dragPath.RemoveAt(i);
        }

        lastPlacedCell = cell;
        dragCables.TryGetValue(cell, out lastPlacedCable);
        UpdateTailDirectionFromPath();
        return true;
    }

    void UpdateTailDirectionFromPath()
    {
        if (dragPath.Count < 2) return;
        var prevCell = dragPath[dragPath.Count - 2];
        var lastCell = dragPath[dragPath.Count - 1];
        if (!dragCables.TryGetValue(lastCell, out var lastCable) || lastCable == null) return;
        var dir = DeltaToDirection(lastCell - prevCell);
        UpdateCableDirection(lastCable, dir);
    }

    void AddDragCable(Vector2Int cell, PowerCable cable, int distance)
    {
        if (cable == null) return;
        dragCables[cell] = cable;
        dragDistances[cell] = distance;
        dragPath.Add(cell);
    }

    void RemoveDragCable(Vector2Int cell)
    {
        if (dragCables.TryGetValue(cell, out var cable) && cable != null)
        {
            var task = cable.GetComponent<BlueprintTask>();
            if (task != null) task.CancelFromDelete();
            else Destroy(cable.gameObject);
        }
        dragCables.Remove(cell);
        dragDistances.Remove(cell);
    }

    void ResetDragPath()
    {
        dragPath.Clear();
        dragCables.Clear();
        dragDistances.Clear();
    }

    bool TryGetStartDragDistance(Vector2Int cell, out int distance)
    {
        distance = 0;
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null)
        {
            distance = 1;
            return true;
        }

        int best = int.MaxValue;
        if (power.IsAdjacentToSourceCell(cell) || power.IsAdjacentToConnectedPole(cell))
            best = 1;

        foreach (var dir in NeighborDirs)
        {
            if (power.TryGetPlacementDistance(cell + dir, out var dist))
                best = Mathf.Min(best, dist + 1);
        }

        if (best == int.MaxValue) return false;
        distance = best;
        return true;
    }

    bool TryGetNextDragDistance(Vector2Int cell, Vector2Int fromCell, out int distance)
    {
        if (!dragDistances.TryGetValue(fromCell, out var prev))
            return TryGetStartDragDistance(cell, out distance);

        distance = prev + 1;
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power != null && (power.IsAdjacentToSourceCell(cell) || power.IsAdjacentToConnectedPole(cell)))
            distance = Mathf.Min(distance, 1);
        return true;
    }

    bool IsWithinLengthLimit(int distance)
    {
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null) return true;
        return distance <= power.MaxCableLength;
    }

    void PinUndergroundView()
    {
        if (undergroundView == null)
            undergroundView = FindAnyObjectByType<UndergroundViewController>();
        undergroundView?.ShowUndergroundView();
    }

    bool IsMachineCell(Vector2Int cell)
    {
        var grid = GridService.Instance;
        if (grid != null)
        {
            var cellData = grid.GetCell(cell);
            if (cellData != null && (cellData.type == GridService.CellType.Machine || cellData.hasMachine))
                return true;
        }

        if (MachineRegistry.TryGet(cell, out _))
            return true;

        if (grid != null && DroneHQ.Instance != null)
        {
            var hqCell = grid.WorldToCell(DroneHQ.Instance.transform.position);
            if (hqCell == cell) return true;
        }

        return false;
    }

    static Direction DeltaToDirection(Vector2Int d)
    {
        if (d.x > 0) return Direction.Right;
        if (d.x < 0) return Direction.Left;
        if (d.y > 0) return Direction.Up;
        return Direction.Down;
    }

    bool TrySpendCost(int amount)
    {
        if (amount <= 0) return true;
        var gm = GameManager.Instance;
        if (gm == null) return true;
        if (gm.TrySpendSweetCredits(amount)) return true;
        Debug.LogWarning($"[PowerBuildManager] Not enough money. Cost: {amount}.");
        return false;
    }

    void RefundCost(int amount)
    {
        if (amount <= 0) return;
        GameManager.Instance?.AddSweetCredits(amount);
    }
}
