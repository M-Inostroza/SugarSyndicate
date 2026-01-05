using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
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

    [Header("Performance")]
    [Tooltip("Enable verbose logs for this service.")]
    [SerializeField] bool enableDebugLogs = false;
    [Tooltip("Update visuals in N buckets (round-robin) to reduce per-frame Transform work.")]
    [SerializeField, Range(1, 8)] int visualUpdateBuckets = 1;

    // bucket state
    int currentVisualBucket;
    int nextVisualBucketAssignment;
    List<Item>[] visualBuckets;

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

    // lightweight logging
    void DLog(string msg) { if (enableDebugLogs) Debug.Log(msg); }
    void DWarn(string msg) { if (enableDebugLogs) Debug.LogWarning(msg); }

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
            ClearVisuals();
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
        // Always pause during Build/Delete so items freeze the moment you enter those modes
        if (s == GameState.Build || s == GameState.Delete) return true;
        // Optionally pause while dragging build previews if enabled
        if (pauseWhileDragging && BuildModeController.IsDragging) return true;
        return false;
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
                ClearVisuals();
            }
            wasPaused = true;
        }
        else if (!paused && wasPaused)
        {
            // exiting pause: snap all item views to their current cell centers & reseed
            SnapAllItemViewsToCells();
            ClearVisuals();
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
        {
            int buckets = Mathf.Max(1, visualUpdateBuckets);
            currentVisualBucket = (currentVisualBucket + 1) % buckets;
            float dtScaled = Time.deltaTime * buckets;
            UpdateVisuals(dtScaled, currentVisualBucket, buckets);
        }
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
                if (cell.type == GridService.CellType.Junction)
                {
                    // Collect available inputs (up to 3)
                    Direction[] inputs = new Direction[] { cell.inA, cell.inB, cell.inC };
                    inputs = inputs.Where(d => DirectionUtil.IsCardinal(d)).ToArray();

                    if (inputs.Length > 1)
                    {
                        // round-robin across inputs for fairness
                        int start = cell.junctionToggle % inputs.Length;
                        for (int i = 0; i < inputs.Length; i++)
                        {
                            var dir = inputs[(start + i) % inputs.Length];
                            if (TryPullFrom(cellPos, dir))
                            {
                                cell.junctionToggle = (byte)((start + i + 1) % inputs.Length);
                                return cellPos;
                            }
                        }
                        return null;
                    }
                    else if (inputs.Length == 1)
                    {
                        if (TryPullFrom(cellPos, inputs[0]))
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

                    if (!cell.hasItem && cell.type == GridService.CellType.Junction)
                    {
                        if (TryPullFrom(cellPos, cell.inB))
                        {
                            return cellPos;
                        }
                    }
                }

                if (cell.hasItem)
                    return cellPos;
            }
            return null;
        }

        // Determine output direction (with splitter improvements)
        Direction outDir = Direction.None;
        Direction altOutDir = Direction.None; // Used only for splitter fallback if primary blocked
        bool twoOutputs = false;
        bool multiInputs = false;
        if (cell.type == GridService.CellType.Belt)
        {
            outDir = cell.outA;
        }
        else if (cell.type == GridService.CellType.Junction)
        {
            multiInputs = DirectionUtil.IsCardinal(cell.inA) || DirectionUtil.IsCardinal(cell.inB) || DirectionUtil.IsCardinal(cell.inC);
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

        // Resolve destination and support splitter fallback: try primary, else alternate if available
        var destPos = cellPos + DirectionUtil.DirVec(outDir);
        var dest = grid.GetCell(destPos);
        if (twoOutputs)
        {
            bool primaryBlocked = dest == null || dest.hasItem || !IsBeltLike(dest);
            if (primaryBlocked)
            {
                var altPos = cellPos + DirectionUtil.DirVec(altOutDir);
                var altDest = grid.GetCell(altPos);
                if (altDest != null && !altDest.hasItem && IsBeltLike(altDest))
                {
                    destPos = altPos;
                    dest = altDest;
                    outDir = altOutDir;
                }
            }
        }

        if (dest == null)
        {
            return cellPos; // out of bounds; retry later
        }
        
        // Machines handle intake before entering the cell
        if (dest.type == GridService.CellType.Machine)
        {
            if (TryHandOffToMachine(destPos, cellPos, outDir, cell, blockWhenMissing: true))
                return cellPos;
        }
        else if (TryHandOffToMachine(destPos, cellPos, outDir, cell, blockWhenMissing: false))
        {
            return cellPos;
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
            bool singleInput = (cell.inA == Direction.None ? 0 : 1)
                             + (cell.inB == Direction.None ? 0 : 1)
                             + (cell.inC == Direction.None ? 0 : 1) <= 1;
            if (singleInput)
            {
                cell.junctionToggle ^= 1; // alternate next output
            }
        }

        // Return destination so caller schedules it for next step
        return destPos;
    }

    // Handle machine intake without coupling to specific machine types
    bool TryHandOffToMachine(Vector2Int destPos, Vector2Int sourcePos, Direction outDir, GridService.Cell sourceCell, bool blockWhenMissing)
    {
        var approachFromVec = -DirectionUtil.DirVec(outDir);
        if (!MachineRegistry.TryGet(destPos, out var machine) || machine == null)
        {
            if (blockWhenMissing)
                Debug.LogWarning($"[BeltSimulationService] Machine cell at {destPos} has no registered machine; blocking item.");
            return blockWhenMissing;
        }

        bool accepts = false;
        try { accepts = machine.CanAcceptFrom(approachFromVec); }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BeltSimulationService] Machine.CanAcceptFrom threw at {destPos}: {ex.Message}");
            return true; // treat as blocked
        }

        if (!accepts)
            return true; // machine present but not accepting now

        var item = sourceCell?.item;
        if (item != null && pendingConsume.ContainsKey(item))
            return true; // already handing off to a sink
        bool started = false;
        try { started = machine.TryStartProcess(item); }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BeltSimulationService] Machine.TryStartProcess threw at {destPos}: {ex.Message}");
            started = false;
        }

        if (started)
        {
            if (machine is Truck)
            {
                if (item == null || !smoothMovement || item.view == null)
                {
                    ConsumeItemAt(sourcePos, item, destPos);
                }
                else
                {
                    MoveView(item, sourcePos, destPos);
                    pendingConsume[item] = new PendingConsume { source = sourcePos, dest = destPos };
                }
            }
            else
            {
                ConsumeItemAt(sourcePos, item);
            }
        }
        return true; // always stop movement toward machines this step
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
        public int bucket; // which bucket updates this visual
    }

    readonly Dictionary<Item, VisualState> visuals = new Dictionary<Item, VisualState>();
    struct PendingConsume
    {
        public Vector2Int source;
        public Vector2Int dest;
    }
    readonly Dictionary<Item, PendingConsume> pendingConsume = new Dictionary<Item, PendingConsume>();

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
        // legacy path: delegate to bucketed version using current state
        UpdateVisuals(dt, currentVisualBucket, Mathf.Max(1, visualUpdateBuckets));
    }

    void UpdateVisuals(float dt, int bucketIndex, int bucketCount)
    {
        if (visuals.Count == 0) return;
        EnsureVisualBuckets();
        if (bucketIndex < 0 || bucketIndex >= visualBuckets.Length) return;

        var bucket = visualBuckets[bucketIndex];
        if (bucket.Count == 0) return;

        visualsRemoveBuffer.Clear();
        for (int i = 0; i < bucket.Count; i++)
        {
            var item = bucket[i];
            if (!visuals.TryGetValue(item, out var vs) || vs == null)
            {
                visualsRemoveBuffer.Add(item);
                continue;
            }
            if (vs.view == null)
            {
                visualsRemoveBuffer.Add(item);
                continue;
            }

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
                    if (pendingConsume.TryGetValue(item, out var pending))
                    {
                        pendingConsume.Remove(item);
                        ConsumeItemAt(pending.source, item, pending.dest);
                    }
                    else
                    {
                        visualsRemoveBuffer.Add(item);
                    }
                }
            }
        }
        for (int i = 0; i < visualsRemoveBuffer.Count; i++)
            RemoveVisual(visualsRemoveBuffer[i]);
    }

    // Enqueue a visual segment. If a segment is already in progress for this item, queue the next target
    // so the item finishes to the current cell center and then turns, avoiding diagonal cuts at corners.
    void EnqueueVisualMove(Item item, Vector3 startWorld, Vector3 targetWorld)
    {
        if (item?.view == null || !smoothMovement)
        {
            if (item?.view != null)
                item.view.position = targetWorld;
            RemoveVisual(item);
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
        EnsureVisualBuckets();
        int bucket = (nextVisualBucketAssignment++) % Mathf.Max(1, visualUpdateBuckets);
        var vs = new VisualState
        {
            view = item.view,
            start = start,
            end = targetWorld,
            elapsed = 0f,
            duration = ComputeSegmentDuration(start, targetWorld),
            bucket = bucket
        };
        visuals[item] = vs;
        visualBuckets[bucket].Add(item);
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
            RemoveVisual(item);
        }
        // register for processing on next step
        lock (pendingActive)
        {
            pendingActive.Add(cellPos);
        }
        return true;
    }

    // Immediately attempts a single logical step from a freshly spawned cell.
    // Returns true if the item moved this frame.
    public bool TryAdvanceSpawnedItem(Vector2Int cellPos)
    {
        if (grid == null) return false;
        if (IsPaused()) return false;
        var movedTo = StepCell(cellPos);
        if (movedTo.HasValue)
        {
            active.Add(movedTo.Value);
            return true;
        }
        return false;
    }

    void EnsureVisualBuckets()
    {
        int count = Mathf.Max(1, visualUpdateBuckets);
        if (visualBuckets != null && visualBuckets.Length == count) return;

        visualBuckets = new List<Item>[count];
        for (int i = 0; i < count; i++) visualBuckets[i] = new List<Item>();

        foreach (var kv in visuals)
        {
            var item = kv.Key;
            var vs = kv.Value;
            if (item == null || vs == null) continue;
            int bucket = vs.bucket;
            if (bucket < 0 || bucket >= count)
                bucket = (nextVisualBucketAssignment++) % count;
            vs.bucket = bucket;
            visualBuckets[bucket].Add(item);
        }
    }

    void RemoveVisual(Item item)
    {
        if (item == null) return;
        pendingConsume.Remove(item);
        if (visuals.Remove(item) && visualBuckets != null)
        {
            int count = visualBuckets.Length;
            for (int i = 0; i < count; i++)
                visualBuckets[i].Remove(item);
        }
    }

    void ClearVisuals()
    {
        if (pendingConsume.Count > 0)
        {
            var toConsume = new List<KeyValuePair<Item, PendingConsume>>(pendingConsume);
            for (int i = 0; i < toConsume.Count; i++)
            {
                var kv = toConsume[i];
                ConsumeItemAt(kv.Value.source, kv.Key, kv.Value.dest);
            }
        }
        visuals.Clear();
        if (visualBuckets == null) return;
        for (int i = 0; i < visualBuckets.Length; i++)
            visualBuckets[i].Clear();
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
        try
        {
            if (MachineRegistry.TryGet(cellPos, out var machine) && machine != null)
                machine.TryStartProcess(item);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BeltSimulationService] Exception while notifying machine at {cellPos}: {ex.Message}");
        }
    }

    void ConsumeItemAt(Vector2Int cellPos, Item item, Vector2Int? destPos = null)
    {
        try
        {

            // Remove visual with a short feedback tween end position matching current position (no move)
            if (item?.view != null)
            {
                if (destPos.HasValue && grid != null)
                {
                    var snap = grid.CellToWorld(destPos.Value, item.view.position.z);
                    item.view.position = snap;
                }
                try { Destroy(item.view.gameObject); } catch { }
            }
            if (item != null)
            {
                RemoveVisual(item);
                pendingConsume.Remove(item);
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

    public void ReseedActiveFromGrid()
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
