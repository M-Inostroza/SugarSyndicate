using System;
using UnityEngine;

public class GameTick : MonoBehaviour
{
    public static event Action OnTick;
    [Range(5, 60)] public int ticksPerSecond = 15;

    float acc;
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
            OnTick?.Invoke();
        }
    }
}
