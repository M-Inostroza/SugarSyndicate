using System.Collections.Generic;
using UnityEngine;

public class UndergroundViewController : MonoBehaviour
{
    [Header("Activation")]
    [SerializeField] string[] showOnSelections = { "PowerCable" };
    [SerializeField] bool matchSubstring = false;

    [Header("Scene Roots")]
    [SerializeField] GameObject[] surfaceRoots;
    [SerializeField] GameObject[] undergroundRoots;

    [Header("Markers")]
    [SerializeField] bool showMachineMarkers = true;
    [SerializeField] GameObject markerPrefab;
    [SerializeField] Transform markerParent;
    [SerializeField] float markerZ = 0f;
    [SerializeField] bool includeBrokenMarkers = true;

    GridService grid;
    BuildModeController buildModeController;
    bool isUndergroundActive;
    readonly Dictionary<GameObject, bool> surfaceStates = new();
    readonly Dictionary<GameObject, bool> undergroundStates = new();
    readonly List<GameObject> markerPool = new();
    bool warnedMissingMarker;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
    }

    void OnEnable()
    {
        BuildSelectionNotifier.OnSelectionChanged += HandleSelectionChanged;
        TryHookBuildModeController();
    }

    void OnDisable()
    {
        BuildSelectionNotifier.OnSelectionChanged -= HandleSelectionChanged;
        if (buildModeController != null)
            buildModeController.onExitBuildMode -= HandleBuildModeExit;
        HideUnderground();
    }

    void HandleSelectionChanged(string selectionName)
    {
        if (ShouldShowForSelection(selectionName))
            ShowUnderground();
        else
            HideUnderground();
    }

    void HandleBuildModeExit()
    {
        HideUnderground();
    }

    bool ShouldShowForSelection(string selectionName)
    {
        if (string.IsNullOrEmpty(selectionName)) return false;
        if (showOnSelections == null || showOnSelections.Length == 0) return false;
        for (int i = 0; i < showOnSelections.Length; i++)
        {
            var target = showOnSelections[i];
            if (string.IsNullOrEmpty(target)) continue;
            if (matchSubstring)
            {
                if (selectionName.IndexOf(target, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            else if (string.Equals(selectionName, target, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    void ShowUnderground()
    {
        if (isUndergroundActive) return;
        isUndergroundActive = true;
        CacheAndSetActive(surfaceRoots, surfaceStates, false);
        CacheAndSetActive(undergroundRoots, undergroundStates, true);
        if (showMachineMarkers)
            RebuildMarkers();
    }

    void HideUnderground()
    {
        if (!isUndergroundActive) return;
        isUndergroundActive = false;
        RestoreStates(surfaceStates);
        RestoreStates(undergroundStates);
        DisableMarkers();
    }

    void CacheAndSetActive(GameObject[] roots, Dictionary<GameObject, bool> cache, bool active)
    {
        if (roots == null) return;
        for (int i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root == null) continue;
            if (!cache.ContainsKey(root)) cache[root] = root.activeSelf;
            root.SetActive(active);
        }
    }

    void RestoreStates(Dictionary<GameObject, bool> cache)
    {
        foreach (var kv in cache)
        {
            if (kv.Key == null) continue;
            kv.Key.SetActive(kv.Value);
        }
        cache.Clear();
    }

    void RebuildMarkers()
    {
        if (markerPrefab == null)
        {
            if (!warnedMissingMarker)
            {
                Debug.LogWarning("[UndergroundViewController] Missing markerPrefab; skipping building markers.");
                warnedMissingMarker = true;
            }
            return;
        }

        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;

        var size = grid.GridSize;
        int used = 0;
        for (int y = 0; y < size.y; y++)
        {
            for (int x = 0; x < size.x; x++)
            {
                var cellPos = new Vector2Int(x, y);
                var cell = grid.GetCell(cellPos);
                if (cell == null) continue;
                if (cell.type != GridService.CellType.Machine) continue;
                if (!includeBrokenMarkers && cell.isBroken) continue;
                var marker = GetMarker(used++);
                marker.transform.position = grid.CellToWorld(cellPos, markerZ);
                marker.SetActive(true);
            }
        }
        DisableMarkers(used);
    }

    GameObject GetMarker(int index)
    {
        if (index < markerPool.Count) return markerPool[index];
        var parent = markerParent != null ? markerParent : transform;
        var marker = Instantiate(markerPrefab, parent);
        markerPool.Add(marker);
        return marker;
    }

    void DisableMarkers(int startIndex = 0)
    {
        for (int i = startIndex; i < markerPool.Count; i++)
        {
            var marker = markerPool[i];
            if (marker != null) marker.SetActive(false);
        }
    }

    void TryHookBuildModeController()
    {
        if (buildModeController != null) return;
        try
        {
            buildModeController = FindAnyObjectByType<BuildModeController>();
            if (buildModeController != null)
                buildModeController.onExitBuildMode += HandleBuildModeExit;
        }
        catch { }
    }
}
