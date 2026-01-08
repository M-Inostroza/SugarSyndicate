using TMPro;
using UnityEngine;

/// <summary>
/// Fills a level summary UI with stars earned, shipped totals, and remaining budget.
/// Hook the TMP_Text references in the Inspector.
/// </summary>
public class LevelSummaryUI : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] TMP_Text starsEarnedText;
    [SerializeField] TMP_Text totalShippedText;
    [SerializeField] TMP_Text budgetText;

    public void Refresh()
    {
        // Stars earned (main goal + bonus objectives)
        var goalManager = FindFirstObjectByType<GoalManager>();
        if (starsEarnedText != null)
        {
            if (goalManager != null)
            {
                goalManager.GetStarCounts(out int earned, out int total);
                starsEarnedText.text = $"{earned} / {total}";
            }
            else
            {
                starsEarnedText.text = "-";
            }
        }

        // Total shipped (item type counts)
        if (totalShippedText != null)
            totalShippedText.text = LevelStats.BuildShippedSummaryString();

        // Budget (money remaining)
        if (budgetText != null)
        {
            int money = GameManager.Instance != null ? GameManager.Instance.Money : 0;
            budgetText.text = money.ToString();
        }
    }

    public void ShowAndRefresh()
    {
        gameObject.SetActive(true);
        Refresh();
    }
}
