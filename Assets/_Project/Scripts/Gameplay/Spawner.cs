using UnityEngine;

public class Spawner : MonoBehaviour
{
    public Direction outputDirection = Direction.Right;

    [Header("What to spawn")]
    [SerializeField] GameObject itemPrefab;
    [SerializeField] Transform itemsParent;

    [Header("When")]
    [SerializeField, Min(1)] int intervalTicks = 10;
    [SerializeField] bool autoStart = true;

    [Header("Pooling")]
    [SerializeField, Min(0)] int poolPrewarm = 8;

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;
    
    int tickCounter;
    bool running;
    Pool<ItemAgent> pool;

    void OnEnable()
    {
        running = autoStart;
        GameTick.OnTick += OnTick;

        if (itemPrefab != null && pool == null)
        {
            var agentPrefab = itemPrefab.GetComponentInChildren<ItemAgent>();
            if (agentPrefab != null)
            {
                var parent = itemsParent != null ? itemsParent : transform.parent;
                pool = new Pool<ItemAgent>(agentPrefab, poolPrewarm, parent);
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

    bool IsCellOccupied()
    {
        if (GridService.Instance == null) return false;
        
        Vector2Int spawnCell = GridService.Instance.WorldToCell(transform.position);
        
        var agents = Object.FindObjectsByType<ItemAgent>(FindObjectsSortMode.None);
        foreach (var agent in agents)
        {
            if (agent.CurrentCell == spawnCell)
            {
                if (debugLogging) Debug.Log($"Spawn blocked: Cell {spawnCell} is occupied by {agent.name}");
                return true;
            }
        }
        return false;
    }

    void Spawn()
    {
        if (IsCellOccupied()) return;

        Vector2Int spawnCell = GridService.Instance?.WorldToCell(transform.position) ?? Vector2Int.zero;
        
        if (pool != null)
        {
            var agent = pool.Get();
            var go = agent.gameObject;
            go.transform.SetParent(itemsParent != null ? itemsParent : transform.parent);

            if (GridService.Instance != null)
            {
                go.transform.position = GridService.Instance.CellToWorld(spawnCell, transform.position.z);
            }
            else
            {
                go.transform.position = transform.position;
            }

            go.transform.rotation = Quaternion.identity;
            agent.SpawnAt(go.transform.position, DirectionUtil.DirVec(outputDirection), a => pool.Release(a));
            
            if (debugLogging) Debug.Log($"Spawned item at {spawnCell}");
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
