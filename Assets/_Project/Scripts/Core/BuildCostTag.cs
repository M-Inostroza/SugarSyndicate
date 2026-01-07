using UnityEngine;

/// <summary>
/// Stores the build cost of a placed object so deletion can refund accurately.
/// </summary>
public class BuildCostTag : MonoBehaviour
{
    [SerializeField, Min(0)] int cost;

    public int Cost
    {
        get => cost;
        set => cost = Mathf.Max(0, value);
    }
}
