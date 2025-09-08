using UnityEngine;

public class Spawner : MonoBehaviour
{
    public Direction outputDirection = Direction.Right;

    [Header("What to spawn")]
    [SerializeField] GameObject itemPrefab;     // assign your item prefab
    [SerializeField] Transform itemsParent;     // usually LevelRoot/Items

    [Header("When")]
    [SerializeField, Min(1)] int intervalTicks = 10;
    [SerializeField] bool autoStart = true;

    [Header("Pooling")]
    [SerializeField, Min(0)] int poolPrewarm = 8;

    int tickCounter;
    bool running;

    Pool<ItemAgent> pool;

    void OnEnable()
    {
        running = autoStart;
        GameTick.OnTick += OnTick;   // uses your existing tick

        // Create pool if the prefab contains ItemAgent (support child components)
        if (itemPrefab != null && pool == null)
        {
            var agentPrefab = itemPrefab.GetComponentInChildren<ItemAgent>();
            if (agentPrefab != null)
            {
                var parent = itemsParent != null ? itemsParent : transform.parent;
                pool = new Pool<ItemAgent>(agentPrefab, poolPrewarm, parent);
            }
            else
            {
                // no ItemAgent on prefab; fallback to Instantiate when spawning
            }
        }
    }

    void OnDisable()
    {
        GameTick.OnTick -= OnTick;
    }

    void OnTick()
    {
        if (!running || itemPrefab == null) return;
        tickCounter++;
        if (tickCounter >= intervalTicks)
        {
            tickCounter = 0;
            Spawn();
        }
    }

    void Spawn()
    {
        var parent = itemsParent != null ? itemsParent : transform.parent;

        // If GridService exists, block spawn when cell occupied (traffic jam)
        if (GridService.Instance != null)
        {
            var spawnCell = GridService.Instance.WorldToCell(transform.position);
            var c = GridService.Instance.GetCell(spawnCell);
            if (c != null && c.itemCount > 0)
            {
                return;
            }
        }

        // If we have a pool, use it
        if (pool != null)
        {
            var agent = pool.Get();
            var go = agent.gameObject;
            go.transform.SetParent(parent);

            // Snap spawn position to grid center if present
            if (GridService.Instance != null)
            {
                var cell = GridService.Instance.WorldToCell(transform.position);
                var world = GridService.Instance.CellToWorld(cell, transform.position.z);
                go.transform.position = world;
            }
            else
            {
                go.transform.position = transform.position;
            }

            go.transform.rotation = Quaternion.identity;

            // Pass a callback that returns the agent to the pool
            agent.SpawnAt(go.transform.position, DirectionUtil.DirVec(outputDirection), a => pool.Release(a));
            return;
        }

        // Fallback: instantiate as before
        var goFallback = Instantiate(itemPrefab, parent);

        if (GridService.Instance != null)
        {
            var cell = GridService.Instance.WorldToCell(transform.position);
            var world = GridService.Instance.CellToWorld(cell, transform.position.z);
            goFallback.transform.position = world;
        }
        else
        {
            goFallback.transform.position = transform.position;
        }

        goFallback.transform.rotation = Quaternion.identity;

        // Support ItemAgent on child objects
        var agentFallback = goFallback.GetComponentInChildren<ItemAgent>();
        if (agentFallback != null)
        {
            agentFallback.SpawnAt(goFallback.transform.position, DirectionUtil.DirVec(outputDirection));
        }
        else
        {
            // no ItemAgent found on instantiated prefab
        }
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
