using UnityEngine;

public enum Direction { Up = 0, Right = 1, Down = 2, Left = 3, None = 4 }

public static class DirectionUtil
{
    public static Vector2Int DirVec(Direction d) => d switch
    {
        Direction.Up => new Vector2Int(0, 1),
        Direction.Right => new Vector2Int(1, 0),
        Direction.Down => new Vector2Int(0, -1),
        Direction.Left => new Vector2Int(-1, 0),
        _ => Vector2Int.zero,
    };

    public static Direction Opposite(Direction d) => d switch
    {
        Direction.Up => Direction.Down,
        Direction.Right => Direction.Left,
        Direction.Down => Direction.Up,
        Direction.Left => Direction.Right,
        _ => Direction.None,
    };

    public static bool IsCardinal(Direction d) => d == Direction.Up || d == Direction.Right || d == Direction.Down || d == Direction.Left;
}