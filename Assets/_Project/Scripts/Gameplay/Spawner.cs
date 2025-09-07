using UnityEngine;

public class Spawner : MonoBehaviour
{
    public enum Direction { Up, Right, Down, Left }

    [Header("What to spawn")]
    [SerializeField] GameObject itemPrefab;     // assign your item prefab
    [SerializeField] Transform itemsParent;     // usually LevelRoot/Items

    [Header("When")]
    [SerializeField, Min(1)] int intervalTicks = 10;
    [SerializeField] bool autoStart = true;

    [Header("Where/How")]
    [SerializeField] Direction outputDirection = Direction.Right;

    int tickCounter;
    bool running;

    void OnEnable()
    {
        running = autoStart;
        GameTick.OnTick += OnTick;   // uses your existing tick
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
        var go = Instantiate(itemPrefab, parent);
        go.transform.position = transform.position;
        go.transform.rotation = Quaternion.identity;

        // If you already have an ItemAgent, initialize it:
        var agent = go.GetComponent<ItemAgent>();
        if (agent != null)
            agent.SpawnAt(transform.position, DirVec(outputDirection));
    }

    static Vector2Int DirVec(Direction d) => d switch
    {
        Direction.Up => new Vector2Int(0, 1),
        Direction.Right => new Vector2Int(1, 0),
        Direction.Down => new Vector2Int(0, -1),
        _ => new Vector2Int(-1, 0),
    };

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.12f);
        var v = (Vector3)(Vector2)DirVec(outputDirection) * 0.8f;
        Gizmos.DrawLine(transform.position, transform.position + v);
        Gizmos.DrawSphere(transform.position + v, 0.05f);
    }
#endif
}
