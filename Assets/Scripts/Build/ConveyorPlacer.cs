using UnityEngine;

// Simple utility for instantiating conveyors or junctions in the scene.
// The previous implementation depended on the removed BeltGraphService;
// this version works with the new cell-based BeltSimulationService.
public class ConveyorPlacer : MonoBehaviour
{
    public Conveyor conveyorPrefab;
    public Junction junctionPrefab;

    public void PlaceConveyor(Vector3 worldPosition, Direction direction)
    {
        if (conveyorPrefab == null) return;
        var conv = Instantiate(conveyorPrefab, worldPosition, Quaternion.identity);
        conv.direction = direction;
        BeltSimulationService.Instance?.RegisterConveyor(conv);
    }

    public void PlaceJunction(Vector3 worldPosition, Direction inA, Direction inB, Direction outA, Direction outB)
    {
        if (junctionPrefab == null) return;
        var j = Instantiate(junctionPrefab, worldPosition, Quaternion.identity);
        j.inA = inA;
        j.inB = inB;
        j.outA = outA;
        j.outB = outB;
        BeltSimulationService.Instance?.RegisterConveyor(j);
    }
}
