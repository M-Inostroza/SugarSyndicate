using System;
using UnityEngine;

public class GameTick : MonoBehaviour
{
    public static event Action OnTickStart;   // new: start-of-tick phase
    public static event Action OnTick;        // existing compatibility
    public static event Action OnTickEnd;     // new: end-of-tick phase (resolve/apply)

    public static long TickIndex { get; private set; }

    [Range(5, 60)] public int ticksPerSecond = 15;
    [SerializeField] bool debugLogs = false;

    float acc;
    float debugTimer;
    static GameTick _instance;
    void Awake()
    {
        if (_instance) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Update()
    {
        acc += Time.deltaTime;
        float interval = 1f / ticksPerSecond;
        while (acc >= interval)
        {
            acc -= interval;
            OnTickStart?.Invoke();
            OnTick?.Invoke();
            OnTickEnd?.Invoke();
            TickIndex++;
        }

        if (debugLogs)
        {
            debugTimer += Time.deltaTime;
            if (debugTimer >= 1f)
            {
                debugTimer = 0f;
                Debug.Log($"[GameTick] tps={ticksPerSecond} tick={TickIndex}");
            }
        }
    }
}
