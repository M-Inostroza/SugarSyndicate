using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Button-friendly controller to activate trucks so they start accepting items.
/// Hook UI Button.onClick to CallTrucks().
/// </summary>
public class TruckCallController : MonoBehaviour
{
    [System.Serializable]
    struct ItemValue
    {
        public string itemType;
        public int value;
    }

    [Header("Settings")]
    [SerializeField, Min(0f)] float activeSeconds = 30f;

    [Header("Sucra Reward")]
    [Tooltip("Base(Lv). If baseSucraByLevel doesn't include the current buildIndex, this value is used.")]
    [SerializeField, Min(0)] int baseSucraDefault = 1000;

    [Tooltip("Optional Base(Lv) overrides by buildIndex. Index 0 = first scene in Build Settings.")]
    [SerializeField] int[] baseSucraByLevel;

    [Tooltip("Item values used for ShipmentBonus: Σ(value[itemType] * shippedUnits[itemType]).")]
    [SerializeField] ItemValue[] itemValues;

    [Tooltip("Optional: +50 if time to first delivered item < 3 seconds after loading starts (trucks docked).")]
    [SerializeField] bool enableFastStartBonus = false;

    [Header("UI")]
    [Tooltip("Optional. If assigned, will be disabled after first use so trucks can't be called twice.")]
    [SerializeField] Button callTrucksButton;

    [Tooltip("Optional. If assigned, will be shown/refreshed when the level ends.")]
    [SerializeField] LevelSummaryUI levelSummaryUI;

    Coroutine autoRecallRoutine;
    bool hasCalledTrucksThisLevel;
    float calledAtTime = -1f;
    float firstDeliveryAtTime = -1f;
    int pendingDockCount;
    bool loadingStarted;

    public void CallTrucks()
    {
        if (hasCalledTrucksThisLevel) return;
        hasCalledTrucksThisLevel = true;

        loadingStarted = false;
        calledAtTime = -1f;
        firstDeliveryAtTime = -1f;
        Truck.OnItemDelivered -= HandleAnyItemDelivered;
        Truck.OnItemDelivered += HandleAnyItemDelivered;
        Truck.OnTruckDocked -= HandleTruckDocked;
        Truck.OnTruckDocked += HandleTruckDocked;

        if (callTrucksButton != null)
            callTrucksButton.interactable = false;

        pendingDockCount = CountPendingDocks();
        Truck.SetTrucksCalled(true);

        if (autoRecallRoutine != null)
        {
            StopCoroutine(autoRecallRoutine);
            autoRecallRoutine = null;
        }

        if (pendingDockCount <= 0)
            StartLoadingWindow();
    }

    public void RecallTrucks()
    {
        if (autoRecallRoutine != null)
        {
            StopCoroutine(autoRecallRoutine);
            autoRecallRoutine = null;
        }
        loadingStarted = false;
        pendingDockCount = 0;
        Truck.OnTruckDocked -= HandleTruckDocked;
        Truck.OnItemDelivered -= HandleAnyItemDelivered;
        Truck.SetTrucksCalled(false);
    }

    void HandleAnyItemDelivered(string _)
    {
        if (firstDeliveryAtTime >= 0f) return;
        firstDeliveryAtTime = Time.time;
    }

    void HandleTruckDocked(Truck truck)
    {
        if (!hasCalledTrucksThisLevel || loadingStarted) return;
        if (pendingDockCount > 0) pendingDockCount--;
        if (pendingDockCount <= 0)
            StartLoadingWindow();
    }

    void OnDisable()
    {
        if (autoRecallRoutine != null)
        {
            StopCoroutine(autoRecallRoutine);
            autoRecallRoutine = null;
        }

        Truck.OnItemDelivered -= HandleAnyItemDelivered;
        Truck.OnTruckDocked -= HandleTruckDocked;
    }

    int CountPendingDocks()
    {
        try
        {
            var trucks = FindObjectsByType<Truck>(FindObjectsSortMode.None);
            if (trucks == null || trucks.Length == 0) return 0;
            int count = 0;
            for (int i = 0; i < trucks.Length; i++)
            {
                var truck = trucks[i];
                if (truck == null) continue;
                if (!truck.IsDocked) count++;
            }
            return count;
        }
        catch { return 0; }
    }

    void StartLoadingWindow()
    {
        if (loadingStarted) return;
        loadingStarted = true;
        calledAtTime = Time.time;

        Truck.OnTruckDocked -= HandleTruckDocked;

        if (autoRecallRoutine != null)
        {
            StopCoroutine(autoRecallRoutine);
            autoRecallRoutine = null;
        }
        autoRecallRoutine = StartCoroutine(EndAfterWindow());
    }

    System.Collections.IEnumerator EndAfterWindow()
    {
        if (activeSeconds > 0f)
            yield return new WaitForSeconds(activeSeconds);

        // Deactivate trucks
        Truck.SetTrucksCalled(false);

        // Stop listening for deliveries; the window is over.
        Truck.OnItemDelivered -= HandleAnyItemDelivered;

        // Log final mission + side mission statuses
        try
        {
            var goalManager = FindFirstObjectByType<GoalManager>();
            if (goalManager != null)
                goalManager.LogFinalMissionAndSideMissionStatuses();
            else
                Debug.LogWarning("[TruckCallController] GoalManager not found; cannot log mission statuses.");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[TruckCallController] Failed to log mission statuses: {e.Message}");
        }

        // Populate summary UI
        int sucraEarned = 0;
        try
        {
            sucraEarned = CalculateSucraReward();
            if (sucraEarned > 0 && GameManager.Instance != null)
                GameManager.Instance.AddSucra(sucraEarned);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[TruckCallController] Failed to calculate/award Sucra: {e.Message}");
        }

        var summaryUI = TryShowSummaryUI();
        if (summaryUI != null)
        {
            try { summaryUI.SetSucraEarned(sucraEarned); } catch { }
        }

        // Finish the level (minimal implementation: stop time/simulation)
        try
        {
            if (TimeManager.Instance != null) TimeManager.Instance.SetRunning(false);
            if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        }
        catch { }

        autoRecallRoutine = null;
    }

