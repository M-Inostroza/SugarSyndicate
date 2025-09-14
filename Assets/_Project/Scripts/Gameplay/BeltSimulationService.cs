using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class BeltSimulationService : MonoBehaviour
{
    public static BeltSimulationService Instance { get; private set; }

    readonly HashSet<Vector2Int> active = new HashSet<Vector2Int>();
    // pending registrations added during frame (e.g. by placer). They are merged into active at the start of the next StepActive
    readonly HashSet<Vector2Int> pendingActive = new HashSet<Vector2Int>();
    GridService grid;

    [Header("Speed")]
    [Tooltip("If true, the belt sim runs on GameTick; otherwise it steps every frame.")]
    [SerializeField] bool useGameTick = true;
    [Tooltip("How many ticks are required before items advance one cell. 1 = move every tick (fast), 2 = half speed, etc.")]
    [SerializeField, Min(1)] int ticksPerStep = 1;
    int tickAccumulator;

    [Header("Visuals")]
    [Tooltip("Enable smooth interpolation of item views between cells.")]
    [SerializeField] bool smoothMovement = true;
    [Tooltip("Fallback movement duration (seconds) when not using GameTick-driven timing.")]
    [SerializeField, Min(0f)] float moveDurationSeconds = 0.2f;

    // track pause transitions
    bool wasPaused;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        grid = GridService.Instance;
        DontDestroyOnLoad(gameObject);
    }

    void OnEnable()
    {
        if (useGameTick)
            GameTick.OnTickStart += OnTick;
    }

    void OnDisable()
    {
        if (useGameTick)
            GameTick.OnTickStart -= OnTick;
    }

    bool IsPaused()
    {
        // Pause only while in Build AND actively dragging to place belts
        if (GameManager.Instance == null) return false;
        bool inBuild = GameManager.Instance.State == GameState.Build;
        bool dragging = BuildModeController.IsDragging;
        return inBuild && dragging;
    }

    void HandlePauseTransitions()
    {
        bool paused = IsPaused();
        if (paused && !wasPaused)
        {
            // entering pause: snap all in-flight visuals to their destination cell centers
            if (visuals.Count > 0)
            {
                foreach (var kv in new List<KeyValuePair<Item, VisualState>>(visuals))
                {
                    var vs = kv.Value;
                    if (vs?.view != null)
                        vs.view.position = vs.end;
                }
                visuals.Clear();
            }
            wasPaused = true;
        }
        else if (!paused && wasPaused)
        {
            // exiting pause: snap all item views to their current cell centers & reseed active
            SnapAllItemViewsToCells();
            ReseedActiveFromGrid();
            wasPaused = false;
        }
    }

    void OnTick()
    {
        HandlePauseTransitions();
        // Do not accumulate ticks or step while paused
        if (IsPaused()) return;

        tickAccumulator++;
        if (tickAccumulator < ticksPerStep) return;
        tickAccumulator = 0;
        StepActive();
    }

    void Update()
    {
        HandlePauseTransitions();
        // Skip stepping and visual interpolation while paused
        if (IsPaused()) return;

        if (!useGameTick)
            StepActive();

        // Advance visuals every frame for smooth movement regardless of tick mode
        if (smoothMovement)
            UpdateVisuals(Time.deltaTime);
    }

    void StepActive()
    {
        if (grid == null) return;
        // Merge any pending registrations so they are processed in this step, but avoid processing registrations that arrive while stepping
        if (pendingActive.Count > 0)
        {
            foreach (var p in pendingActive) active.Add(p);
            pendingActive.Clear();
        }

        var step = new List<Vector2Int>(active);
        active.Clear();

        // movedThisStep prevents chained moves where an item would be moved multiple cells in one StepActive
        var movedThisStep = new HashSet<Vector2Int>();

        foreach (var cell in step)
        {
            // If this cell was the destination of a previous move this step, skip it to avoid double-moving
            if (movedThisStep.Contains(cell)) continue;

            var dest = StepCell(cell);

            if (dest.HasValue)
            {
                // mark destination as moved this step and schedule it for processing on the next StepActive
                movedThisStep.Add(dest.Value);
                active.Add(dest.Value);
            }
        }
    }

    bool IsBeltLike(GridService.Cell c)
        => c != null && (c.type == GridService.CellType.Belt || c.type == GridService.CellType.Junction || c.hasConveyor);

    bool HasOutputTowards(GridService.Cell from, Direction dir)
    {
        if (from == null) return false;
        if (from.type == GridService.CellType.Belt || from.type == GridService.CellType.Junction)
        {
            if (from.outA == dir) return true;
            if (from.outB == dir) return true;
        }
        if (from.conveyor != null)
        {
            // compare vectors for legacy conveyor
            return from.conveyor.DirVec() == DirectionUtil.DirVec(dir);
        }
        return false;
    }

    // Return the destination cell position if an item moved from cellPos, otherwise null.
    Vector2Int? StepCell(Vector2Int cellPos)
    {
        var cell = grid.GetCell(cellPos);
        if (cell == null) return null;

        // Empty junction/belt tries to pull from inputs
        if (!cell.hasItem)
        {
            if (cell.type == GridService.CellType.Belt || cell.type == GridService.CellType.Junction)
            {
                // TryPullFrom will move an item into 'cell' from a neighbor if possible and returns true if moved
                if (TryPullFrom(cellPos, cell.inA))
                {
                    // schedule this cell for processing next step
                    return cellPos;
                }

                if (!grid.GetCell(cellPos).hasItem && cell.type == GridService.CellType.Junction)
                {
                    if (TryPullFrom(cellPos, cell.inB))
                    {
                        return cellPos;
                    }
                }

                if (grid.GetCell(cellPos).hasItem)
                    return cellPos;
            }
            return null;
        }

        // Determine output direction
        Direction outDir = Direction.None;
        if (cell.type == GridService.CellType.Belt)
        {
            outDir = cell.outA;
        }
        else if (cell.type == GridService.CellType.Junction)
        {
            if (cell.outA != Direction.None && cell.outB != Direction.None)
            {
                outDir = (cell.junctionToggle & 1) == 0 ? cell.outA : cell.outB;
            }
            else
            {
                outDir = cell.outA != Direction.None ? cell.outA : cell.outB;
            }
        }
        else if (cell.conveyor != null)
        {
            var v = cell.conveyor.DirVec();
            if (v == new Vector2Int(1, 0)) outDir = Direction.Right;
            else if (v == new Vector2Int(-1, 0)) outDir = Direction.Left;
            else if (v == new Vector2Int(0, 1)) outDir = Direction.Up;
            else if (v == new Vector2Int(0, -1)) outDir = Direction.Down;
        }

        // If no valid output, keep the cell active so it can try again next step
        if (!DirectionUtil.IsCardinal(outDir)) { return cellPos; }

        var destPos = cellPos + DirectionUtil.DirVec(outDir);
        var dest = grid.GetCell(destPos);
        if (dest == null || dest.hasItem || !IsBeltLike(dest))
        {
            // can't move this step, keep active to retry later
            return cellPos;
        }

        // move logical item one cell
        dest.item = cell.item;
        dest.hasItem = true;
        cell.item = null;
        cell.hasItem = false;

        // schedule visual movement (visual system is separate)
        MoveView(dest.item, cellPos, destPos);

        // Return destination so caller schedules it for next step
        return destPos;
    }

    // Try to pull an item from neighbor into target cell. Returns true if a move occurred.
    bool TryPullFrom(Vector2Int target, Direction dir)
    {
        if (!DirectionUtil.IsCardinal(dir)) return false;
        var fromPos = target + DirectionUtil.DirVec(dir);
        var from = grid.GetCell(fromPos);
        var dest = grid.GetCell(target);
        if (from == null || dest == null) return false;
        if (!from.hasItem) return false;
        var requiredOut = DirectionUtil.Opposite(dir);
        if (!HasOutputTowards(from, requiredOut)) return false;
        if (dest.hasItem) return false;

        dest.item = from.item;
        dest.hasItem = true;
        from.item = null;
        from.hasItem = false;
        MoveView(dest.item, fromPos, target);
        return true;
    }

    // Visual interpolation state
    class VisualState
    {
        public Transform view;
        public Vector3 start;
        public Vector3 end;
        public float elapsed;
        public float duration;
    }

    readonly Dictionary<Item, VisualState> visuals = new Dictionary<Item, VisualState>();

    void UpdateVisuals(float dt)
    {
        if (visuals.Count == 0) return;
        var remove = new List<Item>();
        foreach (var kv in visuals)
        {
            var item = kv.Key;
            var vs = kv.Value;
            if (vs.view == null) { remove.Add(item); continue; }
            vs.elapsed += dt;
            float t = vs.duration <= 0f ? 1f : Mathf.Clamp01(vs.elapsed / vs.duration);
            vs.view.position = Vector3.Lerp(vs.start, vs.end, t);
            if (t >= 1f) remove.Add(item);
        }
        foreach (var it in remove)
            visuals.Remove(it);
    }

    // strict start-end move
    void EnqueueVisualMove(Item item, Vector3 startWorld, Vector3 targetWorld)
    {
        if (item?.view == null || !smoothMovement)
        {
            if (item?.view != null)
                item.view.position = targetWorld;
            visuals.Remove(item);
            return;
        }

        float baseDuration = moveDurationSeconds;
        if (useGameTick)
        {
            try
            {
                var gt = UnityEngine.Object.FindObjectOfType<GameTick>();
                if (gt != null)
                {
                    var field = typeof(GameTick).GetField("ticksPerSecond", BindingFlags.Instance | BindingFlags.Public);
                    if (field != null)
                    {
                        var tps = (int)field.GetValue(gt);
                        baseDuration = (1f / Mathf.Max(1, tps)) * Mathf.Max(1, ticksPerStep);
                    }
                }
            }
            catch { }
        }

        var duration = baseDuration * Mathf.Max(0.0001f, Vector3.Distance(startWorld, targetWorld));
        var vs = new VisualState
        {
            view = item.view,
            start = startWorld,
            end = targetWorld,
            elapsed = 0f,
            duration = duration
        };
        visuals[item] = vs;
    }

    // backward compat overload (if ever needed elsewhere)
    void EnqueueVisualMove(Item item, Vector3 targetWorld)
        => EnqueueVisualMove(item, item?.view != null && grid != null ? item.view.position : targetWorld, targetWorld);

    public bool TrySpawnItem(Vector2Int cellPos, Item item)
    {
        if (grid == null)
        {
            Debug.LogWarning($"[BeltSim] TrySpawnItem failed: GridService instance is null. Requested at {cellPos} with item {(item != null ? item.ToString() : "null")}.", this);
            return false;
        }

        var cell = grid.GetCell(cellPos);
        if (cell == null)
        {
            bool inBounds = grid.InBounds(cellPos);
            Debug.LogWarning($"[BeltSim] TrySpawnItem failed: Cell at {cellPos} is null. InBounds={inBounds}.", this);
            return false;
        }
        if (cell.hasItem)
        {
            Debug.LogWarning($"[BeltSim] TrySpawnItem failed: Cell {cellPos} already has an item.", this);
            return false;
        }
        if (!IsBeltLike(cell))
        {
            Debug.LogWarning($"[BeltSim] TrySpawnItem failed: No belt/junction at {cellPos}.", this);
            return false;
        }
        if (item == null)
        {
            Debug.LogWarning($"[BeltSim] TrySpawnItem: Provided item is null for {cellPos}. Proceeding will set hasItem=true with null item.", this);
        }

        cell.item = item;
        cell.hasItem = true;
        // move view instantly to placement location (spawns shouldn't animate from elsewhere)
        if (item?.view != null && grid != null)
        {
            var pos = grid.CellToWorld(cellPos, item.view.position.z);
            item.view.position = pos;
            visuals.Remove(item);
        }
        // register for processing on next step
        lock (pendingActive)
        {
            pendingActive.Add(cellPos);
        }
        Debug.Log($"[BeltSim] Spawned item at {cellPos}. PendingActiveCount={pendingActive.Count}.", this);
        return true;
    }

    public void RegisterConveyor(Conveyor c)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null || c == null) return;
        var cellPos = grid.WorldToCell(c.transform.position);
        lock (pendingActive)
        {
            pendingActive.Add(cellPos);
        }
    }

    public void UnregisterConveyor(Conveyor c)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null || c == null) return;
        var cellPos = grid.WorldToCell(c.transform.position);
        lock (pendingActive)
        {
            pendingActive.Remove(cellPos);
        }
        lock (active)
        {
            active.Remove(cellPos);
        }
    }

    public void RegisterCell(Vector2Int cellPos)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;
        lock (pendingActive)
        {
            pendingActive.Add(cellPos);
        }
    }

    void MoveView(Item item, Vector2Int fromCell, Vector2Int toCell)
    {
        if (item?.view == null || grid == null) return;
        var start = grid.CellToWorld(fromCell, item.view.position.z);
        var end = grid.CellToWorld(toCell, item.view.position.z);
        EnqueueVisualMove(item, start, end);
    }

    void ReseedActiveFromGrid()
    {
        if (grid == null) return;
        try
        {
            // reflect into GridService.cells to enumerate coordinates
            var fi = typeof(GridService).GetField("cells", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null)
            {
                var dict = fi.GetValue(grid) as System.Collections.IDictionary;
                if (dict != null)
                {
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        var pos = (Vector2Int)de.Key;
                        var cell = grid.GetCell(pos);
                        if (cell != null && cell.hasItem)
                        {
                            lock (pendingActive)
                            {
                                pendingActive.Add(pos);
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    void SnapAllItemViewsToCells()
    {
        if (grid == null) return;
        try
        {
            var fi = typeof(GridService).GetField("cells", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null)
            {
                var dict = fi.GetValue(grid) as System.Collections.IDictionary;
                if (dict != null)
                {
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        var pos = (Vector2Int)de.Key;
                        var cell = grid.GetCell(pos);
                        var item = cell?.item;
                        if (cell != null && cell.hasItem && item != null && item.view != null)
                        {
                            var p = grid.CellToWorld(pos, item.view.position.z);
                            item.view.position = p;
                        }
                    }
                }
            }
        }
        catch { }
    }
}
