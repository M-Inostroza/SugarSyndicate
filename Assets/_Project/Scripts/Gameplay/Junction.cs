using UnityEngine;

public class Junction : MonoBehaviour, IConveyor
{
    public Direction inA;
    public Direction inB;
    public Direction outA;
    public Direction outB;

    bool toggle;

    public Vector2Int DirVec() => DirectionUtil.DirVec(outA);

    public Direction SelectOutput()
    {
        if (outA == outB) return outA;
        toggle = !toggle;
        return toggle ? outA : outB;
    }
}
