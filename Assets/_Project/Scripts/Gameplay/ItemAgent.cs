using System;
using UnityEngine;

public class ItemAgent : MonoBehaviour
{
    Vector2Int preferredDir;
    Action<ItemAgent> releaseCallback;

    // movement state
    Vector2Int currentCell;
    Vector2Int targetCell;
    Vector3 fromWorld;
    Vector3 toWorld;
    int ticksPerCell = 4;
    int tickCounter;

    bool active;

    public Vector2Int CurrentCell => currentCell; // expose for external queries

    // Maintain compatibility: optional release callback parameter
    public void SpawnAt(Vector3 worldPos, Vector2Int dir, Action<ItemAgent> release = null, int ticksPerCellOverride = 0)
    {
        transform.position = worldPos;
        preferredDir = dir; // store for your conveyor logic if needed
        releaseCallback = release;
        gameObject.SetActive(true);

        // init grid movement
        if (GridService.Instance != null)
        {
            currentCell = GridService.Instance.WorldToCell(worldPos);
            fromWorld = GridService.Instance.CellToWorld(currentCell, transform.position.z);

            // If spawned on a conveyor, prefer its direction
            if (TryGetConveyorDir(currentCell, out var cellDir))
            {
                preferredDir = cellDir;
            }

            // only start moving if the next cell (according to effective dir) has a conveyor and is free (pulled onto belt)
            var next = currentCell + preferredDir;
            var canAdvance = false;
            if (GridService.Instance.InBounds(next))
            {
                var nextData = GridService.Instance.GetCell(next);
                bool nextFree = nextData == null || nextData.itemCount == 0;

                bool nextIsConveyor = IsCellConveyor(next);

                if (nextFree && nextIsConveyor)
                    canAdvance = true;
            }

            if (canAdvance)
            {
                targetCell = currentCell + preferredDir;
                toWorld = GridService.Instance.CellToWorld(targetCell, transform.position.z);
            }
            else
            {
                targetCell = currentCell;
                toWorld = fromWorld;
            }
        }
        else
        {
            // fallback if no GridService: move by dir in world space by 1 unit
            currentCell = Vector2Int.zero;
            fromWorld = worldPos;
            toWorld = worldPos + new Vector3(dir.x, dir.y, 0);
            targetCell = currentCell + dir;
        }

        ticksPerCell = ticksPerCellOverride > 0 ? ticksPerCellOverride : ticksPerCell;
        tickCounter = 0;
        active = true;

        GameTick.OnTick += OnGameTick;

        // register occupancy
        var cell = GridService.Instance?.GetCell(currentCell);
        if (cell != null) cell.itemCount++;
    }

    void Update()
    {
        if (!active) return;
        // smooth interpolate between fromWorld and toWorld based on tick progress
        float prog = ticksPerCell <= 0 ? 1f : (float)tickCounter / ticksPerCell;
        transform.position = Vector3.Lerp(fromWorld, toworldIfNull(toWorld, fromWorld), Mathf.Clamp01(prog));
    }

    // helper to avoid null Vector3 (defensive)
    static Vector3 toworldIfNull(Vector3 maybe, Vector3 @default) => maybe == default ? @default : maybe;

    void OnGameTick()
    {
        if (!active) return;
        tickCounter++;
        if (tickCounter < ticksPerCell) return;

        // arrival at targetCell
        tickCounter = 0;

        // If targetCell was out of bounds, Release
        if (GridService.Instance != null && !GridService.Instance.InBounds(targetCell))
        {
            ReleaseToPool();
            return;
        }

        // update counts: leave prev cell, enter targetCell
        var prev = GridService.Instance?.GetCell(currentCell);
        if (prev != null) prev.itemCount = Mathf.Max(0, prev.itemCount - 1);

        currentCell = targetCell;
        var cur = GridService.Instance?.GetCell(currentCell);
        if (cur != null) cur.itemCount++;

        // Determine effective direction: conveyor on current cell overrides preferredDir
        var effectiveDir = preferredDir;
        if (TryGetConveyorDir(currentCell, out var cellDir))
        {
            effectiveDir = cellDir;
        }

        // prepare next target based on conveyors and occupancy
        var next = currentCell + effectiveDir;
        fromWorld = toWorld;

        if (GridService.Instance == null)
        {
            toWorld = fromWorld + new Vector3(effectiveDir.x, effectiveDir.y, 0);
            targetCell = next;
            return;
        }

        bool canAdvance = false;
        if (GridService.Instance.InBounds(next))
        {
            var nextData = GridService.Instance.GetCell(next);
            bool nextFree = nextData == null || nextData.itemCount == 0;
            bool nextIsConveyor = IsCellConveyor(next);

            // Only advance if the next cell itself is a conveyor (prevents stepping off last belt)
            if (nextFree && nextIsConveyor)
                canAdvance = true;
        }

        if (!canAdvance)
        {
            // Can't advance: stop at current cell
            targetCell = currentCell;
            toWorld = GridService.Instance.CellToWorld(currentCell, transform.position.z);
            return;
        }

        // advance into next
        targetCell = next;
        toWorld = GridService.Instance.CellToWorld(targetCell, transform.position.z);
    }

    // Call from item logic when this item should be returned to the pool
    public void ReleaseToPool()
    {
        if (!active) return;
        active = false;
        GameTick.OnTick -= OnGameTick;

        // cleanup occupancy
        var cell = GridService.Instance?.GetCell(currentCell);
        if (cell != null) cell.itemCount = Mathf.Max(0, cell.itemCount - 1);

        // Notify the pool; Pool.Release will deactivate and store the object
        releaseCallback?.Invoke(this);
        releaseCallback = null;

        // If not pooled, ensure deactivation
        gameObject.SetActive(false);
    }

    bool IsCellConveyor(Vector2Int cell)
    {
        var gs = GridService.Instance;
        if (gs == null) return false;
        var data = gs.GetCell(cell);
        if (data != null && data.hasConveyor) return true;
        // fallback: check for a Conveyor component at the cell center
        var world = gs.CellToWorld(cell, transform.position.z);
        var col = Physics2D.OverlapPoint((Vector2)world);
        if (col != null)
        {
            if (col.GetComponent<Conveyor>() != null || col.transform.GetComponentInParent<Conveyor>() != null)
                return true;
        }
        // fallback 2: iterate conveyors and compare their cell positions
        var conveyors = UnityEngine.Object.FindObjectsByType<Conveyor>(FindObjectsSortMode.None);
        foreach (var c in conveyors)
        {
            var ccell = gs.WorldToCell(c.transform.position);
            if (ccell == cell) return true;
        }
        return false;
    }

    bool TryGetConveyorDir(Vector2Int cell, out Vector2Int dir)
    {
        dir = preferredDir;
        var gs = GridService.Instance;
        if (gs == null) return false;
        // look for Conveyor components at that cell
        var conveyors = UnityEngine.Object.FindObjectsByType<Conveyor>(FindObjectsSortMode.None);
        foreach (var c in conveyors)
        {
            var ccell = gs.WorldToCell(c.transform.position);
            if (ccell == cell)
            {
                dir = c.DirVec();
                return true;
            }
        }
        return false;
    }

    void OnDisable()
    {
        // Safety cleanup
        GameTick.OnTick -= OnGameTick;
        releaseCallback = null;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        // Snap to cell centers in editor when not playing
        if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
        var gs = UnityEngine.Object.FindFirstObjectByType<GridService>();
        if (gs == null) return;
        var c = gs.WorldToCell(transform.position);
        var world = gs.CellToWorld(c, transform.position.z);
        transform.position = world;
    }
#endif
}
