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

    // Reusable buffers to minimize GC allocations per step/frame
    readonly List<Vector2Int> stepBuffer = new List<Vector2Int>(256);
    readonly HashSet<Vector2Int> movedBuffer = new HashSet<Vector2Int>();
    readonly List<Item> visualsRemoveBuffer = new List<Item>(64);

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

    [Header("Build Pause")]
    [Tooltip("If true, pauses the belt simulation while dragging in Build mode. Turn off to keep items moving while placing belts.")]
    [SerializeField] bool pauseWhileDragging = false;

    // track pause transitions
    bool wasPaused;
    // When true, skip processing one StepActive immediately after unpausing to avoid instant double-moves
    bool skipNextStepAfterUnpause;
    // When true, skip TryPullFrom operations for one step after unpausing to avoid front-of-chain jumps
    bool suppressPullOnceAfterUnpause;

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

        // Subscribe to build mode exit so we can force resume if needed
        var bmc = FindBestObjectOfType<BuildModeController>();
        if (bmc != null)
            bmc.onExitBuildMode += OnBuildModeExit;
    }

    void OnDisable()
    {
        if (useGameTick)
            GameTick.OnTickStart -= OnTick;

        var bmc = FindBestObjectOfType<BuildModeController>();
        if (bmc != null)
            bmc.onExitBuildMode -= OnBuildModeExit;
    }

    void OnBuildModeExit()
    {
        // If we were paused due to build/dragging, ensure we run the resume transition immediately
        if (wasPaused)
        {
            // Snap visuals to logical cell centers and clear any in-flight interpolation
            SnapAllItemViewsToCells();
            visuals.Clear();
            ReseedActiveFromGrid();
            wasPaused = false;
            // prevent immediate processing on the same frame which causes visual jump
            skipNextStepAfterUnpause = true;
            suppressPullOnceAfterUnpause = true;
        }
    }

    bool IsPaused()
    {
        if (!pauseWhileDragging) return false;
        // Pause only while in Build AND actively dragging to preserve normal build-mode behavior
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
            // entering pause: stop all in-flight visuals immediately (freeze at current position)
            if (visuals.Count > 0)
            {
                foreach (var kv in visuals)
                {
                    var vs = kv.Value;
                    if (vs?.view == null) continue;
                    float t = vs.duration <= 0f ? 1f : Mathf.Clamp01(vs.elapsed / vs.duration);
                    var current = Vector3.Lerp(vs.start, vs.end, t);
                    vs.view.position = current;
                }
                visuals.Clear();
            }
            wasPaused = true;
        }
        else if (!paused && wasPaused)
        {
            // exiting pause: snap all item views to their current cell centers & reseed
            SnapAllItemViewsToCells();
            visuals.Clear();
            ReseedActiveFromGrid();
            wasPaused = false;
            // prevent immediate processing on the same frame which causes visual jump
            skipNextStepAfterUnpause = true;
            suppressPullOnceAfterUnpause = true;
        }
    }

    void OnTick()
    {
        HandlePauseTransitions();
        // Do not accumulate ticks or step while paused
        if (IsPaused()) return;

        if (skipNextStepAfterUnpause)
        {
            // consume one tick without stepping to avoid instant move after unpause
            skipNextStepAfterUnpause = false;
            tickAccumulator = 0;
            return;
        }

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

        if (skipNextStepAfterUnpause)
        {
            // consume one frame without stepping to avoid instant move after unpause
            skipNextStepAfterUnpause = false;
            return;
        }

        if (!useGameTick)
            StepActive();

        // Advance visuals every frame for smooth movement regardless of tick mode
        if (smoothMovement)
            UpdateVisuals(Time.deltaTime);
    }

    void StepActive()
    {
        // Extra guard: if we've been flagged to skip a single step after unpause, honor it here as well.
        if (skipNextStepAfterUnpause)
        {
            skipNextStepAfterUnpause = false;
            return;
        }
        if (grid == null) return;
        // Merge any pending registrations so they are processed in this step, but avoid processing registrations that arrive while stepping
        if (pendingActive.Count > 0)
        {
            foreach (var p in pendingActive) active.Add(p);
            pendingActive.Clear();
        }

        // Copy to reusable buffer and clear active for next accumulation
        stepBuffer.Clear();
        foreach (var c in active) stepBuffer.Add(c);
        active.Clear();

        // movedBuffer prevents chained moves where an item would be moved multiple cells in one StepActive
        movedBuffer.Clear();

        for (int i = 0; i < stepBuffer.Count; i++)
        {
            var cell = stepBuffer[i];
            // If this cell was the destination of a previous move this step, skip it to avoid double-moving
            if (movedBuffer.Contains(cell)) continue;

            var dest = StepCell(cell);

            if (dest.HasValue)
            {
                // mark destination as moved this step and schedule it for processing on the next StepActive
                movedBuffer.Add(dest.Value);
                active.Add(dest.Value);
            }
        }

        // Clear one-time suppression flags at the end of a logical step
        if (suppressPullOnceAfterUnpause)
            suppressPullOnceAfterUnpause = false;
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
            // After unpausing, skip pulls for one logical step to avoid immediate chain advances
            if (suppressPullOnceAfterUnpause)
                return null;

            if (cell.type == GridService.CellType.Belt || cell.type == GridService.CellType.Junction)
            {
                // Modified: for junctions, detect whether inputs actually have items ready to push into this cell.
                if (cell.type == GridService.CellType.Junction && cell.inA != Direction.None && cell.inB != Direction.None)
                {
                    // Determine readiness of each input (neighbor has item and outputs toward this cell)
                    var neighborA = grid.GetCell(cellPos + DirectionUtil.DirVec(cell.inA));
                    var neighborB = grid.GetCell(cellPos + DirectionUtil.DirVec(cell.inB));
                    bool aReady = neighborA != null && neighborA.hasItem && HasOutputTowards(neighborA, DirectionUtil.Opposite(cell.inA));
                    bool bReady = neighborB != null && neighborB.hasItem && HasOutputTowards(neighborB, DirectionUtil.Opposite(cell.inB));

                    if (aReady && bReady)
                    {
                        // Both sides have items waiting: enforce round-robin one-by-one behavior
                        var tryDir = (cell.junctionToggle & 1) == 0 ? cell.inA : cell.inB;
                        if (TryPullFrom(cellPos, tryDir))
                        {
                            // successful pull, flip toggle so next time the other side gets priority
                            cell.junctionToggle ^= 1;
                            return cellPos;
                        }
                        // If chosen side wasn't able to pull (race/edge case), do not attempt the other side this step
                        return null;
                    }
                    else
                    {
                        // Only one (or none) side is ready: fall back to opportunistic behavior
                        if (aReady && TryPullFrom(cellPos, cell.inA))
                            return cellPos;
                        if (bReady && TryPullFrom(cellPos, cell.inB))
                            return cellPos;
                    }
                }
                else
                {
                    // non-junction or single-input junction: retain previous behavior
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
        if (suppressPullOnceAfterUnpause) return false;
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
        visualsRemoveBuffer.Clear();
        foreach (var kv in visuals)
        {
            var item = kv.Key;
            var vs = kv.Value;
            if (vs.view == null) { visualsRemoveBuffer.Add(item); continue; }
            vs.elapsed += dt;
            float t = vs.duration <= 0f ? 1f : Mathf.Clamp01(vs.elapsed / vs.duration);
            vs.view.position = Vector3.Lerp(vs.start, vs.end, t);
            if (t >= 1f) visualsRemoveBuffer.Add(item);
        }
        for (int i = 0; i < visualsRemoveBuffer.Count; i++)
            visuals.Remove(visualsRemoveBuffer[i]);
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
                var gt = FindBestObjectOfType<GameTick>();
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
        // enforce a small minimum duration to avoid near-instant jumps on short distances
        const float minVisualDuration = 0.05f; // seconds
        if (duration < minVisualDuration) duration = minVisualDuration;
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
            return false;
        }

        var cell = grid.GetCell(cellPos);
        if (cell == null)
        {
            bool inBounds = grid.InBounds(cellPos);
            return false;
        }
        if (cell.hasItem)
        {
            return false;
        }
        if (!IsBeltLike(cell))
        {
            return false;
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
        // Use explicit start/end so visuals always animate from the source cell center to destination, avoiding inconsistencies.
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

    // Helper: find an instance of T using the newest available Unity API, falling back via reflection.
    static T FindBestObjectOfType<T>() where T : Object
    {
        var objType = typeof(Object);
        // Prefer generic static APIs if present. Use explicit method selection to avoid AmbiguousMatchException.
        string[] candidateNames = new[] { "FindAnyObjectByType", "FindFirstObjectByType", "FindObjectOfType" };
        foreach (var name in candidateNames)
        {
            var methods = objType.GetMethods(BindingFlags.Static | BindingFlags.Public);
            foreach (var method in methods)
            {
                if (method.Name != name) continue;
                if (!method.IsGenericMethodDefinition) continue;
                // we expect a single generic type parameter and no runtime parameters for the generic form
                if (method.GetGenericArguments().Length != 1) continue;
                if (method.GetParameters().Length != 0) continue;
                try
                {
                    return (T)method.MakeGenericMethod(typeof(T)).Invoke(null, null);
                }
                catch { }
            }
        }

        // Fallback: use non-generic FindObjectOfType(Type) if present
        try
        {
            var fallback = objType.GetMethod("FindObjectOfType", BindingFlags.Static | BindingFlags.Public, null, new System.Type[] { typeof(System.Type) }, null);
            if (fallback != null)
            {
                var res = fallback.Invoke(null, new object[] { typeof(T) });
                return (T)res;
            }
        }
        catch { }

        return null;
    }
}
