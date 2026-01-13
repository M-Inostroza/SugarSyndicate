using System;
using UnityEngine;
using UnityEngine.Serialization;

public class SugarMine : MonoBehaviour
{
    public Direction outputDirection = Direction.Right;

    [Header("Prefabs")]
    [SerializeField] GameObject itemPrefab;

    [Header("Item Identity")]
    [Tooltip("Logical item type for spawned items; leave empty to use the prefab name.")]
    [SerializeField] string itemType;

    [Header("When")]
    [FormerlySerializedAs("intervalTicks")]
    [FormerlySerializedAs("spawnRate")]
    [Tooltip("Spawns per second. Smaller = slower.")]
    [SerializeField, Min(0.01f)] float spawnsPerSecond = 1f;
    [SerializeField] bool autoStart = true;

    [Header("Pooling")]
    [Tooltip("Prewarm pool size (only first mine with prefab matters).")]
    [SerializeField, Min(0)] int poolPrewarm = 32;

    [Header("Behavior")]
    [Tooltip("If true, spawns prefer the head cell (in front of the mine). If false, try base cell first then head as fallback.")]
    [SerializeField] bool preferHeadCell = true;
    [Tooltip("If true, only spawn when the head cell is free (no fallback to the base cell).")]
    [SerializeField] bool requireFreeHeadCell = true;

    [Header("Sugar")]
    [Tooltip("If true, production rate scales with the sugar amount in the cell.")]
    [SerializeField] bool scaleBySugarEfficiency = true;

    [Header("Maintenance")]
    [SerializeField] MachineMaintenance maintenance = new MachineMaintenance();

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    [System.NonSerialized] public bool isGhost = false;

    bool running;
    int nextItemId = 1;
    float spawnProgress;
    GameTick tickSource;

    public float Maintenance01 => maintenance != null ? maintenance.Level01 : 1f;
    public bool IsStopped => maintenance != null && maintenance.IsStopped;

    void OnEnable()
    {
        if (isGhost) return;
        running = autoStart;
        GameTick.OnTickStart += OnTick;
        if (tickSource == null) tickSource = FindAnyObjectByType<GameTick>();
        if (itemPrefab != null)
        {
            ItemViewPool.Ensure(itemPrefab, poolPrewarm);
        }
        if (debugLogging) Debug.Log($"[SugarMine] Enabled at world {transform.position}");
    }

    void OnDisable()
    {
        if (isGhost) return;
        GameTick.OnTickStart -= OnTick;
    }

    void OnTick()
    {
        if (isGhost) return;
        if (!running) return;
        if (IsStopped) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
        if (tickSource == null) tickSource = FindAnyObjectByType<GameTick>();
        float tps = tickSource != null ? tickSource.ticksPerSecond : 15f;
        float rate = spawnsPerSecond;
        if (scaleBySugarEfficiency && GridService.Instance != null)
        {
            var cell = GridService.Instance.WorldToCell(transform.position);
            float eff = GridService.Instance.GetSugarEfficiency(cell);
            rate *= Mathf.Max(0f, eff);
        }
        spawnProgress += rate / Mathf.Max(1f, tps);
        while (spawnProgress >= 1f)
        {
            spawnProgress -= 1f;
            Spawn();
            if (IsStopped) break;
        }
    }

    public void SetFacing(Vector2Int dir)
    {
        outputDirection = DirFromVec(dir);
    }

