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
    [SerializeField] Color ghostTint = new Color(1f,1f,1f,0.6f);

    Quaternion rotation = Quaternion.identity;

    class GhostData { public Color[] spriteColors; public RendererColor[] rendererColors; public bool spawnedByPlacer; }
    class RendererColor { public Renderer renderer; public Color color; }

    readonly Dictionary<Conveyor, GhostData> ghostOriginalColors = new Dictionary<Conveyor, GhostData>();
    readonly Dictionary<Vector2Int, Conveyor> ghostByCell = new Dictionary<Vector2Int, Conveyor>();
    readonly Dictionary<GameObject, GhostData> genericGhostOriginalColors = new Dictionary<GameObject, GhostData>();

    // delete mode state
    bool isDeleting = false;
    readonly HashSet<Vector2Int> deleteMarkedCells = new HashSet<Vector2Int>();

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

    // Track the current dragged path to avoid overlapping and to support backtracking
    readonly List<Vector2Int> dragPath = new List<Vector2Int>();

    HashSet<Vector2Int> deferredRegistrations = new HashSet<Vector2Int>();

    void Reset()
    {
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
        if (gridServiceObject != null && gridServiceInstance == null)
            CacheGridServiceReflection(gridServiceObject);
    }

    void CacheGridServiceReflection(GameObject go)
    {
        gridServiceInstance = null; miWorldToCell = null; miCellToWorld = null; miSetBeltCell = null; directionType = null;
        if (go == null) return;
        foreach (var mb in go.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue; var type = mb.GetType(); if (type.Name != "GridService") continue;
            gridServiceInstance = mb;
            miWorldToCell = type.GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
            miCellToWorld = type.GetMethod("CellToWorld", new Type[] { typeof(Vector2Int), typeof(float) });
            miSetBeltCell = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SetBeltCell" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(Vector2Int));
            try { var asm = type.Assembly; directionType = asm.GetTypes().FirstOrDefault(tt => tt.IsEnum && tt.Name == "Direction"); } catch { }
            if (directionType == null) directionType = FindDirectionType();
            break;
        }
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
        RestoreGhostVisuals();
        ghostOriginalColors.Clear(); ghostByCell.Clear(); genericGhostOriginalColors.Clear();
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
        RestoreGhostVisuals();
        ghostOriginalColors.Clear(); ghostByCell.Clear(); genericGhostOriginalColors.Clear();
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
        RestoreGhostVisuals();
        ghostOriginalColors.Clear(); ghostByCell.Clear(); genericGhostOriginalColors.Clear();
        deleteMarkedCells.Clear();
        dragPath.Clear();
    }

    public void EndDeleteMode()
    {
        isDeleting = false;
        RestoreGhostVisuals();
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
        var world = GetMouseWorld();
        var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
        var cell = res is Vector2Int v ? v : new Vector2Int(0,0);

        if (isDeleting)
        {
            isDragging = true; dragStartCell = cell; lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragStartWorld = world;
            MarkCellForDeletion(cell);
            return true;
        }

        // Start drag with a ghost belt at the initial cell so it previews and can be reoriented
        isDragging = true; dragStartCell = cell; lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragStartWorld = world;
        dragPath.Clear();
        var placed = PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation));
        if (placed) dragPath.Add(dragStartCell);
        return true;
    }

    // Called when the user releases the pointer. If no dragging movement occurred
    // then place a single belt at the initial cell (already handled here).
    public bool OnPointerUp()
    {
        if (!isDragging) return false; isDragging = false;
        if (isDeleting)
        {
            // commit deletions
            CommitMarkedDeletions(); RestoreGhostVisuals(); deleteMarkedCells.Clear(); lastDragCell = dragStartCell = new Vector2Int(int.MinValue, int.MinValue); dragPath.Clear(); return true;
        }
        RestoreGhostVisuals();
        if (lastDragCell.x == int.MinValue && dragStartCell.x != int.MinValue)
        {
            var placed = PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation));
            if (placed) { MarkGraphDirtyIfPresent(); if (dragPath.Count == 0) dragPath.Add(dragStartCell); }
            dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
            FlushDeferredRegistrations();
            dragPath.Clear();
            return placed;
        }
        FlushDeferredRegistrations();
        dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
        dragPath.Clear();
        return false;
    }

    void FlushDeferredRegistrations()
    {
        if (deferredRegistrations.Count == 0) return;
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

        if (!EnsureGridServiceCached() || miWorldToCell == null) return;
        var world = GetMouseWorld();
        var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
        var cell = res is Vector2Int v ? v : new Vector2Int(0,0);

        // fallback start drag if controller didn't call OnPointerDown
        if (Input.GetMouseButtonDown(0))
        {
            // unify pointer handling
            isDragging = true; dragStartCell = cell; lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragStartWorld = world;
            dragPath.Clear();
            // initial action
            if (isDeleting) MarkCellForDeletion(cell); else { if (PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation))) dragPath.Add(dragStartCell); }
            return;
        }
        if (Input.GetMouseButton(0) && isDragging)
        {
            if (isDeleting) { TryMarkCellFromDrag(cell); return; }
            TryPlaceCellFromDrag(cell);
            if (lastDragCell.x != int.MinValue) MarkGraphDirtyIfPresent();
            return;
        }
        if (Input.GetMouseButtonUp(0) && isDragging)
        {
            if (isDeleting)
            {
                CommitMarkedDeletions(); RestoreGhostVisuals(); deleteMarkedCells.Clear(); isDragging = false; dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragPath.Clear(); return;
            }
            if (lastDragCell.x == int.MinValue && dragStartCell.x != int.MinValue)
            {
                var placed = PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation)); if (placed) { MarkGraphDirtyIfPresent(); if (dragPath.Count == 0) dragPath.Add(dragStartCell); }
            }
            isDragging = false; RestoreGhostVisuals(); dragStartCell = lastDragCell = new Vector2Int(int.MinValue, int.MinValue); dragStartWorld = Vector3.zero; FlushDeferredRegistrations(); dragPath.Clear();
        }
    }

    void TryPlaceCellFromDrag(Vector2Int cell)
    {
        if (lastDragCell.x == int.MinValue)
        {
            if (cell == dragStartCell) return;
            // ensure start is on path list
            if (dragPath.Count == 0 && ghostByCell.ContainsKey(dragStartCell)) dragPath.Add(dragStartCell);
            var deltaStart = cell - dragStartCell;
            Vector2Int stepStart = Mathf.Abs(deltaStart.x) >= Mathf.Abs(deltaStart.y) ? new Vector2Int(Math.Sign(deltaStart.x), 0) : new Vector2Int(0, Math.Sign(deltaStart.y));
            var next = dragStartCell + stepStart; var dirName = DirectionNameFromDelta(stepStart); if (dirName == null) return;

            // Backtrack if revisiting an existing cell on the path
            if (dragPath.Contains(next)) { BacktrackToCell(next); lastDragCell = next; return; }

            // Ensure start cell belt updates direction immediately, even if reflection lookups fail
            UpdateBeltDirectionFast(dragStartCell, dirName);
            var placedNext = PlaceBeltAtCell(next, dirName);
            if (placedNext)
            {
                dragPath.Add(next);
                lastDragCell = next;
            }
            return;
        }
        if (cell == lastDragCell) return;
        var delta = cell - lastDragCell;
        Vector2Int step = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y) ? new Vector2Int(Math.Sign(delta.x), 0) : new Vector2Int(0, Math.Sign(delta.y));
        var nextCell = lastDragCell + step; var dirNameNext = DirectionNameFromDelta(step); if (dirNameNext == null) return;

        // If the next cell is part of our existing path, backtrack instead of placing over it
        if (dragPath.Contains(nextCell)) { BacktrackToCell(nextCell); lastDragCell = nextCell; return; }

        UpdateBeltDirectionFast(lastDragCell, dirNameNext);
        var placed = PlaceBeltAtCell(nextCell, dirNameNext);
        if (placed)
        {
            dragPath.Add(nextCell);
            lastDragCell = nextCell;
        }
    }

    // Remove tail ghosts until target becomes the last element in the drag path
    void BacktrackToCell(Vector2Int target)
    {
        while (dragPath.Count > 0 && dragPath[dragPath.Count - 1] != target)
        {
            var tail = dragPath[dragPath.Count - 1];
            dragPath.RemoveAt(dragPath.Count - 1);
            try
            {
                if (ghostByCell.TryGetValue(tail, out var conv) && conv != null)
                {
                    // Clean any ghost visual tracking for this conveyor
                    try { ghostOriginalColors.Remove(conv); } catch { }
                    try { ghostByCell.Remove(tail); } catch { }
                    // Destroy the ghost object
                    try { Destroy(conv.gameObject); } catch { }
                }
                else
                {
                    var go = FindBeltVisualAtCell(tail);
                    if (go != null) { try { genericGhostOriginalColors.Remove(go); } catch { } try { Destroy(go); } catch { } }
                }

                // Clear logical grid and simulation for the removed cell
                try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { tail, null }); } catch { }
                try { var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) }); if (clear != null) clear.Invoke(gridServiceInstance, new object[] { tail }); } catch { }
                TryRegisterCellInBeltSim(tail);
            }
            catch { }
        }

        // Optionally, reorient the new last cell to current rotation so it looks nice when paused on it
        if (dragPath.Count > 0)
        {
            var last = dragPath[dragPath.Count - 1];
            UpdateBeltDirectionAtCell(last, RotationToDirectionName(rotation));
        }
    }

    void TryMarkCellFromDrag(Vector2Int cell)
    {
        if (lastDragCell.x == int.MinValue)
        {
            if (cell == dragStartCell) return;
            var deltaStart = cell - dragStartCell;
            Vector2Int stepStart = Mathf.Abs(deltaStart.x) >= Mathf.Abs(deltaStart.y) ? new Vector2Int(Math.Sign(deltaStart.x), 0) : new Vector2Int(0, Math.Sign(deltaStart.y));
            var next = dragStartCell + stepStart; MarkCellForDeletion(dragStartCell); MarkCellForDeletion(next); lastDragCell = next; return;
        }
        if (cell == lastDragCell) return;
        var delta = cell - lastDragCell; Vector2Int step = Mathf.Abs(delta.x) >= Mathf.Abs(delta.y) ? new Vector2Int(Math.Sign(delta.x), 0) : new Vector2Int(0, Math.Sign(delta.y));
        var nextCell = lastDragCell + step; MarkCellForDeletion(nextCell); lastDragCell = nextCell;
    }

    void MarkCellForDeletion(Vector2Int cell)
    {
        // Only mark and show delete ghost for actual belt elements (Conveyor) or logical belt cells in GridService.
        try
        {
            // If already marked, skip
            if (deleteMarkedCells.Contains(cell)) return;

            Conveyor conv = null;
            try
            {
                var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
                if (getConv != null) conv = getConv.Invoke(gridServiceInstance, new object[] { cell }) as Conveyor;
            }
            catch { }

            bool isLogicalBelt = false;
            try
            {
                var getCell = gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
                if (getCell != null)
                {
                    var cellObj = getCell.Invoke(gridServiceInstance, new object[] { cell });
                    if (cellObj != null)
                    {
                        var t = cellObj.GetType();
                        var fiHasConv = t.GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fiHasConv != null)
                        {
                            try { isLogicalBelt = (bool)fiHasConv.GetValue(cellObj); } catch { isLogicalBelt = false; }
                        }
                        if (!isLogicalBelt)
                        {
                            var fiType = t.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fiType != null)
                            {
                                try
                                {
                                    var typeVal = fiType.GetValue(cellObj);
                                    if (typeVal != null)
                                    {
                                        var name = typeVal.ToString();
                                        if (name == "Belt" || name == "Junction") isLogicalBelt = true;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            // If there is no conveyor (belt) visual/object and no logical belt, do not mark this cell for deletion.
            if (conv == null && !isLogicalBelt)
            {
                // No belt to delete; silently ignore.
                return;
            }

            // mark for deletion
            deleteMarkedCells.Add(cell);

            if (conv != null) { ApplyDeleteGhostToConveyor(conv); try { ghostByCell[cell] = conv; } catch { } }
            else
            {
                var go = FindBeltVisualAtCell(cell);
                if (go != null) ApplyDeleteGhostToGameObject(go);
            }
        }
        catch { }
    }

    void CommitMarkedDeletions()
    {
        if (deleteMarkedCells.Count == 0) return;
        foreach (var cell in new List<Vector2Int>(deleteMarkedCells))
        {
            try
            {
                bool removedBelt = false;
                Conveyor conv = null; try { if (ghostByCell.TryGetValue(cell, out var g) && g != null) conv = g; } catch { }
                if (conv == null)
                {
                    try
                    {
                        var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) }); if (getConv != null) conv = getConv.Invoke(gridServiceInstance, new object[] { cell }) as Conveyor;
                    }
                    catch { }
                }
                if (conv == null)
                {
                    try
                    {
                        var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero;
                        var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                        foreach (var col in colliders) { var c = col.gameObject.GetComponent<Conveyor>(); if (c != null) { conv = c; break; } }
                    }
                    catch { }
                }
                if (conv != null)
                {
                    try { ghostOriginalColors.Remove(conv); } catch { } try { ghostByCell.Remove(cell); } catch { }
                    try { Destroy(conv.gameObject); } catch { }
                    try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell, null }); } catch { }
                    removedBelt = true;
                }
                else
                {
                    var go = FindBeltVisualAtCell(cell); if (go != null) { try { genericGhostOriginalColors.Remove(go); } catch { } try { Destroy(go); } catch { } removedBelt = true; }
                }

                // If we didn't find a visual conveyor, check for logical belt cells and clear them
                if (!removedBelt)
                {
                    try
                    {
                        var getCell = gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
                        if (getCell != null)
                        {
                            var cellObj = getCell.Invoke(gridServiceInstance, new object[] { cell });
                            if (cellObj != null)
                            {
                                var t = cellObj.GetType();
                                var fiHasConv = t.GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                bool isLogicalBelt = false;
                                if (fiHasConv != null) { try { isLogicalBelt = (bool)fiHasConv.GetValue(cellObj); } catch { isLogicalBelt = false; } }
                                if (!isLogicalBelt)
                                {
                                    var fiType = t.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (fiType != null)
                                    {
                                        try { var typeVal = fiType.GetValue(cellObj); if (typeVal != null) { var name = typeVal.ToString(); if (name == "Belt" || name == "Junction") isLogicalBelt = true; } } catch { }
                                    }
                                }
                                if (isLogicalBelt)
                                {
                                    removedBelt = true;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Only clear items if we actually removed a belt at this cell (visual or logical)
                if (removedBelt)
                {
                    TryClearItemAtCell(cell);
                }

                try { var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) }); if (clear != null) clear.Invoke(gridServiceInstance, new object[] { cell }); } catch { }
                TryRegisterCellInBeltSim(cell);
                deleteMarkedCells.Remove(cell);
            }
            catch { }
        }
        MarkGraphDirtyIfPresent();
    }

    bool DeleteCellImmediate(Vector2Int cell)
    {
        try
        {
            bool removedBelt = false;
            Conveyor conv = null; try { if (ghostByCell.TryGetValue(cell, out var g) && g != null) conv = g; } catch { }
            if (conv == null)
            {
                var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) }); if (getConv != null) conv = getConv.Invoke(gridServiceInstance, new object[] { cell }) as Conveyor;
            }
            if (conv == null)
            {
                var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero;
                var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                foreach (var col in colliders) { var c = col.gameObject.GetComponent<Conveyor>(); if (c != null) { conv = c; break; } }
            }
            if (conv != null)
            {
                try { ghostOriginalColors.Remove(conv); } catch { } try { ghostByCell.Remove(cell); } catch { }
                try { Destroy(conv.gameObject); } catch { }
                try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell, null }); } catch { }
                removedBelt = true;
            }
            else
            {
                var go = FindBeltVisualAtCell(cell); if (go != null) { try { genericGhostOriginalColors.Remove(go); } catch { } try { Destroy(go); } catch { } removedBelt = true; }
            }

            // If no visual conveyor found, check logical grid for belt cell and treat as removed if present
            if (!removedBelt)
            {
                try
                {
                    var getCell = gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
                    if (getCell != null)
                    {
                        var cellObj = getCell.Invoke(gridServiceInstance, new object[] { cell });
                        if (cellObj != null)
                        {
                            var t = cellObj.GetType();
                            var fiHasConv = t.GetField("hasConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            bool isLogicalBelt = false;
                            if (fiHasConv != null) { try { isLogicalBelt = (bool)fiHasConv.GetValue(cellObj); } catch { isLogicalBelt = false; } }
                            if (!isLogicalBelt)
                            {
                                var fiType = t.GetField("type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                if (fiType != null)
                                {
                                    try { var typeVal = fiType.GetValue(cellObj); if (typeVal != null) { var name = typeVal.ToString(); if (name == "Belt" || name == "Junction") isLogicalBelt = true; } } catch { }
                                }
                            }
                            if (isLogicalBelt) removedBelt = true;
                        }
                    }
                }
                catch { }
            }

            // Only clear item if we've actually removed a belt here
            if (removedBelt)
            {
                TryClearItemAtCell(cell);
            }

            try { var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) }); if (clear != null) clear.Invoke(gridServiceInstance, new object[] { cell }); } catch { }
            TryRegisterCellInBeltSim(cell);
            MarkGraphDirtyIfPresent();
            return true;
        }
        catch { return false; }
    }

    // visuals helpers
    void ApplyGhostToConveyor(Conveyor conv)
    {
        if (conv == null) return;
        try
        {
            var srs = conv.GetComponentsInChildren<SpriteRenderer>(true);
            if (srs != null && srs.Length > 0)
            {
                var orig = new Color[srs.Length];
                for (int i = 0; i < srs.Length; i++) { orig[i] = srs[i].color; var c = ghostTint; c.a = orig[i].a * ghostTint.a; srs[i].color = c; }
                ghostOriginalColors[conv] = new GhostData { spriteColors = orig, spawnedByPlacer = true }; return;
            }
            var rends = conv.GetComponentsInChildren<Renderer>(true);
            if (rends != null && rends.Length > 0)
            {
                var rendList = new List<RendererColor>();
                foreach (var r in rends)
                {
                    try { r.material = new Material(r.material); } catch { }
                    var col = r.material != null && r.material.HasProperty("_Color") ? r.material.color : Color.white;
                    var c = ghostTint; c.a = col.a * ghostTint.a; if (r.material != null && r.material.HasProperty("_Color")) r.material.color = c;
                    rendList.Add(new RendererColor { renderer = r, color = col });
                }
                ghostOriginalColors[conv] = new GhostData { rendererColors = rendList.ToArray(), spawnedByPlacer = true };
            }
        }
        catch { }
    }

    void ApplyDeleteGhostToConveyor(Conveyor conv)
    {
        if (conv == null) return; try
        {
            var srs = conv.GetComponentsInChildren<SpriteRenderer>(true);
            if (srs != null && srs.Length > 0)
            {
                var orig = new Color[srs.Length]; for (int i = 0; i < srs.Length; i++) { orig[i] = srs[i].color; var c = blockedColor; c.a = orig[i].a * blockedColor.a; srs[i].color = c; }
                ghostOriginalColors[conv] = new GhostData { spriteColors = orig, spawnedByPlacer = false }; return;
            }
            var rends = conv.GetComponentsInChildren<Renderer>(true);
            if (rends != null && rends.Length > 0)
            {
                var rendList = new List<RendererColor>(); foreach (var r in rends) { try { r.material = new Material(r.material); } catch { } var col = r.material != null && r.material.HasProperty("_Color") ? r.material.color : Color.white; var c = blockedColor; c.a = col.a * blockedColor.a; if (r.material != null && r.material.HasProperty("_Color")) r.material.color = c; rendList.Add(new RendererColor { renderer = r, color = col }); }
                ghostOriginalColors[conv] = new GhostData { rendererColors = rendList.ToArray(), spawnedByPlacer = false };
            }
        }
        catch { }
    }

    void ApplyDeleteGhostToGameObject(GameObject go)
    {
        if (go == null) return;
        try
        {
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            if (srs != null && srs.Length > 0)
            {
                var orig = new Color[srs.Length]; for (int i = 0; i < srs.Length; i++) { orig[i] = srs[i].color; var c = blockedColor; c.a = orig[i].a * blockedColor.a; srs[i].color = c; }
                genericGhostOriginalColors[go] = new GhostData { spriteColors = orig }; return;
            }
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends != null && rends.Length > 0)
            {
                var rendList = new List<RendererColor>(); foreach (var r in rends) { try { r.material = new Material(r.material); } catch { } var col = r.material != null && r.material.HasProperty("_Color") ? r.material.color : Color.white; var c = blockedColor; c.a = col.a * blockedColor.a; if (r.material != null && r.material.HasProperty("_Color")) r.material.color = c; rendList.Add(new RendererColor { renderer = r, color = col }); }
                genericGhostOriginalColors[go] = new GhostData { rendererColors = rendList.ToArray() };
            }
        }
        catch { }
    }

    void RestoreGhostVisuals()
    {
        try
        {
            foreach (var kv in new List<KeyValuePair<Conveyor, GhostData>>(ghostOriginalColors))
            {
                var conv = kv.Key; var data = kv.Value; if (conv == null || data == null) continue;
                if (data.spawnedByPlacer && conveyorPrefab != null)
                {
                    try
                    {
                        var pos = conv.transform.position; var rot = conv.transform.rotation;
                        var go = Instantiate(conveyorPrefab, pos, rot);
                        var newConv = go.GetComponent<Conveyor>(); if (newConv != null) { try { newConv.direction = conv.direction; } catch { } try { if (EnsureGridServiceCached()) { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); var cell = (Vector2Int)miWorldToCell.Invoke(gridServiceInstance, new object[] { pos }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell, newConv }); TryRegisterCellInBeltSim(cell); } } catch { } }
                        try { Destroy(conv.gameObject); } catch { }
                        try { var keys = new List<Vector2Int>(); foreach (var gk in ghostByCell) if (gk.Value == conv) keys.Add(gk.Key); foreach (var k in keys) ghostByCell.Remove(k); } catch { }
                        continue;
                    }
                    catch { }
                }
                if (data.spriteColors != null)
                {
                    var srs = conv.GetComponentsInChildren<SpriteRenderer>(true);
                    if (srs != null && srs.Length > 0)
                    {
                        for (int i = 0; i < srs.Length && i < data.spriteColors.Length; i++) if (srs[i] != null) srs[i].color = data.spriteColors[Math.Min(i, data.spriteColors.Length-1)];
                    }
                }
                if (data.rendererColors != null)
                {
                    foreach (var rc in data.rendererColors)
                    {
                        if (rc?.renderer == null) continue; try { if (rc.renderer.material != null && rc.renderer.material.HasProperty("_Color")) rc.renderer.material.color = rc.color; } catch { }
                    }
                }
            }
            foreach (var kv in new List<KeyValuePair<GameObject, GhostData>>(genericGhostOriginalColors))
            {
                var go = kv.Key; var data = kv.Value; if (go == null || data == null) continue;
                if (data.spriteColors != null)
                {
                    var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
                    if (srs != null && srs.Length > 0)
                    {
                        for (int i = 0; i < srs.Length && i < data.spriteColors.Length; i++) if (srs[i] != null) srs[i].color = data.spriteColors[Math.Min(i, data.spriteColors.Length-1)];
                    }
                }
                if (data.rendererColors != null)
                {
                    foreach (var rc in data.rendererColors)
                    {
                        if (rc?.renderer == null) continue; try { if (rc.renderer.material != null && rc.renderer.material.HasProperty("_Color")) rc.renderer.material.color = rc.color; } catch { }
                    }
                }
            }
        }
        catch { }
        ghostOriginalColors.Clear(); genericGhostOriginalColors.Clear(); ghostByCell.Clear();
    }

    // placement logic
    bool PlaceBeltAtCell(Vector2Int cell2, string outDirName)
    {
        // When dragging, always overwrite/delete any existing ghost or belt at this cell so we maintain 1 cell = 1 belt.
        if (isDragging && ghostByCell.ContainsKey(cell2))
        {
            try
            {
                var existingGhost = ghostByCell[cell2];
                if (existingGhost != null)
                {
                    try { ghostOriginalColors.Remove(existingGhost); } catch { }
                    try { Destroy(existingGhost.gameObject); } catch { }
                }
            }
            catch { }
            try { ghostByCell.Remove(cell2); } catch { }
            try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell2, null }); } catch { }
            try { var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) }); if (clear != null) clear.Invoke(gridServiceInstance, new object[] { cell2 }); } catch { }
        }

        if (miCellToWorld == null || miSetBeltCell == null || gridServiceInstance == null) return false;
        var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero;

        // When dragging, remove any existing belt (visual or logical) so the drag placement will always overwrite.
        if (isDragging)
        {
            RemoveExistingBeltAtCell(cell2);
            // Also clear any item occupying this cell so belts always replace contents while dragging
            TryClearItemAtCell(cell2);
        }

        // While dragging we ignore blockers entirely (we are force-replacing). Otherwise, apply blocking rules.
        if (!isDragging)
        {
            // Only treat as blocking if overlaps contain non-conveyor colliders. Ignore conveyors (they may be pending-destroy this frame).
            var hits = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f, blockingMask);
            if (hits != null && hits.Length > 0)
            {
                foreach (var h in hits)
                {
                    if (h == null) continue;
                    // ignore any collider that belongs to a Conveyor anywhere in its parent chain
                    if (h.GetComponentInParent<Conveyor>() != null) continue;
                    return false;
                }
            }
        }
        try
        {
            var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) }); if (getConv != null)
            {
                var existing = getConv.Invoke(gridServiceInstance, new object[] { cell2 }) as Conveyor; if (existing != null)
                {
                    // If existing is our own ghost, don't recreate; just ensure direction below and return success
                    if (isDragging && ghostByCell.ContainsKey(cell2) && ghostByCell[cell2] == existing)
                    {
                        UpdateBeltDirectionAtCell(cell2, outDirName);
                        return true;
                    }
                    try { Destroy(existing.gameObject); } catch { }
                    try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell2, null }); } catch { }
                    try { var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) }); if (clear != null) clear.Invoke(gridServiceInstance, new object[] { cell2 }); } catch { }
                }
            }
        }
        catch { }
        if (conveyorPrefab != null)
        {
            float z = outDirName switch { "Right" => 0f, "Up" => 90f, "Left" => 180f, "Down" => 270f, _ => 0f }; z += visualRotationOffset;
            var go = Instantiate(conveyorPrefab, center, Quaternion.Euler(0,0,z));
            try
            {
                var conv = go.GetComponent<Conveyor>() ?? go.GetComponentInChildren<Conveyor>(true); if (conv != null)
                 {
                     switch (outDirName) { case "Right": conv.direction = Direction.Right; break; case "Left": conv.direction = Direction.Left; break; case "Up": conv.direction = Direction.Up; break; case "Down": conv.direction = Direction.Down; break; default: conv.direction = Direction.Right; break; }
                     try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell2, conv }); } catch { }
                     RegisterOrDefer(cell2);
                     if (isDragging) { ApplyGhostToConveyor(conv); try { ghostByCell[cell2] = conv; } catch { } }
                     return true;
                 }
            }
            catch { }
        }
        var oppName = outDirName == "Right" ? "Left" : outDirName == "Left" ? "Right" : outDirName == "Up" ? "Down" : outDirName == "Down" ? "Up" : "None";
        object outObj = null, inObj = null;
        if (directionType != null) { try { outObj = Enum.Parse(directionType, outDirName); inObj = Enum.Parse(directionType, oppName); } catch { outObj = null; inObj = null; } }
        if (outObj == null || inObj == null)
        {
            int outIndex = DirectionIndexFromName(outDirName); int inIndex = DirectionIndexFromName(oppName);
            outObj = Enum.ToObject(directionType ?? typeof(Direction), outIndex); inObj = Enum.ToObject(directionType ?? typeof(Direction), inIndex);
        }
        try { miSetBeltCell.Invoke(gridServiceInstance, new object[] { cell2, inObj, outObj }); RegisterOrDefer(cell2); return true; } catch { return false; }
    }

    bool UpdateBeltDirectionAtCell(Vector2Int cell2, string outDirName)
    {
        try
        {
            Conveyor conv = null;
            try { var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) }); if (getConv != null) conv = getConv.Invoke(gridServiceInstance, new object[] { cell2 }) as Conveyor; } catch { }
            if (conv == null && miCellToWorld != null)
            {
                try { var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f }); var center = worldObj is Vector3 vv ? vv : Vector3.zero; var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f); foreach (var col in colliders) { var c = col.gameObject.GetComponent<Conveyor>(); if (c != null) { conv = c; break; } } } catch { }
            }
            var oppName = outDirName == "Right" ? "Left" : outDirName == "Left" ? "Right" : outDirName == "Up" ? "Down" : outDirName == "Down" ? "Up" : "None";
            if (conv != null)
            {
                try
                {
                    switch (outDirName) { case "Right": conv.direction = Direction.Right; break; case "Left": conv.direction = Direction.Left; break; case "Up": conv.direction = Direction.Up; break; case "Down": conv.direction = Direction.Down; break; }
                    float z = outDirName switch { "Right" => 0f, "Up" => 90f, "Left" => 180f, "Down" => 270f, _ => 0f } + visualRotationOffset; conv.transform.rotation = Quaternion.Euler(0,0,z);
                    try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell2, conv }); } catch { }
                }
                catch { }
            }
            object outObj = null, inObj = null; if (directionType != null) { try { outObj = Enum.Parse(directionType, outDirName); inObj = Enum.Parse(directionType, oppName); } catch { outObj = null; inObj = null; } }
            if (outObj == null || inObj == null) { int outIndex = DirectionIndexFromName(outDirName); int inIndex = DirectionIndexFromName(oppName); outObj = Enum.ToObject(directionType ?? typeof(Direction), outIndex); inObj = Enum.ToObject(directionType ?? typeof(Direction), inIndex); }
            try { miSetBeltCell?.Invoke(gridServiceInstance, new object[] { cell2, inObj, outObj }); } catch { }
            RegisterOrDefer(cell2); TryRegisterCellInBeltSim(cell2); MarkGraphDirtyIfPresent();
            return true;
        }
        catch { return false; }
    }

    // sim + utils
    void TryRegisterCellInBeltSim(Vector2Int cell)
    {
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all) { if (mb == null) continue; var t = mb.GetType(); if (t.Name == "BeltSimulationService") { var mi = t.GetMethod("RegisterCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (mi != null) mi.Invoke(mb, new object[] { cell }); break; } }
    }

    public void RefreshPreviewAfterPlace() { }

    void MarkGraphDirtyIfPresent()
    {
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all) { if (mb == null) continue; var t = mb.GetType(); if (t.Name == "BeltGraphService") { var mi = t.GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (mi != null) mi.Invoke(mb, null); break; } }
    }

    Vector3 GetMouseWorld()
    {
        var cam = Camera.main; var pos = Input.mousePosition; var world = cam != null ? cam.ScreenToWorldPoint(pos) : new Vector3(pos.x, pos.y, 0f); world.z = 0f; return world;
    }

    bool EnsureGridServiceCached()
    {
        if (gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null) return true;
        if (gridServiceObject != null) { CacheGridServiceReflection(gridServiceObject); return gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null; }
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all) { if (mb == null) continue; var t = mb.GetType(); if (t.Name == "GridService") { gridServiceObject = mb.gameObject; CacheGridServiceReflection(gridServiceObject); break; } }
        return gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null;
    }

    int DirectionIndexFromName(string name)
    {
        return name switch { "Up" => 0, "Right" => 1, "Down" => 2, "Left" => 3, _ => 4 };
    }

    string DirectionNameFromDelta(Vector2Int delta)
    {
        if (delta.x > 0) return "Right"; if (delta.x < 0) return "Left"; if (delta.y > 0) return "Up"; if (delta.y < 0) return "Down"; return null;
    }

    string RotationToDirectionName(Quaternion q)
    {
        var z = Mathf.Repeat(q.eulerAngles.z, 360f); var snapped = Mathf.Round(z / 90f) * 90f; int i = Mathf.RoundToInt(snapped) % 360;
        return i switch { 0 => "Right", 90 => "Up", 180 => "Left", 270 => "Down", _ => "Right" };
    }

    Type FindDirectionType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { try { var t = asm.GetTypes().FirstOrDefault(tt => tt.IsEnum && tt.Name == "Direction"); if (t != null) return t; } catch { } } return null;
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
                        // Only consider objects that represent belts (have a Conveyor component)
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
                    // Only treat it as a belt visual if it has a Conveyor component (avoid matching item views)
                    var conv = tr.GetComponent<Conveyor>() ?? tr.GetComponentInChildren<Conveyor>(true);
                    if (conv != null) return tr.gameObject;
                }
            }
        }
        catch { }
        return null;
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

    void RegisterOrDefer(Vector2Int cell)
    {
        if (isDragging) deferredRegistrations.Add(cell); else TryRegisterCellInBeltSim(cell);
    }

    // Helper: remove any visual Conveyor GameObject and logical grid entries for a belt at the given cell
    void RemoveExistingBeltAtCell(Vector2Int cell)
    {
        if (gridServiceInstance == null || miCellToWorld == null) return;
        try
        {
            // remove any ghost tracking and object
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

            // clear any Conveyor returned by GridService.GetConveyor
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

            // also destroy any Conveyor components present at the world position
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

            // clear logical references in GridService
            try { var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) }); if (setConv != null) setConv.Invoke(gridServiceInstance, new object[] { cell, null }); } catch { }
            try { var clear = gridServiceInstance.GetType().GetMethod("ClearCell", new Type[] { typeof(Vector2Int) }); if (clear != null) clear.Invoke(gridServiceInstance, new object[] { cell }); } catch { }
        }
        catch { }
    }

    // Robust direction updater that prefers the currently tracked ghost/placed conveyor
    void UpdateBeltDirectionFast(Vector2Int cell, string outDirName)
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

            // apply direction to visual
            if (conv != null)
            {
                try
                {
                    switch (outDirName) { case "Right": conv.direction = Direction.Right; break; case "Left": conv.direction = Direction.Left; break; case "Up": conv.direction = Direction.Up; break; case "Down": conv.direction = Direction.Down; break; }
                    float z = outDirName switch { "Right" => 0f, "Up" => 90f, "Left" => 180f, "Down" => 270f, _ => 0f } + visualRotationOffset; conv.transform.rotation = Quaternion.Euler(0,0,z);
                }
                catch { }
            }

            // update logical cell directions as well
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
}
