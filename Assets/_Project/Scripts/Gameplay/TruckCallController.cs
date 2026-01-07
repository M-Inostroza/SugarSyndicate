using UnityEngine;

/// <summary>
/// Button-friendly controller to activate trucks so they start accepting items.
/// Hook UI Button.onClick to CallTrucks().
/// </summary>
public class TruckCallController : MonoBehaviour
{
    public void CallTrucks()
    {
        Truck.SetTrucksCalled(true);
    }

    public void RecallTrucks()
    {
        Truck.SetTrucksCalled(false);
    }
}
