using System;
using System.Collections.Generic;
using UnityEngine;

public interface IPowerSource
{
    float GetOutputWatts(TimePhase phase);
}

public class PowerService : MonoBehaviour
{
    public static PowerService Instance { get; private set; }

    [Header("Debug")]
    [SerializeField] bool logPowerChanges = false;

    readonly HashSet<IPowerSource> sources = new();
    TimeManager timeManager;
    TimePhase cachedPhase = TimePhase.Day;
    bool hasPhase;

    float totalWatts;
    public float TotalWatts => totalWatts;
    public event Action<float> OnPowerChanged;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnEnable()
    {
        TryHookTimeManager();
    }

    void Update()
    {
        if (timeManager == null && TimeManager.Instance != null)
            TryHookTimeManager();
    }

    void OnDestroy()
    {
        if (timeManager != null)
            timeManager.OnPhaseChanged -= HandlePhaseChanged;
    }

    public static PowerService EnsureInstance()
    {
        if (Instance != null) return Instance;
        var existing = FindAnyObjectByType<PowerService>();
        if (existing != null) { Instance = existing; return Instance; }
        var go = new GameObject("PowerService");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<PowerService>();
        return Instance;
    }

    public void RegisterSource(IPowerSource source)
    {
        if (source == null) return;
        if (sources.Add(source))
            Recalculate();
    }

    public void UnregisterSource(IPowerSource source)
    {
        if (source == null) return;
        if (sources.Remove(source))
            Recalculate();
    }

    void TryHookTimeManager()
    {
        timeManager = TimeManager.Instance;
        if (timeManager == null) return;
        timeManager.OnPhaseChanged -= HandlePhaseChanged;
        timeManager.OnPhaseChanged += HandlePhaseChanged;
        cachedPhase = timeManager.CurrentPhase;
        hasPhase = true;
        Recalculate();
    }

    void HandlePhaseChanged(TimePhase phase)
    {
        cachedPhase = phase;
        hasPhase = true;
        Recalculate();
    }

    void Recalculate()
    {
        var phase = hasPhase ? cachedPhase : (TimeManager.Instance != null ? TimeManager.Instance.CurrentPhase : TimePhase.Day);
        float sum = 0f;
        foreach (var source in sources)
        {
            if (source == null) continue;
            sum += Mathf.Max(0f, source.GetOutputWatts(phase));
        }
        if (Mathf.Abs(sum - totalWatts) < 0.001f) return;
        totalWatts = sum;
        if (logPowerChanges) Debug.Log($"[PowerService] Power = {totalWatts:0.##} W");
        OnPowerChanged?.Invoke(totalWatts);
    }

    public static string FormatPower(float watts)
    {
        float abs = Mathf.Abs(watts);
        if (abs >= 1000000f) return $"{watts / 1000000f:0.##} MW";
        if (abs >= 1000f) return $"{watts / 1000f:0.##} kW";
        return $"{watts:0.##} W";
    }
}
