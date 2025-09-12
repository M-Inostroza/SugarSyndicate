using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class BeltSimulationService : MonoBehaviour
{
    public static BeltSimulationService Instance { get; private set; }

    readonly HashSet<Vector2Int> active = new HashSet<Vector2Int>();
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

    void OnTick()
    {
        tickAccumulator++;
        if (tickAccumulator < ticksPerStep) return;
        tickAccumulator = 0;
        StepActive();
    }

    void Update()
    {
        if (!useGameTick)
            StepActive();

        // Advance visuals every frame for smooth movement regardless of tick mode
        if (smoothMovement)
            UpdateVisuals(Time.deltaTime);
    }

    void StepActive()
    {
        if (grid == null) return;
        var step = new List<Vector2Int>(active);
        active.Clear();
        foreach (var cell in step)
            StepCell(cell);
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

    void StepCell(Vector2Int cellPos)
    {
        var cell = grid.GetCell(cellPos);
        if (cell == null) return;

        // Empty junction/belt tries to pull from inputs
        if (!cell.hasItem)
        {
            if (cell.type == GridService.CellType.Belt || cell.type == GridService.CellType.Junction)
            {
                TryPullFrom(cellPos, cell.inA);
                if (!grid.GetCell(cellPos).hasItem && cell.type == GridService.CellType.Junction)
                    TryPullFrom(cellPos, cell.inB);
                if (grid.GetCell(cellPos).hasItem)
                    active.Add(cellPos);
            }
            return;
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

        if (!DirectionUtil.IsCardinal(outDir)) { active.Add(cellPos); return; }

        var destPos = cellPos + DirectionUtil.DirVec(outDir);
        var dest = grid.GetCell(destPos);
        if (dest == null || dest.hasItem || !IsBeltLike(dest))
        {
            active.Add(cellPos);
            return;
        }

        dest.item = cell.item;
        dest.hasItem = true;
        cell.item = null;
        cell.hasItem = false;

        // schedule visual movement
        MoveView(dest.item, destPos);

        active.Add(destPos);

        if (cell.type == GridService.CellType.Junction && cell.outA != Direction.None && cell.outB != Direction.None)
            cell.junctionToggle ^= 1;
    }

    void TryPullFrom(Vector2Int target, Direction dir)
    {
        if (!DirectionUtil.IsCardinal(dir)) return;
        var fromPos = target + DirectionUtil.DirVec(dir);
        var from = grid.GetCell(fromPos);
        var dest = grid.GetCell(target);
        if (from == null || dest == null) return;
        if (!from.hasItem) return;
        var requiredOut = DirectionUtil.Opposite(dir);
        if (!HasOutputTowards(from, requiredOut)) return;
        if (dest.hasItem) return;

        dest.item = from.item;
        dest.hasItem = true;
        from.item = null;
        from.hasItem = false;
        MoveView(dest.item, target);
        active.Add(target);
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

    void EnqueueVisualMove(Item item, Vector3 targetWorld)
    {
        if (item?.view == null || !smoothMovement)
        {
            if (item?.view != null)
                item.view.position = targetWorld;
            visuals.Remove(item);
            return;
        }

        float duration = moveDurationSeconds;
        if (useGameTick)
        {
            // derive duration from GameTick rate and ticksPerStep
            try
            {
                var gt = UnityEngine.Object.FindObjectOfType<GameTick>();
                if (gt != null)
                {
                    var field = typeof(GameTick).GetField("ticksPerSecond", BindingFlags.Instance | BindingFlags.Public);
                    if (field != null)
                    {
                        var tps = (int)field.GetValue(gt);
                        duration = (1f / Mathf.Max(1, tps)) * Mathf.Max(1, ticksPerStep);
                      }
                 }
            }
            catch { }
        }

        if (!visuals.TryGetValue(item, out var vs))
        {
            vs = new VisualState();
            vs.view = item.view;
            vs.start = item.view.position;
            vs.end = targetWorld;
            vs.elapsed = 0f;
            vs.duration = duration;
            visuals[item] = vs;
        }
        else
        {
            // restart from current position for smooth chaining
            vs.start = vs.view.position;
            vs.end = targetWorld;
            vs.elapsed = 0f;
            vs.duration = duration;
        }
    }

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
        active.Add(cellPos);
        Debug.Log($"[BeltSim] Spawned item at {cellPos}. ActiveCount={active.Count}.", this);
        return true;
    }

    public void RegisterConveyor(Conveyor c)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null || c == null) return;
        var cellPos = grid.WorldToCell(c.transform.position);
        active.Add(cellPos);
    }

    public void UnregisterConveyor(Conveyor c)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null || c == null) return;
        var cellPos = grid.WorldToCell(c.transform.position);
        active.Remove(cellPos);
    }

    public void RegisterCell(Vector2Int cellPos)
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;
        active.Add(cellPos);
    }

    void MoveView(Item item, Vector2Int cellPos)
    {
        if (item?.view == null || grid == null) return;
        var pos = grid.CellToWorld(cellPos, item.view.position.z);
        EnqueueVisualMove(item, pos);
    }
}
