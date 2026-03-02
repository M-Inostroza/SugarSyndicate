using System.Collections.Generic;
using UnityEngine;

public static class PowerNodeUtil
{
    static readonly Vector2Int[] PoleNeighborDirs =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1),
    };

    public static bool IsConnectableNode(Component component)
    {
        if (component == null) return false;
        if (!component.gameObject.activeInHierarchy) return false;
        if (component is IGhostState ghost && ghost.IsGhost) return false;
        return component is PowerPole || component is IPowerSourceNode || component is IPowerConsumer;
    }

    public static bool TryGetNodeCell(Component component, out Vector2Int cell)
    {
        cell = default;
        if (component == null) return false;

        if (component is IPowerSourceNode sourceNode)
        {
            foreach (var sourceCell in sourceNode.PowerCells)
            {
                cell = sourceCell;
                return true;
            }
        }

        if (component is IMachine machine)
        {
            cell = machine.Cell;
            return true;
        }

        if (component is IPowerTerminal terminal)
        {
            foreach (var terminalCell in terminal.PowerCells)
            {
                cell = terminalCell;
                return true;
            }
        }

        var grid = GridService.Instance;
        if (grid == null) return false;
        cell = grid.WorldToCell(component.transform.position);
        return true;
    }

    public static Vector3 GetNodeWorldPosition(Component component, float z)
    {
        if (component == null) return Vector3.zero;
        var grid = GridService.Instance;
        if (grid != null && TryGetNodeCell(component, out var cell))
            return grid.CellToWorld(cell, z);
        var world = component.transform.position;
        world.z = z;
        return world;
    }

    public static Component FindConnectableNodeAtCell(Vector2Int cell)
    {
        var poles = Object.FindObjectsByType<PowerPole>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < poles.Length; i++)
        {
            var pole = poles[i];
            if (pole == null || !IsConnectableNode(pole)) continue;
            if (TryGetNodeCell(pole, out var poleCell) && poleCell == cell)
                return pole;
        }

        var allBehaviours = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < allBehaviours.Length; i++)
        {
            var behaviour = allBehaviours[i];
            if (behaviour == null) continue;
            if (behaviour is PowerPole) continue;
            if (!IsConnectableNode(behaviour)) continue;
            if (!TryGetNodeCell(behaviour, out var behaviourCell)) continue;
            if (behaviourCell == cell)
                return behaviour;
        }

        return null;
    }

    public static bool TryBuildCableCells(Component startNode, Component endNode, out List<Vector2Int> cableCells)
    {
        cableCells = null;
        if (!TryGetNodeCell(startNode, out var startCellRaw)) return false;
        if (!TryGetNodeCell(endNode, out var endCellRaw)) return false;

        var startCell = startCellRaw;
        var endCell = endCellRaw;

        bool startIsPole = startNode is PowerPole;
        bool endIsPole = endNode is PowerPole;

        if (startIsPole)
        {
            if (!TryGetPoleAttachmentCell(startCellRaw, endCellRaw, endIsPole, out startCell))
                return false;
        }
        if (endIsPole)
        {
            if (!TryGetPoleAttachmentCell(endCellRaw, startCell, false, out endCell))
                return false;
        }

        if (startCell == endCell)
        {
            if (startIsPole ^ endIsPole)
            {
                cableCells = new List<Vector2Int> { startCell };
                return true;
            }
            return false;
        }
        cableCells = BuildCardinalPath(startCell, endCell);
        return cableCells != null && cableCells.Count > 0;
    }

    static bool TryGetPoleAttachmentCell(Vector2Int poleCell, Vector2Int targetCell, bool targetIsPole, out Vector2Int attachmentCell)
    {
        attachmentCell = poleCell;
        var grid = GridService.Instance;
        var power = PowerService.Instance ?? PowerService.EnsureInstance();

        float bestScore = float.MaxValue;
        bool found = false;
        Vector2Int fallback = poleCell;
        bool hasFallback = false;

        for (int i = 0; i < PoleNeighborDirs.Length; i++)
        {
            var candidate = poleCell + PoleNeighborDirs[i];
            if (grid != null && !grid.InBounds(candidate))
                continue;
            if (targetIsPole && candidate == targetCell)
                continue;

            float score = Mathf.Abs(candidate.x - targetCell.x) + Mathf.Abs(candidate.y - targetCell.y);
            if (!hasFallback || score < bestScore)
            {
                fallback = candidate;
                hasFallback = true;
            }

            bool occupied = power != null && power.IsCellOccupiedOrBlueprint(candidate);
            if (occupied)
                continue;

            if (!found || score < bestScore)
            {
                bestScore = score;
                attachmentCell = candidate;
                found = true;
            }
        }

        if (found) return true;
        if (hasFallback)
        {
            attachmentCell = fallback;
            return false;
        }

        return false;
    }

    static List<Vector2Int> BuildCardinalPath(Vector2Int start, Vector2Int end)
    {
        var raw = BuildBresenham(start, end);
        if (raw.Count == 0) return raw;

        var result = new List<Vector2Int>(raw.Count * 2);
        result.Add(raw[0]);
        for (int i = 1; i < raw.Count; i++)
        {
            var prev = result[result.Count - 1];
            var next = raw[i];
            int dx = next.x - prev.x;
            int dy = next.y - prev.y;

            if (Mathf.Abs(dx) == 1 && Mathf.Abs(dy) == 1)
            {
                var bridge = new Vector2Int(prev.x + dx, prev.y);
                if (bridge != prev)
                    result.Add(bridge);
            }

            if (result[result.Count - 1] != next)
                result.Add(next);
        }

        return result;
    }

    static List<Vector2Int> BuildBresenham(Vector2Int start, Vector2Int end)
    {
        var points = new List<Vector2Int>();
        int x0 = start.x;
        int y0 = start.y;
        int x1 = end.x;
        int y1 = end.y;

        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            points.Add(new Vector2Int(x0, y0));
            if (x0 == x1 && y0 == y1) break;

            int e2 = err * 2;
            if (e2 > -dy)
            {
                err -= dy;
                x0 += sx;
            }
            if (e2 < dx)
            {
                err += dx;
                y0 += sy;
            }
        }

        return points;
    }
}
