using UnityEngine;

public class Spawner : MonoBehaviour
{
    public Direction outputDirection = Direction.Right;

    [Header("Prefabs")]
    [SerializeField] GameObject itemPrefab;

    [Header("When")]
    [SerializeField, Min(1)] int intervalTicks = 10;
    [SerializeField] bool autoStart = true;

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    int tickCounter;
    bool running;
    int nextItemId = 1;

    void OnEnable()
    {
        running = autoStart;
        GameTick.OnTickStart += OnTick;
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
        Vector2Int spawnCell = baseCell;
        if (!BeltSimulationService.Instance.TrySpawnItem(spawnCell, item))
        {
            if (debugLogging) Debug.Log($"[Spawner] Base {baseCell} blocked, trying head {headCell}");
            spawnCell = headCell;
            if (!BeltSimulationService.Instance.TrySpawnItem(spawnCell, item))
            {
                if (debugLogging)
                    Debug.LogWarning($"[Spawner] Unable to spawn item at {baseCell} or {headCell}");
                return;
            }
        }

        float z = itemPrefab != null ? itemPrefab.transform.position.z : 0f;
        var world = gs.CellToWorld(spawnCell, z);
        if (itemPrefab != null)
            item.view = Instantiate(itemPrefab, world, Quaternion.identity).transform;

        if (item.view != null)
            item.view.position = world;

        // Set the parent of the instantiated item view to the Item Container
        var itemParent = ContainerLocator.GetItemContainer();
        if (item.view != null && itemParent != null)
            item.view.SetParent(itemParent, worldPositionStays: true);

        if (debugLogging) Debug.Log($"[Spawner] Produced item {nextItemId} at {spawnCell}");
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
