using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Simple build manager that enters a "place conveyor" mode when StartPlacingConveyor
/// is called (e.g. from a UI Button). While in that mode left-clicking the game view
/// places a conveyor prefab snapped to the GridService cell centers. Press Escape or
/// call StopPlacing to exit the mode.
/// Supports click-and-drag placement: hold mouse and drag to place conveyors along the cardinal direction.
/// </summary>
public class BuildManager : MonoBehaviour
{
    [Header("Prefabs & Parents")]
    [SerializeField] GameObject conveyorPrefab;
    [SerializeField] Transform placeParent; // parent for spawned conveyors
    [SerializeField, Min(0.1f)] float beltBuildSeconds = 0.4f;

    bool isPlacingConveyor = false;
    Camera cam;

    // drag placement state
    bool isMouseDown = false;
    Vector2Int lastPlacedCell;
    Conveyor lastPlacedConveyor;
    Direction? lastMoveDirection;
    readonly List<Vector2Int> cachedMineOutputCells = new List<Vector2Int>(2);

    void Awake()
    {
        cam = Camera.main;
        if (cam == null) cam = Camera.current;
    }

    void Update()
    {
        if (!isPlacingConveyor) return;

        // Cancel with Esc
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            StopPlacing();
            return;
        }

        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        // Mouse down: start placement (place first conveyor if possible)
        if (Input.GetMouseButtonDown(0))
        {
            // avoid clicks on UI
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            isMouseDown = true;

            var screen = Input.mousePosition;
            var worldPoint = cam.ScreenToWorldPoint(screen);
            worldPoint.z = 0f;

            var gs = GridService.Instance;
            if (gs == null)
            {
                Debug.LogWarning("BuildManager: No GridService instance found.");
                return;
            }

            var cell = gs.WorldToCell(worldPoint);
            if (!gs.InBounds(cell)) return;

            // avoid overwriting existing conveyor
            var cellData = gs.GetCell(cell);
            if (cellData != null && cellData.hasConveyor) return;

            if (PlaceConveyorAtCell(cell, null))
                lastPlacedCell = cell;
        }

        // Mouse held: allow dragging to place along cardinal directions
        if (isMouseDown && Input.GetMouseButton(0))
        {
            var screen = Input.mousePosition;
            var worldPoint = cam.ScreenToWorldPoint(screen);
            worldPoint.z = 0f;

            var gs = GridService.Instance;
            if (gs == null) return;

            var curCell = gs.WorldToCell(worldPoint);
            if (!gs.InBounds(curCell)) return;

            if (curCell != lastPlacedCell)
            {
                // Step one cell at a time from lastPlacedCell toward curCell
                Vector2Int from = lastPlacedCell;
                Vector2Int to = curCell;
                // while there are remaining steps
                while (from != to)
                {
                    var delta = to - from;
                    Vector2Int step;
                    // prefer larger axis to decide step direction for a straight-ish path
                    if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                        step = new Vector2Int(Math.Sign(delta.x), 0);
                    else
                        step = new Vector2Int(0, Math.Sign(delta.y));

                    var next = from + step;

                    // ensure adjacency (defensive)
                    if (Mathf.Abs(next.x - from.x) + Mathf.Abs(next.y - from.y) != 1) break;

                    // stop if next cell already has a conveyor
                    var nextData = gs.GetCell(next);
                    if (nextData != null && nextData.hasConveyor)
                        break;

                    var dir = DeltaToDirection(step);

                    // update previous conveyor's direction to point to the next cell
                    if (lastPlacedConveyor != null)
                    {
                        var incomingSide = lastMoveDirection.HasValue
                            ? DirectionUtil.Opposite(lastMoveDirection.Value)
                            : ResolveIncomingSide(lastPlacedCell);
                        ApplyConveyorShape(lastPlacedConveyor, dir, incomingSide);
                        // ensure belt graph sees the updated direction immediately
                        BeltSimulationService.Instance?.RegisterConveyor(lastPlacedConveyor);
                    }

                    // place conveyor at next cell and set its direction
                    if (!PlaceConveyorAtCell(next, dir))
                        break;
                    lastMoveDirection = dir;

                    // advance
                    from = next;
                    lastPlacedCell = from;

                    // If we've placed up to the target cell, stop
                    if (from == to) break;
                }
            }
        }

