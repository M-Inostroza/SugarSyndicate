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

    [Header("Placement Feedback")]
    [SerializeField] bool showCableLengthLimitHint = true;
    [SerializeField, TextArea(1, 2)] string cableLengthLimitMessage = "Cable limit reached";
    [SerializeField, Min(0f)] float cableHintCooldownSeconds = 0.2f;

    [Header("Node Link Cables")]
    [SerializeField] bool useNodeLinkCables = true;
    [SerializeField, Min(0.01f)] float cableLineWidth = 0.09f;
    [SerializeField] Color cableLineColor = new Color(1f, 0.92f, 0.35f, 0.95f);
    [SerializeField] Material cableLineMaterial;
    [SerializeField] string cableSortingLayerName = "Default";
    [SerializeField] int cableSortingOrder = 5000;
    [SerializeField, TextArea(1, 2)] string cableNodeRequiredMessage = "Select a machine or power node to connect.";
    [SerializeField, TextArea(1, 2)] string cableAlreadyConnectedMessage = "These nodes are already connected.";
    [SerializeField, TextArea(1, 2)] string cableInvalidDirectionMessage = "Connect from a power source/node to a machine/node.";
    [SerializeField, TextArea(1, 2)] string consumerInputLimitMessage = "This machine already has a power cable connected.";
    [SerializeField, TextArea(1, 2)] string providerOutputLimitMessage = "This power provider already has an output cable.";
    [SerializeField, Min(1)] int powerNodeMaxConnections = 4;
    [SerializeField, TextArea(1, 2)] string powerNodeLimitMessage = "This power node can only connect up to 4 cables.";
    [SerializeField, TextArea(1, 2)] string surfaceOnlyMessage = "Power lines and nodes can only be built on the surface.";

    enum Mode { None, Cable, Pole }
    Mode mode = Mode.None;

    Camera cam;
    bool isMouseDown;
    Vector2Int lastPlacedCell;
    PowerCable lastPlacedCable;
    readonly List<Vector2Int> dragPath = new();
    readonly Dictionary<Vector2Int, PowerCable> dragCables = new();
    readonly Dictionary<Vector2Int, int> dragDistances = new();
    bool isDeleteDragging;
    Vector2Int lastDeleteCell = new Vector2Int(int.MinValue, int.MinValue);
    float lastCableLimitHintTime = -999f;
    Component pendingCableStartNode;
    bool isNodeLinkDragging;
    LineRenderer nodeLinkPreviewLine;
    static PowerBuildManager activeInstance;

    public static bool AllowCameraPanWithCableTool { get; private set; }
    public static bool IsCameraPanBlockedByCableDrag { get; private set; }

    public static bool ShouldBlockCameraPanInput()
    {
        if (IsCameraPanBlockedByCableDrag) return true;
        if (activeInstance == null) return false;
        return activeInstance.ShouldBlockCameraPanInputInternal();
    }

    static readonly Vector2Int[] NeighborDirs =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0),
    };

    void Awake()
    {
        activeInstance = this;
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
        if (activeInstance == this)
            activeInstance = null;
        EndNodeLinkDrag(clearStartNode: true);
        AllowCameraPanWithCableTool = false;
        IsCameraPanBlockedByCableDrag = false;
    }

    void Update()
    {
        if (IsDeleteModeActive())
        {
            HandleDeleteModeInput();
            return;
        }

        if (mode == Mode.None) return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            StopPlacing();
            return;
        }

        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        if (mode == Mode.Cable && useNodeLinkCables)
        {
            HandleNodeLinkCableInput();
            return;
        }

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

            if (TryGetExistingCableAtCell(cell.Value, out var existingCable))
            {
                if (!TryGetStartDragDistanceFromExisting(cell.Value, out var startDistance))
                {
                    isMouseDown = false;
                    return;
                }
                if (!IsWithinLengthLimit(startDistance))
                {
                    ShowCableLengthLimitHint(cell.Value);
                    isMouseDown = false;
                    return;
                }

                lastPlacedCell = cell.Value;
                lastPlacedCable = existingCable;
                AddDragCable(cell.Value, existingCable, startDistance);
                PinUndergroundView();
                UpdateDragCableConnections();
                return;
            }

            if (!TryGetStartDragDistance(cell.Value, out var newStartDistance))
            {
                isMouseDown = false;
                return;
            }
            if (!IsWithinLengthLimit(newStartDistance))
            {
                ShowCableLengthLimitHint(cell.Value);
                isMouseDown = false;
                return;
            }

            if (TryPlaceCable(cell.Value, null, out lastPlacedCable))
            {
                lastPlacedCell = cell.Value;
                AddDragCable(cell.Value, lastPlacedCable, newStartDistance);
                PinUndergroundView();
                UpdateDragCableConnections();
            }
            else
                isMouseDown = false;
        }

        if (mode == Mode.Cable && allowCableDrag && isMouseDown && Input.GetMouseButton(0))
        {
            var cell = GetMouseCell();
            if (!cell.HasValue) return;
            if (cell.Value == lastPlacedCell) return;
            if (dragPath.Count == 0) return;

            var start = dragPath[0];
            var targetPath = FindCablePath(start, cell.Value);
            if (targetPath == null || targetPath.Count == 0) return;

            ApplyDragPath(targetPath);
        }

        if (isMouseDown && Input.GetMouseButtonUp(0))
        {
            isMouseDown = false;
            lastPlacedCable = null;
            ResetDragPath();
        }
    }

    void HandleDeleteModeInput()
    {
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        if (Input.GetMouseButtonDown(0))
        {
            isDeleteDragging = true;
            lastDeleteCell = new Vector2Int(int.MinValue, int.MinValue);
        }

        if (Input.GetMouseButtonUp(0))
        {
            isDeleteDragging = false;
            return;
        }

        if (!isDeleteDragging || !Input.GetMouseButton(0))
            return;

        var cell = GetMouseCell();
        if (!cell.HasValue) return;
        if (cell.Value == lastDeleteCell) return;

        TryDeletePowerAtCell(cell.Value);
        lastDeleteCell = cell.Value;
    }

    bool TryDeletePowerAtCell(Vector2Int cell)
    {
        if (TryDeleteLinkAtCell(cell)) return true;
        if (TryDeleteCableAtCell(cell)) return true;
        if (TryDeletePoleAtCell(cell)) return true;
        return false;
    }

    bool TryDeleteLinkAtCell(Vector2Int cell)
    {
        if (!PowerLinkLine.TryGetAtCell(cell, out var link) || link == null) return false;
        DeletePowerObject(link.gameObject, cableCost);
        return true;
    }

    bool TryDeleteCableAtCell(Vector2Int cell)
    {
        var cable = FindPowerCableAtCell(cell);
        if (cable == null) return false;
        DeletePowerObject(cable.gameObject, cableCost);
        return true;
    }

    bool TryDeletePoleAtCell(Vector2Int cell)
    {
        var pole = FindPowerPoleAtCell(cell);
        if (pole == null) return false;
        DeletePowerObject(pole.gameObject, poleCost);
        return true;
    }

    PowerCable FindPowerCableAtCell(Vector2Int cell)
    {
        if (PowerCable.TryGetAtCell(cell, out var cached))
            return cached;
        var grid = GridService.Instance;
        if (grid == null) return null;
        var all = FindObjectsByType<PowerCable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var cable in all)
        {
            if (cable == null) continue;
            if (grid.WorldToCell(cable.transform.position) == cell)
                return cable;
        }
        return null;
    }

    bool TryGetExistingCableAtCell(Vector2Int cell, out PowerCable cable)
    {
        cable = FindPowerCableAtCell(cell);
        return cable != null;
    }

    PowerPole FindPowerPoleAtCell(Vector2Int cell)
    {
        var grid = GridService.Instance;
        if (grid == null) return null;
        var all = FindObjectsByType<PowerPole>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var pole in all)
        {
            if (pole == null) continue;
            if (grid.WorldToCell(pole.transform.position) == cell)
                return pole;
        }
        return null;
    }

    void DeletePowerObject(GameObject go, int fallbackCost)
    {
        if (go == null) return;
        var task = go.GetComponent<BlueprintTask>();
        if (task != null)
        {
            task.CancelFromDelete();
            return;
        }

        TryRefundCost(go, fallbackCost);
        Destroy(go);
    }

    void TryRefundCost(GameObject go, int fallbackCost)
    {
        int refund = 0;
        var tag = go.GetComponentInParent<BuildCostTag>() ?? go.GetComponentInChildren<BuildCostTag>(true);
        if (tag != null) refund = Mathf.Max(0, tag.Cost);
        if (refund <= 0) refund = Mathf.Max(0, fallbackCost);
        if (refund > 0) GameManager.Instance?.AddSweetCredits(refund);
    }

    public void StartPlacingCable()
    {
        TryStopOtherTools();
        mode = Mode.Cable;
        pendingCableStartNode = null;
        EndNodeLinkDrag(clearStartNode: true);
        AllowCameraPanWithCableTool = useNodeLinkCables;
        IsCameraPanBlockedByCableDrag = false;
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        try { BuildModeController.SetToolActive(true); } catch { }
        BuildSelectionNotifier.Notify("PowerCable");
        ForceSurfacePowerPlacementView();
    }

    public void StartPlacingPole()
    {
        TryStopOtherTools();
        mode = Mode.Pole;
        pendingCableStartNode = null;
        EndNodeLinkDrag(clearStartNode: true);
        AllowCameraPanWithCableTool = false;
        IsCameraPanBlockedByCableDrag = false;
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        try { BuildModeController.SetToolActive(true); } catch { }
        BuildSelectionNotifier.Notify("PowerPole");
        ForceSurfacePowerPlacementView();
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
        pendingCableStartNode = null;
        EndNodeLinkDrag(clearStartNode: true);
        AllowCameraPanWithCableTool = false;
        IsCameraPanBlockedByCableDrag = false;
        ResetDragPath();
        if (setGameState && GameManager.Instance != null) GameManager.Instance.SetState(GameState.Play);
        if (setToolActive)
        {
            try { BuildModeController.SetToolActive(false); } catch { }
        }
        if (notifySelection)
            BuildSelectionNotifier.Notify(null);
        if (undergroundView != null)
            undergroundView.ClearManualOverride();
    }

    bool IsDeleteModeActive()
    {
        return GameManager.Instance != null && GameManager.Instance.State == GameState.Delete;
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

    bool ShouldBlockCameraPanInputInternal()
    {
        if (mode != Mode.Cable || !useNodeLinkCables) return false;
        if (!Input.GetMouseButtonDown(0)) return false;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return false;

        if (cam == null) cam = Camera.main;
        if (cam == null) return false;

        var cell = GetMouseCell();
        if (!cell.HasValue) return false;
        return PowerNodeUtil.FindConnectableNodeAtCell(cell.Value) != null;
    }

    void HandleNodeLinkCableInput()
    {
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            if (isNodeLinkDragging && Input.GetMouseButtonUp(0))
                EndNodeLinkDrag(clearStartNode: true);
            return;
        }

        if (Input.GetMouseButtonDown(0))
            TryBeginNodeLinkDrag();

        if (isNodeLinkDragging && Input.GetMouseButton(0))
            UpdateNodeLinkPreviewToMouse();

        if (isNodeLinkDragging && Input.GetMouseButtonUp(0))
            TryCompleteNodeLinkDrag();
    }

    bool TryPlaceNodeLink(Component startNode, Component endNode, Vector2Int hintCell)
    {
        if (!TryResolveDirectedLink(startNode, endNode, out var fromNode, out var toNode, out var failMessage))
        {
            ShowPlacementHint(hintCell, failMessage);
            return false;
        }

        if (!PowerNodeUtil.TryBuildCableCells(fromNode, toNode, out var cableCells) || cableCells == null || cableCells.Count == 0)
        {
            ShowPlacementHint(hintCell, cableNodeRequiredMessage);
            return false;
        }

        int distance = Mathf.Max(0, cableCells.Count - 1);
        if (!IsWithinLengthLimit(distance))
        {
            ShowCableLengthLimitHint(hintCell);
            return false;
        }

        if (!TrySpendCost(cableCost))
            return false;

        var parent = placeParent != null ? placeParent : null;
        var go = new GameObject($"PowerLink_{fromNode.name}_{toNode.name}");
        if (parent != null)
            go.transform.SetParent(parent, true);

        float z = Mathf.Min(fromNode.transform.position.z, toNode.transform.position.z);
        go.transform.position = new Vector3(0f, 0f, z);

        var tag = go.AddComponent<BuildCostTag>();
        tag.Cost = cableCost;

        var link = go.AddComponent<PowerLinkLine>();
        bool initialized = link.Initialize(fromNode, toNode, cableLineWidth, cableLineColor, cableLineMaterial, cableSortingLayerName, cableSortingOrder, cableCells);
        if (!initialized)
        {
            RefundCost(cableCost);
            Destroy(go);
            ShowPlacementHint(hintCell, cableNodeRequiredMessage);
            return false;
        }

        return true;
    }

    void TryBeginNodeLinkDrag()
    {
        var cell = GetMouseCell();
        if (!cell.HasValue) return;

        if (IsUndergroundPlacementBlocked())
        {
            ShowPlacementHint(cell.Value, surfaceOnlyMessage);
            return;
        }

        var node = PowerNodeUtil.FindConnectableNodeAtCell(cell.Value);
        if (node == null)
        {
            EndNodeLinkDrag(clearStartNode: true);
            return;
        }

        pendingCableStartNode = node;
        isNodeLinkDragging = true;
        IsCameraPanBlockedByCableDrag = true;
        EnsureNodeLinkPreviewLine();
        UpdateNodeLinkPreviewToMouse();
    }

    void UpdateNodeLinkPreviewToMouse()
    {
        if (!isNodeLinkDragging || pendingCableStartNode == null)
        {
            EndNodeLinkDrag(clearStartNode: true);
            return;
        }

        EnsureNodeLinkPreviewLine();
        if (nodeLinkPreviewLine == null) return;

        float z = pendingCableStartNode.transform.position.z;
        var startPos = PowerNodeUtil.GetNodeWorldPosition(pendingCableStartNode, z);
        var endPos = GetMouseWorldOnPlane(z);
        nodeLinkPreviewLine.enabled = true;
        nodeLinkPreviewLine.SetPosition(0, startPos);
        nodeLinkPreviewLine.SetPosition(1, endPos);
    }

    void TryCompleteNodeLinkDrag()
    {
        if (!isNodeLinkDragging || pendingCableStartNode == null)
        {
            EndNodeLinkDrag(clearStartNode: true);
            return;
        }

        var startNode = pendingCableStartNode;
        var cell = GetMouseCell();
        if (cell.HasValue)
        {
            var targetNode = PowerNodeUtil.FindConnectableNodeAtCell(cell.Value);
            if (targetNode != null && targetNode != startNode)
                TryPlaceNodeLink(startNode, targetNode, cell.Value);
        }

        EndNodeLinkDrag(clearStartNode: true);
    }

    void EndNodeLinkDrag(bool clearStartNode)
    {
        isNodeLinkDragging = false;
        IsCameraPanBlockedByCableDrag = false;
        if (clearStartNode)
            pendingCableStartNode = null;
        if (nodeLinkPreviewLine != null)
            nodeLinkPreviewLine.enabled = false;
    }

    void EnsureNodeLinkPreviewLine()
    {
        if (nodeLinkPreviewLine != null) return;

        var previewGo = new GameObject("PowerLinkPreview");
        previewGo.transform.SetParent(transform, false);
        nodeLinkPreviewLine = previewGo.AddComponent<LineRenderer>();
        nodeLinkPreviewLine.positionCount = 2;
        nodeLinkPreviewLine.useWorldSpace = true;
        nodeLinkPreviewLine.numCapVertices = 3;
        nodeLinkPreviewLine.numCornerVertices = 2;
        nodeLinkPreviewLine.startWidth = cableLineWidth;
        nodeLinkPreviewLine.endWidth = cableLineWidth;
        nodeLinkPreviewLine.startColor = cableLineColor;
        nodeLinkPreviewLine.endColor = cableLineColor;
        if (!string.IsNullOrWhiteSpace(cableSortingLayerName))
            nodeLinkPreviewLine.sortingLayerName = cableSortingLayerName;
        nodeLinkPreviewLine.sortingOrder = cableSortingOrder + 1;
        if (cableLineMaterial != null)
        {
            nodeLinkPreviewLine.sharedMaterial = cableLineMaterial;
        }
        else
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader != null)
                nodeLinkPreviewLine.sharedMaterial = new Material(shader);
        }
        nodeLinkPreviewLine.enabled = false;
    }

    Vector3 GetMouseWorldOnPlane(float zPlane)
    {
        var mp = Input.mousePosition;
        float camZ = cam != null ? cam.transform.position.z : -10f;
        mp.z = zPlane - camZ;
        var world = cam != null ? cam.ScreenToWorldPoint(mp) : Vector3.zero;
        world.z = zPlane;
        return world;
    }

    bool TryResolveDirectedLink(Component firstNode, Component secondNode, out Component fromNode, out Component toNode, out string failMessage)
    {
        fromNode = null;
        toNode = null;
        failMessage = cableNodeRequiredMessage;

        if (firstNode == null || secondNode == null || firstNode == secondNode)
            return false;

        if (PowerLinkLine.HasLinkBetween(firstNode, secondNode))
        {
            failMessage = cableAlreadyConnectedMessage;
            return false;
        }

        if (CanConnectDirected(firstNode, secondNode, out var firstReason))
        {
            fromNode = firstNode;
            toNode = secondNode;
            return true;
        }

        if (CanConnectDirected(secondNode, firstNode, out var secondReason))
        {
            fromNode = secondNode;
            toNode = firstNode;
            return true;
        }

        bool secondIsGeneric = string.Equals(secondReason, cableInvalidDirectionMessage, StringComparison.Ordinal);
        failMessage = secondIsGeneric && !string.IsNullOrWhiteSpace(firstReason)
            ? firstReason
            : (!string.IsNullOrWhiteSpace(secondReason) ? secondReason : firstReason);
        if (string.IsNullOrWhiteSpace(failMessage))
            failMessage = cableInvalidDirectionMessage;
        return false;
    }

    bool CanConnectDirected(Component fromNode, Component toNode, out string failMessage)
    {
        failMessage = cableInvalidDirectionMessage;
        if (!CanOutputPower(fromNode)) return false;
        if (!CanInputPower(toNode)) return false;

        if (!HasNodeConnectionCapacity(fromNode, out failMessage)) return false;
        if (!HasNodeConnectionCapacity(toNode, out failMessage)) return false;

        if (IsProviderMachineNode(fromNode) && PowerLinkLine.CountLinksForNode(fromNode) >= 1)
        {
            failMessage = providerOutputLimitMessage;
            return false;
        }

        if (IsConsumerMachineNode(toNode) && PowerLinkLine.CountLinksForNode(toNode) >= 1)
        {
            failMessage = consumerInputLimitMessage;
            return false;
        }

        failMessage = null;
        return true;
    }

    bool HasNodeConnectionCapacity(Component node, out string failMessage)
    {
        failMessage = null;
        if (!(node is PowerPole)) return true;
        int limit = Mathf.Max(1, powerNodeMaxConnections);
        if (PowerLinkLine.CountLinksForNode(node) < limit) return true;
        failMessage = powerNodeLimitMessage;
        return false;
    }

    static bool CanOutputPower(Component node)
    {
        if (node == null) return false;
        if (node is PowerPole) return true;
        return node is IPowerSourceNode;
    }

    static bool CanInputPower(Component node)
    {
        if (node == null) return false;
        if (node is PowerPole) return true;
        return node is IPowerConsumer;
    }

    static bool IsProviderMachineNode(Component node)
    {
        return node != null && !(node is PowerPole) && node is IPowerSourceNode;
    }

    static bool IsConsumerMachineNode(Component node)
    {
        return node != null && !(node is PowerPole) && node is IPowerConsumer;
    }

    bool TryPlaceCable(Vector2Int cell, Direction? direction, out PowerCable placedCable, bool allowChainPlacement = false)
    {
        placedCable = null;
        if (allowChainPlacement)
        {
            if (!CanPlaceCableAtForDrag(cell)) return false;
        }
        else if (!CanPlaceCableAt(cell)) return false;
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
        if (IsUndergroundPlacementBlocked())
        {
            ShowPlacementHint(cell, surfaceOnlyMessage);
            return false;
        }
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
        return true;
    }

    bool CanPlaceCableAt(Vector2Int cell)
    {
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null) return true;
        return power.CanPlaceCableAt(cell) && !IsMachineCell(cell);
    }

    bool CanPlaceCableAtForDrag(Vector2Int cell)
    {
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null) return !IsMachineCell(cell);
        if (power.IsCellOccupiedOrBlueprint(cell)) return false;
        return !IsMachineCell(cell);
    }

    bool CanPlacePoleAt(Vector2Int cell)
    {
        if (IsUndergroundPlacementBlocked())
            return false;
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
        if (cable == null || !cable.isGhost) return;
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
        UpdateDragCableConnections();
        return true;
    }

    void UpdateTailDirectionFromPath()
    {
        if (dragPath.Count < 2) return;
        var prevCell = dragPath[dragPath.Count - 2];
        var lastCell = dragPath[dragPath.Count - 1];
        if (!dragCables.TryGetValue(lastCell, out var lastCable) || lastCable == null) return;
        if (!lastCable.isGhost) return;
        var dir = DeltaToDirection(lastCell - prevCell);
        UpdateCableDirection(lastCable, dir);
    }

    void UpdateDragCableConnections()
    {
        if (dragPath.Count == 0) return;

        for (int i = 0; i < dragPath.Count; i++)
        {
            var cell = dragPath[i];
            if (!dragCables.TryGetValue(cell, out var cable) || cable == null) continue;
            if (!cable.isGhost) continue;

            PowerCable.ConnectionMask mask = PowerCable.ConnectionMask.None;
            if (i > 0)
            {
                var prev = dragPath[i - 1];
                var dirToPrev = DeltaToDirection(prev - cell);
                mask |= PowerCable.MaskFromDirection(dirToPrev);
            }
            if (i < dragPath.Count - 1)
            {
                var next = dragPath[i + 1];
                var dirToNext = DeltaToDirection(next - cell);
                mask |= PowerCable.MaskFromDirection(dirToNext);
            }

            cable.SetBaseConnections(mask, refresh: true);
        }
    }

    void ApplyDragPath(List<Vector2Int> targetPath)
    {
        if (targetPath == null || targetPath.Count == 0) return;
        if (dragPath.Count == 0) return;
        if (targetPath[0] != dragPath[0]) return;

        int common = 0;
        int min = Mathf.Min(dragPath.Count, targetPath.Count);
        while (common < min && dragPath[common] == targetPath[common])
            common++;

        if (common == 0) return;

        for (int i = dragPath.Count - 1; i >= common; i--)
        {
            var removeCell = dragPath[i];
            RemoveDragCable(removeCell);
            dragPath.RemoveAt(i);
        }

        UpdateTailDirectionFromPath();

        for (int i = common; i < targetPath.Count; i++)
        {
            var cell = targetPath[i];
            if (dragCables.ContainsKey(cell)) continue;

            var prev = targetPath[i - 1];
            var dir = DeltaToDirection(cell - prev);

            if (!TryGetNextDragDistance(cell, prev, out var nextDistance))
                break;
            if (!IsWithinLengthLimit(nextDistance))
            {
                ShowCableLengthLimitHint(cell);
                break;
            }
            if (!TryPlaceCable(cell, dir, out var newCable, allowChainPlacement: true))
                break;

            if (dragCables.TryGetValue(prev, out var prevCable) && prevCable != null)
                UpdateCableVisualForTurn(prevCable, dir);

            lastPlacedCable = newCable;
            AddDragCable(cell, newCable, nextDistance);
            lastPlacedCell = cell;
            PinUndergroundView();
        }

        if (dragPath.Count > 0)
        {
            lastPlacedCell = dragPath[dragPath.Count - 1];
            dragCables.TryGetValue(lastPlacedCell, out lastPlacedCable);
        }
        UpdateDragCableConnections();
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

    List<Vector2Int> FindCablePath(Vector2Int start, Vector2Int goal)
    {
        if (start == goal)
            return new List<Vector2Int> { start };

        var grid = GridService.Instance;
        if (grid == null) return null;
        if (!grid.InBounds(goal)) return null;
        if (!IsPathCellWalkable(goal, start, goal)) return null;

        var open = new List<Vector2Int> { start };
        var openSet = new HashSet<Vector2Int> { start };
        var closed = new HashSet<Vector2Int>();
        var cameFrom = new Dictionary<Vector2Int, Vector2Int>();
        var gScore = new Dictionary<Vector2Int, int> { [start] = 0 };

        while (open.Count > 0)
        {
            Vector2Int current = open[0];
            int currentIndex = 0;
            int bestF = gScore[current] + Heuristic(current, goal);
            for (int i = 1; i < open.Count; i++)
            {
                var node = open[i];
                int f = gScore[node] + Heuristic(node, goal);
                if (f < bestF)
                {
                    bestF = f;
                    current = node;
                    currentIndex = i;
                }
            }

            open.RemoveAt(currentIndex);
            openSet.Remove(current);
            if (current == goal)
                return ReconstructPath(cameFrom, current);

            closed.Add(current);

            foreach (var dir in NeighborDirs)
            {
                var next = current + dir;
                if (!grid.InBounds(next)) continue;
                if (closed.Contains(next)) continue;
                if (!IsPathCellWalkable(next, start, goal)) continue;

                int tentative = gScore[current] + 1;
                if (!gScore.TryGetValue(next, out var prevG) || tentative < prevG)
                {
                    cameFrom[next] = current;
                    gScore[next] = tentative;
                    if (!openSet.Contains(next))
                    {
                        open.Add(next);
                        openSet.Add(next);
                    }
                }
            }
        }

        return null;
    }

    bool IsPathCellWalkable(Vector2Int cell, Vector2Int start, Vector2Int goal)
    {
        if (cell == start) return true;
        if (dragCables.ContainsKey(cell)) return true;
        return CanPlaceCableAtForDrag(cell);
    }

    static int Heuristic(Vector2Int a, Vector2Int b)
        => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

    static List<Vector2Int> ReconstructPath(Dictionary<Vector2Int, Vector2Int> cameFrom, Vector2Int current)
    {
        var path = new List<Vector2Int> { current };
        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            path.Add(current);
        }
        path.Reverse();
        return path;
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
        if (power.IsAdjacentToSourceCell(cell) || power.IsAdjacentToConnectedPole(cell))
        {
            distance = 1;
            return true;
        }
        return false;
    }

    bool TryGetStartDragDistanceFromExisting(Vector2Int cell, out int distance)
    {
        distance = 0;
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null)
        {
            distance = 1;
            return true;
        }
        if (power.TryGetPlacementDistance(cell, out distance))
            return true;
        return TryGetStartDragDistance(cell, out distance);
    }

    bool TryGetNextDragDistance(Vector2Int cell, Vector2Int fromCell, out int distance)
    {
        if (!dragDistances.TryGetValue(fromCell, out var prev))
        {
            var powerService = PowerService.Instance ?? PowerService.EnsureInstance();
            if (powerService != null && powerService.TryGetPlacementDistance(fromCell, out prev))
            {
                // Use existing cable distance to keep max-length enforcement intact.
            }
            else
            {
                return TryGetStartDragDistance(cell, out distance);
            }
        }

        distance = prev + 1;
        var powerService2 = PowerService.Instance ?? PowerService.EnsureInstance();
        if (powerService2 != null && (powerService2.IsAdjacentToSourceCell(cell) || powerService2.IsAdjacentToConnectedPole(cell)))
            distance = Mathf.Min(distance, 1);
        return true;
    }

    bool IsWithinLengthLimit(int distance)
    {
        var power = PowerService.Instance ?? PowerService.EnsureInstance();
        if (power == null) return true;
        if (!power.HasCableLengthLimit) return true;
        return distance <= power.MaxCableLength;
    }

    void ShowCableLengthLimitHint(Vector2Int cell)
    {
        if (!showCableLengthLimitHint || string.IsNullOrWhiteSpace(cableLengthLimitMessage))
            return;

        float now = Time.unscaledTime;
        if (now - lastCableLimitHintTime < cableHintCooldownSeconds)
            return;

        lastCableLimitHintTime = now;
        BuildPlacementHintUI.ShowAtCell(cell, cableLengthLimitMessage);
    }

    void ShowPlacementHint(Vector2Int cell, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        BuildPlacementHintUI.ShowAtCell(cell, message);
    }

    void PinUndergroundView()
    {
        if (undergroundView == null)
            undergroundView = FindAnyObjectByType<UndergroundViewController>();
        undergroundView?.ShowUndergroundView();
    }

    void ForceSurfacePowerPlacementView()
    {
        if (undergroundView == null)
            undergroundView = FindAnyObjectByType<UndergroundViewController>();
        undergroundView?.HideUndergroundView();
    }

    bool IsUndergroundPlacementBlocked()
    {
        if (undergroundView == null)
            undergroundView = FindAnyObjectByType<UndergroundViewController>();
        return undergroundView != null && undergroundView.IsUndergroundActive;
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

        var power = PowerService.Instance;
        if (power != null && power.HasPowerTerminalAt(cell))
            return true;

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
