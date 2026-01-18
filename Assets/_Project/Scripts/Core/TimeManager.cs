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
    [SerializeField, Min(1f)] float dayLengthSeconds = 100f;
    [SerializeField, Min(1f)] float nightLengthSeconds = 100f;

    [Header("State")]
    [SerializeField, Min(1)] int dayCount = 1;
    [SerializeField] TimePhase currentPhase = TimePhase.Day;
    [SerializeField] bool running = true;

    float phaseElapsed;
    float totalElapsed;

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
        OnDayCountChanged?.Invoke(dayCount);
        OnPhaseChanged?.Invoke(currentPhase);
        LogPhaseStart();
        OnPhaseTimeChanged?.Invoke(PhaseRemaining);
    }

    void AdvancePhase()
    {
        if (currentPhase == TimePhase.Day)
        {
            currentPhase = TimePhase.Night;
            OnPhaseChanged?.Invoke(currentPhase);
            LogPhaseStart();
            return;
        }

        currentPhase = TimePhase.Day;
        dayCount++;
        OnDayCountChanged?.Invoke(dayCount);
        OnPhaseChanged?.Invoke(currentPhase);
        LogPhaseStart();
    }

    void LogPhaseStart()
    {
        Debug.Log($"[TimeManager] {currentPhase} begins (Day {dayCount}).");
    }
}