        // Mouse up: stop drag state
        if (isMouseDown && Input.GetMouseButtonUp(0))
        {
            FinalizeLastPlacedConveyor();
            isMouseDown = false;
            lastPlacedConveyor = null;
            lastMoveDirection = null;
        }
    }

    // Called by UI button to start placing conveyors
    public void StartPlacingConveyor()
    {
        isPlacingConveyor = true;
        Debug.Log("BuildManager: Entering conveyor placement mode.");
        // set global state to Build if GameManager exists
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
    }

    // Stop placement mode
    public void StopPlacing()
    {
        isPlacingConveyor = false;
        isMouseDown = false;
        lastPlacedConveyor = null;
        lastMoveDirection = null;
        Debug.Log("BuildManager: Exiting placement mode.");
        // restore global state to Play if GameManager exists
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Play);
    }

    // Toggle helper
    public void TogglePlacingConveyor()
    {
        if (isPlacingConveyor) StopPlacing(); else StartPlacingConveyor();
    }

    public bool IsPlacing => isPlacingConveyor;

    // Helper: instantiate conveyor at cell and optionally set direction
    bool PlaceConveyorAtCell(Vector2Int cell, Direction? setDirection)
    {
        var gs = GridService.Instance;
        if (gs == null) return false;

        var cellData = gs.GetCell(cell);
        if (cellData != null && cellData.hasConveyor) return false;

        if (conveyorPrefab == null)
        {
            Debug.LogWarning("BuildManager: conveyorPrefab not assigned.");
            return false;
        }

        float z = conveyorPrefab.transform.position.z;
        var spawnPos = gs.CellToWorld(cell, z);

        // choose parent: explicit placeParent if assigned, otherwise use the Belt Container found in scene
        Transform parent = placeParent != null ? placeParent : ContainerLocator.GetBeltContainer();

        var go = Instantiate(conveyorPrefab, spawnPos, Quaternion.identity, parent);
        var conv = go.GetComponent<Conveyor>();
        if (conv != null)
        {
            conv.isGhost = true;
            var task = conv.GetComponent<BlueprintTask>();
            if (task == null) task = conv.gameObject.AddComponent<BlueprintTask>();

            var outgoing = ResolveOutgoingDirection(cell, setDirection);
            var incomingSide = ResolveIncomingSide(cell);
            ApplyConveyorShape(conv, outgoing, incomingSide);

            task.InitializeBelt(cell, conv.direction, conv.transform.rotation, conveyorPrefab, 0, beltBuildSeconds);
            SyncBlueprintShape(task, conv, outgoing, incomingSide);
            lastPlacedConveyor = conv;
            return true;
        }
        return false;
    }

    void FinalizeLastPlacedConveyor()
    {
        if (lastPlacedConveyor == null)
            return;

        var incomingSide = lastMoveDirection.HasValue
            ? DirectionUtil.Opposite(lastMoveDirection.Value)
            : ResolveIncomingSide(lastPlacedCell);
        var outgoing = ResolveOutgoingDirection(lastPlacedCell, null);
        ApplyConveyorShape(lastPlacedConveyor, outgoing, incomingSide);
        BeltSimulationService.Instance?.RegisterConveyor(lastPlacedConveyor);
    }

    Direction ResolveOutgoingDirection(Vector2Int cell, Direction? preferredDirection)
    {
        if (preferredDirection.HasValue)
            return preferredDirection.Value;

        if (TryResolveMachineInputDirection(cell, out var machineInputDirection))
            return machineInputDirection;

        var incomingSide = ResolveIncomingSide(cell);
        if (incomingSide.HasValue)
            return DirectionUtil.Opposite(incomingSide.Value);

        return Direction.Right;
    }

    Direction? ResolveIncomingSide(Vector2Int cell)
    {
        if (TryResolveNeighborConveyorOutput(cell, out var conveyorSide))
            return conveyorSide;

        if (TryResolveMachineOutputSide(cell, out var machineSide))
            return machineSide;

        return null;
    }

    bool TryResolveNeighborConveyorOutput(Vector2Int cell, out Direction side)
    {
        side = Direction.None;
        var gs = GridService.Instance;
        if (gs == null) return false;

        var dirs = CardinalDirections();
        for (int i = 0; i < dirs.Length; i++)
        {
            var delta = dirs[i];
            var neighborCell = cell + delta;
            if (!gs.InBounds(neighborCell)) continue;

            var neighbor = gs.GetCell(neighborCell);
            if (!HasConveyorOutputTowards(neighbor, DirectionUtil.Opposite(DirectionFromDelta(delta))))
                continue;

            side = DirectionFromDelta(delta);
            return true;
        }

        return false;
    }

    bool TryResolveMachineOutputSide(Vector2Int cell, out Direction side)
    {
        side = Direction.None;

        var mines = FindObjectsByType<SugarMine>(FindObjectsSortMode.None);
        for (int i = 0; i < mines.Length; i++)
        {
            var mine = mines[i];
            if (mine == null || mine.isGhost) continue;

            mine.GetOutputCellsForConnectivity(cachedMineOutputCells);
            for (int j = 0; j < cachedMineOutputCells.Count; j++)
            {
                if (cachedMineOutputCells[j] != cell) continue;
                side = DirectionUtil.Opposite(DirectionFromDelta(DirectionUtil.DirVec(mine.outputDirection)));
                return true;
            }
        }

        var presses = FindObjectsByType<PressMachine>(FindObjectsSortMode.None);
        for (int i = 0; i < presses.Length; i++)
        {
            var press = presses[i];
            if (press == null || press.isGhost) continue;

            if (press.Cell + press.OutputVec * 2 != cell) continue;
            side = DirectionUtil.Opposite(DirectionFromDelta(press.OutputVec));
            return true;
        }

        var storages = FindObjectsByType<StorageContainerMachine>(FindObjectsSortMode.None);
        for (int i = 0; i < storages.Length; i++)
        {
            var storage = storages[i];
            if (storage == null || storage.isGhost) continue;

            if (storage.Cell + storage.OutputVec * 2 != cell) continue;
            side = DirectionUtil.Opposite(DirectionFromDelta(storage.OutputVec));
            return true;
        }

        var colorizers = FindObjectsByType<ColorizerMachine>(FindObjectsSortMode.None);
        for (int i = 0; i < colorizers.Length; i++)
        {
            var colorizer = colorizers[i];
            if (colorizer == null || colorizer.isGhost) continue;

            if (colorizer.Cell + colorizer.OutputVec != cell) continue;
            side = DirectionUtil.Opposite(DirectionFromDelta(colorizer.OutputVec));
            return true;
        }

        return false;
    }

    bool TryResolveMachineInputDirection(Vector2Int cell, out Direction direction)
    {
        direction = Direction.None;
        var dirs = CardinalDirections();
        for (int i = 0; i < dirs.Length; i++)
        {
            var delta = dirs[i];
            var neighborCell = cell + delta;
            if (!MachineRegistry.TryGet(neighborCell, out var machine) || machine == null)
                continue;

            var approachFromVec = cell - neighborCell;
            bool accepts = false;
            try { accepts = machine.CanAcceptFrom(approachFromVec); } catch { accepts = false; }
            if (!accepts)
                continue;

            direction = DirectionFromDelta(delta);
            return true;
        }

        return false;
    }

    void ApplyConveyorShape(Conveyor conveyor, Direction outgoing, Direction? incomingSide)
    {
        if (conveyor == null) return;

        if (incomingSide.HasValue && incomingSide.Value != Direction.None && incomingSide.Value != outgoing)
            conveyor.SetCurve(incomingSide.Value, outgoing);
        else
            conveyor.SetStraight(outgoing);
    }

    void SyncBlueprintShape(BlueprintTask task, Conveyor conveyor, Direction outgoing, Direction? incomingSide)
    {
        if (task == null || conveyor == null)
            return;

        if (incomingSide.HasValue && incomingSide.Value != Direction.None && incomingSide.Value != outgoing)
            task.UpdateBeltCurve(incomingSide.Value, outgoing, conveyor.transform.rotation);
        else
            task.UpdateBeltDirection(outgoing, conveyor.transform.rotation);
    }

    static bool HasConveyorOutputTowards(GridService.Cell cell, Direction direction)
    {
        if (cell == null) return false;
        if (cell.type == GridService.CellType.Belt || cell.type == GridService.CellType.Junction)
        {
            if (cell.outA == direction || cell.outB == direction)
                return true;
        }

        return cell.conveyor != null && cell.conveyor.DirVec() == DirectionUtil.DirVec(direction);
    }

    static Vector2Int[] CardinalDirections()
    {
        return new[]
        {
            Vector2Int.left,
            Vector2Int.right,
            Vector2Int.down,
            Vector2Int.up,
        };
    }

    static Direction DeltaToDirection(Vector2Int d)
    {
        if (d.x > 0) return Direction.Right;
        if (d.x < 0) return Direction.Left;
        if (d.y > 0) return Direction.Up;
        return Direction.Down;
    }

    static Direction DirectionFromDelta(Vector2Int delta)
    {
        if (delta.x > 0) return Direction.Right;
        if (delta.x < 0) return Direction.Left;
        if (delta.y > 0) return Direction.Up;
        if (delta.y < 0) return Direction.Down;
        return Direction.None;
    }

    static void RotateTransformToDirection(Transform t, Direction d)
    {
        // Map Direction to degrees (Right = 0, Up = 90, Left = 180, Down = 270)
        float angle = d switch
        {
            Direction.Right => 0f,
            Direction.Up => 90f,
            Direction.Left => 180f,
            Direction.Down => 270f,
            _ => 0f,
        };
        if (t != null) t.rotation = Quaternion.Euler(0f, 0f, angle);
    }
}
