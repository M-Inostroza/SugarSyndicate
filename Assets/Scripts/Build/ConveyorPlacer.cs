using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using System.Collections.Generic;

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

    Quaternion rotation = Quaternion.identity;

    // reflection cache for GridService methods
    object gridServiceInstance;
    MethodInfo miWorldToCell; // Vector2Int WorldToCell(Vector3)
    MethodInfo miCellToWorld; // Vector3 CellToWorld(Vector2Int, float)
    MethodInfo miSetBeltCell; // void SetBeltCell(Vector2Int, Direction, Direction)

    // cached Direction enum Type (from gameplay assembly)
    Type directionType;

    // Drag placement state
    Vector2Int lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
    Vector2Int dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
    Vector3 dragStartWorld = Vector3.zero; // store world position where pointer down began
    bool isDragging = false;

    // Defer belt-sim registrations while dragging to avoid disturbing running items mid-step
    HashSet<Vector2Int> deferredRegistrations = new HashSet<Vector2Int>();

    void Reset()
    {
        // try to auto-find an instance by name if not assigned
        if (gridServiceObject == null)
        {
            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                if (t.Name == "GridService")
                {
                    gridServiceObject = mb.gameObject;
                    break;
                }
            }
        }

        if (gridServiceObject != null)
            CacheGridServiceReflection(gridServiceObject);
    }

    void Awake()
    {
        if (gridServiceObject != null && gridServiceInstance == null)
            CacheGridServiceReflection(gridServiceObject);
    }

    void CacheGridServiceReflection(GameObject go)
    {
        gridServiceInstance = null;
        miWorldToCell = null;
        miCellToWorld = null;
        miSetBeltCell = null;
        directionType = null;
        if (go == null) return;
        var mbs = go.GetComponents<MonoBehaviour>();
        foreach (var mb in mbs)
        {
            if (mb == null) continue;
            var type = mb.GetType();
            if (type.Name != "GridService") continue;
            gridServiceInstance = mb;
            miWorldToCell = type.GetMethod("WorldToCell", new Type[] { typeof(Vector3) });
            miCellToWorld = type.GetMethod("CellToWorld", new Type[] { typeof(Vector2Int), typeof(float) });
            // find SetBeltCell(Vector2Int, Direction, Direction) without referencing enum type at compile time
            miSetBeltCell = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "SetBeltCell" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(Vector2Int));
            // Prefer Direction enum from the GridService's assembly to avoid picking similarly named Unity enums
            try
            {
                var asm = type.Assembly;
                directionType = asm.GetTypes().FirstOrDefault(tt => tt.IsEnum && tt.Name == "Direction");
            }
            catch { }
            // fallback to global search
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
    }

    // Called by BuildModeController when exiting build mode
    public void EndPreview()
    {
        // reset drag state
        lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
        dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
        isDragging = false;
        deferredRegistrations.Clear();
    }

    public void RotatePreview()
    {
        rotation = Quaternion.Euler(0,0, Mathf.Round((rotation.eulerAngles.z + 90f) % 360f));
    }

    // UI entry point used by BuildModeController
    public bool TryPlaceAtMouse()
    {
        if (!EnsureGridServiceCached()) return false;
        if (miWorldToCell == null) return false;
        var world = GetMouseWorld();
        var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
        var cell = res is Vector2Int v ? v : new Vector2Int(0,0);
        if (IsBlocked(cell)) { Debug.Log($"[Placer] Blocked at {cell}"); return false; }
        var placed = PlaceBeltAtCell(cell, RotationToDirectionName(rotation));
        if (placed) MarkGraphDirtyIfPresent();
        return placed;
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
        isDragging = true;
        dragStartCell = cell;
        lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
        dragStartWorld = world;

        // Immediately place the initial belt so it exists to be reoriented when drag begins
        // Use current preview rotation for initial placement
        PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation));
        return true;
    }

    // Called when the user releases the pointer. If no dragging movement occurred
    // then place a single belt at the initial cell (already handled here).
    public bool OnPointerUp()
    {
        if (!isDragging) return false;
        isDragging = false;
        // if no movement occurred, place at the start cell
        if (lastDragCell.x == int.MinValue && dragStartCell.x != int.MinValue)
        {
            var placed = PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation));
            if (placed) MarkGraphDirtyIfPresent();
            dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
            lastDragCell = new Vector2Int(int.MinValue, int.MinValue);

            // flush deferred registrations now that drag finished
            FlushDeferredRegistrations();
            return placed;
        }

        // flush deferred registrations now that drag finished
        FlushDeferredRegistrations();

        dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
        lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
        return false;
    }

    void FlushDeferredRegistrations()
    {
        if (deferredRegistrations.Count == 0) return;
        foreach (var c in deferredRegistrations)
        {
            TryRegisterCellInBeltSim(c);
        }
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
        if (!EnsureGridServiceCached()) return;
        if (miWorldToCell == null) return;

        var world = GetMouseWorld();
        var res = miWorldToCell.Invoke(gridServiceInstance, new object[] { world });
        var cell = res is Vector2Int v ? v : new Vector2Int(0,0);

        // fallback start drag if controller didn't call OnPointerDown
        if (Input.GetMouseButtonDown(0))
        {
            isDragging = true;
            dragStartCell = cell;
            lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
            dragStartWorld = world;
            return;
        }

        if (Input.GetMouseButton(0) && isDragging)
        {
            // Only attempt to place/update when the pointer has moved into a new cell
            // TryPlaceCellFromDrag will handle updating the previous cell direction and placing the next cell
            TryPlaceCellFromDrag(cell);

            // If something changed (lastDragCell set), mark graph dirty
            if (lastDragCell.x != int.MinValue) MarkGraphDirtyIfPresent();
            return;
        }

        if (Input.GetMouseButtonUp(0) && isDragging)
        {
            // if the drag never moved (no placements), perform a single-click place at dragStartCell
            if (lastDragCell.x == int.MinValue && dragStartCell.x != int.MinValue)
            {
                var placed = PlaceBeltAtCell(dragStartCell, RotationToDirectionName(rotation));
                if (placed) MarkGraphDirtyIfPresent();
            }
            isDragging = false;
            dragStartCell = new Vector2Int(int.MinValue, int.MinValue);
            lastDragCell = new Vector2Int(int.MinValue, int.MinValue);
            dragStartWorld = Vector3.zero;

            // flush deferred registrations after drag
            FlushDeferredRegistrations();
            return;
        }
    }

    void PlacePathBetween(Vector2Int start, Vector2Int end)
    {
        // Walk from start to end using same stepping logic; place each cell with direction towards next cell
        var current = start;
        while (true)
        {
            var remaining = end - current;
            if (remaining == Vector2Int.zero) break;
            Vector2Int step = Math.Abs(remaining.x) >= Math.Abs(remaining.y) ? new Vector2Int(Math.Sign(remaining.x), 0) : new Vector2Int(0, Math.Sign(remaining.y));
            var next = current + step;
            var dirName = DirectionNameFromDelta(step);
            if (dirName == null) break;
            var placed = PlaceBeltAtCell(current, dirName);
            if (!placed) break;
            lastDragCell = current;
            current = next;
            if (current == end)
            {
                // place the final cell as well
                PlaceBeltAtCell(current, dirName);
                lastDragCell = current;
                break;
            }
        }
    }

    void TryPlaceCellFromDrag(Vector2Int cell)
    {
        // If we haven't placed any cell yet (only the initial placed on pointer down)
        // then when the pointer moves into a different cell we should:
        // 1) update the start cell's direction to point toward the next cell
        // 2) place the next cell and set lastDragCell to that newly placed cell
        if (lastDragCell.x == int.MinValue)
        {
            // If pointer still inside the start cell, do nothing
            if (cell == dragStartCell) return;

            // pointer left the start cell - compute step toward current cell
            var deltaStart = cell - dragStartCell;
            Vector2Int stepStart;
            if (Math.Abs(deltaStart.x) >= Math.Abs(deltaStart.y))
                stepStart = new Vector2Int(Math.Sign(deltaStart.x), 0);
            else
                stepStart = new Vector2Int(0, Math.Sign(deltaStart.y));

            var next = dragStartCell + stepStart;
            var dirName = DirectionNameFromDelta(stepStart);
            if (dirName == null) return;

            // update the start cell to point to 'next'
            UpdateBeltDirectionAtCell(dragStartCell, dirName);

            // attempt to place the next cell and mark it as last placed if successful
            var placedNext = PlaceBeltAtCell(next, dirName);
            if (placedNext) lastDragCell = next;
            return;
        }

        // If pointer hasn't moved past the last placed cell, do nothing (we don't update direction while inside cell)
        if (cell == lastDragCell) return;

        // pointer moved beyond last placed cell - step one cell toward the current pointer cell
        var delta = cell - lastDragCell;
        Vector2Int step = Vector2Int.zero;
        if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
            step = new Vector2Int(Math.Sign(delta.x), 0);
        else
            step = new Vector2Int(0, Math.Sign(delta.y));

        var nextCell = lastDragCell + step;
        var dirNameNext = DirectionNameFromDelta(step);
        if (dirNameNext == null) return;

        // Before placing the next cell, update the previous (lastDragCell) to point toward it
        UpdateBeltDirectionAtCell(lastDragCell, dirNameNext);

        var placed = PlaceBeltAtCell(nextCell, dirNameNext);
        if (placed) lastDragCell = nextCell;
    }

    // Place a belt at a specific cell with an output direction name ("Right","Left","Up","Down")
    // This handles collision checks, instantiates visuals, hooks the Conveyor prefab into GridService
    // and finally calls GridService.SetBeltCell via reflection to set the logical in/out directions.
    bool PlaceBeltAtCell(Vector2Int cell2, string outDirName)
    {
        if (miCellToWorld == null) { Debug.LogWarning("[Placer] GridService.CellToWorld not found"); return false; }
        if (miSetBeltCell == null)
        {
            Debug.LogWarning("[Placer] GridService.SetBeltCell not found; cannot configure cell directions.");
            return false;
        }

        // compute blocking
        var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f });
        var center = worldObj is Vector3 vv ? vv : Vector3.zero;
        var hit = Physics2D.OverlapBox((Vector2)center, Vector2.one * 0.9f, 0f, blockingMask);
        if (hit != null)
        {
            // blocked; skip
            return false;
        }

        // instantiate visual if provided
        if (conveyorPrefab != null)
        {
            // align rotation to outDirName
            float z = outDirName switch { "Right" => 0f, "Up" => 90f, "Left" => 180f, "Down" => 270f, _ => 0f };
            z += visualRotationOffset;
            var rot = Quaternion.Euler(0,0,z);
            var go = Instantiate(conveyorPrefab, center, rot);
            // if prefab contains a Conveyor component, update its logical direction to match placement
            try
            {
                var conv = go.GetComponent<Conveyor>();
                if (conv != null)
                {
                    // map name to Direction enum
                    switch (outDirName)
                    {
                        case "Right": conv.direction = Direction.Right; break;
                        case "Left": conv.direction = Direction.Left; break;
                        case "Up": conv.direction = Direction.Up; break;
                        case "Down": conv.direction = Direction.Down; break;
                        default: conv.direction = Direction.Right; break;
                    }
                    Debug.Log($"[Placer] Set prefab conveyor.direction={conv.direction}");

                    // Inform GridService about this Conveyor instance so it can set cell.inA/outA from the component
                    try
                    {
                        var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) });
                        if (setConv != null)
                        {
                            setConv.Invoke(gridServiceInstance, new object[] { cell2, conv });
                            // notify belt sim about the new conveyor immediately
                            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                            foreach (var mb in all)
                            {
                                if (mb == null) continue;
                                var t = mb.GetType();
                                if (t.Name == "BeltSimulationService")
                                {
                                    var mi = t.GetMethod("RegisterConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (mi != null) mi.Invoke(mb, new object[] { conv });
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    // we're done: visual and grid are in sync

                    // defer belt sim registration when dragging
                    RegisterOrDefer(cell2);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Placer] Failed to set Conveyor on prefab: {ex.Message}");
            }
        }

        // set grid cell via reflection
        // Prefer using enum name parsing on the GridService's Direction type if available to avoid numeric-mapping mismatches across assemblies
        object outObj = null;
        object inObj = null;
        var oppName = outDirName == "Right" ? "Left" : outDirName == "Left" ? "Right" : outDirName == "Up" ? "Down" : outDirName == "Down" ? "Up" : "None";
        if (directionType != null)
        {
            try
            {
                outObj = Enum.Parse(directionType, outDirName);
                inObj = Enum.Parse(directionType, oppName);
            }
            catch
            {
                // fallback to numeric mapping below
                outObj = null; inObj = null;
            }
        }
        if (outObj == null || inObj == null)
        {
            int outIndex = DirectionIndexFromName(outDirName);
            int inIndex = DirectionIndexFromName(oppName);
            outObj = Enum.ToObject(directionType ?? typeof(Direction), outIndex);
            inObj = Enum.ToObject(directionType ?? typeof(Direction), inIndex);
        }
        try
        {
            miSetBeltCell.Invoke(gridServiceInstance, new object[] { cell2, inObj, outObj });
            // register with belt sim (deferred while dragging)
            RegisterOrDefer(cell2);
            // If we instantiated a visual with a Conveyor component, ensure GridService knows about it immediately
            if (conveyorPrefab != null)
            {
                // find any Conveyor on objects at this world position (approx)
                var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                foreach (var col in colliders)
                {
                    var go = col.gameObject;
                    var conv = go.GetComponent<Conveyor>();
                    if (conv != null)
                    {
                        try
                        {
                            var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) });
                            if (setConv != null)
                                setConv.Invoke(gridServiceInstance, new object[] { cell2, conv });
                            // also inform belt sim
                            var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                            foreach (var mb in all)
                            {
                                if (mb == null) continue;
                                var t = mb.GetType();
                                if (t.Name == "BeltSimulationService")
                                {
                                    var mi = t.GetMethod("RegisterConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (mi != null) mi.Invoke(mb, new object[] { conv });
                                    break;
                                }
                            }
                        }
                        catch { }
                        break;
                    }
                }
            }
             return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Placer] Failed to set belt cell {cell2}: {ex.Message}");
            return false;
        }
    }

    void RegisterOrDefer(Vector2Int cell)
    {
        if (isDragging)
            deferredRegistrations.Add(cell);
        else
            TryRegisterCellInBeltSim(cell);
    }

    string DirectionNameFromDelta(Vector2Int delta)
    {
        if (delta.x > 0) return "Right";
        if (delta.x < 0) return "Left";
        if (delta.y > 0) return "Up";
        if (delta.y < 0) return "Down";
        return null;
    }

    // Update an existing belt cell's output direction and visual Conveyor (if present)
    // This will try to find a Conveyor GameObject at the cell, update its "direction" field and rotation,
    // notify GridService.SetConveyor and GridService.SetBeltCell (via reflection) so the grid data matches the visual.
    bool UpdateBeltDirectionAtCell(Vector2Int cell2, string outDirName)
    {
        if (miCellToWorld == null) { Debug.LogWarning("[Placer] GridService.CellToWorld not found"); return false; }
        if (miSetBeltCell == null)
        {
            Debug.LogWarning("[Placer] GridService.SetBeltCell not found; cannot configure cell directions.");
            return false;
        }

        // Try to find an existing Conveyor object for this cell first
        Conveyor conv = null;
        try
        {
            var getConv = gridServiceInstance.GetType().GetMethod("GetConveyor", new Type[] { typeof(Vector2Int) });
            if (getConv != null)
            {
                var convObj = getConv.Invoke(gridServiceInstance, new object[] { cell2 });
                conv = convObj as Conveyor;
            }

            if (conv == null)
            {
                var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f });
                var center = worldObj is Vector3 vv ? vv : Vector3.zero;
                var colliders = Physics2D.OverlapBoxAll((Vector2)center, Vector2.one * 0.9f, 0f);
                foreach (var col in colliders)
                {
                    var go = col.gameObject;
                    var c = go.GetComponent<Conveyor>();
                    if (c != null)
                    {
                        conv = c;
                        break;
                    }
                }
            }
        }
        catch { }

        var oppName = outDirName == "Right" ? "Left" : outDirName == "Left" ? "Right" : outDirName == "Up" ? "Down" : outDirName == "Down" ? "Up" : "None";

        // If we have a Conveyor GameObject, update it and notify GridService via SetConveyor so grid fields stay consistent
        if (conv != null)
        {
            try
            {
                // map name to Direction enum on local Conveyor component if available
                switch (outDirName)
                {
                    case "Right": conv.direction = Direction.Right; break;
                    case "Left": conv.direction = Direction.Left; break;
                    case "Up": conv.direction = Direction.Up; break;
                    case "Down": conv.direction = Direction.Down; break;
                    default: conv.direction = Direction.Right; break;
                }

                // rotate visual to match
                float z = outDirName switch { "Right" => 0f, "Up" => 90f, "Left" => 180f, "Down" => 270f, _ => 0f };
                z += visualRotationOffset;
                conv.transform.rotation = Quaternion.Euler(0, 0, z);

                Debug.Log($"[Placer] Updated Conveyor GameObject '{conv.gameObject.name}' direction={conv.direction} rotationZ={z}");

                // Try to call GridService.SetConveyor if available (best-effort)
                try
                {
                    var setConv = gridServiceInstance.GetType().GetMethod("SetConveyor", new Type[] { typeof(Vector2Int), typeof(Conveyor) });
                    if (setConv != null)
                    {
                        setConv.Invoke(gridServiceInstance, new object[] { cell2, conv });

                        // Read back the cell to ensure grid stored the expected direction
                        try
                        {
                            var getCell = gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
                            if (getCell != null)
                            {
                                var cellObj = getCell.Invoke(gridServiceInstance, new object[] { cell2 });
                                if (cellObj != null)
                                {
                                    var tcell = cellObj.GetType();
                                    var outA = tcell.GetField("outA");
                                    if (outA != null)
                                    {
                                        var outVal = outA.GetValue(cellObj);
                                        // If the grid stored a different value than conv.direction, try fixing via miSetBeltCell using the GridService enum type
                                        if (outVal == null || outVal.ToString() != conv.direction.ToString())
                                        {
                                            try
                                            {
                                                if (miSetBeltCell != null && directionType != null)
                                                {
                                                    var outObjFix = Enum.Parse(directionType, conv.direction.ToString());
                                                    var inObjFix = Enum.Parse(directionType, DirectionUtil.Opposite(conv.direction).ToString());
                                                    miSetBeltCell.Invoke(gridServiceInstance, new object[] { cell2, inObjFix, outObjFix });
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // ALSO update the underlying belt cell explicitly via miSetBeltCell to ensure grid fields match the Conveyor direction
                try
                {
                    var oppNameLocal = outDirName == "Right" ? "Left" : outDirName == "Left" ? "Right" : outDirName == "Up" ? "Down" : outDirName == "Down" ? "Up" : "None";
                    object outObjLocal = null; object inObjLocal = null;
                    if (directionType != null)
                    {
                        try { outObjLocal = Enum.Parse(directionType, outDirName); inObjLocal = Enum.Parse(directionType, oppNameLocal); }
                        catch { outObjLocal = null; inObjLocal = null; }
                    }
                    if (outObjLocal == null || inObjLocal == null)
                    {
                        int outIndexLocal = DirectionIndexFromName(outDirName);
                        int inIndexLocal = DirectionIndexFromName(oppNameLocal);
                        outObjLocal = Enum.ToObject(directionType ?? typeof(Direction), outIndexLocal);
                        inObjLocal = Enum.ToObject(directionType ?? typeof(Direction), inIndexLocal);
                    }

                    miSetBeltCell?.Invoke(gridServiceInstance, new object[] { cell2, inObjLocal, outObjLocal });
                    // defer belt sim registration while dragging
                    RegisterOrDefer(cell2);
                }
                catch { }

                // notify belt sim about updated conveyor
                var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                foreach (var mb in all)
                {
                    if (mb == null) continue;
                    var t = mb.GetType();
                    if (t.Name == "BeltSimulationService")
                    {
                        var mi = t.GetMethod("RegisterConveyor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (mi != null) mi.Invoke(mb, new object[] { conv });
                        break;
                    }
                }

                // also defer TryRegisterCellInBeltSim call
                RegisterOrDefer(cell2);

                TryRegisterCellInBeltSim(cell2);
                MarkGraphDirtyIfPresent();
                return true;
            }
            catch { }
        }

        // Fallback: set grid cell via reflection when no Conveyor component available
        try
        {
            object outObj = null;
            object inObj = null;
            if (directionType != null)
            {
                try
                {
                    outObj = Enum.Parse(directionType, outDirName);
                    inObj = Enum.Parse(directionType, oppName);
                }
                catch { outObj = null; inObj = null; }
            }
            if (outObj == null || inObj == null)
            {
                int outIndex = DirectionIndexFromName(outDirName);
                int inIndex = DirectionIndexFromName(oppName);
                outObj = Enum.ToObject(directionType ?? typeof(Direction), outIndex);
                inObj = Enum.ToObject(directionType ?? typeof(Direction), inIndex);
            }

            miSetBeltCell.Invoke(gridServiceInstance, new object[] { cell2, inObj, outObj });
            // defer registration while dragging
            RegisterOrDefer(cell2);

            // Debug readback
            try
            {
                var getCell = gridServiceInstance.GetType().GetMethod("GetCell", new Type[] { typeof(Vector2Int) });
                if (getCell != null)
                {
                    var cellObj = getCell.Invoke(gridServiceInstance, new object[] { cell2 });
                    if (cellObj != null)
                    {
                        var t = cellObj.GetType();
                        var outA = t.GetField("outA");
                        var inA = t.GetField("inA");
                        if (outA != null && inA != null)
                        {
                            var outVal = outA.GetValue(cellObj);
                            var inVal = inA.GetValue(cellObj);
                            Debug.Log($"[Placer] Grid cell {cell2} after SetBeltCell: inA={inVal} outA={outVal} (expected out={outDirName})");
                        }
                    }
                }
            }
            catch { }

            MarkGraphDirtyIfPresent();
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Placer] Failed to update belt cell {cell2}: {ex.Message}");
            return false;
        }
    }

    // helper to notify the running BeltSimulationService about a new/changed cell
    void TryRegisterCellInBeltSim(Vector2Int cell)
    {
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name == "BeltSimulationService")
            {
                var mi = t.GetMethod("RegisterCell", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(mb, new object[] { cell });
                break;
            }
        }
    }

    object RotationToDirectionObject(Quaternion q)
    {
        directionType = directionType ?? FindDirectionType();
        if (directionType == null || !directionType.IsEnum)
            return null;
        // Map Z-rotation to cardinal names
        var z = q.eulerAngles.z;
        z = Mathf.Repeat(z, 360f);
        var snapped = Mathf.Round(z / 90f) * 90f;
        int i = Mathf.RoundToInt(snapped) % 360;
        string name = i switch { 0 => "Right", 90 => "Up", 180 => "Left", 270 => "Down", _ => "Right" };
        return Enum.Parse(directionType, name);
    }

    string RotationToDirectionName(Quaternion q)
    {
        var z = q.eulerAngles.z;
        z = Mathf.Repeat(z, 360f);
        var snapped = Mathf.Round(z / 90f) * 90f;
        int i = Mathf.RoundToInt(snapped) % 360;
        return i switch { 0 => "Right", 90 => "Up", 180 => "Left", 270 => "Down", _ => "Right" };
    }

    object OppositeDirectionObject(object dirObj)
    {
        directionType = directionType ?? FindDirectionType();
        if (directionType == null || dirObj == null) return null;
        var name = dirObj.ToString();
        string opp = name == "Right" ? "Left" : name == "Left" ? "Right" : name == "Up" ? "Down" : name == "Down" ? "Up" : "None";
        return Enum.Parse(directionType, opp);
    }

    Type FindDirectionType()
    {
        // Search loaded assemblies for an enum named "Direction"
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

    public void RefreshPreviewAfterPlace()
    {
        // no-op for tap-to-place
    }

    void MarkGraphDirtyIfPresent()
    {
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name == "BeltGraphService")
            {
                var mi = t.GetMethod("MarkDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(mb, null);
                break;
            }
        }
    }

    bool IsBlocked(Vector2Int cell2)
    {
        if (miCellToWorld == null) return false;
        var worldObj = miCellToWorld.Invoke(gridServiceInstance, new object[] { cell2, 0f });
        var center = worldObj is Vector3 vv ? vv : Vector3.zero;

        var hit = Physics2D.OverlapBox((Vector2)center, Vector2.one * 0.9f, 0f, blockingMask);
        return hit != null;
    }

    Vector3 GetMouseWorld()
    {
        var cam = Camera.main;
        var pos = Input.mousePosition;
        var world = cam != null ? cam.ScreenToWorldPoint(pos) : new Vector3(pos.x, pos.y, 0f);
        world.z = 0f;
        return world;
    }

    bool EnsureGridServiceCached()
    {
        if (gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null) return true;
        if (gridServiceObject != null)
        {
            CacheGridServiceReflection(gridServiceObject);
            return gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null;
        }

        // try to find a MonoBehaviour named GridService in scene
        var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in all)
        {
            if (mb == null) continue;
            var t = mb.GetType();
            if (t.Name == "GridService")
            {
                gridServiceObject = mb.gameObject;
                CacheGridServiceReflection(gridServiceObject);
                break;
            }
        }
        return gridServiceInstance != null && miWorldToCell != null && miCellToWorld != null;
    }

    int DirectionIndexFromName(string name)
    {
        return name switch
        {
            "Up" => 0,
            "Right" => 1,
            "Down" => 2,
            "Left" => 3,
            _ => 4,
        };
    }
}
