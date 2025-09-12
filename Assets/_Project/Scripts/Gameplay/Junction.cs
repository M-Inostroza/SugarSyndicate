using UnityEngine;

// Junctions behave like conveyors with multiple configured inputs/outputs.
// Inherit from Conveyor so they register with grid and simulation services
// automatically, but override direction handling for custom routing logic.
public class Junction : Conveyor
{
    public Direction inA;
    public Direction inB;
    public Direction outA;
    public Direction outB;

    bool toggle;

    // Use the first output as our canonical direction so neighbouring cells
    // can query orientation when pulling items.
    public new Vector2Int DirVec() => DirectionUtil.DirVec(outA);

    public Direction SelectOutput()
    {
        if (outA == outB) return outA;
        toggle = !toggle;
        return toggle ? outA : outB;
    }
}
