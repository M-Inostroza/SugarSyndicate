using System;
using UnityEngine;
using UnityEngine.EventSystems;

public class PowerBuildManager : MonoBehaviour
{
    [Header("Prefabs & Parents")]
    [SerializeField] GameObject cablePrefab;
    [SerializeField] GameObject polePrefab;
    [SerializeField] Transform placeParent;

    [Header("Placement")]
    [SerializeField] bool allowCableDrag = true;

    enum Mode { None, Cable, Pole }
    Mode mode = Mode.None;

    Camera cam;
    bool isMouseDown;
    Vector2Int lastPlacedCell;

    void Awake()
    {
        cam = Camera.main;
        if (cam == null) cam = Camera.current;
    }

    void Update()
    {
        if (mode == Mode.None) return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            StopPlacing();
            return;
        }

        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        if (Input.GetMouseButtonDown(0))
        {
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            var cell = GetMouseCell();
            if (!cell.HasValue) return;

            isMouseDown = true;
            lastPlacedCell = cell.Value;

            if (mode == Mode.Pole)
            {
                TryPlacePole(cell.Value);
                return;
            }

            TryPlaceCable(cell.Value);
        }

        if (mode == Mode.Cable && allowCableDrag && isMouseDown && Input.GetMouseButton(0))
        {
            var cell = GetMouseCell();
            if (!cell.HasValue) return;
            if (cell.Value == lastPlacedCell) return;

            Vector2Int from = lastPlacedCell;
            Vector2Int to = cell.Value;
            while (from != to)
            {
                var delta = to - from;
                Vector2Int step;
                if (Mathf.Abs(delta.x) >= Mathf.Abs(delta.y))
                    step = new Vector2Int(Math.Sign(delta.x), 0);
                else
                    step = new Vector2Int(0, Math.Sign(delta.y));

                var next = from + step;
                if (Mathf.Abs(next.x - from.x) + Mathf.Abs(next.y - from.y) != 1) break;

                if (!TryPlaceCable(next))
                    break;

                from = next;
                lastPlacedCell = from;

                if (from == to) break;
            }
        }

        if (isMouseDown && Input.GetMouseButtonUp(0))
            isMouseDown = false;
    }

    public void StartPlacingCable()
    {
        mode = Mode.Cable;
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        try { BuildModeController.SetToolActive(true); } catch { }
        BuildSelectionNotifier.Notify("PowerCable");
    }

    public void StartPlacingPole()
    {
        mode = Mode.Pole;
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        try { BuildModeController.SetToolActive(true); } catch { }
        BuildSelectionNotifier.Notify("PowerPole");
    }

    public void StopPlacing()
    {
        mode = Mode.None;
        isMouseDown = false;
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Play);
        try { BuildModeController.SetToolActive(false); } catch { }
        BuildSelectionNotifier.Notify(null);
    }

    Vector2Int? GetMouseCell()
    {
        var gs = GridService.Instance;
        if (gs == null) return null;
        var worldPoint = cam.ScreenToWorldPoint(Input.mousePosition);
        worldPoint.z = 0f;
        var cell = gs.WorldToCell(worldPoint);
        if (!gs.InBounds(cell)) return null;
        return cell;
    }

    bool TryPlaceCable(Vector2Int cell)
    {
        if (!CanPlaceAt(cell)) return false;
        if (cablePrefab == null)
        {
            Debug.LogWarning("[PowerBuildManager] Cable prefab not assigned.");
            return false;
        }

        SpawnAt(cell, cablePrefab);
        return true;
    }

    bool TryPlacePole(Vector2Int cell)
    {
        if (!CanPlaceAt(cell)) return false;
        if (polePrefab == null)
        {
            Debug.LogWarning("[PowerBuildManager] Pole prefab not assigned.");
            return false;
        }

        SpawnAt(cell, polePrefab);
        return true;
    }

    bool CanPlaceAt(Vector2Int cell)
    {
        var power = PowerService.Instance;
        if (power != null && power.IsCellOccupied(cell)) return false;
        return true;
    }

    void SpawnAt(Vector2Int cell, GameObject prefab)
    {
        var gs = GridService.Instance;
        if (gs == null) return;
        float z = prefab.transform.position.z;
        var pos = gs.CellToWorld(cell, z);
        var parent = placeParent != null ? placeParent : null;
        Instantiate(prefab, pos, Quaternion.identity, parent);
    }
}