    int CalculateSucraReward()
    {
        int baseLv = GetBaseSucraForCurrentLevel();

        // ShipmentBonus = Σ(value[p] * shippedUnits[p])
        int shipmentBonus = 0;
        var shipped = LevelStats.GetAllShippedCountsSnapshot();
        foreach (var kvp in shipped)
        {
            int value = GetItemValueOrDefault(kvp.Key);
            if (value <= 0) continue;
            shipmentBonus += value * Mathf.Max(0, kvp.Value);
        }

        // EfficiencyBonus = max(0, BudgetRemaining * 0.25)
        int budgetRemaining = 0;
        try { if (GameManager.Instance != null) budgetRemaining = GameManager.Instance.SweetCredits; } catch { }
        int efficiencyBonus = budgetRemaining > 0 ? Mathf.FloorToInt(budgetRemaining * 0.25f) : 0;

        // Surplus is shippedUnitsTotal - orderQty (goal item only)
        int surplusUnits = 0;
        try
        {
            var goalManager = FindFirstObjectByType<GoalManager>();
            if (goalManager != null && goalManager.TryGetOrderInfo(out _, out int orderQty, out int shippedInWindow))
                surplusUnits = Mathf.Max(0, shippedInWindow - orderQty);
        }
        catch { }

        // WindowBonuses = OverloadBonus + BalancedFleetBonus [+ FastStartBonus]
        int overloadBonus = 3 * Mathf.Max(0, surplusUnits);
        int balancedFleetBonus = IsBalancedFleet() ? Mathf.FloorToInt((baseLv + shipmentBonus) * 0.10f) : 0;
        int fastStartBonus = 0;
        if (enableFastStartBonus && calledAtTime >= 0f && firstDeliveryAtTime >= 0f)
        {
            float dt = firstDeliveryAtTime - calledAtTime;
            if (dt < 3f) fastStartBonus = 50;
        }
        int windowBonuses = overloadBonus + balancedFleetBonus + fastStartBonus;

        // OverdraftFee = ceil(max(0, -Budget) * 0.25)
        int overdraftFee = budgetRemaining < 0 ? Mathf.CeilToInt((-budgetRemaining) * 0.25f) : 0;

        int total = baseLv + shipmentBonus + efficiencyBonus + windowBonuses - overdraftFee;
        return Mathf.Max(0, total);
    }

    int GetBaseSucraForCurrentLevel()
    {
        int idx = 0;
        try { idx = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex; } catch { }
        if (baseSucraByLevel != null && idx >= 0 && idx < baseSucraByLevel.Length)
            return Mathf.Max(0, baseSucraByLevel[idx]);
        return Mathf.Max(0, baseSucraDefault);
    }

    int GetItemValueOrDefault(string itemType)
    {
        if (string.IsNullOrWhiteSpace(itemType)) return 0;
        string t = itemType.Trim();

        if (itemValues != null)
        {
            for (int i = 0; i < itemValues.Length; i++)
            {
                var iv = itemValues[i];
                if (string.IsNullOrWhiteSpace(iv.itemType)) continue;
                if (string.Equals(iv.itemType.Trim(), t, System.StringComparison.OrdinalIgnoreCase))
                    return Mathf.Max(0, iv.value);
            }
        }

        // Defaults from spec
        if (string.Equals(t, "Dust", System.StringComparison.OrdinalIgnoreCase)) return 6;
        if (string.Equals(t, "Syrup", System.StringComparison.OrdinalIgnoreCase)) return 10;
        if (string.Equals(t, "Crystals", System.StringComparison.OrdinalIgnoreCase)) return 14;
        if (string.Equals(t, "Clouds", System.StringComparison.OrdinalIgnoreCase)) return 12;
        return 0;
    }

    bool IsBalancedFleet()
    {
        try
        {
            var trucks = FindObjectsByType<Truck>(FindObjectsSortMode.None);
            if (trucks == null || trucks.Length == 0) return false;
            for (int i = 0; i < trucks.Length; i++)
            {
                var t = trucks[i];
                if (t == null) continue;
                int cap = t.Capacity;
                if (cap <= 0) return false;
                float ratio = (float)t.StoredItemCount / cap;
                if (ratio < 0.90f) return false;
            }
            return true;
        }
        catch { return false; }
    }

    LevelSummaryUI TryShowSummaryUI()
    {
        try
        {
            var ui = levelSummaryUI;
            if (ui == null)
            {
                // Note: FindFirstObjectByType won't find inactive objects.
                ui = FindFirstObjectByType<LevelSummaryUI>();
            }

            if (ui == null)
            {
                // Fallback: include inactive objects.
                var all = Resources.FindObjectsOfTypeAll<LevelSummaryUI>();
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var candidate = all[i];
                        if (candidate == null) continue;
                        if (!candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded) continue;
                        ui = candidate;
                        break;
                    }
                }
            }

            if (ui == null)
            {
                Debug.LogWarning("[TruckCallController] LevelSummaryUI not found; summary screen will not show.");
                return null;
            }

            ui.gameObject.SetActive(true);
            ui.transform.SetAsLastSibling();
            ui.Refresh();
            return ui;
        }
        catch { return null; }
    }
}
