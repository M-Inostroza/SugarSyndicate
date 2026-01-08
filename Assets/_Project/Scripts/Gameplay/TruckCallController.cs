using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Button-friendly controller to activate trucks so they start accepting items.
/// Hook UI Button.onClick to CallTrucks().
/// </summary>
public class TruckCallController : MonoBehaviour
{
    [Header("Settings")]
    [SerializeField, Min(0f)] float activeSeconds = 30f;

    [Header("UI")]
    [Tooltip("Optional. If assigned, will be disabled after first use so trucks can't be called twice.")]
    [SerializeField] Button callTrucksButton;

    [Tooltip("Optional. If assigned, will be shown/refreshed when the level ends.")]
    [SerializeField] LevelSummaryUI levelSummaryUI;

    Coroutine autoRecallRoutine;
    bool hasCalledTrucksThisLevel;

    public void CallTrucks()
    {
        if (hasCalledTrucksThisLevel) return;
        hasCalledTrucksThisLevel = true;

        if (callTrucksButton != null)
            callTrucksButton.interactable = false;

        Truck.SetTrucksCalled(true);

        if (autoRecallRoutine != null)
        {
            StopCoroutine(autoRecallRoutine);
            autoRecallRoutine = null;
        }
        autoRecallRoutine = StartCoroutine(EndAfterWindow());
    }

    public void RecallTrucks()
    {
        if (autoRecallRoutine != null)
        {
            StopCoroutine(autoRecallRoutine);
            autoRecallRoutine = null;
        }
        Truck.SetTrucksCalled(false);
    }

    void OnDisable()
    {
        if (autoRecallRoutine != null)
        {
            StopCoroutine(autoRecallRoutine);
            autoRecallRoutine = null;
        }
    }

    System.Collections.IEnumerator EndAfterWindow()
    {
        if (activeSeconds > 0f)
            yield return new WaitForSeconds(activeSeconds);

        // Deactivate trucks
        Truck.SetTrucksCalled(false);

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
        TryShowSummaryUI();

        // Finish the level (minimal implementation: stop time/simulation)
        try
        {
            if (TimeManager.Instance != null) TimeManager.Instance.SetRunning(false);
            if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        }
        catch { }

        autoRecallRoutine = null;
    }

    void TryShowSummaryUI()
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
                return;
            }

            ui.gameObject.SetActive(true);
            ui.transform.SetAsLastSibling();
            ui.Refresh();
        }
        catch { }
    }
}
