using UnityEngine;

public class GoalManager : MonoBehaviour
{
    [Header("Goal")]
    [SerializeField, Min(1)] int sugarBlockTarget = 20;

    [Header("State")]
    [SerializeField] bool autoCompleteOnTarget = true;

    int sugarBlocksDelivered;
    bool completed;

    void OnEnable()
    {
        Truck.OnSugarBlockDelivered += OnSugarBlockDelivered;
    }

    void OnDisable()
    {
        Truck.OnSugarBlockDelivered -= OnSugarBlockDelivered;
    }

    void OnSugarBlockDelivered(int total)
    {
        if (completed) return;
        sugarBlocksDelivered = total;
        Debug.Log($"[GoalManager] Sugar blocks delivered: {sugarBlocksDelivered}/{sugarBlockTarget}");
        if (autoCompleteOnTarget && sugarBlocksDelivered >= sugarBlockTarget)
        {
            completed = true;
            Debug.Log("[GoalManager] Goal complete.");
            // TODO: hook into your level complete flow.
        }
    }
}
