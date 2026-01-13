using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

public class Repairable : DroneTaskTarget
{
    [SerializeField, Min(0.1f)] float repairSeconds = 2f;
    [SerializeField] Color brokenTint = new Color(1f, 0.3f, 0.3f, 0.8f);

    bool isBroken;
    Vector2Int[] footprintCells = new Vector2Int[0];
    readonly List<Behaviour> disabledBehaviours = new List<Behaviour>();
    List<Color> cachedColors;
    List<SpriteRenderer> cachedRenderers;

    public bool IsBroken => isBroken;

    public void Initialize(Vector2Int[] footprint)
    {
        footprintCells = footprint ?? new Vector2Int[0];
    }

    public void Break()
    {
        if (isBroken) return;
        isBroken = true;
        MarkBrokenCells(true);
        CacheOriginalColors();
        ApplyBrokenTint();
        DisableMachineBehaviours();

        BeginTask(DroneTaskType.Repair, repairSeconds, DroneTaskPriority.Priority, ComputeFootprintCenter());
    }

    void OnMouseDown()
    {
        if (!isBroken) return;
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        TogglePriority();
    }

    protected override void OnTaskCompleted()
    {
        if (!isBroken) return;
        isBroken = false;
        MarkBrokenCells(false);
        RestoreOriginalColors();
        EnableMachineBehaviours();

        var belt = BeltSimulationService.Instance;
        if (belt != null)
        {
            foreach (var c in footprintCells)
                belt.RegisterCell(c);
        }
    }

    void MarkBrokenCells(bool broken)
    {
        var grid = GridService.Instance;
        if (grid == null) return;
        foreach (var c in footprintCells)
        {
            var cell = grid.GetCell(c);
            if (cell == null) continue;
            cell.isBroken = broken;
        }
    }

    Vector3 ComputeFootprintCenter()
    {
        var grid = GridService.Instance;
        if (grid == null || footprintCells == null || footprintCells.Length == 0)
            return transform.position;

        Vector3 sum = Vector3.zero;
        foreach (var c in footprintCells)
            sum += grid.CellToWorld(c, transform.position.z);
        return sum / Mathf.Max(1, footprintCells.Length);
    }

    void CacheOriginalColors()
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        if (srs == null || srs.Length == 0) return;
        cachedRenderers = new List<SpriteRenderer>(srs.Length);
        cachedColors = new List<Color>(srs.Length);
        foreach (var sr in srs)
        {
            if (sr == null) continue;
            cachedRenderers.Add(sr);
            cachedColors.Add(sr.color);
        }
    }

    void ApplyBrokenTint()
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        if (srs == null || srs.Length == 0) return;
        foreach (var sr in srs)
        {
            if (sr == null) continue;
            var baseCol = sr.color;
            sr.color = new Color(baseCol.r * brokenTint.r, baseCol.g * brokenTint.g, baseCol.b * brokenTint.b, Mathf.Clamp01(brokenTint.a));
        }
    }

    void RestoreOriginalColors()
    {
        if (cachedRenderers == null || cachedColors == null) return;
        for (int i = 0; i < cachedRenderers.Count && i < cachedColors.Count; i++)
        {
            var sr = cachedRenderers[i];
            if (sr == null) continue;
            sr.color = cachedColors[i];
        }
    }

    void DisableMachineBehaviours()
    {
        disabledBehaviours.Clear();
        var behaviours = GetComponentsInChildren<Behaviour>(true);
        foreach (var behaviour in behaviours)
        {
            if (behaviour == null || behaviour == this) continue;
            if (!ShouldDisable(behaviour)) continue;
            if (!behaviour.enabled) continue;
            behaviour.enabled = false;
            disabledBehaviours.Add(behaviour);
        }
    }

    void EnableMachineBehaviours()
    {
        foreach (var behaviour in disabledBehaviours)
        {
            if (behaviour == null) continue;
            behaviour.enabled = true;
        }
        disabledBehaviours.Clear();
    }

    bool ShouldDisable(Behaviour behaviour)
    {
        if (behaviour is IMachine) return true;
        if (behaviour is SugarMine) return true;
        if (behaviour is WaterPipe) return true;
        return false;
    }
}
