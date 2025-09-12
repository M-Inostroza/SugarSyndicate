using System;
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

    bool isPlacingConveyor = false;
    Camera cam;

    // drag placement state
    bool isMouseDown = false;
    Vector2Int lastPlacedCell;
    Conveyor lastPlacedConveyor;

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

            PlaceConveyorAtCell(cell, null);
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
                        lastPlacedConveyor.direction = dir;
                        // rotate visual to match
                        RotateTransformToDirection(lastPlacedConveyor.transform, dir);
                        // ensure belt graph sees the updated direction immediately
                        BeltSimulationService.Instance?.RegisterConveyor(lastPlacedConveyor);
                    }

                    // place conveyor at next cell and set its direction
                    PlaceConveyorAtCell(next, dir);

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
            isMouseDown = false;
            lastPlacedConveyor = null;
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
    void PlaceConveyorAtCell(Vector2Int cell, Direction? setDirection)
    {
        var gs = GridService.Instance;
        if (gs == null) return;

        var cellData = gs.GetCell(cell);
        if (cellData != null && cellData.hasConveyor) return;

        if (conveyorPrefab == null)
        {
            Debug.LogWarning("BuildManager: conveyorPrefab not assigned.");
            return;
        }

        float z = conveyorPrefab.transform.position.z;
        var spawnPos = gs.CellToWorld(cell, z);
        var go = Instantiate(conveyorPrefab, spawnPos, Quaternion.identity, placeParent);
        var conv = go.GetComponent<Conveyor>();
        if (conv != null)
        {
            if (setDirection.HasValue)
            {
                conv.direction = setDirection.Value;
                RotateTransformToDirection(conv.transform, setDirection.Value);
            }

            gs.SetConveyor(cell, conv);
            lastPlacedConveyor = conv;

            // register immediately so graph updates this frame
            BeltSimulationService.Instance?.RegisterConveyor(conv);
        }
    }

    static Direction DeltaToDirection(Vector2Int d)
    {
        if (d.x > 0) return Direction.Right;
        if (d.x < 0) return Direction.Left;
        if (d.y > 0) return Direction.Up;
        return Direction.Down;
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
