using UnityEngine;

public interface IGridService
{
    Vector2Int WorldToCell(Vector3 world);
    Vector3 CellToWorld(Vector2Int cell, float height);
}

public interface IConveyor
{
    Vector2Int DirVec();
}
