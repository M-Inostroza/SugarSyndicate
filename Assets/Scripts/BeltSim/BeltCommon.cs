using System;
using System.Collections.Generic;
using UnityEngine;

public enum Direction2D { Up, Right, Down, Left }

public static class Dir2D
{
    public static Vector2Int ToVec(Direction2D d)
    {
        switch (d)
        {
            case Direction2D.Up: return Vector2Int.up;
            case Direction2D.Right: return Vector2Int.right;
            case Direction2D.Down: return Vector2Int.down;
            default: return Vector2Int.left;
        }
    }
}

// Simple belt tile descriptor (placed elsewhere in your game)
public struct BeltTile
{
    public Vector2Int cell;
    public Direction2D dir;
    // Optional identifier used to pair special endpoints such as tunnels
    public int tunnelId;
}

// Lightweight runtime item representation (id/payload optional)
public struct BeltItem
{
    public int id;
    public float offset; // along run; maintained by BeltRun
}

// Strongly typed service hooks so callers can avoid reflection when interacting
// with the belt simulation. External grid and conveyor implementations can
// simply implement these interfaces.
public interface IGridService
{
    Vector2Int WorldToCell(Vector3 world);
    Vector3 CellToWorld(Vector2Int cell, float height);
}

public interface IConveyor
{
    /// <summary>Unit vector describing the forward direction of the conveyor.</summary>
    Vector2Int DirVec();
}
