using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Minimal per-level stats aggregator for summary UI.
/// Tracks shipped item counts and resets automatically on scene load.
/// </summary>
public static class LevelStats
{
    // Goal-only counts are used by the existing summary UI.
    static readonly Dictionary<string, int> shippedGoalCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // All shipped counts are used by reward calculations.
    static readonly Dictionary<string, int> shippedAllCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static bool initialized;
    static string goalItemType;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Init()
    {
        shippedGoalCounts.Clear();
        shippedAllCounts.Clear();
        initialized = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureInitialized()
    {
        if (initialized) return;
        initialized = true;
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        shippedGoalCounts.Clear();
        shippedAllCounts.Clear();

        goalItemType = null;
        try
        {
            var gm = UnityEngine.Object.FindFirstObjectByType<GoalManager>();
            if (gm != null && gm.TryGetGoalItemType(out var t))
                goalItemType = t.Trim();
        }
        catch { }
    }

    public static void RecordShipped(string itemType)
    {
        if (string.IsNullOrWhiteSpace(itemType)) itemType = "Unknown";
        itemType = itemType.Trim();

        if (shippedAllCounts.TryGetValue(itemType, out int allCount))
            shippedAllCounts[itemType] = allCount + 1;
        else
            shippedAllCounts[itemType] = 1;

        // Only track goal items for the existing level summary.
        if (string.IsNullOrWhiteSpace(goalItemType))
        {
            // Late-bind if GoalManager wasn't ready at sceneLoaded time.
            try
            {
                var gm = UnityEngine.Object.FindFirstObjectByType<GoalManager>();
                if (gm != null && gm.TryGetGoalItemType(out var t))
                    goalItemType = t.Trim();
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(goalItemType)) return;
        if (!string.Equals(itemType, goalItemType, StringComparison.OrdinalIgnoreCase)) return;

        if (shippedGoalCounts.TryGetValue(itemType, out int count))
            shippedGoalCounts[itemType] = count + 1;
        else
            shippedGoalCounts[itemType] = 1;
    }

    public static Dictionary<string, int> GetShippedCountsSnapshot()
    {
        return new Dictionary<string, int>(shippedGoalCounts, StringComparer.OrdinalIgnoreCase);
    }

    public static Dictionary<string, int> GetAllShippedCountsSnapshot()
    {
        return new Dictionary<string, int>(shippedAllCounts, StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildShippedSummaryString()
    {
        if (shippedGoalCounts.Count == 0) return "-";

        var sb = new StringBuilder();
        bool first = true;
        foreach (var kvp in shippedGoalCounts)
        {
            if (!first) sb.AppendLine();
            first = false;
            sb.Append($"{kvp.Key}: {kvp.Value}");
        }
        return sb.ToString();
    }
}
