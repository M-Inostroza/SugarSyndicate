using UnityEngine;

// Rotates a UI dial to show day/night progress from the TimeManager.
[DisallowMultipleComponent]
public class DayNightDial : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] RectTransform target;

    [Header("Angles (Z)")]
    [Tooltip("Angle when day starts (default -180).")]
    [SerializeField] float dayStartAngle = -180f;
    [Tooltip("Angle when night starts (default 0).")]
    [SerializeField] float nightStartAngle = 0f;
    [Tooltip("Angle when night ends (default 180, same orientation as -180).")]
    [SerializeField] float nightEndAngle = 180f;
    [Tooltip("If true, rotates the dial in the opposite (counter-clockwise) direction.")]
    [SerializeField] bool rotateCounterClockwise = true;

    TimeManager timeManager;

    void Awake()
    {
        if (target == null) target = GetComponent<RectTransform>();
    }

    void OnEnable()
    {
        TryHookTimeManager();
        UpdateRotation();
    }

    void OnDisable()
    {
        UnhookTimeManager();
    }

    void Update()
    {
        if (timeManager == null)
        {
            TryHookTimeManager();
        }
        UpdateRotation();
    }

    void TryHookTimeManager()
    {
        if (timeManager != null) return;
        timeManager = TimeManager.Instance ?? FindAnyObjectByType<TimeManager>();
        if (timeManager == null) return;
        timeManager.OnPhaseChanged += HandlePhaseChanged;
        timeManager.OnPhaseTimeChanged += HandlePhaseTimeChanged;
    }

    void UnhookTimeManager()
    {
        if (timeManager == null) return;
        timeManager.OnPhaseChanged -= HandlePhaseChanged;
        timeManager.OnPhaseTimeChanged -= HandlePhaseTimeChanged;
        timeManager = null;
    }

    void HandlePhaseChanged(TimePhase _)
    {
        UpdateRotation();
    }

    void HandlePhaseTimeChanged(float _)
    {
        UpdateRotation();
    }

    void UpdateRotation()
    {
        if (target == null) return;
        if (timeManager == null)
        {
            target.localRotation = Quaternion.Euler(0f, 0f, dayStartAngle);
            return;
        }

        float duration = Mathf.Max(0.0001f, timeManager.PhaseDuration);
        float progress = Mathf.Clamp01(timeManager.PhaseElapsed / duration);
        float dayStart = dayStartAngle;
        float nightStart = nightStartAngle;
        float nightEnd = nightEndAngle;

        if (rotateCounterClockwise)
        {
            nightStart = MakeAngleBehind(dayStart, nightStart);
            nightEnd = MakeAngleBehind(nightStart, nightEnd);
        }
        else
        {
            nightStart = MakeAngleAhead(dayStart, nightStart);
            nightEnd = MakeAngleAhead(nightStart, nightEnd);
        }

        float angle = timeManager.CurrentPhase == TimePhase.Day
            ? Mathf.Lerp(dayStart, nightStart, progress)
            : Mathf.Lerp(nightStart, nightEnd, progress);

        target.localRotation = Quaternion.Euler(0f, 0f, angle);
    }

    static float MakeAngleAhead(float from, float target)
    {
        float result = target;
        while (result < from) result += 360f;
        return result;
    }

    static float MakeAngleBehind(float from, float target)
    {
        float result = target;
        while (result > from) result -= 360f;
        return result;
    }
}
