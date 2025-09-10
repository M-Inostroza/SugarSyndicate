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
        if (GridService.Instance == null || BeltGraphService.Instance == null) return;
        var gs = GridService.Instance;
        var baseCell = gs.WorldToCell(transform.position);
        var dir = DirectionUtil.DirVec(outputDirection);
        var headCell = baseCell + dir; // preferred
        bool ok = BeltGraphService.Instance.TryProduceAtHead(headCell, nextItemId);
        if (!ok)
        {
            ok = BeltGraphService.Instance.TryProduceAtHead(baseCell, nextItemId);
        }
        if (ok) nextItemId++;
        if (debugLogging) Debug.Log(ok ? $"Produced item into run at {headCell} or {baseCell}" : $"No run head at {headCell} or {baseCell}, item skipped");
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
