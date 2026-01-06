using System;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GoalManager : MonoBehaviour
{
    public event Action<string> OnGoalTextChanged;

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

    [Header("UI")]
    [SerializeField] TMP_Text goalText;
    [SerializeField] TMP_Text progressText;

    int itemsDelivered;
    bool completed;
    bool hasGoal;
    int itemTarget;
    string goalItemType;
    BonusObjective[] activeBonusObjectives;
    bool[] bonusCompleted;
    int startDayCount;

    void Awake()
    {
        ResolveGoalForLevel();
    }

    void OnEnable()
    {
        Truck.OnItemDelivered += OnItemDelivered;
    }

    void OnDisable()
    {
        Truck.OnItemDelivered -= OnItemDelivered;
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
            NotifyGoalText();
            UpdateProgressText();
            return;
        }

        hasGoal = false;
        activeBonusObjectives = null;
        bonusCompleted = null;
        Debug.LogWarning($"[GoalManager] No goal configured for level {currentLevel}.");
        NotifyGoalText();
        UpdateProgressText();
    }

    void OnItemDelivered(string itemType)
    {
        if (!hasGoal) return;
        if (!IsGoalItem(itemType)) return;
        itemsDelivered++;
        Debug.Log($"[GoalManager] Items delivered: {itemsDelivered}/{itemTarget} ({FormatItemType(goalItemType)})");
        UpdateProgressText();
        UpdateBonusObjectives();
        if (!completed && autoCompleteOnTarget && itemsDelivered >= itemTarget)
            CompleteGoal();
    }

    public void CompleteGoal()
    {
        if (completed) return;
        completed = true;
        Debug.Log("[GoalManager] Goal complete.");
        EvaluateTimeBonuses();
        // TODO: hook into your level complete flow.
    }

    public string GetGoalText()
    {
        if (!hasGoal) return "Deliver -";
        return $"Deliver {itemTarget} {FormatItemType(goalItemType)}";
    }

    public string GetProgressText()
    {
        if (!hasGoal) return "- / -";
        return $"{Mathf.Min(itemsDelivered, itemTarget)} / {itemTarget}";
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
                int required = itemTarget + Mathf.Max(1, objective.extraItemCount);
                return $"Deliver {required} {FormatItemType(goalItemType)}";
            }
            case BonusObjectiveType.FinishUnderDays:
                return $"Finish in under {Mathf.Max(1, objective.maxDays)} days";
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

    void NotifyGoalText()
    {
        string text = GetGoalText();
        if (goalText != null) goalText.text = text;
        OnGoalTextChanged?.Invoke(text);
    }

    void UpdateProgressText()
    {
        if (progressText == null) return;
        progressText.text = GetProgressText();
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
                    int required = itemTarget + Mathf.Max(1, objective.extraItemCount);
                    if (itemsDelivered >= required)
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
        return TimeManager.Instance != null ? TimeManager.Instance.DayCount : 1;
    }

    int GetElapsedDays()
    {
        int current = GetCurrentDayCount();
        return Mathf.Max(1, current - startDayCount + 1);
    }
}
