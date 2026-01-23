using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.EventSystems;

public class ConveyorPlacer : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GameObject conveyorPrefab;
    [Tooltip("Optional: provide a GameObject that hosts GridService (if you want to assign manually)")]
    [SerializeField] GameObject gridServiceObject;
    [SerializeField] LayerMask blockingMask;

    [Header("Placement")]
    [SerializeField] Color okColor = new Color(1f,1f,1f,0.5f);
    [SerializeField] Color blockedColor = new Color(1f,0f,0f,0.5f);
    [Tooltip("Additional Z rotation to apply to the visual prefab so artwork aligns with logical direction.")]
    [SerializeField] float visualRotationOffset = 0f;
    [Tooltip("Tint applied to conveyors placed while dragging (preview/ghost color).")]
    [SerializeField] Color ghostTint = new Color(0.15f, 0.4f, 0.85f, 0.7f);
    [Tooltip("Small sorting order offset applied to ghost previews to draw them just above existing belts without leaving mask range.")]
    [SerializeField] int ghostSortingOrderOffset = 1;

    [Header("Economy")]
    [SerializeField, Min(0)] int beltCost = 5;
    [Tooltip("Fallback belt build time used when MachineBuilder isn't available.")]
    [SerializeField, Min(0.1f)] float beltBuildSeconds = 0.4f;
    [SerializeField] bool refundBeltOnDelete = true;

    [Header("Camera Assist")]
    [SerializeField] bool assistCameraWhileDragging = true;
    [SerializeField] Camera assistCamera;
    [Tooltip("Start panning when pointer enters the outer half of the screen (0.5 = center line).")]
    [SerializeField, Range(0.1f, 0.5f)] float assistEdgeThreshold = 0.5f;
    [SerializeField, Min(0f)] float assistPanSpeed = 4f;
    [SerializeField] bool assistConstrainToGrid = true;
    [SerializeField, Min(0f)] float assistEdgePadding = 0.1f;

    Quaternion rotation = Quaternion.identity;

    class GhostData { public Color[] spriteColors; public int[] spriteOrders; public int[] spriteMaskInteractions; public RendererBlock[] rendererBlocks; public bool spawnedByPlacer; public int? sortingGroupOrder; public int? sortingGroupLayerId; }
    class RendererBlock { public Renderer renderer; public MaterialPropertyBlock block; }

    readonly Dictionary<Conveyor, GhostData> ghostOriginalColors = new Dictionary<Conveyor, GhostData>();
    readonly Dictionary<Vector2Int, Conveyor> ghostByCell = new Dictionary<Vector2Int, Conveyor>();
    readonly Dictionary<GameObject, GhostData> genericGhostOriginalColors = new Dictionary<GameObject, GhostData>();
    // delete overlays for junctions without a detectable visual
    readonly HashSet<GameObject> deleteOverlayGhosts = new HashSet<GameObject>();
    float cachedCellSize = 1f;

    GridAdapter gridAdapter;
    GhostVisuals ghostVisuals;
    DeleteService deleteService;
    ServiceCache serviceCache;

    // delete mode state
    bool isDeleting = false;
    readonly HashSet<Vector2Int> deleteMarkedCells = new HashSet<Vector2Int>();

    // Cached builder references for refund fallbacks (avoids repeated scene scans)
    MachineBuilder cachedMachineBuilder;
    bool didSearchMachineBuilder;
    JunctionBuilder cachedJunctionBuilder;
    bool didSearchJunctionBuilder;

    // Cached reflected fields for costs
    FieldInfo fiPressCost;
    FieldInfo fiColorizerCost;
    FieldInfo fiWaterPumpCost;
    FieldInfo fiWaterPipeCost;
    FieldInfo fiMineCost;
    FieldInfo fiShrederCost;
    FieldInfo fiSplitterCost;
    FieldInfo fiMergerCost;
    FieldInfo fiBeltBuildSeconds;

    MachineBuilder GetMachineBuilderCached()
    {
        if (!didSearchMachineBuilder)
        {
            didSearchMachineBuilder = true;
            cachedMachineBuilder = FindAnyObjectByType<MachineBuilder>();
            if (cachedMachineBuilder != null)
            {
                try
                {
                    var t = cachedMachineBuilder.GetType();
                    fiPressCost = t.GetField("pressCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fiColorizerCost = t.GetField("colorizerCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fiWaterPumpCost = t.GetField("waterPumpCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fiWaterPipeCost = t.GetField("waterPipeCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fiMineCost = t.GetField("mineCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fiShrederCost = t.GetField("shrederCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fiBeltBuildSeconds = t.GetField("beltBuildSeconds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                catch { }
            }
        }
        return cachedMachineBuilder;
    }

    JunctionBuilder GetJunctionBuilderCached()
    {
        if (!didSearchJunctionBuilder)
        {
            didSearchJunctionBuilder = true;
            cachedJunctionBuilder = FindAnyObjectByType<JunctionBuilder>();
            if (cachedJunctionBuilder != null)
            {
                try
                {
                    var t = cachedJunctionBuilder.GetType();
                    fiSplitterCost = t.GetField("splitterCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    fiMergerCost = t.GetField("mergerCost", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                catch { }
            }
        }
        return cachedJunctionBuilder;
    }

    int GetRefundCostForPlacedObject(GameObject go)
    {
        if (go == null) return 0;
        try
        {
            var tag = go.GetComponentInParent<BuildCostTag>();
            if (tag == null) tag = go.GetComponentInChildren<BuildCostTag>(true);
            if (tag != null) return Mathf.Max(0, tag.Cost);
        }
        catch { }

        // Fallback inference for older placed objects without BuildCostTag
        try
        {
            var mb = GetMachineBuilderCached();
            if (mb != null)
            {
                int FieldCost(FieldInfo fi)
                {
                    try
                    {
                        if (fi == null) return 0;
                        return Mathf.Max(0, (int)fi.GetValue(mb));
                    }
                    catch { return 0; }
                }

                if (go.GetComponentInParent<PressMachine>() != null) return FieldCost(fiPressCost);
                if (go.GetComponentInParent<ColorizerMachine>() != null) return FieldCost(fiColorizerCost);
                if (go.GetComponentInParent<WaterPump>() != null) return FieldCost(fiWaterPumpCost);
                if (go.GetComponentInParent<SugarMine>() != null) return FieldCost(fiMineCost);
                if (go.GetComponentInParent<WaterPipe>() != null) return FieldCost(fiWaterPipeCost);

                // "Shreder" is a project spelling; infer from names if no dedicated component type.
                if (go.name.Contains("Shreder") || go.name.Contains("Shredder")) return FieldCost(fiShrederCost);
                try
                {
                    var monos = go.GetComponentsInChildren<MonoBehaviour>(true);
                    foreach (var mono in monos)
                    {
                        if (mono == null) continue;
                        var n = mono.GetType().Name;
                        if (n.Contains("Shreder") || n.Contains("Shredder")) return FieldCost(fiShrederCost);
                    }
                }
                catch { }
            }
        }
        catch { }

        try
        {
            var jb = GetJunctionBuilderCached();
            if (jb != null)
            {
                int FieldCost(FieldInfo fi)
                {
                    try
                    {
                        if (fi == null) return 0;
                        return Mathf.Max(0, (int)fi.GetValue(jb));
                    }
                    catch { return 0; }
                }

                if (go.name.Contains("Splitter")) return FieldCost(fiSplitterCost);
                if (go.name.Contains("Merger")) return FieldCost(fiMergerCost);
            }
        }
        catch { }

        return 0;
    }

    float GetBeltBuildSeconds()
    {
        var mb = GetMachineBuilderCached();
        if (mb != null && fiBeltBuildSeconds != null)
        {
            try
            {
                var val = fiBeltBuildSeconds.GetValue(mb);
                if (val is float f) return Mathf.Max(0.1f, f);
            }
            catch { }
        }
        return beltBuildSeconds;
    }

    void RefundFullBuildCost(GameObject go)
    {
        try
        {
            int refund = GetRefundCostForPlacedObject(go);
            if (refund > 0) GameManager.Instance?.AddSweetCredits(refund);
        }
        catch { }
    }

    // reflection cache for GridService
    object gridServiceInstance;
    MethodInfo miWorldToCell; // Vector2Int WorldToCell(Vector3)
    MethodInfo miCellToWorld; // Vector3 CellToWorld(Vector2Int, float)
    MethodInfo miSetBeltCell; // void SetBeltCell(Vector2Int, Direction, Direction)
    Type directionType;

    // drag state
    Vector2Int lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
    Vector2Int dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
    Vector3 dragStartWorld = Vector3.zero;
    bool isDragging = false;
    bool dragHasMoved = false; // Track if the mouse has moved to a new cell after starting a drag

    // Track the current dragged path to avoid overlapping and to support backtracking
    readonly List<Vector2Int> dragPath = new List<Vector2Int>();

    HashSet<Vector2Int> deferredRegistrations = new HashSet<Vector2Int>();

    void EnsureHelpers()
    {
        if (gridAdapter == null) gridAdapter = new GridAdapter(this);
        if (ghostVisuals == null) ghostVisuals = new GhostVisuals(this);
        if (deleteService == null) deleteService = new DeleteService(this);
        if (serviceCache == null) serviceCache = new ServiceCache(this);
    }

    void Reset()
    {
        EnsureHelpers();
        if (gridServiceObject == null)
        {
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "GridService") { gridServiceObject = mb.gameObject; break; }
            }
        }
        if (gridServiceObject != null) CacheGridServiceReflection(gridServiceObject);
    }

    void Awake()
    {
        EnsureHelpers();
        if (gridServiceObject != null && gridServiceInstance == null)
            CacheGridServiceReflection(gridServiceObject);
    }

    void CacheGridServiceReflection(GameObject go)
    {
        EnsureHelpers();
        gridAdapter.CacheGridServiceReflection(go);
    }

    // Called by BuildModeController when entering build mode
    public void BeginPreview()
    {
        rotation = Quaternion.identity;
        // ensure GridService is available early
        EnsureGridServiceCached();
        lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
        dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
        isDragging = false;
        deferredRegistrations.Clear();
        // Clear any leftover ghost visuals from previous sessions
        RestoreGhostVisuals(false);
        ghostOriginalColors.Clear(); ghostByCell.Clear(); genericGhostOriginalColors.Clear(); deleteOverlayGhosts.Clear();
        deleteMarkedCells.Clear();
        isDeleting = false;
        dragPath.Clear();
    }

    // Called by BuildModeController when exiting build mode
    public void EndPreview()
    {
        // reset drag state
        lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
        dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
        isDragging = false;
        deferredRegistrations.Clear();
        // ensure any ghost visuals are cleared
        RestoreGhostVisuals(false);
        ghostOriginalColors.Clear(); ghostByCell.Clear(); genericGhostOriginalColors.Clear(); deleteOverlayGhosts.Clear();
        deleteMarkedCells.Clear();
        isDeleting = false;
        dragPath.Clear();
    }

    public void RotatePreview() => rotation = Quaternion.Euler(0,0, Mathf.Round((rotation.eulerAngles.z + 90f) % 360f));

    // Public API to toggle delete mode (call from UI button)
    public void StartDeleteMode()
    {
        isDeleting = true;
        // clear any existing ghost state
        RestoreGhostVisuals(false);
        ghostOriginalColors.Clear(); ghostByCell.Clear(); genericGhostOriginalColors.Clear();
        deleteMarkedCells.Clear();
        dragPath.Clear();
    }

    public void EndDeleteMode()
    {
        isDeleting = false;
        RestoreGhostVisuals(false);
        ghostOriginalColors.Clear(); ghostByCell.Clear(); genericGhostOriginalColors.Clear();
        deleteMarkedCells.Clear();
        dragPath.Clear();
    }

    // Public immediate delete at mouse (tap)
    public bool TryDeleteAtMouse()
    {
        if (!EnsureGridServiceCached() || miWorldToCell == null) return false;
        var world = GetMouseWorld();
        var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
        var cell = res is Vector2Int v ? v : new Vector2Int(0,0);
        return DeleteCellImmediate(cell);
    }

    // Called when the user presses pointer down to start placing/dragging belts.
    // This immediately places the first belt at the pointer-down cell so there is
    // something to reorient when the drag moves.
    public bool OnPointerDown()
    {
        if (!EnsureGridServiceCached() || miWorldToCell == null) return false;
        
        // Prevent placement when clicking on UI elements
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return false;

        var world = GetMouseWorld();
        var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
        var cell = res is Vector2Int v ? v : new Vector2Int(0,0);

        dragHasMoved = false; // Reset drag movement flag

        if (isDeleting)
        {
            isDragging = true; dragStartCell = cell; lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragStartWorld = world;
            MarkCellForDeletion(cell);
            return true;
        }

        // Set up for a drag, but DO NOT place the initial belt or set lastDragCell.
        // This will be handled on the first actual drag movement.
        isDragging = true; 
        dragStartCell = cell; 
        lastDragCell = new Vector2Int(int.MinValue, int.MinValue); // Keep lastDragCell invalid
        dragStartWorld = world;
        dragPath.Clear();
        
        return true;
    }

    // Called when the user releases the pointer. If no dragging movement occurred
    // then place a single belt at the initial cell (already handled here).
    public bool OnPointerUp()
    {
        if (!isDragging) return false; 
        isDragging = false;
        
        if (isDeleting)
        {
            // commit deletions
            CommitMarkedDeletions(); RestoreGhostVisuals(false); deleteMarkedCells.Clear(); lastDragCell = dragStartCell = new Vector2Int(int.MinValue, int.MinValue); dragPath.Clear(); return true;
        }
        
        // If the drag never moved, it's a single click. Place a single belt now.
        if (!dragHasMoved)
        {
            RestoreGhostVisuals(false); 
            PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation));
            FlushDeferredRegistrations();
            // Prevent an instant chain pull on the very next step
            BeltSimulationService.Instance?.SuppressNextStepPulls();
            dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
            dragPath.Clear();
            return true;
        }

        // Commit ghosts -> real belts and register to grid/sim
        RestoreGhostVisuals(true);
        FlushDeferredRegistrations();
        // Prevent an instant chain pull on the very next step after committing a path
        BeltSimulationService.Instance?.SuppressNextStepPulls();
        dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
        dragPath.Clear();
        return true; // Return true since a drag operation is a successful placement
    }

    void FlushDeferredRegistrations()
    {
        if (deferredRegistrations.Count == 0) return;
        
        // Only flush if we're not currently dragging
        if (BuildModeController.IsDragging)
        {
            return; // Don't flush while still dragging
        }
        
        foreach (var c in deferredRegistrations) TryRegisterCellInBeltSim(c);
        deferredRegistrations.Clear();
        MarkGraphDirtyIfPresent();
    }

    // This method should be called every frame while in build mode to update the
    // preview and handle drag placement. It contains the core drag logic:
    // - Do not change a placed belt's direction while pointer remains inside that belt's cell.
    // - When pointer moves out of a cell into the next cell, update the previous cell's
    //   direction to point toward the next cell and then place the next cell.
    public void UpdatePreviewPosition()
    {
        // Ignore input when pointer is over UI to avoid accidental placement when clicking buttons
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        UpdateCameraAssist();

        if (!EnsureGridServiceCached() || miWorldToCell == null) return;
        var world = GetMouseWorld();
        var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
        var cell = res is Vector2Int v ? v : new Vector2Int(0,0);

        if (Input.GetMouseButton(0) && isDragging)
        {
            if (isDeleting) { TryMarkCellFromDrag(cell); return; }

            // The initial belt is now placed in OnPointerDown, so we don't need to check for it here.
            
            TryPlaceCellFromDrag(cell);
            // Do NOT mark graph dirty while dragging; commit on release
            return;
        }
        if (Input.GetMouseButtonUp(0) && isDragging)
        {
            
            if (isDeleting)
            {
                CommitMarkedDeletions(); RestoreGhostVisuals(false); deleteMarkedCells.Clear(); isDragging = false; dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragPath.Clear(); return;
            }
            
            // DON'T place a belt here; OnPointerUp handles commit.
            isDragging = false; RestoreGhostVisuals(true); dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragStartWorld = Vector3.zero; FlushDeferredRegistrations(); dragPath.Clear();
        }
    }

    void UpdateCameraAssist()
    {
        if (!assistCameraWhileDragging || isDeleting) return;
        if (!isDragging || !Input.GetMouseButton(0)) return;
        if (assistCamera == null) assistCamera = Camera.main;
        if (assistCamera == null) return;

        var vp = assistCamera.ScreenToViewportPoint(Input.mousePosition);
        float edge = Mathf.Clamp(assistEdgeThreshold, 0.01f, 0.5f);
        float left = edge;
        float right = 1f - edge;
        float bottom = edge;
        float top = 1f - edge;

        float xDir = 0f;
        if (vp.x < left) xDir = -Mathf.InverseLerp(left, 0f, vp.x);
        else if (vp.x > right) xDir = Mathf.InverseLerp(right, 1f, vp.x);

        float yDir = 0f;
        if (vp.y < bottom) yDir = -Mathf.InverseLerp(bottom, 0f, vp.y);
        else if (vp.y > top) yDir = Mathf.InverseLerp(top, 1f, vp.y);

        if (Mathf.Approximately(xDir, 0f) && Mathf.Approximately(yDir, 0f)) return;

        var delta = new Vector3(xDir, yDir, 0f) * assistPanSpeed * Time.deltaTime;
        var pos = assistCamera.transform.position + delta;
        ClampCameraToGrid(ref pos);
        assistCamera.transform.position = pos;
    }

    void ClampCameraToGrid(ref Vector3 pos)
    {
        if (!assistConstrainToGrid) return;
        if (assistCamera == null) return;
        if (!assistCamera.orthographic) return;

        var grid = GridService.Instance;
        if (grid == null) return;

        var origin = (Vector2)grid.Origin;
        var size = grid.GridSize;
        float cs = grid.CellSize;
        float minX = origin.x;
        float minY = origin.y;
        float maxX = origin.x + size.x * cs;
        float maxY = origin.y + size.y * cs;

        float halfH = assistCamera.orthographicSize;
        float halfW = halfH * assistCamera.aspect;
        float pad = assistEdgePadding;

        float clampMinX = minX + halfW + pad;
        float clampMaxX = maxX - halfW - pad;
        float clampMinY = minY + halfH + pad;
        float clampMaxY = maxY - halfH - pad;

        if (clampMinX > clampMaxX) { pos.x = (minX + maxX) * 0.5f; }
        else pos.x = Mathf.Clamp(pos.x, clampMinX, clampMaxX);
        if (clampMinY > clampMaxY) { pos.y = (minY + maxY) * 0.5f; }
        else pos.y = Mathf.Clamp(pos.y, clampMinY, clampMaxY);
    }

    // Rotate the tail belt toward the current pointer position while still inside the same cell
    void UpdateTailDirectionToPointer(Vector3 pointerWorld)
    {
        if (!isDragging || isDeleting) return;
        // Determine which cell is currently the tail we should rotate
        Vector2Int tailCell = new Vector2Int(int.MinValue, int.MinValue);
        if (dragPath.Count > 0) tailCell = dragPath[dragPath.Count - 1];
        else if (lastDragCell.x != int.MinValue) tailCell = lastDragCell;
        else if (dragStartCell.x != int.MinValue) tailCell = dragStartCell;
        if (tailCell.x == int.MinValue) return;

        // Get center of tail cell
        Vector3 center = Vector3.zero;
        try { var worldObj = miCellToWorld?.Invoke(gridServiceInstance, new object[] { tailCell, 0f }); center = worldObj is Vector3 vv ? vv : Vector3.zero; } catch { }
        var delta = (Vector2)(pointerWorld - center);
        if (delta.magnitude < 0.05f) return; // too small to decide a direction
        Vector2Int step = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y) ? new Vector2Int(Math.Sign(delta.x), 0) : new Vector2Int(0, Math.Sign(delta.y));
        // Prevent pointing directly back toward the previous segment to avoid conflicts
        Vector2Int originDir = Vector2Int.zero;
        if (dragPath.Count >= 2)
        {
            var prev = dragPath[dragPath.Count - 2];
            originDir = tailCell - prev;
        }
        if (originDir != Vector2Int.zero && step == -originDir) return;
        var dirName = DirectionNameFromDelta(step);
        if (dirName == null) return;
        UpdateBeltDirectionFast(tailCell, dirName);
    }

    void TryPlaceCellFromDrag(Vector2Int cell)
    {
        // If the drag just started, lastDragCell is invalid. This is the first move.
        if (lastDragCell.x == int.MinValue)
        {
            // Don't do anything if the mouse is still in the start cell
            if (cell == dragStartCell) return;

            // This is the first actual movement. Place the STARTING belt now,
            // oriented towards the current cell.
            var delta = cell - dragStartCell;
            Vector2Int step = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y) ? new Vector2Int(Math.Sign(delta.x), 0) : new Vector2Int(0, Math.Sign(delta.y));
            var dirName = DirectionNameFromDelta(step);
            if (dirName == null) return;

            // Place the very first belt and add it to the path
            if (IsCellBlockedForBelt(dragStartCell)) return;
            dragHasMoved = true;
            if (PlaceBeltAtCell(dragStartCell, dirName))
            {
                dragPath.Add(dragStartCell);
            }
            else
            {
                return;
            }
            
            // Now, place the second belt to start the chain
            var nextCell = dragStartCell + step;
            if (IsCellBlockedForBelt(nextCell))
            {
                lastDragCell = dragStartCell;
                return;
            }
            // Ensure no duplicate ghost at next cell
            RemoveGhostAtCell(nextCell);
            if (PlaceBeltAtCell(nextCell, dirName))
            {
                dragPath.Add(nextCell);
            }
            else
            {
                lastDragCell = dragStartCell;
                return;
            }

            // IMPORTANT: Update lastDragCell and exit.
            lastDragCell = nextCell;
            return;
        }

        if (cell == lastDragCell) return;

        var deltaMove = cell - lastDragCell;
        Vector2Int stepMove = Mathf.Abs(deltaMove.x) >= Mathf.Abs(deltaMove.y) ? new Vector2Int(Math.Sign(deltaMove.x), 0) : new Vector2Int(0, Math.Sign(deltaMove.y));
        var nextCellMove = lastDragCell + stepMove;
        var dirNameNext = DirectionNameFromDelta(stepMove);
        if (dirNameNext == null) return;

        // Handle backtracking: if moving into the previous cell in the path, pop the tail ghost instead of placing a new one
        if (dragPath.Count >= 2)
        {
            var prevCell = dragPath[dragPath.Count - 2];
            if (nextCellMove == prevCell)
            {
                // Remove current tail ghost
                var tail = dragPath[dragPath.Count - 1];
                RemoveGhostAtCell(tail);
                dragPath.RemoveAt(dragPath.Count - 1);
                // Orient the new tail toward the direction we're moving (optional for visual feedback)
            UpdateBeltDirectionFast(prevCell, dirNameNext);
                lastDragCell = prevCell;
                return;
            }
        }

        // If we moved back multiple cells at once (fast mouse), remove all tail cells until the new cell is the last
        int idx = dragPath.IndexOf(nextCellMove);
        if (idx != -1 && idx < dragPath.Count - 1)
        {
            for (int i = dragPath.Count - 1; i > idx; i--)
            {
                RemoveGhostAtCell(dragPath[i]);
                dragPath.RemoveAt(i);
            }
            // Re-orient last to face toward next direction
            UpdateBeltDirectionFast(dragPath[dragPath.Count - 1], dirNameNext);
            lastDragCell = dragPath[dragPath.Count - 1];
            return;
        }

        // Ensure the previous belt now points toward this next step (rotate second-to-last)
        Direction? incomingDirection = null;
        if (dragPath.Count >= 2)
        {
            var prevCell = dragPath[dragPath.Count - 2];
            incomingDirection = DirectionFromDelta(lastDragCell - prevCell);
        }
        UpdateBeltDirectionFast(lastDragCell, dirNameNext, incomingDirection);

        if (IsCellBlockedForBelt(nextCellMove)) return;

        // Avoid duplicating ghosts in the next cell
        RemoveGhostAtCell(nextCellMove);

        // Place the next belt in the direction of movement
        if (PlaceBeltAtCell(nextCellMove, dirNameNext))
        {
            dragPath.Add(nextCellMove);
        }

        // Update the last dragged cell
        lastDragCell = nextCellMove;
    }

    void RemoveGhostAtCell(Vector2Int cell)
    {
        EnsureHelpers();
        ghostVisuals.RemoveGhostAtCell(cell);
    }

    // Restore: delete-mode drag helper to mark cells along drag path
    void TryMarkCellFromDrag(Vector2Int cell)
    {
        EnsureHelpers();
        deleteService.TryMarkCellFromDrag(cell);
    }

    void MarkCellForDeletion(Vector2Int cell)
    {
        EnsureHelpers();
        deleteService.MarkCellForDeletion(cell);
    }

    void CommitMarkedDeletions()
    {
        EnsureHelpers();
        deleteService.CommitMarkedDeletions();
    }

    // Public immediate delete at mouse (tap)
    public bool DeleteCellImmediate(Vector2Int cell)
    {
        EnsureHelpers();
        return deleteService.DeleteCellImmediate(cell);
    }

    void ApplyGhostToConveyor(Conveyor conv)
    {
        EnsureHelpers();
        ghostVisuals.ApplyGhostToConveyor(conv);
    }

    void ApplyDeleteGhostToConveyor(Conveyor conv)
    {
        EnsureHelpers();
        ghostVisuals.ApplyDeleteGhostToConveyor(conv);
    }

    void ApplyDeleteGhostToGameObject(GameObject go)
    {
        EnsureHelpers();
        ghostVisuals.ApplyDeleteGhostToGameObject(go);
    }

    void RestoreGhostVisuals(bool commitBlueprints)
    {
        EnsureHelpers();
        ghostVisuals.RestoreGhostVisuals(commitBlueprints);
    }

    void CreateBeltBlueprintFromGhost(Conveyor conv, Vector2Int cell)
    {
        if (conv == null) return;
        conv.isGhost = true;
        var task = conv.GetComponent<BlueprintTask>();
        if (task == null) task = conv.gameObject.AddComponent<BlueprintTask>();
        task.InitializeBelt(cell, conv.direction, conv.transform.rotation, conveyorPrefab, beltCost, GetBeltBuildSeconds());
        if (conv.IsCurve)
            task.UpdateBeltCurve(conv.CurveFrom, conv.CurveTo, conv.transform.rotation);
    }

    // Copy sorting layer from existing conveyor at cell so ghost draws in the same layer
    void MatchGhostSortingLayer(Conveyor ghost, Vector2Int cell)
    {
        EnsureHelpers();
        ghostVisuals.MatchGhostSortingLayer(ghost, cell);
    }

    bool PlaceBeltAtCell(Vector2Int cell2, string outDirName)
    {
        if (miCellToWorld == null) return false;
        var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero;

        if (IsCellBlockedForBelt(cell2)) return false;

        if (!isDragging && !BuildModeController.IsDragging)
        {
            try
            {
                var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                if (getConv != null)
                {
                    var existing = getConv.Invoke(gridServiceInstance, new object[] { cell2 }) as Conveyor;
                    if (existing != null && !existing.isGhost) return false;
                }
            }
            catch { }
        }

        if (!isDragging)
        {
            var hits = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f, blockingMask);
            if (hits != null && hits.Length > 0)
            {
                foreach (var h in hits)
                {
                    if (h == null) continue;
                    if (h.GetComponentInParent<Conveyor>() != null) continue;
                    return false;
                }
            }
        }

        if (!isDragging)
        {
            if (!TrySpendBeltCost(cell2)) return false;
        }

        if (conveyorPrefab != null)
        {
            try
            {
                var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                if (getConv != null)
                {
                    var existing = getConv.Invoke(gridServiceInstance, new object[] { cell2 }) as Conveyor;
                    if (existing != null && existing.isGhost)
                    {
                        try { Destroy(existing.gameObject); } catch { }
                        try 
                        { 
                            var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) });
                            if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell2, null });
                        } catch { }
                    }
                }
            }
            catch { }

            float z = outDirName switch { "Right" => 0f, "Up" => 90f, "Left" => 180f, "Down" => 270f, _ => 0f }; z += visualRotationOffset;
            Vector3 position = center;
            // Keep Z consistent so ghosts aren't hidden by transparency sort settings
            // if (isDragging || BuildModeController.IsDragging) position.z = -0.1f;

            var parent = ContainerLocator.GetBeltContainer();
            var go = parent != null ? Instantiate(conveyorPrefab, position, Quaternion.Euler(0,0,z), parent) : Instantiate(conveyorPrefab, position, Quaternion.Euler(0,0,z));
            try
            {
                var conv = go.GetComponent<Conveyor>() ?? go.GetComponentInChildren<Conveyor>(true);
                if (conv != null)
                {
                    var outDir = DirectionFromName(outDirName);
                    conv.SetStraight(outDir, visualRotationOffset);

                    if (isDragging || BuildModeController.IsDragging)
                    {
                        conv.isGhost = true;
                        MatchGhostSortingLayer(conv, cell2);
                        ApplyGhostToConveyor(conv);
                        try { ghostByCell[cell2] = conv; } catch { }
                        return true;
                    }
                    else
                    {
                        conv.isGhost = true;
                        var task = conv.GetComponent<BlueprintTask>();
                        if (task == null) task = conv.gameObject.AddComponent<BlueprintTask>();
                        task.InitializeBelt(cell2, conv.direction, conv.transform.rotation, conveyorPrefab, beltCost, GetBeltBuildSeconds());
                        return true;
                    }
                }
            }
            catch { }
        }

        if (!isDragging && !BuildModeController.IsDragging)
        {
            var go = new GameObject("BeltBlueprint");
            go.transform.position = center;
            var task = go.AddComponent<BlueprintTask>();
            var dir = DirectionFromName(outDirName);
            float z = outDirName switch { "Right" => 0f, "Up" => 90f, "Left" => 180f, "Down" => 270f, _ => 0f };
            task.InitializeBelt(cell2, dir, Quaternion.Euler(0f, 0f, z), null, beltCost, GetBeltBuildSeconds());
            return true;
        }

        return true;
    }

    bool UpdateBeltDirectionAtCell(Vector2Int cell2, string outDirName)
    {
        try
        {
            Conveyor conv = null;
            try { var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) }); if (getConv != null) conv = getConv.Invoke(gridServiceInstance, new object[] { cell2 }) as Conveyor; } catch { }
            if (conv == null && miCellToWorld != null)
            {
                try { var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero; var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f); foreach (var col in colliders) { var c = col.GetComponent<Conveyor>() ?? col.GetComponentInChildren<Conveyor>(true); if (c != null) { conv = c; break; } } } catch { }
            }
            if (conv != null)
            {
                try
                {
                    var outDir = DirectionFromName(outDirName);
                    conv.SetStraight(outDir, visualRotationOffset);
                    var task = conv.GetComponent<BlueprintTask>();
                    if (task != null)
                        task.UpdateBeltDirection(outDir, conv.transform.rotation);
                }
                catch { }
            }
            
            if (isDragging || BuildModeController.IsDragging) return true;

            var oppNameFallback = outDirName == "Right" ? "Left" : outDirName == "Left" ? "Right" : outDirName == "Up" ? "Down" : outDirName == "Down" ? "Up" : "None";
            object outDirObjFallback = null, inDirObjFallback = null;
            if (directionType != null) { try { outDirObjFallback = Enum.Parse(directionType, outDirName); inDirObjFallback = Enum.Parse(directionType, oppNameFallback); } catch { outDirObjFallback = null; inDirObjFallback = null; } }
            if (outDirObjFallback == null || inDirObjFallback == null) { int outIndex = DirectionIndexFromName(outDirName); int inIndex = DirectionIndexFromName(oppNameFallback); outDirObjFallback = Enum.ToObject(directionType ?? typeof(Direction), outIndex); inDirObjFallback = Enum.ToObject(directionType ?? typeof(Direction), inIndex); }
            try { miSetBeltCell?.Invoke(gridServiceInstance, new object[] { cell2, inDirObjFallback, outDirObjFallback }); } catch { }
            try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell2, conv }); } catch { }
            RegisterOrDefer(cell2); TryRegisterCellInBeltSim(cell2); MarkGraphDirtyIfPresent();
            return true;
        }
        catch { return false; }
    }

    void TryRegisterCellInBeltSim(Vector2Int cell)
    {
        if (GameManager.Instance != null && GameManager.Instance.State == GameState.Build && BuildModeController.IsDragging) return;
        EnsureHelpers();
        serviceCache.RegisterCell(cell);
    }

    public void RefreshPreviewAfterPlace() { }

    void MarkGraphDirtyIfPresent()
    {
        EnsureHelpers();
        serviceCache.MarkGraphDirty();
    }

    Vector3 GetMouseWorld()
    {
        var cam = Camera.main; var pos = Input.mousePosition; var world = cam != null ? cam.ScreenToWorldPoint(pos) : new Vector3(pos.x, pos.y, 0f); world.z = 0f; return world;
    }

    bool EnsureGridServiceCached()
    {
        EnsureHelpers();
        return gridAdapter.EnsureGridServiceCached();
    }

    bool TrySpendBeltCost(Vector2Int cell)
    {
        if (beltCost <= 0) return true;
        var gm = GameManager.Instance;
        if (gm == null) return true;
        if (!EnsureGridServiceCached()) return true;
        if (HasExistingRealBelt(cell)) return true;
        return gm.TrySpendSweetCredits(beltCost);
    }

    bool ShouldRefundBeltAtCell(Vector2Int cell)
    {
        if (!refundBeltOnDelete || beltCost <= 0) return false;
        if (GameManager.Instance == null) return false;
        if (!EnsureGridServiceCached()) return false;

        try
        {
            var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
            if (getConv != null)
            {
                var existing = getConv.Invoke(gridServiceInstance, new object[] { cell }) as Conveyor;
                if (existing != null && !existing.isGhost) return true;
            }
        }
        catch { }

        if (TryGetLogicalCellInfo(cell, out var hasConv, out var typeName, out _, out _))
        {
            if (hasConv) return true;
            if (typeName == "Belt") return true;
        }

        return false;
    }

    bool HasExistingRealBelt(Vector2Int cell)
    {
        if (!EnsureGridServiceCached()) return false;
        try
        {
            var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
            if (getConv != null)
            {
                var existing = getConv.Invoke(gridServiceInstance, new object[] { cell }) as Conveyor;
                if (existing != null && !existing.isGhost) return true;
            }
        }
        catch { }

        if (TryGetLogicalCellInfo(cell, out var hasConv, out var typeName, out _, out _))
        {
            if (hasConv) return true;
            if (typeName == "Belt" || typeName == "Junction") return true;
        }

        return false;
    }

    bool IsCellBlockedForBelt(Vector2Int cell)
    {
        if (!EnsureGridServiceCached()) return false;
        if (HasExistingRealBelt(cell)) return true;
        if (TryGetLogicalCellInfo(cell, out var hasConv, out var typeName, out var isBlueprint, out var isBroken))
        {
            if (isBlueprint || isBroken) return true;
            if (hasConv) return true;
            if (typeName == "Machine" || typeName == "Belt" || typeName == "Junction") return true;
        }
        try
        {
            if (MachineRegistry.TryGet(cell, out var machine) && machine != null) return true;
        }
        catch { }
        if (FindMachineAtCell(cell) != null) return true;
        return false;
    }

    bool TryGetLogicalCellInfo(Vector2Int cell, out bool hasConveyor, out string typeName, out bool isBlueprint, out bool isBroken)
    {
        hasConveyor = false;
        typeName = null;
        isBlueprint = false;
        isBroken = false;
        if (!EnsureGridServiceCached()) return false;
        try
        {
            var getCell = gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
            if (getCell == null) return false;
            var cellObj = getCell.Invoke(gridServiceInstance, new object[] { cell });
            if (cellObj == null) return false;
            var t = cellObj.GetType();
            var fiHasConv = t.GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fiType = t.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fiBlueprint = t.GetField("isBlueprint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fiBroken = t.GetField("isBroken", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fiHasConv != null)
            {
                try { hasConveyor = (bool)fiHasConv.GetValue(cellObj); } catch { hasConveyor = false; }
            }
            if (fiType != null)
            {
                typeName = fiType.GetValue(cellObj)?.ToString();
            }
            if (fiBlueprint != null)
            {
                try { isBlueprint = (bool)fiBlueprint.GetValue(cellObj); } catch { isBlueprint = false; }
            }
            if (fiBroken != null)
            {
                try { isBroken = (bool)fiBroken.GetValue(cellObj); } catch { isBroken = false; }
            }
            return true;
        }
        catch { return false; }
    }

    bool TryGetBlueprintAtCell(Vector2Int cell, out BlueprintTask task)
    {
        task = null;
        try
        {
            var tasks = UnityEngine.Object.FindObjectsByType<BlueprintTask>(FindObjectsSortMode.None);
            foreach (var t in tasks)
            {
                if (t == null) continue;
                if (t.ContainsCell(cell))
                {
                    task = t;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    bool TryCancelBlueprintAtCell(Vector2Int cell)
    {
        if (!TryGetBlueprintAtCell(cell, out var task)) return false;
        if (task == null) return false;
        task.CancelFromDelete();
        return true;
    }

    int DirectionIndexFromName(string name)
    {
        return name switch { "Up" => 0, "Right" => 1, "Down" => 2, "Left" => 3, _ => 4 };
    }

    static Direction DirectionFromName(string name)
    {
        return name switch
        {
            "Up" => Direction.Up,
            "Right" => Direction.Right,
            "Down" => Direction.Down,
            "Left" => Direction.Left,
            _ => Direction.None,
        };
    }

    string DirectionNameFromDelta(Vector2Int delta)
    {
        if (delta.x > 0) return "Right"; if (delta.x < 0) return "Left"; if (delta.y > 0) return "Up"; if (delta.y < 0) return "Down"; return null;
    }

    static Direction DirectionFromDelta(Vector2Int delta)
    {
        if (delta.x > 0) return Direction.Right;
        if (delta.x < 0) return Direction.Left;
        if (delta.y > 0) return Direction.Up;
        if (delta.y < 0) return Direction.Down;
        return Direction.None;
    }

    string RotationToDirectionName(Quaternion q)
    {
        var z = Mathf.Repeat(q.eulerAngles.z, 360f); var snapped = Mathf.Round(z / 90f) * 90f; int i = Mathf.RoundToInt(snapped) % 360;
        return i switch { 0 => "Right", 90 => "Up", 180 => "Left", 270 => "Down", _ => "Right" };
    }

    Type FindDirectionType()
    {
        EnsureHelpers();
        return gridAdapter.FindDirectionType();
    }

    GameObject FindBeltVisualAtCell(Vector2Int cell)
    {
        try
        {
            var worldObj = miCellToWorld?.Invoke(gridServiceInstance, new object[] { cell, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero;
            var container = ContainerLocator.GetBeltContainer(); if (container != null)
            {
                foreach (Transform child in container)
                {
                    if (child == null) continue;
                    var p = child.position;
                    if (Mathf.Abs(p.x - center.x) < 0.05f && Mathf.Abs(p.y - center.y) < 0.05f)
                    {
                        var conv = child.GetComponent<Conveyor>() ?? child.GetComponentInChildren<Conveyor>(true);
                        if (conv != null) return child.gameObject;
                    }
                }
            }
            var allRoots = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
            foreach (var tr in allRoots)
            {
                if (tr == null) continue;
                if (Mathf.Abs(tr.position.x - center.x) < 0.05f && Mathf.Abs(tr.position.y - center.y) < 0.05f)
                {
                    var conv = tr.GetComponent<Conveyor>() ?? tr.GetComponentInChildren<Conveyor>(true);
                    if (conv != null) return tr.gameObject;
                    var pipe = tr.GetComponent<WaterPipe>() ?? tr.GetComponentInChildren<WaterPipe>(true);
                    if (pipe != null) return tr.gameObject;
                }
            }
        }
        catch { }
        return null;
    }

    GameObject FindPipeAtCell(Vector2Int cell)
    {
        try
        {
            var worldObj = miCellToWorld?.Invoke(gridServiceInstance, new object[] { cell, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero;
            var pipes = UnityEngine.Object.FindObjectsByType<WaterPipe>(FindObjectsSortMode.None);
            foreach (var p in pipes)
            {
                if (p == null) continue;
                var pos = p.transform.position;
                if (Mathf.Abs(pos.x - center.x) < 0.05f && Mathf.Abs(pos.y - center.y) < 0.05f)
                    return p.gameObject;
            }
        }
        catch { }
        return null;
    }

    // Helper: find a machine GameObject (press/mine/etc.) at a cell center
    GameObject FindMachineAtCell(Vector2Int cell)
    {
        try
        {
            if (MachineRegistry.TryGet(cell, out var machine) && machine is MonoBehaviour mb)
                return mb.gameObject;
        }
        catch { }

        try
        {
            var storages = UnityEngine.Object.FindObjectsByType<StorageContainerMachine>(FindObjectsSortMode.None);
            foreach (var storage in storages)
            {
                if (storage == null || storage.isGhost) continue;
                var inputCell = storage.Cell;
                var footprintCell = inputCell + storage.OutputVec;
                if (cell == inputCell || cell == footprintCell)
                    return storage.gameObject;
            }
        }
        catch { }

        Vector3 center = Vector3.zero;
        float tol = 0.05f;
        bool hasCenter = false;
        try
        {
            if (miCellToWorld == null || gridServiceInstance == null) return null;
            var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell, 0f });
            center = worldObj is Vector3 vv ? vv : Vector3.zero;
            hasCenter = worldObj is Vector3;
            tol = Mathf.Max(0.05f, SafeGetCellSize() * 0.45f);
        }
        catch { }

        try
        {
            if (!hasCenter) return null;
            var cols = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
            foreach (var col in cols)
            {
                if (col == null) continue;
                var parents = col.GetComponentsInParent<MonoBehaviour>(true);
                foreach (var parent in parents)
                {
                    if (parent == null) continue;
                    if (parent is PressMachine press)
                    {
                        if (!press.isGhost) return press.gameObject;
                        continue;
                    }
                    if (parent is DroneHQ hq)
                    {
                        if (!hq.isGhost) return hq.gameObject;
                        continue;
                    }
                    if (parent is IMachine) return parent.gameObject;
                }
                var mine = col.GetComponentInParent<SugarMine>();
                if (mine != null) return mine.gameObject;
            }
        }
        catch { }

        try
        {
            if (!hasCenter) return null;
            var mines = UnityEngine.Object.FindObjectsByType<SugarMine>(FindObjectsSortMode.None);
            foreach (var mine in mines)
            {
                if (mine == null) continue;
                var pos = mine.transform.position;
                if (Mathf.Abs(pos.x - center.x) < tol && Mathf.Abs(pos.y - center.y) < tol)
                    return mine.gameObject;
            }
        }
        catch { }

        try
        {
            if (!hasCenter) return null;
            var hqs = UnityEngine.Object.FindObjectsByType<DroneHQ>(FindObjectsSortMode.None);
            foreach (var hq in hqs)
            {
                if (hq == null || hq.isGhost) continue;
                var pos = hq.transform.position;
                if (Mathf.Abs(pos.x - center.x) < tol && Mathf.Abs(pos.y - center.y) < tol)
                    return hq.gameObject;
            }
        }
        catch { }

        return null;
    }

    // Helper: find a junction object at a cell center (splitter/merger visuals tagged as Junction)
    GameObject FindJunctionAtCell(Vector2Int cell)
    {
        try
        {
            if (miCellToWorld == null || gridServiceInstance == null) return null;
            var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell, 0f });
            var center = worldObj is Vector3 vv ? vv : Vector3.zero;
            // Primary: physics overlap (if junction has collider)
            var cols = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
            foreach (var col in cols)
            {
                if (col == null) continue;
                // Search any MonoBehaviour up the parent chain whose type name suggests a junction
                var parents = col.GetComponentsInParent<MonoBehaviour>(true);
                foreach (var mb in parents)
                {
                    if (mb == null) continue;
                    var tn = mb.GetType().Name;
                    if (tn.Contains("Junction") || tn.Contains("Splitter") || tn.Contains("Merger"))
                        return mb.gameObject;
                }
                // fallback: detect by name contains Splitter/Merger
                var go = col.GetComponentInParent<Transform>()?.gameObject;
                if (go != null && (go.name.Contains("Splitter") || go.name.Contains("Merger")))
                    return go;
            }

            // Fallback: position-based search in belt container and scene transforms
            var container = ContainerLocator.GetBeltContainer();
            if (container != null)
            {
                foreach (Transform child in container)
                {
                    if (child == null) continue;
                    if (Mathf.Abs(child.position.x - center.x) < 0.05f && Mathf.Abs(child.position.y - center.y) < 0.05f)
                    {
                        var tn = child.name;
                        if (tn.Contains("Splitter") || tn.Contains("Merger") || tn.Contains("Junction"))
                            return child.gameObject;
                    }
                }
            }
            var allRoots = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
            foreach (var tr in allRoots)
            {
                if (tr == null) continue;
                if (Mathf.Abs(tr.position.x - center.x) < 0.05f && Mathf.Abs(tr.position.y - center.y) < 0.05f)
                {
                    var tn = tr.name;
                    if (tn.Contains("Splitter") || tn.Contains("Merger") || tn.Contains("Junction"))
                        return tr.gameObject;
                }
            }
        }
        catch { }
        return null;
    }

    void DestroyJunctionVisualsAtCell(Vector2Int cell)
    {
        try
        {
            var worldObj = miCellToWorld?.Invoke(gridServiceInstance, new object[] { cell, 0f });
            var center = worldObj is Vector3 vv ? vv : Vector3.zero;
            var cols = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
            foreach (var col in cols)
            {
                if (col == null) continue;
                var parents = col.GetComponentsInParent<MonoBehaviour>(true);
                foreach (var mb in parents)
                {
                    if (mb == null) continue;
                    var tn = mb.GetType().Name;
                    if (tn.Contains("Junction") || tn.Contains("Splitter") || tn.Contains("Merger"))
                    {
                        RefundFullBuildCost(mb.gameObject);
                        try { Destroy(mb.gameObject); } catch { }
                        break;
                    }
                }
            }
        }
        catch { }
    }

    bool ApplyDeleteGhostToJunctionVisualsAtCell(Vector2Int cell)
    {
        try
        {
            if (miCellToWorld == null || gridServiceInstance == null) return false;
            var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell, 0f });
            var center = worldObj is Vector3 vv ? vv : Vector3.zero;
            float size = Mathf.Max(0.5f, SafeGetCellSize());
            var cols = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * size * 0.9f, 0f);
            var seen = new HashSet<GameObject>();
            bool applied = false;
            foreach (var col in cols)
            {
                if (col == null) continue;
                var parents = col.GetComponentsInParent<Transform>(true);
                foreach (var tr in parents)
                {
                    if (tr == null || tr.gameObject == null) continue;
                    if (seen.Contains(tr.gameObject)) continue;
                    var tn = tr.gameObject.name;
                    if (tn.Contains("Splitter") || tn.Contains("Merger") || tn.Contains("Junction"))
                    {
                        ApplyDeleteGhostToGameObject(tr.gameObject);
                        seen.Add(tr.gameObject);
                        applied = true;
                    }
                }
            }

            // If we still didn't tint anything, tint any renderer at this cell as a fallback
            if (!applied)
            {
                foreach (var col in cols)
                {
                    if (col == null) continue;
                    var root = col.GetComponentInParent<Transform>()?.gameObject;
                    if (root != null && !seen.Contains(root))
                    {
                        ApplyDeleteGhostToGameObject(root);
                        seen.Add(root);
                        applied = true;
                    }
                }

                if (!applied)
                {
                    // Position-based search across all transforms using cell size tolerance
                    float tol = size * 0.55f;
                    var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                    foreach (var tr in all)
                    {
                        if (tr == null) continue;
                        if (Mathf.Abs(tr.position.x - center.x) < tol && Mathf.Abs(tr.position.y - center.y) < tol)
                        {
                            ApplyDeleteGhostToGameObject(tr.gameObject);
                            applied = true;
                            break;
                        }
                    }
                }
            }

            if (!applied)
            {
                SpawnDeleteOverlayAtCell(cell);
            }
            return applied;
        }
        catch { return false; }
    }

    void SpawnDeleteOverlayAtCell(Vector2Int cell)
    {
        EnsureHelpers();
        ghostVisuals.SpawnDeleteOverlayAtCell(cell);
    }

    void DestroyDeleteOverlayForCell(Vector2Int cell)
    {
        EnsureHelpers();
        ghostVisuals.DestroyDeleteOverlayForCell(cell);
    }

    float SafeGetCellSize(Type gridType = null, object gridInstance = null)
    {
        EnsureHelpers();
        return gridAdapter.SafeGetCellSize(gridType, gridInstance);
    }

    void DestroyDeleteOverlays()
    {
        EnsureHelpers();
        ghostVisuals.DestroyDeleteOverlays();
    }

    void TryClearItemAtCell(Vector2Int cell)
    {
        try
        {
            var getCell = gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
            if (getCell == null) return;
            var cellObj = getCell.Invoke(gridServiceInstance, new object[] { cell });
            if (cellObj == null) return;
            var t = cellObj.GetType();
            var fiHasItem = t.GetField("hasItem", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var fiItem = t.GetField("item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fiItem == null || fiHasItem == null) return;
            bool hasItem = false;
            try { hasItem = (bool)fiHasItem.GetValue(cellObj); } catch { hasItem = false; }
            var itemObj = fiItem.GetValue(cellObj);
            if (!hasItem && itemObj == null) return;
            try
            {
                if (itemObj != null)
                {
                    var tItem = itemObj.GetType();
                    var fiView = tItem.GetField("view", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var tr = fiView != null ? fiView.GetValue(itemObj) as Transform : null;
                    if (tr != null)
                    {
                        try { Destroy(tr.gameObject); } catch { }
                    }
                }
            }
            catch { }
            try { fiItem.SetValue(cellObj, null); } catch { }
            try { fiHasItem.SetValue(cellObj, false); } catch { }
        }
        catch { }
    }

    // Wipe logical belt data so simulation no longer treats the cell as a belt
    void ClearLogicalCell(Vector2Int cell)
    {
        try
        {
            if (!EnsureGridServiceCached()) return;
            var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) });
            if (clear != null) clear.Invoke(gridServiceInstance, new object[] { cell });
        }
        catch { }
    }

    void RegisterOrDefer(Vector2Int cell)
    {
        if (isDragging) 
        {
            deferredRegistrations.Add(cell);
        } 
        else 
        {
            TryRegisterCellInBeltSim(cell);
        }
    }

    void RemoveExistingBeltAtCell(Vector2Int cell)
    {
        if (gridServiceInstance == null || miCellToWorld == null) return;
        try
        {
            try
            {
                if (ghostByCell.TryGetValue(cell, out var g) && g != null)
                {
                    try { ghostOriginalColors.Remove(g); } catch { }
                    try { Destroy(g.gameObject); } catch { }
                }
            }
            catch { }
            try { ghostByCell.Remove(cell); } catch { }

            try
            {
                var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                if (getConv != null)
                {
                    var conv = getConv.Invoke(gridServiceInstance, new object[] { cell }) as Conveyor;
                    if (conv != null)
                    {
                        try { ghostOriginalColors.Remove(conv); } catch { }
                        try { Destroy(conv.gameObject); } catch { }
                    }
                }
            }
            catch { }

            try
            {
                var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell, 0f }); var center = worldObj is Vector3 vvc ? vvc : Vector3.zero;
                var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                foreach (var col in colliders)
                {
                    try
                    {
                        var c = col.gameObject.GetComponent<Conveyor>() ?? col.GetComponentInChildren<Conveyor>(true);
                        if (c != null)
                        {
                            try { ghostOriginalColors.Remove(c); } catch { }
                            try { Destroy(c.gameObject); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell, null }); } catch { }
            try { var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) }); if (clear != null) clear.Invoke(gridServiceInstance, new object[] { cell }); } catch { }
        }
        catch { }
    }

    void UpdateBeltDirectionFast(Vector2Int cell, string outDirName, Direction? incomingDirection = null)
    {
        try
        {
            Conveyor conv = null;
            try { if (ghostByCell.TryGetValue(cell, out var g) && g != null) conv = g; } catch { }
            if (conv == null)
            {
                try
                {
                    var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                    if (getConv != null) conv = getConv.Invoke(gridServiceInstance, new object[] { cell }) as Conveyor;
                }
                catch { }
            }
            if (conv == null && miCellToWorld != null)
            {
                try
                {
                    var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell, 0f }); var center = worldObj is Vector3 vv2 ? vv2 : Vector3.zero;
                    var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                    foreach (var col in colliders) { var c = col.gameObject.GetComponent<Conveyor>() ?? col.GetComponentInChildren<Conveyor>(true); if (c != null) { conv = c; break; } }
                }
                catch { }
            }

            if (conv != null)
            {
                try
                {
                    var outDir = DirectionFromName(outDirName);
                    bool shouldCurve = incomingDirection.HasValue && incomingDirection.Value != Direction.None && incomingDirection.Value != outDir;
                    if (shouldCurve)
                    {
                        var fromSide = DirectionUtil.Opposite(incomingDirection.Value);
                        conv.SetCurve(fromSide, outDir, visualRotationOffset);
                        var task = conv.GetComponent<BlueprintTask>();
                        if (task != null)
                            task.UpdateBeltCurve(fromSide, outDir, conv.transform.rotation);
                    }
                    else
                    {
                        conv.SetStraight(outDir, visualRotationOffset);
                        var task = conv.GetComponent<BlueprintTask>();
                        if (task != null)
                            task.UpdateBeltDirection(outDir, conv.transform.rotation);
                    }
                }
                catch { }
            }

            if (isDragging || BuildModeController.IsDragging) return;

            var oppName = outDirName == "Right" ? "Left" : outDirName == "Left" ? "Right" : outDirName == "Up" ? "Down" : outDirName == "Down" ? "Up" : "None";
            object outObj = null, inObj = null;
            if (directionType != null) { try { outObj = Enum.Parse(directionType, outDirName); inObj = Enum.Parse(directionType, oppName); } catch { outObj = null; inObj = null; } }
            if (outObj == null || inObj == null)
            {
                int outIndex = DirectionIndexFromName(outDirName); int inIndex = DirectionIndexFromName(oppName);
                outObj = Enum.ToObject(directionType ?? typeof(Direction), outIndex); inObj = Enum.ToObject(directionType ?? typeof(Direction), inIndex);
            }
            try
            {
                var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null && conv != null) setConv.Invoke(gridServiceInstance, new object[] { cell, conv });
            }
            catch { }
            try { miSetBeltCell?.Invoke(gridServiceInstance, new object[] { cell, inObj, outObj }); } catch { }
            RegisterOrDefer(cell);
            MarkGraphDirtyIfPresent();
        }
        catch { }
    }

    class GridAdapter
    {
        readonly ConveyorPlacer owner;

        public GridAdapter(ConveyorPlacer owner)
        {
            this.owner = owner;
        }

        public void CacheGridServiceReflection(GameObject go)
        {
            owner.gridServiceInstance = null;
            owner.miWorldToCell = null;
            owner.miCellToWorld = null;
            owner.miSetBeltCell = null;
            owner.directionType = null;
            if (go == null) return;
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var type = mb.GetType();
                if (type.Name != "GridService") continue;
                owner.gridServiceInstance = mb;
                owner.miWorldToCell = type.GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
                owner.miCellToWorld = type.GetMethod("CellToWorld", new Type[] { typeof(Vector2Int), typeof(float) });
                owner.miSetBeltCell = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "SetBeltCell" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(Vector2Int));
                try { var asm = type.Assembly; owner.directionType = asm.GetTypes().FirstOrDefault(tt => tt.IsEnum && tt.Name == "Direction"); } catch { }
                if (owner.directionType == null) owner.directionType = FindDirectionType();
                owner.cachedCellSize = SafeGetCellSize(type, mb);
                break;
            }
        }

        public bool EnsureGridServiceCached()
        {
            if (owner.gridServiceInstance != null && owner.miWorldToCell != null && owner.miCellToWorld != null) return true;
            if (owner.gridServiceObject != null)
            {
                CacheGridServiceReflection(owner.gridServiceObject);
                return owner.gridServiceInstance != null && owner.miWorldToCell != null && owner.miCellToWorld != null;
            }
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "GridService")
                {
                    owner.gridServiceObject = mb.gameObject;
                    CacheGridServiceReflection(owner.gridServiceObject);
                    break;
                }
            }
            return owner.gridServiceInstance != null && owner.miWorldToCell != null && owner.miCellToWorld != null;
        }

        public float SafeGetCellSize(Type gridType = null, object gridInstance = null)
        {
            try
            {
                object inst = gridInstance ?? owner.gridServiceInstance;
                var type = gridType ?? inst?.GetType();
                if (type == null) return owner.cachedCellSize;
                var pi = type.GetProperty("CellSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null) return Mathf.Max(0.01f, Convert.ToSingle(pi.GetValue(inst)));
                var fi = type.GetField("cellSize", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fi != null) return Mathf.Max(0.01f, Convert.ToSingle(fi.GetValue(inst)));
            }
            catch { }
            return owner.cachedCellSize;
        }

        public Type FindDirectionType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetTypes().FirstOrDefault(tt => tt.IsEnum && tt.Name == "Direction");
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }

    class GhostVisuals
    {
        readonly ConveyorPlacer owner;
        readonly int baseColorId;
        readonly int colorId;

        public GhostVisuals(ConveyorPlacer owner)
        {
            this.owner = owner;
            baseColorId = Shader.PropertyToID("_BaseColor");
            colorId = Shader.PropertyToID("_Color");
        }

        RendererBlock TryApplyRendererTint(Renderer renderer, Color tint, bool multiplyAlphaFromMaterial)
        {
            if (renderer == null) return null;
            var mat = renderer.sharedMaterial;
            if (mat == null) return null;

            int propId;
            Color baseColor;
            if (mat.HasProperty("_BaseColor"))
            {
                propId = baseColorId;
                baseColor = mat.GetColor("_BaseColor");
            }
            else if (mat.HasProperty("_Color"))
            {
                propId = colorId;
                baseColor = mat.GetColor("_Color");
            }
            else
            {
                return null;
            }

            var originalBlock = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(originalBlock);
            var tintedBlock = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(tintedBlock);

            var final = tint;
            final.a = multiplyAlphaFromMaterial ? baseColor.a * tint.a : Mathf.Clamp01(tint.a);
            tintedBlock.SetColor(propId, final);
            renderer.SetPropertyBlock(tintedBlock);

            return new RendererBlock { renderer = renderer, block = originalBlock };
        }

        void RestoreRendererBlocks(RendererBlock[] blocks)
        {
            if (blocks == null) return;
            foreach (var rb in blocks)
            {
                if (rb == null || rb.renderer == null) continue;
                try { rb.renderer.SetPropertyBlock(rb.block); } catch { }
            }
        }

        public void ApplyGhostToConveyor(Conveyor conv)
        {
            if (conv == null) return;

            conv.isGhost = true;
            try
            {
                var sg = conv.GetComponent<UnityEngine.Rendering.SortingGroup>();
                int? origOrder = null;
                int? origLayerId = null;
                if (sg != null)
                {
                    origOrder = sg.sortingOrder;
                    origLayerId = sg.sortingLayerID;
                }

                var srs = conv.GetComponentsInChildren<SpriteRenderer>(true);
                if (srs != null && srs.Length > 0)
                {
                    var orig = new Color[srs.Length];
                    var orders = new int[srs.Length];
                    var masks = new int[srs.Length];
                    for (int i = 0; i < srs.Length; i++)
                    {
                        orig[i] = srs[i].color;
                        orders[i] = srs[i].sortingOrder;
                        masks[i] = (int)srs[i].maskInteraction;

                        var baseCol = orig[i];
                        var c = new Color(baseCol.r * owner.ghostTint.r, baseCol.g * owner.ghostTint.g, baseCol.b * owner.ghostTint.b, Mathf.Clamp01(owner.ghostTint.a));
                        srs[i].color = c;

                        srs[i].maskInteraction = SpriteMaskInteraction.None;
                        srs[i].sortingOrder = orders[i] + owner.ghostSortingOrderOffset;
                    }
                    owner.ghostOriginalColors[conv] = new GhostData { spriteColors = orig, spriteOrders = orders, spriteMaskInteractions = masks, spawnedByPlacer = true, sortingGroupOrder = origOrder, sortingGroupLayerId = origLayerId };
                    return;
                }

                var rends = conv.GetComponentsInChildren<Renderer>(true);
                if (rends != null && rends.Length > 0)
                {
                    var blocks = new List<RendererBlock>();
                    foreach (var r in rends)
                    {
                        if (r == null) continue;
                        var block = TryApplyRendererTint(r, owner.ghostTint, false);
                        if (block != null) blocks.Add(block);
                    }
                    if (blocks.Count > 0)
                    {
                        owner.ghostOriginalColors[conv] = new GhostData { rendererBlocks = blocks.ToArray(), spawnedByPlacer = true, sortingGroupOrder = origOrder, sortingGroupLayerId = origLayerId };
                    }
                }
            }
            catch { }
        }

        public void ApplyDeleteGhostToConveyor(Conveyor conv)
        {
            if (conv == null) return;
            try
            {
                var srs = conv.GetComponentsInChildren<SpriteRenderer>(true);
                if (srs != null && srs.Length > 0)
                {
                    var orig = new Color[srs.Length];
                    for (int i = 0; i < srs.Length; i++)
                    {
                        orig[i] = srs[i].color;
                        var c = owner.blockedColor;
                        c.a = orig[i].a * owner.blockedColor.a;
                        srs[i].color = c;
                    }
                    owner.ghostOriginalColors[conv] = new GhostData { spriteColors = orig, spawnedByPlacer = false };
                    return;
                }

                var rends = conv.GetComponentsInChildren<Renderer>(true);
                if (rends != null && rends.Length > 0)
                {
                    var blocks = new List<RendererBlock>();
                    foreach (var r in rends)
                    {
                        if (r == null) continue;
                        var block = TryApplyRendererTint(r, owner.blockedColor, true);
                        if (block != null) blocks.Add(block);
                    }
                    if (blocks.Count > 0)
                        owner.ghostOriginalColors[conv] = new GhostData { rendererBlocks = blocks.ToArray(), spawnedByPlacer = false };
                }
            }
            catch { }
        }

        public void ApplyDeleteGhostToGameObject(GameObject go)
        {
            if (go == null) return;
            try
            {
                var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
                if (srs != null && srs.Length > 0)
                {
                    var orig = new Color[srs.Length];
                    for (int i = 0; i < srs.Length; i++)
                    {
                        orig[i] = srs[i].color;
                        var c = owner.blockedColor;
                        c.a = orig[i].a * owner.blockedColor.a;
                        srs[i].color = c;
                    }
                    owner.genericGhostOriginalColors[go] = new GhostData { spriteColors = orig };
                    return;
                }

                var rends = go.GetComponentsInChildren<Renderer>(true);
                if (rends != null && rends.Length > 0)
                {
                    var blocks = new List<RendererBlock>();
                    foreach (var r in rends)
                    {
                        if (r == null) continue;
                        var block = TryApplyRendererTint(r, owner.blockedColor, true);
                        if (block != null) blocks.Add(block);
                    }
                    if (blocks.Count > 0)
                        owner.genericGhostOriginalColors[go] = new GhostData { rendererBlocks = blocks.ToArray() };
                }
            }
            catch { }
        }

        public void RestoreGhostVisuals(bool commitBlueprints)
        {
            try
            {
                foreach (var kv in new List<KeyValuePair<Conveyor, GhostData>>(owner.ghostOriginalColors))
                {
                    var conv = kv.Key;
                    var data = kv.Value;
                    if (conv == null || data == null) continue;
                    if (data.spawnedByPlacer && owner.conveyorPrefab != null)
                    {
                        try
                        {
                            var pos = conv.transform.position;
                            var cell = (Vector2Int)owner.miWorldToCell.Invoke(owner.gridServiceInstance, new object[] { pos });

                            if (!commitBlueprints || owner.IsCellBlockedForBelt(cell))
                            {
                                try { UnityEngine.Object.Destroy(conv.gameObject); } catch { }
                                try
                                {
                                    var keys = new List<Vector2Int>();
                                    foreach (var gk in owner.ghostByCell) if (gk.Value == conv) keys.Add(gk.Key);
                                    foreach (var k in keys) owner.ghostByCell.Remove(k);
                                }
                                catch { }
                                continue;
                            }

                            if (!commitBlueprints || !owner.TrySpendBeltCost(cell))
                            {
                                try { UnityEngine.Object.Destroy(conv.gameObject); } catch { }
                                try
                                {
                                    var keys = new List<Vector2Int>();
                                    foreach (var gk in owner.ghostByCell) if (gk.Value == conv) keys.Add(gk.Key);
                                    foreach (var k in keys) owner.ghostByCell.Remove(k);
                                }
                                catch { }
                                continue;
                            }

                            owner.CreateBeltBlueprintFromGhost(conv, cell);
                            try
                            {
                                var keys = new List<Vector2Int>();
                                foreach (var gk in owner.ghostByCell) if (gk.Value == conv) keys.Add(gk.Key);
                                foreach (var k in keys) owner.ghostByCell.Remove(k);
                            }
                            catch { }
                            continue;
                        }
                        catch { }
                    }
                    if (data.spriteColors != null)
                    {
                        var srs = conv.GetComponentsInChildren<SpriteRenderer>(true);
                        if (srs != null && srs.Length > 0)
                        {
                            for (int i = 0; i < srs.Length && i < data.spriteColors.Length; i++)
                                if (srs[i] != null) srs[i].color = data.spriteColors[Math.Min(i, data.spriteColors.Length - 1)];
                            if (data.spriteOrders != null)
                            {
                                for (int i = 0; i < srs.Length && i < data.spriteOrders.Length; i++)
                                    if (srs[i] != null) srs[i].sortingOrder = data.spriteOrders[Math.Min(i, data.spriteOrders.Length - 1)];
                            }
                            if (data.spriteMaskInteractions != null)
                            {
                                for (int i = 0; i < srs.Length && i < data.spriteMaskInteractions.Length; i++)
                                    if (srs[i] != null) srs[i].maskInteraction = (SpriteMaskInteraction)data.spriteMaskInteractions[Math.Min(i, data.spriteMaskInteractions.Length - 1)];
                            }
                        }
                    }
                    if (data.rendererBlocks != null)
                        RestoreRendererBlocks(data.rendererBlocks);
                    try
                    {
                        var sgroup = conv.GetComponent<UnityEngine.Rendering.SortingGroup>();
                        if (sgroup != null && data.sortingGroupOrder.HasValue)
                        {
                            sgroup.sortingOrder = data.sortingGroupOrder.Value;
                            if (data.sortingGroupLayerId.HasValue) sgroup.sortingLayerID = data.sortingGroupLayerId.Value;
                        }
                    }
                    catch { }
                }
                foreach (var kv in new List<KeyValuePair<GameObject, GhostData>>(owner.genericGhostOriginalColors))
                {
                    var go = kv.Key;
                    var data = kv.Value;
                    if (go == null || data == null) continue;
                    if (data.spriteColors != null)
                    {
                        var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
                        if (srs != null && srs.Length > 0)
                        {
                            for (int i = 0; i < srs.Length && i < data.spriteColors.Length; i++)
                                if (srs[i] != null) srs[i].color = data.spriteColors[Math.Min(i, data.spriteColors.Length - 1)];
                            if (data.spriteMaskInteractions != null)
                            {
                                for (int i = 0; i < srs.Length && i < data.spriteMaskInteractions.Length; i++)
                                    if (srs[i] != null) srs[i].maskInteraction = (SpriteMaskInteraction)data.spriteMaskInteractions[Math.Min(i, data.spriteMaskInteractions.Length - 1)];
                            }
                        }
                    }
                    if (data.rendererBlocks != null)
                        RestoreRendererBlocks(data.rendererBlocks);
                }
            }
            catch { }
            DestroyDeleteOverlays();
            owner.ghostOriginalColors.Clear();
            owner.genericGhostOriginalColors.Clear();
            owner.ghostByCell.Clear();
        }

        public void MatchGhostSortingLayer(Conveyor ghost, Vector2Int cell)
        {
            if (ghost == null || owner.gridServiceInstance == null) return;
            try
            {
                Conveyor existing = null;
                try
                {
                    var getConv = owner.gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                    if (getConv != null) existing = getConv.Invoke(owner.gridServiceInstance, new object[] { cell }) as Conveyor;
                }
                catch { }
                if (existing == null && owner.miCellToWorld != null)
                {
                    try
                    {
                        var worldObj = owner.miCellToWorld.Invoke(owner.gridServiceInstance, new object[] { cell, 0f });
                        var center = worldObj is Vector3 vv ? vv : Vector3.zero;
                        var cols = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                        foreach (var col in cols)
                        {
                            var c = col.GetComponent<Conveyor>() ?? col.GetComponentInChildren<Conveyor>(true);
                            if (c != null && !c.isGhost) { existing = c; break; }
                        }
                    }
                    catch { }
                }
                if (existing == null) return;

                int layerId = 0;
                try
                {
                    var exSg = existing.GetComponentInParent<UnityEngine.Rendering.SortingGroup>();
                    if (exSg != null) layerId = exSg.sortingLayerID;
                    else
                    {
                        var exSr = existing.GetComponentsInChildren<SpriteRenderer>(true);
                        if (exSr != null && exSr.Length > 0) layerId = exSr[0].sortingLayerID;
                    }
                }
                catch { }

                if (layerId == 0) return;

                try
                {
                    var sg = ghost.GetComponentInParent<UnityEngine.Rendering.SortingGroup>();
                    if (sg != null) sg.sortingLayerID = layerId;
                    var srs = ghost.GetComponentsInChildren<SpriteRenderer>(true);
                    foreach (var sr in srs) { try { sr.sortingLayerID = layerId; } catch { } }
                }
                catch { }
            }
            catch { }
        }

        public void RemoveGhostAtCell(Vector2Int cell)
        {
            try
            {
                if (owner.ghostByCell.TryGetValue(cell, out var existing) && existing != null)
                {
                    try { owner.ghostOriginalColors.Remove(existing); } catch { }
                    try { UnityEngine.Object.Destroy(existing.gameObject); } catch { }
                    try { owner.ghostByCell.Remove(cell); } catch { }
                }
                else if (owner.miCellToWorld != null)
                {
                    var worldObj = owner.miCellToWorld.Invoke(owner.gridServiceInstance, new object[] { cell, 0f });
                    var center = worldObj is Vector3 vv ? vv : Vector3.zero;
                    var cols = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                    foreach (var col in cols)
                    {
                        var c = col.GetComponent<Conveyor>() ?? col.GetComponentInChildren<Conveyor>(true);
                        if (c != null && c.isGhost)
                        {
                            try { owner.ghostOriginalColors.Remove(c); } catch { }
                            try { UnityEngine.Object.Destroy(c.gameObject); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public void SpawnDeleteOverlayAtCell(Vector2Int cell)
        {
            try
            {
                if (owner.miCellToWorld == null || owner.gridServiceInstance == null) return;
                var worldObj = owner.miCellToWorld.Invoke(owner.gridServiceInstance, new object[] { cell, 0f });
                var center = worldObj is Vector3 vv ? vv : Vector3.zero;
                var go = new GameObject("JunctionDeleteOverlay");
                go.transform.position = center;
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height), new Vector2(0.5f, 0.5f));
                var col = owner.blockedColor;
                col.a = Mathf.Clamp01(col.a);
                sr.color = col;
                sr.sortingOrder = 5000;
                owner.deleteOverlayGhosts.Add(go);
            }
            catch { }
        }

        public void DestroyDeleteOverlayForCell(Vector2Int cell)
        {
            try
            {
                Vector3 center = Vector3.zero;
                if (owner.miCellToWorld != null && owner.gridServiceInstance != null)
                {
                    var worldObj = owner.miCellToWorld.Invoke(owner.gridServiceInstance, new object[] { cell, 0f });
                    center = worldObj is Vector3 vv ? vv : Vector3.zero;
                }
                foreach (var go in new List<GameObject>(owner.deleteOverlayGhosts))
                {
                    if (go == null) { owner.deleteOverlayGhosts.Remove(go); continue; }
                    if (center == Vector3.zero || (Vector2)go.transform.position == (Vector2)center)
                    {
                        try { UnityEngine.Object.Destroy(go); } catch { }
                        owner.deleteOverlayGhosts.Remove(go);
                    }
                }
            }
            catch { }
        }

        public void DestroyDeleteOverlays()
        {
            foreach (var go in new List<GameObject>(owner.deleteOverlayGhosts))
            {
                if (go != null) { try { UnityEngine.Object.Destroy(go); } catch { } }
            }
            owner.deleteOverlayGhosts.Clear();
        }
    }

    class DeleteService
    {
        readonly ConveyorPlacer owner;

        public DeleteService(ConveyorPlacer owner)
        {
            this.owner = owner;
        }

        public void TryMarkCellFromDrag(Vector2Int cell)
        {
            if (owner.lastDragCell.x == int.MinValue)
            {
                if (cell == owner.dragStartCell) return;
                var deltaStart = cell - owner.dragStartCell;
                Vector2Int stepStart = Mathf.Abs(deltaStart.x) >= Mathf.Abs(deltaStart.y) ? new Vector2Int(Math.Sign(deltaStart.x), 0) : new Vector2Int(0, Math.Sign(deltaStart.y));
                var next = owner.dragStartCell + stepStart;
                MarkCellForDeletion(owner.dragStartCell);
                MarkCellForDeletion(next);
                owner.lastDragCell = next;
                return;
            }
            if (cell == owner.lastDragCell) return;
            var delta = cell - owner.lastDragCell;
            Vector2Int step = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y) ? new Vector2Int(Math.Sign(delta.x), 0) : new Vector2Int(0, Math.Sign(delta.y));
            var nextCell = owner.lastDragCell + step;
            MarkCellForDeletion(nextCell);
            owner.lastDragCell = nextCell;
        }

        public void MarkCellForDeletion(Vector2Int cell)
        {
            try
            {
                if (owner.deleteMarkedCells.Contains(cell)) return;

                if (owner.TryGetBlueprintAtCell(cell, out var blueprint))
                {
                    owner.deleteMarkedCells.Add(cell);
                    if (blueprint != null && !owner.genericGhostOriginalColors.ContainsKey(blueprint.gameObject))
                    {
                        owner.ApplyDeleteGhostToGameObject(blueprint.gameObject);
                    }
                    return;
                }

                Conveyor conv = null;
                try
                {
                    var getConv = owner.gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                    if (getConv != null) conv = getConv.Invoke(owner.gridServiceInstance, new object[] { cell }) as Conveyor;
                }
                catch { }

                bool isLogicalBelt = false;
                bool isMachine = false;
                bool isJunction = false;
                try
                {
                    var getCell = owner.gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
                    if (getCell != null)
                    {
                        var cellObj = getCell.Invoke(owner.gridServiceInstance, new object[] { cell });
                        if (cellObj != null)
                        {
                            var t = cellObj.GetType();
                            var fiHasConv = t.GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            var fiType = t.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fiHasConv != null)
                            {
                                try { isLogicalBelt = (bool)fiHasConv.GetValue(cellObj); } catch { isLogicalBelt = false; }
                            }
                            if (!isLogicalBelt && fiType != null)
                            {
                                try
                                {
                                    var typeVal = fiType.GetValue(cellObj);
                                    if (typeVal != null)
                                    {
                                        var name = typeVal.ToString();
                                        if (name == "Belt" || name == "Junction") { isLogicalBelt = true; if (name == "Junction") isJunction = true; }
                                        if (name == "Machine") isMachine = true;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                catch { }

                if (conv == null && !isLogicalBelt && !isMachine && !isJunction)
                {
                    var mg = owner.FindMachineAtCell(cell);
                    if (mg == null) return;
                    isMachine = true;
                }

                owner.deleteMarkedCells.Add(cell);

                bool debugApplied = false;

                if (conv != null)
                {
                    owner.ApplyDeleteGhostToConveyor(conv);
                    try { owner.ghostByCell[cell] = conv; } catch { }
                    debugApplied = true;
                }
                else
                {
                    GameObject go = null;
                    if (isJunction) go = owner.FindJunctionAtCell(cell);
                    if (go == null && isLogicalBelt) go = owner.FindBeltVisualAtCell(cell);
                    if (go == null && isMachine) go = owner.FindMachineAtCell(cell);
                    if (go == null && isJunction) go = owner.FindJunctionAtCell(cell);
                    if (go != null)
                    {
                        owner.ApplyDeleteGhostToGameObject(go);
                        debugApplied = true;
                    }
                    if (!debugApplied && (isJunction || true))
                    {
                        if (owner.ApplyDeleteGhostToJunctionVisualsAtCell(cell))
                        {
                            debugApplied = true;
                        }
                    }
                }
            }
            catch { }
        }

        public void CommitMarkedDeletions()
        {
            if (owner.deleteMarkedCells.Count == 0) return;
            foreach (var cell in new List<Vector2Int>(owner.deleteMarkedCells))
            {
                try
                {
                    bool refundBelt = owner.ShouldRefundBeltAtCell(cell);
                    DeleteCellInternal(cell, refundBelt, true);
                    owner.deleteMarkedCells.Remove(cell);
                }
                catch { }
            }
            owner.MarkGraphDirtyIfPresent();
            owner.serviceCache.ReseedFromGrid();
        }

        public bool DeleteCellImmediate(Vector2Int cell)
        {
            try
            {
                bool refundBelt = owner.ShouldRefundBeltAtCell(cell);
                bool removedSomething = DeleteCellInternal(cell, refundBelt, false);
                owner.MarkGraphDirtyIfPresent();
                return removedSomething;
            }
            catch { return false; }
        }

        bool DeleteCellInternal(Vector2Int cell, bool refundBelt, bool destroyOverlay)
        {
            bool removedSomething = false;
            if (owner.TryCancelBlueprintAtCell(cell))
            {
                removedSomething = true;
                refundBelt = false;
            }
            if (!removedSomething)
            {
                Conveyor conv = null;
                try { if (owner.ghostByCell.TryGetValue(cell, out var g) && g != null) conv = g; } catch { }
                if (conv == null)
                {
                    try
                    {
                        var getConv = owner.gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                        if (getConv != null) conv = getConv.Invoke(owner.gridServiceInstance, new object[] { cell }) as Conveyor;
                    }
                    catch { }
                }
                if (conv == null)
                {
                    try
                    {
                        var worldObj = owner.miCellToWorld.Invoke(owner.gridServiceInstance, new object[] { cell, 0f });
                        var center = worldObj is Vector3 vv ? vv : Vector3.zero;
                        var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                        foreach (var col in colliders) { var c = col.gameObject.GetComponent<Conveyor>(); if (c != null) { conv = c; break; } }
                    }
                    catch { }
                }
                if (conv != null)
                {
                    try { owner.ghostOriginalColors.Remove(conv); } catch { }
                    try { owner.ghostByCell.Remove(cell); } catch { }
                    try { UnityEngine.Object.Destroy(conv.gameObject); } catch { }
                    try { var setConv = owner.gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(owner.gridServiceInstance, new object[] { cell, null }); } catch { }
                    removedSomething = true;
                }
                else
                {
                    var mg = owner.FindMachineAtCell(cell);
                    if (mg != null)
                    {
                        owner.RefundFullBuildCost(mg);
                        try { UnityEngine.Object.Destroy(mg); } catch { }
                        removedSomething = true;
                        try
                        {
                            var clear = owner.gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) });
                            if (clear != null) clear.Invoke(owner.gridServiceInstance, new object[] { cell });
                        }
                        catch { }
                    }
                    else
                    {
                        var pipe = owner.FindPipeAtCell(cell);
                        if (pipe != null)
                        {
                            owner.RefundFullBuildCost(pipe);
                            try { UnityEngine.Object.Destroy(pipe); } catch { }
                            removedSomething = true;
                        }
                    }

                    if (!removedSomething)
                    {
                        var jg = owner.FindJunctionAtCell(cell);
                        if (jg != null)
                        {
                            owner.RefundFullBuildCost(jg);
                            try { UnityEngine.Object.Destroy(jg); } catch { }
                            removedSomething = true;
                        }
                        else
                        {
                            var go = owner.FindBeltVisualAtCell(cell);
                            if (go != null)
                            {
                                try { owner.genericGhostOriginalColors.Remove(go); } catch { }
                                try { UnityEngine.Object.Destroy(go); } catch { }
                                removedSomething = true;
                            }
                        }
                    }
                }
            }

            if (!removedSomething)
            {
                try
                {
                    var getCell = owner.gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
                    var clear = owner.gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) });
                    if (getCell != null)
                    {
                        var cellObj = getCell.Invoke(owner.gridServiceInstance, new object[] { cell });
                        if (cellObj != null)
                        {
                            var t = cellObj.GetType();
                            var fiType = t.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            bool wasJunction = false;
                            if (fiType != null)
                            {
                                var name = fiType.GetValue(cellObj)?.ToString();
                                if (name == "Belt" || name == "Junction" || name == "Machine")
                                {
                                    wasJunction = name == "Junction";
                                    if (clear != null) clear.Invoke(owner.gridServiceInstance, new object[] { cell });
                                    removedSomething = true;
                                }
                            }
                            if (wasJunction)
                            {
                                owner.DestroyJunctionVisualsAtCell(cell);
                            }
                        }
                    }
                }
                catch { }
            }

            if (removedSomething)
            {
                owner.ClearLogicalCell(cell);
                owner.TryClearItemAtCell(cell);
                if (refundBelt) GameManager.Instance?.AddSweetCredits(owner.beltCost);
            }

            if (destroyOverlay) owner.DestroyDeleteOverlayForCell(cell);
            owner.TryRegisterCellInBeltSim(cell);
            return removedSomething;
        }
    }

    class ServiceCache
    {
        readonly ConveyorPlacer owner;
        MonoBehaviour cachedBeltSimulation;
        MethodInfo miRegisterCell;
        MethodInfo miReseed;
        MonoBehaviour cachedBeltGraph;
        MethodInfo miMarkDirty;
        int lastBeltSimulationSearchFrame = -9999;
        int lastBeltGraphSearchFrame = -9999;

        public ServiceCache(ConveyorPlacer owner)
        {
            this.owner = owner;
        }

        MonoBehaviour FindServiceByName(ref MonoBehaviour cached, ref int lastSearchFrame, string name)
        {
            if (cached != null) return cached;
            if (Time.frameCount - lastSearchFrame < 30) return null;
            lastSearchFrame = Time.frameCount;
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == name)
                {
                    cached = mb;
                    break;
                }
            }
            return cached;
        }

        public void RegisterCell(Vector2Int cell)
        {
            var svc = FindServiceByName(ref cachedBeltSimulation, ref lastBeltSimulationSearchFrame, "BeltSimulationService");
            if (svc == null) return;
            if (miRegisterCell == null)
                miRegisterCell = svc.GetType().GetMethod("RegisterCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            try { miRegisterCell?.Invoke(svc, new object[] { cell }); } catch { }
        }

        public void ReseedFromGrid()
        {
            var svc = FindServiceByName(ref cachedBeltSimulation, ref lastBeltSimulationSearchFrame, "BeltSimulationService");
            if (svc == null) return;
            if (miReseed == null)
                miReseed = svc.GetType().GetMethod("ReseedActiveFromGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            try { miReseed?.Invoke(svc, null); } catch { }
        }

        public void MarkGraphDirty()
        {
            var svc = FindServiceByName(ref cachedBeltGraph, ref lastBeltGraphSearchFrame, "BeltGraphService");
            if (svc == null) return;
            if (miMarkDirty == null)
                miMarkDirty = svc.GetType().GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            try { miMarkDirty?.Invoke(svc, null); } catch { }
        }
    }
}
