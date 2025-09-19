using System;
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

    public enum VisualTimingMode { TickSynced, ConstantSpeed }

    [Header("Visuals")]
    [Tooltip("Enable smooth interpolation of item views between cells.")]
    [SerializeField] bool smoothMovement = true;
    [Tooltip("When VisualTimingMode = TickSynced, each 1-cell move lasts exactly ticksPerStep / GameTick.ticksPerSecond seconds.")]
    [SerializeField] VisualTimingMode visualTiming = VisualTimingMode.ConstantSpeed;
    [Tooltip("When ConstantSpeed is active and matchTickRate = true, speed is auto set to ticksPerSecond/ticksPerStep cells/sec. Otherwise this manual value is used.")]
    [SerializeField] bool matchTickRate = true;
    [Tooltip("Manual visual speed in cells per second when ConstantSpeed and matchTickRate=false.")]
    [SerializeField, Min(0.01f)] float visualCellsPerSecond = 6f;
    [Tooltip("Fallback movement duration (seconds) when neither GameTick nor Grid are available.")]
    [SerializeField, Min(0f)] float moveDurationSeconds = 0.2f;

    [Header("Build Pause")]
    [Tooltip("If true, pauses the belt simulation during build operations.")]
    [SerializeField] bool pauseWhileDragging = true;
    
    [Tooltip("If true, prevents items from moving on ghost/temporary belts during drag operations but allows movement after committing.")]
    [SerializeField] bool preventGhostMovement = true;

    // track pause transitions
    bool wasPaused;
    // When true, skip processing one StepActive immediately after unpausing to avoid instant double-moves
    bool skipNextStepAfterUnpause;
    // When true, skip TryPullFrom operations for one step after unpausing to avoid front-of-chain jumps
    bool suppressPullOnceAfterUnpausing;

    // Track tick timing to sync visual durations with logic (legacy jitter-prone approach kept for fallback only)
    float lastTickTime;
    float lastTickDurationSec;
    bool tickTimeInitialized;

    // Public helper so build/placement code can request a one-step suppression of pulls
    public void SuppressNextStepPulls(bool alsoSkipOneStep = true)
    {
        suppressPullOnceAfterUnpausing = true;
        if (alsoSkipOneStep) skipNextStepAfterUnpause = true;
        // Reset tick accumulator so we don't immediately step after suppression request
        tickAccumulator = 0;
        // Also reset the wasPaused flag to ensure the pause transition logic
        // correctly re-evaluates the state on the next frame.
        wasPaused = false;
    }

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

        // Initialize tick timing reference (used only as a fallback)
        lastTickTime = Time.time;
        lastTickDurationSec = 0f;
        tickTimeInitialized = false;
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
            suppressPullOnceAfterUnpausing = true;
        }
    }

    bool IsPaused()
    {
        if (GameManager.Instance == null) return false;
        var s = GameManager.Instance.State;
        // Respect the pauseWhileDragging toggle
        if (!pauseWhileDragging) return false;
        // Pause item flow during Build and Delete modes
        return s == GameState.Build || s == GameState.Delete;
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
                    vs.queue.Clear();
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
            suppressPullOnceAfterUnpausing = true;
        }
    }

    void OnTick()
    {
        // Update tick duration timestamp (fallback only)
        if (!tickTimeInitialized)
        {
            tickTimeInitialized = true;
            lastTickTime = Time.time;
            lastTickDurationSec = 0f;
        }
        else
        {
            var now = Time.time;
            lastTickDurationSec = Mathf.Max(0f, now - lastTickTime);
            lastTickTime = now;
        }

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
        if (suppressPullOnceAfterUnpausing)
            suppressPullOnceAfterUnpausing = false;
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
            if (suppressPullOnceAfterUnpausing)
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

        // Determine output direction (with splitter improvements)
        Direction outDir = Direction.None;
        Direction altOutDir = Direction.None; // Used only for splitter fallback if primary blocked
        bool twoOutputs = false;
        if (cell.type == GridService.CellType.Belt)
        {
            outDir = cell.outA;
        }
        else if (cell.type == GridService.CellType.Junction)
        {
            if (cell.outA != Direction.None && cell.outB != Direction.None)
            {
                twoOutputs = true;
                // primary based on toggle
                outDir = (cell.junctionToggle & 1) == 0 ? cell.outA : cell.outB;
                altOutDir = outDir == cell.outA ? cell.outB : cell.outA;
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

        if (dest == null)
        {
            return cellPos; // out of bounds; retry later
        }
        
        // Special: machine intake happens before entering the cell
        if (dest.type == GridService.CellType.Machine)
        {
            Debug.Log($"[BeltSimulationService] Item at {cellPos} trying to move to machine at {destPos}");
            
            // Respect machine orientation: only accept if approaching from its input side
            var approachFromVec = -DirectionUtil.DirVec(outDir); // vector pointing from machine toward current cell
            Debug.Log($"[BeltSimulationService] Approach vector: {approachFromVec}");
            
            PressMachine press;
            if (PressMachine.TryGetAt(destPos, out press) && press != null)
            {
                Debug.Log($"[BeltSimulationService] Found PressMachine at {destPos}. Checking if it accepts from {approachFromVec}");
                
                if (press.AcceptsFromVec(approachFromVec))
                {
                    // Double-check acceptance at notify time to avoid consuming while busy
                    var theItem = cell.item;
                    bool started = false;
                    try { started = press.OnItemArrived(); } catch { started = false; }
                    if (started)
                    {
                        Debug.Log($"[BeltSimulationService] PressMachine started processing. Consuming item at {cellPos}.");
                        ConsumeItemAt(cellPos, theItem);
                    }
                    else
                    {
                        Debug.LogWarning($"[BeltSimulationService] PressMachine reported busy at notify; keeping item at {cellPos}.");
                    }
                }
                else
                {
                    Debug.LogWarning($"[BeltSimulationService] PressMachine rejected item from approach vector {approachFromVec}");
                }
                // Whether accepted or not, we do not move into the machine cell this step
                return cellPos;
            }
            else
            {
                Debug.LogWarning($"[BeltSimulationService] No PressMachine found at {destPos}");
            }
            // No registered press found: treat as blocked
            return cellPos;
        }

        // Fallback: if the grid cell isn't marked as Machine but we do have a registered press there,
        // treat it like a machine intake. This guards against rare registration ordering issues.
        {
            PressMachine press;
            if (PressMachine.TryGetAt(destPos, out press) && press != null)
            {
                var approachFromVec = -DirectionUtil.DirVec(outDir);
                Debug.Log($"[BeltSimulationService] Fallback intake: cell {destPos} not typed as Machine but press is registered. Approach {approachFromVec}");
                if (press.AcceptsFromVec(approachFromVec))
                {
                    var theItem = cell.item;
                    bool started = false;
                    try { started = press.OnItemArrived(); } catch { started = false; }
                    if (started)
                    {
                        ConsumeItemAt(cellPos, theItem);
                    }
                    else
                    {
                        Debug.LogWarning($"[BeltSimulationService] PressMachine reported busy at notify; keeping item at {cellPos}.");
                    }
                }
                return cellPos;
            }
        }

        if (dest.hasItem || !IsBeltLike(dest))
        {
            // can't move this step, keep active to retry later
            return cellPos;
        }

        // move logical item one cell
        var movedItem = cell.item;
        dest.item = movedItem;
        dest.hasItem = true;
        cell.item = null;
        cell.hasItem = false;

        // schedule visual movement (visual system is separate)
        MoveView(dest.item, cellPos, destPos);

        // Splitter alternation: if junction has exactly one input and two outputs, flip toggle after successful dispatch
        if (cell.type == GridService.CellType.Junction && twoOutputs)
        {
            bool singleInput = (cell.inA == Direction.None) ^ (cell.inB == Direction.None); // XOR: true if exactly one input defined
            if (singleInput)
            {
                cell.junctionToggle ^= 1; // alternate next output
            }
        }

        // Return destination so caller schedules it for next step
        return destPos;
    }

    // Try to pull an item from neighbor into target cell. Returns true if a move occurred.
    bool TryPullFrom(Vector2Int target, Direction dir)
    {
        if (suppressPullOnceAfterUnpausing) return false;
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
        public readonly Queue<Vector3> queue = new Queue<Vector3>();
    }

    readonly Dictionary<Item, VisualState> visuals = new Dictionary<Item, VisualState>();

    float ComputeSegmentDuration(Vector3 a, Vector3 b)
    {
        // Deterministic visual timing
        float distance = Vector3.Distance(a, b);
        if (grid != null && grid.CellSize > 0.0001f)
        {
            if (visualTiming == VisualTimingMode.TickSynced)
            {
                int tps = GetGameTickRateOrDefault(15);
                float perCell = ticksPerStep > 0 && tps > 0 ? (float)ticksPerStep / tps : moveDurationSeconds;
                // scale linearly with distance for partial segments (should still be ~1 cell)
                return Mathf.Max(1f / 120f, perCell * (distance / grid.CellSize));
            }
            else // ConstantSpeed
            {
                float cellsPerSec = visualCellsPerSecond;
                if (matchTickRate)
                {
                    int tps = GetGameTickRateOrDefault(15);
                    if (ticksPerStep > 0) cellsPerSec = (float)tps / ticksPerStep;
                }
                float sec = Mathf.Approximately(cellsPerSec, 0f) ? moveDurationSeconds : (distance / grid.CellSize) / Mathf.Max(0.01f, cellsPerSec);
                return Mathf.Max(1f / 120f, sec);
            }
        }
        // Fallback
        return Mathf.Max(1f / 120f, moveDurationSeconds);
    }

    int GetGameTickRateOrDefault(int fallback)
    {
        try
        {
            // Try modern API first
            var arr = UnityEngine.Object.FindObjectsByType<GameTick>(FindObjectsSortMode.None);
            if (arr != null && arr.Length > 0 && arr[0] != null)
            {
                return Mathf.Clamp(arr[0].ticksPerSecond, 1, 1000);
            }
        }
        catch { }
        try
        {
            var gt = UnityEngine.Object.FindFirstObjectByType<GameTick>();
            if (gt != null) return Mathf.Clamp(gt.ticksPerSecond, 1, 1000);
        }
        catch { }
        return fallback;
    }

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
            if (t >= 1f)
            {
                if (vs.queue.Count > 0)
                {
                    // Start next queued segment from the current end point
                    vs.start = vs.end;
                    vs.end = vs.queue.Dequeue();
                    vs.elapsed = 0f;
                    vs.duration = ComputeSegmentDuration(vs.start, vs.end);
                }
                else
                {
                    visualsRemoveBuffer.Add(item);
                }
            }
        }
        for (int i = 0; i < visualsRemoveBuffer.Count; i++)
            visuals.Remove(visualsRemoveBuffer[i]);
    }

    // Enqueue a visual segment. If a segment is already in progress for this item, queue the next target
    // so the item finishes to the current cell center and then turns, avoiding diagonal cuts at corners.
    void EnqueueVisualMove(Item item, Vector3 startWorld, Vector3 targetWorld)
    {
        if (item?.view == null || !smoothMovement)
        {
            if (item?.view != null)
                item.view.position = targetWorld;
            visuals.Remove(item);
            return;
        }

        if (visuals.TryGetValue(item, out var existing) && existing != null && existing.view != null)
        {
            // Just queue the next target; keep current segment intact
            existing.queue.Enqueue(targetWorld);
            return;
        }

        // Start a new segment from the current view position towards the target
        var start = item.view != null ? item.view.position : startWorld;
        var vs = new VisualState
        {
            view = item.view,
            start = start,
            end = targetWorld,
            elapsed = 0f,
            duration = ComputeSegmentDuration(start, targetWorld)
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
        
        // Don't register ghost conveyors with the belt simulation
        if (c.isGhost) return;
        
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
        
        // Don't register cells during dragging to prevent ghost belt processing
        if (preventGhostMovement && GameManager.Instance != null && GameManager.Instance.State == GameState.Build && BuildModeController.IsDragging)
        {
            return;
        }
        
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
        // Enqueue one segment per logical step. Any subsequent step while still moving will be queued and started after this reaches its end.
        EnqueueVisualMove(item, start, end);
    }

    void TryNotifyMachineAt(Vector2Int cellPos, Item item)
    {
        Debug.Log($"[BeltSimulationService] Trying to notify machine at {cellPos} with item {item}");

        try
        {
            PressMachine press;
            if (PressMachine.TryGetAt(cellPos, out press) && press != null)
            {
                Debug.Log($"[BeltSimulationService] Found PressMachine at {cellPos}. Calling OnItemArrived.");
                press.OnItemArrived();
                Debug.Log($"[BeltSimulationService] Successfully notified PressMachine at {cellPos}.");
                return;
            }
            else
            {
                Debug.LogWarning($"[BeltSimulationService] PressMachine not found at {cellPos}.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BeltSimulationService] Exception while notifying machine at {cellPos}: {ex.Message}");
        }
    }

    void ConsumeItemAt(Vector2Int cellPos, Item item)
    {
        try
        {
            // Log as requested
            Debug.Log($"[Press] item entered press from {cellPos}");

            // Remove visual with a short feedback tween end position matching current position (no move)
            if (item?.view != null)
            {
                try { Destroy(item.view.gameObject); } catch { }
            }
            if (item != null)
            {
                visuals.Remove(item);
            }
            // Clear logical cell where the item was before the press
            var c = grid?.GetCell(cellPos);
            if (c != null)
            {
                c.item = null;
                c.hasItem = false;
            }
        }
        catch { }
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
    static T FindBestObjectOfType<T>() where T : UnityEngine.Object
    {
        var objType = typeof(UnityEngine.Object);
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

        return default(T);
    }
}