    void Spawn()
    {
        if (GridService.Instance == null || BeltSimulationService.Instance == null)
        {
            if (debugLogging) Debug.LogWarning("[SugarMine] Missing GridService or BeltSimulationService.");
            return;
        }
        var gs = GridService.Instance;
        var baseCell = gs.WorldToCell(transform.position);
        var dir = DirectionUtil.DirVec(outputDirection);
        var headCell = baseCell + dir;

        var item = new Item { id = nextItemId, type = ResolveItemType() };

        bool delivered = false;
        bool spawnedOnBelt = false;
        Vector2Int outputCell = baseCell;
        if (preferHeadCell)
        {
            if (TrySendToMachine(gs, headCell, baseCell, item))
            {
                delivered = true;
                outputCell = headCell;
            }
            else if (BeltSimulationService.Instance.TrySpawnItem(headCell, item))
            {
                delivered = true;
                spawnedOnBelt = true;
                outputCell = headCell;
            }
            else if (!requireFreeHeadCell && TrySendToMachine(gs, baseCell, baseCell, item))
            {
                delivered = true;
                outputCell = baseCell;
            }
            else if (!requireFreeHeadCell && BeltSimulationService.Instance.TrySpawnItem(baseCell, item))
            {
                delivered = true;
                spawnedOnBelt = true;
                outputCell = baseCell;
            }
        }
        else
        {
            if (TrySendToMachine(gs, baseCell, baseCell, item))
            {
                delivered = true;
                outputCell = baseCell;
            }
            else if (BeltSimulationService.Instance.TrySpawnItem(baseCell, item))
            {
                delivered = true;
                spawnedOnBelt = true;
                outputCell = baseCell;
            }
            else if (!requireFreeHeadCell && TrySendToMachine(gs, headCell, baseCell, item))
            {
                delivered = true;
                outputCell = headCell;
            }
            else if (!requireFreeHeadCell && BeltSimulationService.Instance.TrySpawnItem(headCell, item))
            {
                delivered = true;
                spawnedOnBelt = true;
                outputCell = headCell;
            }
        }

        if (!delivered)
        {
            if (debugLogging)
                Debug.LogWarning($"[SugarMine] Unable to spawn item at {baseCell} or {headCell}");
            return;
        }

        if (spawnedOnBelt)
        {
            item.view = CreateViewAt(gs, outputCell);
            BeltSimulationService.Instance.TryAdvanceSpawnedItem(outputCell);
        }

        if (debugLogging) Debug.Log($"[SugarMine] Produced item {nextItemId} ({item.type}) at {outputCell}");
        nextItemId++;

        if (maintenance != null && !maintenance.TryConsume(1))
        {
            if (debugLogging) Debug.LogWarning("[SugarMine] Maintenance stopped production.");
        }
    }

    string ResolveItemType()
    {
        if (!string.IsNullOrWhiteSpace(itemType)) return itemType.Trim();
        if (itemPrefab != null) return itemPrefab.name;
        return string.Empty;
    }

    public string GetProcessSummary()
    {
        var type = ResolveItemType();
        if (string.IsNullOrWhiteSpace(type)) type = "Sugar";
        string scaleNote = scaleBySugarEfficiency ? " (scaled by sugar)" : string.Empty;
        return $"Spawns {type} @ {spawnsPerSecond:0.##}/s{scaleNote}";
    }

    static Direction DirFromVec(Vector2Int dir)
    {
        if (dir == Vector2Int.up) return Direction.Up;
        if (dir == Vector2Int.right) return Direction.Right;
        if (dir == Vector2Int.down) return Direction.Down;
        if (dir == Vector2Int.left) return Direction.Left;
        return Direction.Right;
    }

    bool TrySendToMachine(GridService gs, Vector2Int outCell, Vector2Int sourceCell, Item item)
    {
        if (item == null || gs == null) return false;
        if (!TryResolveOutputMachine(outCell, sourceCell, out var targetMachine))
            return false;

        var view = CreateViewAt(gs, outCell);
        item.view = view;
        bool ok = false;
        try { ok = targetMachine.TryStartProcess(item); } catch { ok = false; }
        if (!ok)
        {
            if (view != null) ItemViewPool.Return(view);
            item.view = null;
            return false;
        }
        return true;
    }

    bool TryResolveOutputMachine(Vector2Int outCell, Vector2Int sourceCell, out IMachine targetMachine)
    {
        targetMachine = null;
        try
        {
            if (MachineRegistry.TryGet(outCell, out var machine) && machine is IMachineStorageWithCapacity)
            {
                var approachFromVec = sourceCell - outCell;
                bool accepts = false;
                try { accepts = machine.CanAcceptFrom(approachFromVec); } catch { return false; }
                if (!accepts) return false;
                targetMachine = machine;
                return true;
            }
        }
        catch { }
        return false;
    }

    Transform CreateViewAt(GridService gs, Vector2Int cellPos)
    {
        if (itemPrefab == null || gs == null) return null;
        float z = itemPrefab.transform.position.z;
        var world = gs.CellToWorld(cellPos, z);
        var parent = ContainerLocator.GetItemContainer();
        var t = ItemViewPool.Get(itemPrefab, world, Quaternion.identity, parent);
        if (t != null) t.position = world;
        return t;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, 0.12f);
        var v = (Vector3)(Vector2)DirectionUtil.DirVec(outputDirection) * 0.8f;
        Gizmos.DrawLine(transform.position, transform.position + v);
        Gizmos.DrawSphere(transform.position + v, 0.05f);
    }
#endif
}
