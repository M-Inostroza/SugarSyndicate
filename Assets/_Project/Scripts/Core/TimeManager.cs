using System;
using UnityEngine;

public enum TimePhase
{
    Day,
    Night
}

public class TimeManager : MonoBehaviour
{
    public static TimeManager Instance { get; private set; }

    [Header("Cycle Lengths (Seconds)")]
    [SerializeField, Min(1f)] float dayLengthSeconds = 45f;
    [SerializeField, Min(1f)] float nightLengthSeconds = 45f;

    [Header("State")]
    [SerializeField, Min(1)] int dayCount = 1;
    [SerializeField] TimePhase currentPhase = TimePhase.Day;
    [SerializeField] bool running = true;

    [Header("Debug")]
    [SerializeField] bool logPhaseEveryInterval = true;
    [SerializeField, Min(0.1f)] float logIntervalSeconds = 5f;

    float phaseElapsed;
    float totalElapsed;
    float logElapsed;

    public event Action<TimePhase> OnPhaseChanged;
    public event Action<int> OnDayCountChanged;
    public event Action<float> OnPhaseTimeChanged;

    public int DayCount => dayCount;
    public TimePhase CurrentPhase => currentPhase;
    public float PhaseElapsed => phaseElapsed;
    public float PhaseDuration => currentPhase == TimePhase.Day ? dayLengthSeconds : nightLengthSeconds;
    public float PhaseRemaining => Mathf.Max(0f, PhaseDuration - phaseElapsed);
    public bool IsRunning => running;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Update()
    {
        if (!running) return;
        if (BuildModeController.HasActiveTool) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;

        float delta = Time.deltaTime;
        if (delta <= 0f) return;

        totalElapsed += delta;
        if (logPhaseEveryInterval && logIntervalSeconds > 0f)
        {
            logElapsed += delta;
            while (logElapsed >= logIntervalSeconds)
            {
                logElapsed -= logIntervalSeconds;
                Debug.Log($"[TimeManager] Time: {totalElapsed:F1}s, Phase: {currentPhase}");
            }
        }

        phaseElapsed += delta;
        float duration = PhaseDuration;
        while (phaseElapsed >= duration)
        {
            phaseElapsed -= duration;
            AdvancePhase();
            duration = PhaseDuration;
        }

        OnPhaseTimeChanged?.Invoke(PhaseRemaining);
    }

    public void SetRunning(bool value)
    {
        running = value;
    }

    public void ResetCycle(int newDayCount = 1, TimePhase startPhase = TimePhase.Day)
    {
        dayCount = Mathf.Max(1, newDayCount);
        currentPhase = startPhase;
        phaseElapsed = 0f;
        totalElapsed = 0f;
        logElapsed = 0f;
        OnDayCountChanged?.Invoke(dayCount);
        OnPhaseChanged?.Invoke(currentPhase);
        OnPhaseTimeChanged?.Invoke(PhaseRemaining);
    }

    void AdvancePhase()
    {
        if (currentPhase == TimePhase.Day)
        {
            currentPhase = TimePhase.Night;
            OnPhaseChanged?.Invoke(currentPhase);
            return;
        }

        currentPhase = TimePhase.Day;
        dayCount++;
        OnDayCountChanged?.Invoke(dayCount);
        OnPhaseChanged?.Invoke(currentPhase);
    }
}
