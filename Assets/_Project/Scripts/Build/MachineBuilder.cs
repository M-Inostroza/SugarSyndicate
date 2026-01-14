using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine.Rendering;
using UnityEngine;
using UnityEngine.EventSystems;

public class MachineBuilder : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GameObject pressMachinePrefab;
    [SerializeField] GameObject shrederPrefab;
    [SerializeField] GameObject colorizerPrefab;
    [SerializeField] GameObject waterPumpPrefab;
    [SerializeField] GameObject waterPipePrefab;
    [SerializeField] GameObject minePrefab;
    [SerializeField] GameObject storageContainerPrefab;
    [SerializeField] GameObject solarPanelPrefab;
    [SerializeField] GameObject droneHqPrefab;
    [SerializeField] WaterAreaOverlay waterOverlay;
    [SerializeField] SugarZoneOverlay sugarOverlay;
    [SerializeField] int ghostSortingOrder = 10000;
    [SerializeField] int mineSortingOrder = 1100;

    [Header("Economy")]
    [SerializeField, Min(0)] int pressCost = 150;
    [SerializeField, Min(0)] int shrederCost = 0;
    [SerializeField, Min(0)] int colorizerCost = 0;
    [SerializeField, Min(0)] int waterPumpCost = 0;
    [SerializeField, Min(0)] int waterPipeCost = 0;
    [SerializeField, Min(0)] int mineCost = 90;
    [SerializeField, Min(0)] int storageContainerCost = 0;
    [SerializeField, Min(0)] int solarPanelCost = 0;
    [SerializeField, Min(0)] int droneHqCost = 0;

    [Header("Build Times")]
    [SerializeField, Min(0.1f)] float beltBuildSeconds = 0.4f;
    [SerializeField, Min(0.1f)] float pressBuildSeconds = 2f;
    [SerializeField, Min(0.1f)] float shrederBuildSeconds = 2f;
    [SerializeField, Min(0.1f)] float colorizerBuildSeconds = 2f;
    [SerializeField, Min(0.1f)] float waterPumpBuildSeconds = 2f;
    [SerializeField, Min(0.1f)] float waterPipeBuildSeconds = 0.6f;
    [SerializeField, Min(0.1f)] float mineBuildSeconds = 2f;
    [SerializeField, Min(0.1f)] float storageContainerBuildSeconds = 2f;
    [SerializeField, Min(0.1f)] float solarPanelBuildSeconds = 2f;

    object grid;
    MethodInfo miWorldToCell;
    MethodInfo miCellToWorld;
    MethodInfo miGetCell;
    MethodInfo miIsWater;
    MethodInfo miSetMachineCell;

    bool awaitingWorldClick;
    bool waitRelease; // ignore the click that triggered the button until released
    int activationFrame;

    // Ghost placement state
    bool placing;
    Vector2Int baseCell;
    GameObject ghostGO;
    PressMachine ghostPress;
    ColorizerMachine ghostColorizer;
    WaterPump ghostWaterPump;
    StorageContainerMachine ghostStorage;
    SolarPanelMachine ghostSolarPanel;
    SugarMine ghostMine;
    DroneHQ ghostHq;
    GameObject activePrefab;
    string activeName = "PressMachine";
    bool placingPipePath;
    readonly List<GameObject> ghostPipes = new List<GameObject>();
    readonly List<Vector2Int> pipePath = new List<Vector2Int>();

    void Awake()
    {
        CacheGrid();
        TryEnsureWaterOverlay();
        HideSugarOverlay();
    }

    void Update()
    {
        // Enforce global Build state: if not in Build, cancel any pending placement and ignore input
        if (GameManager.Instance == null || GameManager.Instance.State != GameState.Build)
        {
            if (awaitingWorldClick || placing)
            {
                CancelPreview();
                awaitingWorldClick = false;
                placing = false;
            }
            HideWaterOverlay();
            HideSugarOverlay();
            return;
        }

        if (activeName != "Mine") HideSugarOverlay();

        if (!awaitingWorldClick) return;

        // Ensure we don't accept the same click that triggered the button: wait for release and a new frame
        if (waitRelease)
        {
            if (Input.GetMouseButton(0)) return;
            if (Time.frameCount == activationFrame) return;
            waitRelease = false;
            return;
        }

        // Cancel flow -> end only local preview; DO NOT change GameManager state
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
        {
            CancelPreview();
            awaitingWorldClick = false;
            HideWaterOverlay();
            HideSugarOverlay();
            return;
        }

        var cam = Camera.main; if (cam == null) return;
        var world = GetMouseWorldOnPlane(cam);

        if (IsPipeMode())
        {
            if (!placingPipePath)
            {
                if (Input.GetMouseButtonDown(0))
                {
                    if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
                    if (!TryCellFromWorld(world, out baseCell)) return;
                    if (IsBlocked(baseCell)) return;
                    StartPipePath(baseCell);
                    placingPipePath = true;
                }
            }
            else
            {
                if (!TryCellFromWorld(world, out var curCell)) return;
                UpdatePipeGhost(baseCell, curCell);
                if (Input.GetMouseButtonUp(0))
                {
                    CommitPipePath();
                    placingPipePath = false;
                    waitRelease = true;
                    activationFrame = Time.frameCount;
                    awaitingWorldClick = true;
                }
            }
        }
        else
        {
            if (!placing)
            {
                // First world click: choose base cell and spawn ghost
                if (Input.GetMouseButtonDown(0))
                {
                    if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

                if (!TryCellFromWorld(world, out baseCell)) return;
                var footprint = GetFootprintCells(baseCell, new Vector2Int(1, 0));
                if (IsAnyBlocked(footprint)) return;
                if (RequiresWaterCell() && !IsWaterCell(baseCell))
                {
                    Debug.LogWarning("[MachineBuilder] Water pump must be placed on water.");
                    return;
                }
                if (RequiresSugarCell() && !IsSugarCell(baseCell))
                {
                    Debug.LogWarning("[MachineBuilder] Mine must be placed on sugar.");
                    return;
                }
                TrySetBuildToolActive(true);
                SpawnGhost(baseCell);
                placing = true;
            }
        }
            else
            {
                // Drag to set orientation, release to commit
                if (!TryCellFromWorld(world, out var curCell)) return;
            var dir = curCell - baseCell;
            dir = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y) ? new Vector2Int(Mathf.Clamp(dir.x, -1, 1), 0) : new Vector2Int(0, Mathf.Clamp(dir.y, -1, 1));
            if (dir == Vector2Int.zero) dir = new Vector2Int(1, 0); // default facing right
            UpdateGhost(baseCell, dir);

            if (Input.GetMouseButtonUp(0))
                {
                    // Commit the press but STAY in Build mode so user can place another
                    Commit(baseCell, dir);
                    placing = false; // Clear for next placement
                    // Prepare for next placement: require mouse release + new click
                    waitRelease = true;
                    activationFrame = Time.frameCount;
                    awaitingWorldClick = true; // remain in build loop
                }
            }
        }
    }

    // Public API: stop any active press placement session (called when switching tools)
    public void StopBuilding()
    {
        awaitingWorldClick = false;
        placing = false;
        // Clear any ghost preview safely
        CancelPreview();
        HideWaterOverlay();
        HideSugarOverlay();
    }

    void CacheGrid()
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                if (mb.GetType().Name == "GridService") { grid = mb; break; }
            }
            if (grid != null)
            {
                var t = grid.GetType();
                miWorldToCell = t.GetMethod("WorldToCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3) }, null);
                miCellToWorld = t.GetMethod("CellToWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(float) }, null);
                miGetCell = t.GetMethod("GetCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
                miIsWater = t.GetMethod("IsWater", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
                miSetMachineCell = t.GetMethod("SetMachineCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            }
        }
        catch { }
    }

    void TryEnsureWaterOverlay()
    {
        if (waterOverlay != null) return;
        try
        {
            var gs = grid as GridService ?? GridService.Instance ?? FindAnyObjectByType<GridService>();
            if (gs != null)
            {
                waterOverlay = WaterAreaOverlay.FindOrCreate(gs);
                if (waterOverlay == null)
                {
                    var go = new GameObject("WaterAreaOverlay");
                    go.transform.SetParent(gs.transform, false);
                    waterOverlay = go.AddComponent<WaterAreaOverlay>();
                    waterOverlay.GetType().GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.SetValue(waterOverlay, gs);
                }
            }
        }
        catch { }
    }

    void TryCacheSugarOverlay()
    {
        if (sugarOverlay != null) return;
        try
        {
            var gs = grid as GridService ?? GridService.Instance ?? FindAnyObjectByType<GridService>();
            if (gs != null) sugarOverlay = gs.GetComponent<SugarZoneOverlay>();
        }
        catch { }
    }

    void TryEnsureSugarOverlay()
    {
        if (sugarOverlay != null) return;
        try
        {
            var gs = grid as GridService ?? GridService.Instance ?? FindAnyObjectByType<GridService>();
            if (gs != null) sugarOverlay = SugarZoneOverlay.FindOrCreate(gs);
        }
        catch { }
    }

    void ShowWaterOverlay()
    {
        TryEnsureWaterOverlay();
        try
        {
            waterOverlay?.Rebuild();
            waterOverlay?.Show();
            if (waterOverlay != null)
            {
                Debug.Log($"[MachineBuilder] Water overlay ready ({waterOverlay.LastQuadCount} quads).");
            }
            else
                Debug.LogWarning("[MachineBuilder] Water overlay missing.");
        }
        catch { }
    }

    void HideWaterOverlay()
    {
        try { waterOverlay?.Hide(); } catch { }
    }

    void ShowSugarOverlay()
    {
        TryEnsureSugarOverlay();
        try
        {
            sugarOverlay?.Rebuild();
            sugarOverlay?.Show();
        }
        catch { }
    }

    void HideSugarOverlay()
    {
        TryCacheSugarOverlay();
        try { sugarOverlay?.Hide(); } catch { }
    }

    void NotifySelectionChanged(string selectionName)
    {
        BuildSelectionNotifier.Notify(selectionName);
    }

    bool IsPipeMode() => activeName == "WaterPipe";

    void TrySetBuildToolActive(bool active)
    {
        try { BuildModeController.SetToolActive(active); } catch { }
    }

    int GetCostForActiveName(string name)
    {
        return name switch
        {
            "PressMachine" => pressCost,
            "Shreder" => shrederCost,
            "Colorizer" => colorizerCost,
            "WaterPump" => waterPumpCost,
            "WaterPipe" => waterPipeCost,
            "Mine" => mineCost,
            "StorageContainer" => storageContainerCost,
            "SolarPanel" => solarPanelCost,
            "DroneHQ" => droneHqCost,
            _ => 0,
        };
    }

    float GetBuildSecondsForActiveName(string name)
    {
        return name switch
        {
            "PressMachine" => pressBuildSeconds,
            "Shreder" => shrederBuildSeconds,
            "Colorizer" => colorizerBuildSeconds,
            "WaterPump" => waterPumpBuildSeconds,
            "WaterPipe" => waterPipeBuildSeconds,
            "Mine" => mineBuildSeconds,
            "StorageContainer" => storageContainerBuildSeconds,
            "SolarPanel" => solarPanelBuildSeconds,
            _ => 1f,
        };
    }

    bool TrySpendBuildCost(int amount, string label)
    {
        if (amount <= 0) return true;
        var gm = GameManager.Instance;
        if (gm == null) return true;
        if (gm.TrySpendSweetCredits(amount)) return true;
        Debug.LogWarning($"[MachineBuilder] Not enough money to place {label}. Cost: {amount}.");
        return false;
    }

    void StartPipePath(Vector2Int start)
    {
        ClearPipeGhosts();
        pipePath.Clear();
        pipePath.Add(start);
        TrySetBuildToolActive(true);
        UpdatePipeGhost(start, start);
    }

    void UpdatePipeGhost(Vector2Int start, Vector2Int end)
    {
        var newPath = BuildManhattanPath(start, end);
        pipePath.Clear();
        pipePath.AddRange(newPath);
        // Ensure we have ghosts for each cell in path
        for (int i = 0; i < pipePath.Count; i++)
        {
            if (i >= ghostPipes.Count)
            {
                var go = Instantiate(activePrefab);
                TintGhost(go);
                DisablePipeBehaviors(go);
                ghostPipes.Add(go);
            }
            var cell = pipePath[i];
            var pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
            ghostPipes[i].transform.position = pos;
            ghostPipes[i].transform.rotation = RotationForPipeSegment(i);
            ghostPipes[i].SetActive(true);
        }
        // Hide extra ghosts if any
        for (int i = pipePath.Count; i < ghostPipes.Count; i++)
        {
            ghostPipes[i].SetActive(false);
        }
    }

    List<Vector2Int> BuildManhattanPath(Vector2Int start, Vector2Int end)
    {
        var path = new List<Vector2Int>();
        path.Add(start);
        var cur = start;

        // Step horizontally first to align X, then vertically to align Y
        int dx = end.x - cur.x;
        int stepX = Math.Sign(dx);
        for (int i = 0; i < Mathf.Abs(dx); i++)
        {
            cur += new Vector2Int(stepX, 0);
            path.Add(cur);
        }

        int dy = end.y - cur.y;
        int stepY = Math.Sign(dy);
        for (int i = 0; i < Mathf.Abs(dy); i++)
        {
            cur += new Vector2Int(0, stepY);
            path.Add(cur);
        }
        return path;
    }

    void TintGhost(GameObject go)
    {
        var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in srs)
        {
            var c = sr.color;
            c.a = 0.6f;
            sr.color = c;
        }
    }

    void DisablePipeBehaviors(GameObject go)
    {
        if (go == null) return;
        var pipe = go.GetComponent<WaterPipe>();
        if (pipe != null) Destroy(pipe);
    }

    Quaternion RotationForPipeSegment(int index)
    {
        if (pipePath.Count <= 1) return Quaternion.identity;
        var cur = pipePath[index];
        Vector2Int dir = Vector2Int.right;
        if (index < pipePath.Count - 1)
        {
            dir = pipePath[index + 1] - cur;
        }
        else
        {
            dir = cur - pipePath[index - 1];
        }
        if (dir == Vector2Int.up || dir == Vector2Int.down) return Quaternion.Euler(0, 0, 90f);
        return Quaternion.identity;
    }

    Quaternion RotationForPipeCell(Vector2Int cell)
    {
        if (pipePath.Count <= 1) return Quaternion.identity;
        var idx = pipePath.IndexOf(cell);
        if (idx < 0) return Quaternion.identity;
        return RotationForPipeSegment(idx);
    }

    void CommitPipePath()
    {
        if (pipePath.Count == 0) return;
        int totalCost = waterPipeCost * pipePath.Count;
        if (!CanAffordCost(totalCost))
        {
            Debug.LogWarning($"[MachineBuilder] Not enough money to place WaterPipe. Cost: {totalCost}.");
            ClearPipeGhosts();
            ClearPreviewState(clearActiveSelection: false);
            return;
        }

        // Validate blocked cells
        foreach (var cell in pipePath)
        {
            if (IsBlocked(cell)) TryRemoveBeltAtCell(cell);
            if (IsBlocked(cell))
            {
                Debug.LogWarning($"[MachineBuilder] Cannot place pipe: cell {cell} blocked.");
                ClearPipeGhosts();
                ClearPreviewState(clearActiveSelection: false);
                return;
            }
        }

        if (!TrySpendBuildCost(totalCost, "WaterPipe"))
        {
            ClearPipeGhosts();
            ClearPreviewState(clearActiveSelection: false);
            return;
        }

        for (int i = 0; i < pipePath.Count; i++)
        {
            var cell = pipePath[i];
            TryRemoveBeltAtCell(cell);
            var pos = (Vector3)miCellToWorld.Invoke(grid, new object[] { cell, 0f });
            var rot = RotationForPipeCell(cell);

            var go = i < ghostPipes.Count ? ghostPipes[i] : null;
            if (go == null)
            {
                go = new GameObject("WaterPipeBlueprint");
            }
            go.transform.position = pos;
            go.transform.rotation = rot;
            go.SetActive(true);

            var task = go.GetComponent<BlueprintTask>();
            if (task == null) task = go.AddComponent<BlueprintTask>();
            task.InitializePipe(cell, rot, activePrefab, waterPipeCost, waterPipeBuildSeconds);
        }
        ClearPipeGhosts(false);
        // Keep the tool armed so the player can place another path without re-selecting.
        ClearPreviewState(clearActiveSelection: false);
    }

    void ClearPipeGhosts(bool destroy = true)
    {
        if (destroy)
        {
            foreach (var go in ghostPipes) { if (go != null) Destroy(go); }
        }
        ghostPipes.Clear();
        pipePath.Clear();
    }

    // UI Button-friendly helper: drag this component into a Button.onClick and pick BuildPress
    public void BuildPress()
    {
        activePrefab = pressMachinePrefab;
        activeName = "PressMachine";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    public void BuildShreder()
    {
        activePrefab = shrederPrefab;
        activeName = "Shreder";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    public void BuildColorizer()
    {
        activePrefab = colorizerPrefab;
        activeName = "Colorizer";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    public void BuildWaterPump()
    {
        activePrefab = waterPumpPrefab;
        activeName = "WaterPump";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        ShowWaterOverlay();
        HideSugarOverlay();
    }

    public void BuildWaterPipe()
    {
        activePrefab = waterPipePrefab;
        activeName = "WaterPipe";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    public void BuildMine()
    {
        activePrefab = minePrefab;
        activeName = "Mine";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        ShowSugarOverlay();
    }

    public void BuildStorageContainer()
    {
        activePrefab = storageContainerPrefab;
        activeName = "StorageContainer";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    public void BuildSolarPanel()
    {
        activePrefab = solarPanelPrefab;
        activeName = "SolarPanel";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    public void BuildDroneHQ()
    {
        if (DroneHQ.Instance != null || BlueprintTask.HasHqBlueprint)
        {
            Debug.LogWarning("[MachineBuilder] Only one Drone HQ is allowed.");
            return;
        }

        activePrefab = droneHqPrefab;
        activeName = "DroneHQ";
        ArmPlacement();
        NotifySelectionChanged(activeName);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    void ArmPlacement()
    {
        // End any active conveyor preview WITHOUT changing global state
        TryEndConveyorPreviewWithoutState();

        // Ensure we enter Build mode so placement + overlay stay active
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Build)
            GameManager.Instance.SetState(GameState.Build);

        if (activePrefab == null)
        {
            Debug.LogWarning($"[MachineBuilder] No prefab assigned for {activeName}.");
            return;
        }

        // Treat this as an active tool immediately (so Esc cancels tool instead of quitting,
        // and to keep placement armed across repeated placements).
        TrySetBuildToolActive(true);

        // Wait for the next world click to place
        awaitingWorldClick = true;
        waitRelease = true;
        activationFrame = Time.frameCount;
    }

    // Back-compatible alias
    public void BuildPressAtMouse() => BuildPress();

    void TryEndConveyorPreviewWithoutState()
    {
        try
        {
            var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "BuildModeController")
                {
                    // Prefer calling the private EndCurrentPreview() helper we added earlier
                    var miEnd = t.GetMethod("EndCurrentPreview", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (miEnd != null)
                    {
                        miEnd.Invoke(mb, null);
                        break;
                    }

                    // Fallback: try to grab conveyorPlacer field and call EndPreview directly
                    var fiPlacer = t.GetField("conveyorPlacer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var placer = fiPlacer != null ? fiPlacer.GetValue(mb) : null;
                    if (placer != null)
                    {
                        var mi = placer.GetType().GetMethod("EndPreview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mi != null) mi.Invoke(placer, null);
                    }
                    // Also attempt to set current = BuildableType.None so Update() doesn't keep driving the placer
                    var fiCurrent = t.GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (fiCurrent != null)
                    {
                        var enumType = fiCurrent.FieldType; // BuildableType
                        object noneVal = null;
                        try { noneVal = System.Enum.Parse(enumType, "None"); } catch { }
                        if (noneVal != null) fiCurrent.SetValue(mb, noneVal);
                    }
                    break;
                }
            }
        }
        catch { }
    }

    bool TryCellFromWorld(Vector3 world, out Vector2Int cell)
    {
        cell = default;
        if (grid == null || miWorldToCell == null) { CacheGrid(); if (grid == null || miWorldToCell == null) return false; }
        cell = (Vector2Int)miWorldToCell.Invoke(grid, new object[] { world });
        return true;
    }

    bool IsBlocked(Vector2Int cell)
    {
        try
        {
            var cellObj = miGetCell != null ? miGetCell.Invoke(grid, new object[] { cell }) : null;
            if (cellObj != null)
            {
                var ct = cellObj.GetType();
                var fiHasConv = ct.GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fiType = ct.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fiBlueprint = ct.GetField("isBlueprint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var fiBroken = ct.GetField("isBroken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                bool hasConv = fiHasConv != null && (bool)fiHasConv.GetValue(cellObj);
                string typeName = fiType != null ? fiType.GetValue(cellObj)?.ToString() : string.Empty;
                bool isBlueprint = fiBlueprint != null && (bool)fiBlueprint.GetValue(cellObj);
                bool isBroken = fiBroken != null && (bool)fiBroken.GetValue(cellObj);
                if (isBlueprint || isBroken) return true;
                if (hasConv || typeName == "Machine") return true;
            }
        }
        catch { }
        return false;
    }

    void SpawnGhost(Vector2Int cell)
    {
        if (activePrefab == null || grid == null || miCellToWorld == null) return;
        var pos = GetFootprintCenterWorld(cell, new Vector2Int(1, 0));
        ghostGO = Instantiate(activePrefab, pos, Quaternion.identity);
        ApplyGhostSorting(ghostGO);
        ghostPress = ghostGO.GetComponent<PressMachine>();
        ghostColorizer = ghostGO.GetComponent<ColorizerMachine>();
        ghostWaterPump = ghostGO.GetComponent<WaterPump>();
        ghostStorage = ghostGO.GetComponent<StorageContainerMachine>();
        ghostSolarPanel = ghostGO.GetComponent<SolarPanelMachine>();
        ghostMine = ghostGO.GetComponent<SugarMine>();
        ghostHq = ghostGO.GetComponent<DroneHQ>();
        if (ghostPress != null)
        {
            ghostPress.isGhost = true;
            // tint ghost
            var srs = ghostGO.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in srs) { var c = sr.color; c.a = 0.6f; sr.color = c; }
        }
        if (ghostColorizer != null) ghostColorizer.isGhost = true;
        if (ghostWaterPump != null) ghostWaterPump.isGhost = true;
        if (ghostMine != null) ghostMine.isGhost = true;
        if (ghostHq != null) ghostHq.isGhost = true;
        if (ghostStorage != null)
        {
            ghostStorage.isGhost = true;
            TintGhost(ghostGO);
        }
        if (ghostSolarPanel != null)
        {
            ghostSolarPanel.isGhost = true;
            TintGhost(ghostGO);
        }
    }

    void UpdateGhost(Vector2Int cell, Vector2Int outputDir)
    {
        if (ghostGO == null) return;
        // Rotate ghost to face outputDir (Right=0, Up=90, Left=180, Down=270)
        float z = 0f;
        if (outputDir == new Vector2Int(1, 0)) z = 0f;
        else if (outputDir == new Vector2Int(0, 1)) z = 90f;
        else if (outputDir == new Vector2Int(-1, 0)) z = 180f;
        else if (outputDir == new Vector2Int(0, -1)) z = 270f;
        ghostGO.transform.rotation = Quaternion.Euler(0, 0, z);
        // Update position to footprint center
        ghostGO.transform.position = GetFootprintCenterWorld(cell, outputDir);
        if (ghostPress != null) ghostPress.facingVec = outputDir;
        if (ghostColorizer != null) ghostColorizer.facingVec = outputDir;
        if (ghostWaterPump != null) ghostWaterPump.facingVec = outputDir;
        if (ghostStorage != null) ghostStorage.facingVec = outputDir;
        if (ghostSolarPanel != null) ghostSolarPanel.facingVec = outputDir;
        if (ghostMine != null) ghostMine.SetFacing(outputDir);
    }

    void ApplyGhostSorting(GameObject go)
    {
        if (go == null) return;
        var group = go.GetComponentInChildren<SortingGroup>(true);
        if (group != null) group.sortingOrder = ghostSortingOrder;
        var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in srs) sr.sortingOrder = ghostSortingOrder;
    }

    void ApplyPlacedSorting(GameObject go, int sortingOrder)
    {
        if (go == null) return;
        var group = go.GetComponentInChildren<SortingGroup>(true);
        if (group != null) group.sortingOrder = sortingOrder;
        var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in srs) sr.sortingOrder = sortingOrder;
    }

    void Commit(Vector2Int cell, Vector2Int outputDir)
    {
        Debug.Log($"[MachineBuilder] Committing {activeName} at cell {cell} facing {outputDir}");

        if (RequiresWaterCell() && !IsWaterCell(cell))
        {
            Debug.LogWarning("[MachineBuilder] Water pump must be placed on water.");
            if (ghostGO != null) Destroy(ghostGO);
            ClearPreviewState(clearActiveSelection: false);
            return;
        }
        if (RequiresSugarCell() && !IsSugarCell(cell))
        {
            Debug.LogWarning("[MachineBuilder] Mine must be placed on sugar.");
            if (ghostGO != null) Destroy(ghostGO);
            ClearPreviewState(clearActiveSelection: false);
            return;
        }
        if (activeName == "DroneHQ" && (DroneHQ.Instance != null || BlueprintTask.HasHqBlueprint))
        {
            Debug.LogWarning("[MachineBuilder] Only one Drone HQ is allowed.");
            if (ghostGO != null) Destroy(ghostGO);
            ClearPreviewState(clearActiveSelection: false);
            return;
        }

        var footprint = GetFootprintCells(cell, outputDir);
        if (IsAnyBlocked(footprint))
        {
            Debug.LogWarning($"[MachineBuilder] Cannot place {activeName}: footprint blocked.");
            if (ghostGO != null) Destroy(ghostGO);
            ClearPreviewState(clearActiveSelection: false);
            return;
        }

        int cost = GetCostForActiveName(activeName);
        if (!TrySpendBuildCost(cost, activeName))
        {
            if (ghostGO != null) Destroy(ghostGO);
            ClearPreviewState(clearActiveSelection: false);
            return;
        }

        // Ensure the target footprint cells are not belts anymore
        foreach (var fp in footprint) TryRemoveBeltAtCell(fp);

        if (activeName == "DroneHQ")
        {
            Vector3 hqPos = GetFootprintCenterWorld(cell, outputDir);
            if (ghostGO != null) Destroy(ghostGO);

            var hqGo = Instantiate(activePrefab, hqPos, Quaternion.Euler(0, 0, DirToZ(outputDir)));
            var hq = hqGo.GetComponent<DroneHQ>();
            if (hq != null) hq.isGhost = false;
            try
            {
                var tag = hqGo.GetComponent<BuildCostTag>();
                if (tag == null) tag = hqGo.AddComponent<BuildCostTag>();
                tag.Cost = cost;
            }
            catch { }

            TryMarkMachineFootprint(footprint);

            var repairable = hqGo.GetComponent<Repairable>();
            if (repairable == null) repairable = hqGo.AddComponent<Repairable>();
            repairable.Initialize(footprint.ToArray());

            ClearPreviewState(clearActiveSelection: false);
            return;
        }

        if (ghostGO == null)
        {
            SpawnGhost(cell);
        }

        if (ghostGO != null)
        {
            ghostGO.transform.position = GetFootprintCenterWorld(cell, outputDir);
            ghostGO.transform.rotation = Quaternion.Euler(0, 0, DirToZ(outputDir));
            var task = ghostGO.GetComponent<BlueprintTask>();
            if (task == null) task = ghostGO.AddComponent<BlueprintTask>();
            bool isHq = activeName == "DroneHQ";
            int sortingOverride = RequiresSugarCell() ? mineSortingOrder : int.MinValue;
            task.InitializeMachine(footprint.ToArray(), outputDir, ghostGO.transform.rotation, activePrefab, cost, GetBuildSecondsForActiveName(activeName), isHq, sortingOverride);
        }

        // Successful placement: clear only temporary preview state, keep tool selection armed.
        ClearPreviewState(clearActiveSelection: false);
    }

    void TryRemoveBeltAtCell(Vector2Int cell)
    {
        if (grid == null) { CacheGrid(); if (grid == null) return; }
        try
        {
            var t = grid.GetType();
            var miGetConv = t.GetMethod("GetConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);
            var miSetConv = t.GetMethod("SetConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int), typeof(Conveyor) }, null);
            var miClearCell = t.GetMethod("ClearCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector2Int) }, null);

            Conveyor existing = null;
            if (miGetConv != null)
            {
                try { existing = miGetConv.Invoke(grid, new object[] { cell }) as Conveyor; } catch { existing = null; }
            }
            if (existing != null)
            {
                try { Destroy(existing.gameObject); } catch { }
            }
            if (miSetConv != null)
            {
                try { miSetConv.Invoke(grid, new object[] { cell, null }); } catch { }
            }
            if (miClearCell != null)
            {
                try { miClearCell.Invoke(grid, new object[] { cell }); } catch { }
            }
        }
        catch { }
    }

    void TryMarkMachineFootprint(List<Vector2Int> cells)
    {
        if (grid == null) { CacheGrid(); if (grid == null) return; }
        if (miSetMachineCell == null) return;
        foreach (var c in cells)
        {
            try { miSetMachineCell.Invoke(grid, new object[] { c }); } catch { }
        }
    }

    void CancelPreview()
    {
        if (ghostGO != null) Destroy(ghostGO);
        ClearPreviewState(clearActiveSelection: true);
        NotifySelectionChanged(null);
        TrySetBuildToolActive(false);
        HideWaterOverlay();
        HideSugarOverlay();
    }

    void ClearPreviewState(bool clearActiveSelection = true)
    {
        placing = false;
        placingPipePath = false;
        baseCell = default;
        ghostGO = null;
        ghostPress = null;
        ghostColorizer = null;
        ghostWaterPump = null;
        ghostStorage = null;
        ghostSolarPanel = null;
        ghostMine = null;
        ghostHq = null;
        if (clearActiveSelection) activePrefab = null;
        ClearPipeGhosts();
        if (clearActiveSelection) TrySetBuildToolActive(false);
    }

    static float DirToZ(Vector2Int d)
    {
        if (d == new Vector2Int(1, 0)) return 0f;
        if (d == new Vector2Int(0, 1)) return 90f;
        if (d == new Vector2Int(-1, 0)) return 180f;
        if (d == new Vector2Int(0, -1)) return 270f;
        return 0f;
    }

    static Vector3 GetMouseWorldOnPlane(Camera cam)
    {
        var mp = Input.mousePosition;
        float planeZ = 0f;
        float camZ = cam.transform.position.z;
        mp.z = planeZ - camZ;
        var world = cam.ScreenToWorldPoint(mp);
        world.z = 0f;
        return world;
    }

    bool RequiresWaterCell() => activeName == "WaterPump";
    bool RequiresSugarCell() => activeName == "Mine";

    bool IsWaterCell(Vector2Int cell)
    {
        if (grid == null || miIsWater == null) { CacheGrid(); if (grid == null || miIsWater == null) return false; }
        try { return (bool)miIsWater.Invoke(grid, new object[] { cell }); }
        catch { return false; }
    }

    bool IsSugarCell(Vector2Int cell)
    {
        var gs = grid as GridService ?? GridService.Instance ?? FindAnyObjectByType<GridService>();
        return gs != null && gs.IsSugar(cell);
    }

    bool IsAnyBlocked(List<Vector2Int> cells)
    {
        foreach (var c in cells) if (IsBlocked(c)) return true;
        return false;
    }

    bool CanAffordCost(int amount)
    {
        if (amount <= 0) return true;
        var gm = GameManager.Instance;
        if (gm == null) return true;
        return gm.SweetCredits >= amount;
    }

    List<Vector2Int> GetFootprintCells(Vector2Int origin, Vector2Int facing)
    {
        if (activeName == "StorageContainer" || activeName == "SolarPanel")
        {
            var dir = facing == Vector2Int.zero ? Vector2Int.right : facing;
            return new List<Vector2Int> { origin, origin + dir };
        }
        return new List<Vector2Int> { origin };
    }

    Vector3 GetFootprintCenterWorld(Vector2Int origin, Vector2Int facing)
    {
        var cells = GetFootprintCells(origin, facing);
        if (cells.Count == 0 || miCellToWorld == null) return Vector3.zero;
        Vector3 sum2 = Vector3.zero;
        foreach (var c in cells)
        {
            sum2 += (Vector3)miCellToWorld.Invoke(grid, new object[] { c, 0f });
        }
        return sum2 / cells.Count;
    }
}
