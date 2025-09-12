using UnityEngine;

public class Spawner : MonoBehaviour
{
    public Direction outputDirection = Direction.Right;

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
        GameTick.OnTick += OnTick;
        if (debugLogging) Debug.Log($"[Spawner] Enabled at world {transform.position}");
    }

    void OnDisable()
    {
        GameTick.OnTick -= OnTick;
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
        bool ok = BeltSimulationService.Instance.TrySpawnItem(headCell, item);
        if (!ok)
        {
            if (debugLogging) Debug.Log($"[Spawner] Head {headCell} blocked, trying base {baseCell}");
            ok = BeltSimulationService.Instance.TrySpawnItem(baseCell, item);
        }
        if (ok)
        {
            if (debugLogging) Debug.Log($"[Spawner] Produced item {nextItemId} at {headCell} or {baseCell}");
            nextItemId++;
        }
        else if (debugLogging)
        {
            Debug.LogWarning($"[Spawner] Unable to spawn item at {headCell} or {baseCell}");
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
