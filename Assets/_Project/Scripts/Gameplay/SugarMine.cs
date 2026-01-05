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

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    bool running;
    int nextItemId = 1;
    float spawnProgress;
    GameTick tickSource;

    void OnEnable()
    {
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
        GameTick.OnTickStart -= OnTick;
    }

    void OnTick()
    {
        if (!running) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
        if (tickSource == null) tickSource = FindAnyObjectByType<GameTick>();
        float tps = tickSource != null ? tickSource.ticksPerSecond : 15f;
        spawnProgress += spawnsPerSecond / Mathf.Max(1f, tps);
        while (spawnProgress >= 1f)
        {
            spawnProgress -= 1f;
            Spawn();
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

        bool spawned = false;
        Vector2Int spawnedCell = baseCell;
        if (preferHeadCell)
        {
            if (BeltSimulationService.Instance.TrySpawnItem(headCell, item))
            {
                spawned = true;
                spawnedCell = headCell;
            }
            else if (!requireFreeHeadCell && BeltSimulationService.Instance.TrySpawnItem(baseCell, item))
            {
                spawned = true;
                spawnedCell = baseCell;
            }
        }
        else
        {
            if (BeltSimulationService.Instance.TrySpawnItem(baseCell, item))
            {
                spawned = true;
                spawnedCell = baseCell;
            }
            else if (!requireFreeHeadCell && BeltSimulationService.Instance.TrySpawnItem(headCell, item))
            {
                spawned = true;
                spawnedCell = headCell;
            }
        }

        if (!spawned)
        {
            if (debugLogging)
                Debug.LogWarning($"[SugarMine] Unable to spawn item at {baseCell} or {headCell}");
            return;
        }

        float z = itemPrefab != null ? itemPrefab.transform.position.z : 0f;
        var world = gs.CellToWorld(spawnedCell, z);
        if (itemPrefab != null)
        {
            var parent = ContainerLocator.GetItemContainer();
            var t = ItemViewPool.Get(itemPrefab, world, Quaternion.identity, parent);
            item.view = t;
        }

        if (item.view != null)
            item.view.position = world;

        BeltSimulationService.Instance.TryAdvanceSpawnedItem(spawnedCell);

        if (debugLogging) Debug.Log($"[SugarMine] Produced item {nextItemId} ({item.type}) at {spawnedCell}");
        nextItemId++;
    }

    string ResolveItemType()
    {
        if (!string.IsNullOrWhiteSpace(itemType)) return itemType.Trim();
        if (itemPrefab != null) return itemPrefab.name;
        return string.Empty;
    }

    static Direction DirFromVec(Vector2Int dir)
    {
        if (dir == Vector2Int.up) return Direction.Up;
        if (dir == Vector2Int.right) return Direction.Right;
        if (dir == Vector2Int.down) return Direction.Down;
        if (dir == Vector2Int.left) return Direction.Left;
        return Direction.Right;
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
