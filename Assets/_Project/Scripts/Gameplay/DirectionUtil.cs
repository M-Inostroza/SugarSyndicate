using UnityEngine;

public enum Direction { Up, Right, Down, Left }

public static class DirectionUtil
{
    public static Vector2Int DirVec(Direction d) => d switch
    {
        Direction.Up => new Vector2Int(0, 1),
        Direction.Right => new Vector2Int(1, 0),
        Direction.Down => new Vector2Int(0, -1),
        _ => new Vector2Int(-1, 0),
    };
}