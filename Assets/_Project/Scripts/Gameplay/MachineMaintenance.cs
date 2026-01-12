using System;
using UnityEngine;

[Serializable]
public class MachineMaintenance
{
    [Tooltip("Current maintenance level (0-1).")]
    [SerializeField, Range(0f, 1f)] float level01 = 1f;

    [Tooltip("Items consumed before the maintenance level drops once.")]
    [SerializeField, Min(1)] int itemsPerDrop = 50;

    [Tooltip("Maintenance drop applied per batch of items.")]
    [SerializeField, Range(0f, 1f)] float dropPerBatch = 0.05f;

    [Tooltip("Breakdown check starts once maintenance is at or below this level.")]
    [SerializeField, Range(0f, 1f)] float breakdownThreshold01 = 0.4f;

    [Tooltip("Chance to stop per item consumed once below the threshold.")]
    [SerializeField, Range(0f, 1f)] float breakdownChance = 0.1f;

    [SerializeField] bool enabled = true;

    int itemsSinceDrop;
    bool stopped;

    public float Level01 => level01;
    public bool IsStopped => stopped;

    public bool TryConsume(int count = 1)
    {
        if (!enabled) return true;
        if (stopped) return false;

        int items = Mathf.Max(1, count);
        if (level01 <= breakdownThreshold01 && breakdownChance > 0f)
        {
            for (int i = 0; i < items; i++)
            {
                if (UnityEngine.Random.value < breakdownChance)
                {
                    stopped = true;
                    return false;
                }
            }
        }

        ApplyWear(items);
        return true;
    }

    void ApplyWear(int items)
    {
        int perDrop = Mathf.Max(1, itemsPerDrop);
        itemsSinceDrop += items;
        if (itemsSinceDrop < perDrop) return;

        int batches = itemsSinceDrop / perDrop;
        itemsSinceDrop -= batches * perDrop;
        level01 = Mathf.Clamp01(level01 - dropPerBatch * batches);
    }
}
