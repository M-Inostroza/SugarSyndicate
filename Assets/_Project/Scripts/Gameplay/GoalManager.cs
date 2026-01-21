using System;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GoalManager : MonoBehaviour
{
    public event Action<string> OnGoalTextChanged;
    public event Action OnGoalUiChanged;

    public enum BonusObjectiveType
    {
        DeliverExtraItems,
        FinishUnderDays
    }

    [System.Serializable]
    public class BonusObjective
    {
        public BonusObjectiveType type = BonusObjectiveType.DeliverExtraItems;
        [Min(1)] public int extraItemCount = 10;
        [Min(1)] public int maxDays = 2;
    }

    [System.Serializable]
    class LevelGoal
    {
        [Min(0)] public int levelIndex;
        public string itemType = "SugarBlock";
        [Min(1)] public int itemTarget = 40;
        public BonusObjective[] bonusObjectives;
    }

    [Header("Goal")]
    [SerializeField] LevelGoal[] levelGoals =
    {
        new LevelGoal { levelIndex = 0, itemType = "SugarBlock", itemTarget = 40 }
    };

    [Header("State")]
    [SerializeField] bool autoCompleteOnTarget = true;

    int itemsDelivered;
    bool completed;
    bool hasGoal;
    int itemTarget;
    string goalItemType;
    BonusObjective[] activeBonusObjectives;
    bool[] bonusCompleted;
    int startDayCount;
    TimeManager timeManager;

    void Awake()
    {
        ResolveGoalForLevel();
    }

    void Start()
    {
        HookTimeManager();
    }

    void OnEnable()
    {
        Truck.OnItemDelivered += OnItemDelivered;
        HookTimeManager();
    }

    void OnDisable()
    {
        Truck.OnItemDelivered -= OnItemDelivered;
        if (timeManager != null) timeManager.OnDayCountChanged -= OnDayCountChanged;
    }

    void ResolveGoalForLevel()
    {
        int currentLevel = SceneManager.GetActiveScene().buildIndex;
        foreach (LevelGoal goal in levelGoals)
        {
            if (goal.levelIndex != currentLevel) continue;
            goalItemType = goal.itemType;
            itemTarget = goal.itemTarget;
            itemsDelivered = 0;
            completed = false;
            hasGoal = true;
            activeBonusObjectives = goal.bonusObjectives;
            int bonusCount = activeBonusObjectives != null ? activeBonusObjectives.Length : 0;
            bonusCompleted = bonusCount > 0 ? new bool[bonusCount] : null;
            startDayCount = GetCurrentDayCount();
            Debug.Log($"[GoalManager] Level {currentLevel} target: {itemTarget} ({FormatItemType(goalItemType)})");
            NotifyGoalUiChanged();
            return;
        }

        hasGoal = false;
        activeBonusObjectives = null;
        bonusCompleted = null;
        Debug.LogWarning($"[GoalManager] No goal configured for level {currentLevel}.");
        NotifyGoalUiChanged();
    }

    void OnItemDelivered(string itemType)
    {
        if (!hasGoal) return;
        if (!IsGoalItem(itemType)) return;
        itemsDelivered++;
        Debug.Log($"[GoalManager] Items delivered: {itemsDelivered}/{itemTarget} ({FormatItemType(goalItemType)})");
        UpdateBonusObjectives();
        NotifyGoalUiChanged();
        if (!completed && autoCompleteOnTarget && itemsDelivered >= itemTarget)
            CompleteGoal();
    }

    public void CompleteGoal()
    {
        if (completed) return;
        completed = true;
        Debug.Log("[GoalManager] Goal complete.");
        EvaluateTimeBonuses();
        NotifyGoalUiChanged();
        // TODO: hook into your level complete flow.
    }

    public string GetGoalText()
    {
        if (!hasGoal) return "Deliver -";
        return $"Deliver {itemTarget} {FormatItemType(goalItemType)}";
    }

    public bool TryGetGoalItemType(out string itemType)
    {
        itemType = goalItemType;
        return hasGoal && !string.IsNullOrWhiteSpace(itemType);
    }

    public bool TryGetOrderInfo(out string itemType, out int orderQty, out int shippedInWindow)
    {
        itemType = goalItemType;
        orderQty = itemTarget;
        shippedInWindow = itemsDelivered;
        return hasGoal && !string.IsNullOrWhiteSpace(itemType) && orderQty > 0;
    }

    public string GetProgressText()
    {
        if (!hasGoal) return "- / -";
        int shown = Mathf.Min(Mathf.Max(0, itemsDelivered), itemTarget);
        return $"{shown} / {itemTarget}";
    }

    public int BonusObjectiveCount => activeBonusObjectives != null ? activeBonusObjectives.Length : 0;

    public bool IsBonusObjectiveComplete(int index)
    {
        if (bonusCompleted == null) return false;
        if (index < 0 || index >= bonusCompleted.Length) return false;
        return bonusCompleted[index];
    }

    public string GetBonusObjectiveText(int index)
    {
        if (activeBonusObjectives == null) return string.Empty;
        if (index < 0 || index >= activeBonusObjectives.Length) return string.Empty;
        var objective = activeBonusObjectives[index];
        switch (objective.type)
        {
            case BonusObjectiveType.DeliverExtraItems:
            {
                int extra = Mathf.Max(1, objective.extraItemCount);
                return $"Deliver {extra} bonus {FormatItemType(goalItemType)}";
            }
            case BonusObjectiveType.FinishUnderDays:
                return $"Finish in under {Mathf.Max(1, objective.maxDays)} days";
            default:
                return string.Empty;
        }
    }

    public string GetBonusObjectiveProgressText(int index)
    {
        if (activeBonusObjectives == null) return string.Empty;
        if (index < 0 || index >= activeBonusObjectives.Length) return string.Empty;
        var objective = activeBonusObjectives[index];
        switch (objective.type)
        {
            case BonusObjectiveType.DeliverExtraItems:
            {
                int requiredExtra = Mathf.Max(1, objective.extraItemCount);
                int extraDelivered = Mathf.Max(0, itemsDelivered - itemTarget);
                return $"{extraDelivered} / {requiredExtra}";
            }
            case BonusObjectiveType.FinishUnderDays:
            {
                int maxDays = Mathf.Max(1, objective.maxDays);
                int elapsedDays = GetElapsedDays();
                int shown = Mathf.Min(elapsedDays, maxDays);
                return $"{shown} / {maxDays}";
            }
            default:
                return string.Empty;
        }
    }

    bool IsGoalItem(string itemType)
    {
        return IsItemMatch(itemType, goalItemType, true);
    }

    bool IsItemMatch(string deliveredType, string expectedType, bool allowAnyIfBlank)
    {
        if (string.IsNullOrWhiteSpace(expectedType)) return allowAnyIfBlank;
        if (string.IsNullOrWhiteSpace(deliveredType)) return false;
        return string.Equals(deliveredType.Trim(), expectedType.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    static string FormatItemType(string itemType)
    {
        return string.IsNullOrWhiteSpace(itemType) ? "Any" : itemType.Trim();
    }

    void NotifyGoalUiChanged()
    {
        OnGoalTextChanged?.Invoke(GetGoalText());
        OnGoalUiChanged?.Invoke();
    }

    void UpdateBonusObjectives()
    {
        if (activeBonusObjectives == null || bonusCompleted == null) return;
        for (int i = 0; i < activeBonusObjectives.Length; i++)
        {
            if (bonusCompleted[i]) continue;
            var objective = activeBonusObjectives[i];
            switch (objective.type)
            {
                case BonusObjectiveType.DeliverExtraItems:
                {
                    int requiredExtra = Mathf.Max(1, objective.extraItemCount);
                    int extraDelivered = Mathf.Max(0, itemsDelivered - itemTarget);
                    if (extraDelivered >= requiredExtra)
                    {
                        bonusCompleted[i] = true;
                        Debug.Log($"[GoalManager] Bonus complete: {GetBonusObjectiveText(i)}");
                    }
                    break;
                }
            }
        }
    }

    void EvaluateTimeBonuses()
    {
        if (activeBonusObjectives == null || bonusCompleted == null) return;
        for (int i = 0; i < activeBonusObjectives.Length; i++)
        {
            if (bonusCompleted[i]) continue;
            var objective = activeBonusObjectives[i];
            if (objective.type != BonusObjectiveType.FinishUnderDays) continue;
            if (TimeManager.Instance == null)
            {
                Debug.LogWarning("[GoalManager] TimeManager not found; time bonus cannot be evaluated.");
                continue;
            }
            int maxDays = Mathf.Max(1, objective.maxDays);
            int elapsedDays = GetElapsedDays();
            if (elapsedDays <= maxDays)
            {
                bonusCompleted[i] = true;
                Debug.Log($"[GoalManager] Bonus complete: {GetBonusObjectiveText(i)}");
            }
        }
    }

    int GetCurrentDayCount()
    {
        var tm = timeManager != null ? timeManager : TimeManager.Instance;
        return tm != null ? tm.DayCount : 1;
    }

    int GetElapsedDays()
    {
        int current = GetCurrentDayCount();
        return Mathf.Max(1, current - startDayCount + 1);
    }

    void HookTimeManager()
    {
        if (timeManager != null) return;
        timeManager = TimeManager.Instance ?? FindAnyObjectByType<TimeManager>();
        if (timeManager != null)
            timeManager.OnDayCountChanged += OnDayCountChanged;
    }

    void OnDayCountChanged(int dayCount)
    {
        if (!hasGoal) return;
        NotifyGoalUiChanged();
    }

    public bool IsMainMissionComplete()
    {
        if (!hasGoal) return false;
        return itemsDelivered >= itemTarget;
    }

    bool IsBonusObjectiveCompleteComputed(int index)
    {
        if (activeBonusObjectives == null) return false;
        if (index < 0 || index >= activeBonusObjectives.Length) return false;

        var objective = activeBonusObjectives[index];
        switch (objective.type)
        {
            case BonusObjectiveType.DeliverExtraItems:
            {
                int requiredExtra = Mathf.Max(1, objective.extraItemCount);
                int extraDelivered = Mathf.Max(0, itemsDelivered - itemTarget);
                return extraDelivered >= requiredExtra;
            }
            case BonusObjectiveType.FinishUnderDays:
            {
                int maxDays = Mathf.Max(1, objective.maxDays);
                int elapsedDays = GetElapsedDays();
                return elapsedDays <= maxDays;
            }
            default:
                return false;
        }
    }

    public void LogFinalMissionAndSideMissionStatuses()
    {
        if (!hasGoal)
        {
            Debug.Log("[GoalManager] Final Status: No goal configured.");
            return;
        }

        string mainStatus = IsMainMissionComplete() ? "COMPLETE" : "INCOMPLETE";
        Debug.Log($"[GoalManager] Final Main Mission: {mainStatus} | {GetGoalText()} | Progress: {GetProgressText()}");

        int bonusCount = BonusObjectiveCount;
        if (bonusCount <= 0)
        {
            Debug.Log("[GoalManager] Final Side Missions: None");
            return;
        }

        for (int i = 0; i < bonusCount; i++)
        {
            string status = IsBonusObjectiveCompleteComputed(i) ? "COMPLETE" : "INCOMPLETE";
            Debug.Log($"[GoalManager] Final Side Mission {i + 1}: {status} | {GetBonusObjectiveText(i)} | Progress: {GetBonusObjectiveProgressText(i)}");
        }
    }

    public void GetStarCounts(out int earned, out int total)
    {
        earned = 0;
        total = 0;
        if (!hasGoal)
            return;

        // Main goal is worth 1 star.
        total = 1 + BonusObjectiveCount;
        if (IsMainMissionComplete()) earned++;

        for (int i = 0; i < BonusObjectiveCount; i++)
        {
            if (IsBonusObjectiveCompleteComputed(i)) earned++;
        }
    }
}
