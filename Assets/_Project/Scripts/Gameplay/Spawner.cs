using UnityEngine;

public class Spawner : MonoBehaviour
{
    public Direction outputDirection = Direction.Right;

    [Header("Prefabs")]
    [SerializeField] GameObject itemPrefab;

    [Header("When")]
    [SerializeField, Min(1)] int intervalTicks = 10;
    [SerializeField] bool autoStart = true;

    [Header("Pooling")] 
    [Tooltip("Prewarm pool size (only first spawner with prefab matters)." )]
    [SerializeField, Min(0)] int poolPrewarm = 32;

    [Header("Behavior")]
    [Tooltip("If true, spawns prefer the head cell (in front of the spawner). If false, try base cell first then head as fallback.")]
    [SerializeField] bool preferHeadCell = true;

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    int tickCounter;
    bool running;
    int nextItemId = 1;

    void OnEnable()
    {
        running = autoStart;
        GameTick.OnTickStart += OnTick;
        if (itemPrefab != null)
        {
            ItemViewPool.Ensure(itemPrefab, poolPrewarm);
        }
        if (debugLogging) Debug.Log($"[Spawner] Enabled at world {transform.position}");
    }

    void OnDisable()
    {
        GameTick.OnTickStart -= OnTick;
    }

    void OnTick()
    {
        if (!running) return;
        tickCounter++;
        if (tickCounter >= intervalTicks)
        {
            tickCounter = 0;
            Spawn();
        }
    }

    void Spawn()
    {
        if (GridService.Instance == null || BeltSimulationService.Instance == null)
        {
            if (debugLogging) Debug.LogWarning("[Spawner] Missing GridService or BeltSimulationService.");
            return;
        }
        var gs = GridService.Instance;
        var baseCell = gs.WorldToCell(transform.position);
        var dir = DirectionUtil.DirVec(outputDirection);
        var headCell = baseCell + dir;

        var item = new Item { id = nextItemId };

        bool spawned = false;
        Vector2Int spawnedCell = baseCell; // track where we actually placed the item
        if (preferHeadCell)
        {
            if (BeltSimulationService.Instance.TrySpawnItem(headCell, item))
            {
                spawned = true;
                spawnedCell = headCell;
            }
            else if (BeltSimulationService.Instance.TrySpawnItem(baseCell, item))
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
            else if (BeltSimulationService.Instance.TrySpawnItem(headCell, item))
            {
                spawned = true;
                spawnedCell = headCell;
            }
        }

        if (!spawned)
        {
            if (debugLogging)
                Debug.LogWarning($"[Spawner] Unable to spawn item at {baseCell} or {headCell}");
            return;
        }

        float z = itemPrefab != null ? itemPrefab.transform.position.z : 0f;
        var world = gs.CellToWorld(spawnedCell, z);
        if (itemPrefab != null)
        {
            // pooled acquire for this specific prefab
            var parent = ContainerLocator.GetItemContainer();
            var t = ItemViewPool.Get(itemPrefab, world, Quaternion.identity, parent);
            item.view = t;
        }

        if (item.view != null)
            item.view.position = world;

        if (debugLogging) Debug.Log($"[Spawner] Produced item {nextItemId} at {spawnedCell}");
        nextItemId++;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.12f);
        var v = (Vector3)(Vector2)DirectionUtil.DirVec(outputDirection) * 0.8f;
        Gizmos.DrawLine(transform.position, transform.position + v);
        Gizmos.DrawSphere(transform.position + v, 0.05f);
    }
#endif
}
