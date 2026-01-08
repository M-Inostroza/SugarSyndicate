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
    static readonly Dictionary<string, int> shippedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static bool initialized;
    static string goalItemType;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Init()
    {
        shippedCounts.Clear();
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
        shippedCounts.Clear();

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

        // Only track goal items for the level summary.
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

        if (shippedCounts.TryGetValue(itemType, out int count))
            shippedCounts[itemType] = count + 1;
        else
            shippedCounts[itemType] = 1;
    }

    public static Dictionary<string, int> GetShippedCountsSnapshot()
    {
        return new Dictionary<string, int>(shippedCounts, StringComparer.OrdinalIgnoreCase);
    }

    public static string BuildShippedSummaryString()
    {
        if (shippedCounts.Count == 0) return "-";

        var sb = new StringBuilder();
        bool first = true;
        foreach (var kvp in shippedCounts)
        {
            if (!first) sb.AppendLine();
            first = false;
            sb.Append($"{kvp.Key}: {kvp.Value}");
        }
        return sb.ToString();
    }
}
