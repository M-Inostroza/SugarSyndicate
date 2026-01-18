using System.Collections.Generic;
using UnityEngine;

public class UndergroundViewController : MonoBehaviour
{
    [Header("Activation")]
    [SerializeField] string[] showOnSelections = { "PowerCable", "PowerPole" };
    [SerializeField] bool matchSubstring = false;

    [Header("Scene Roots")]
    [SerializeField] GameObject[] surfaceRoots;
    [SerializeField] GameObject[] undergroundRoots;

    [Header("Markers")]
    [SerializeField] GameObject markerPrefab;
    [SerializeField] Transform markerParent;
    [SerializeField] float markerZ = 0f;
    [SerializeField] bool includeBrokenMarkers = true;
    [SerializeField] bool scaleMarkerToCell = true;
    [SerializeField, Range(0.1f, 1f)] float markerCellFill = 0.9f;

    [Header("Surface Hiding")]
    [SerializeField] bool tintSurfaceMachines = true;
    [SerializeField] bool hideSurfaceMachineRenderers = true;
    [SerializeField] bool hideSurfaceBelts = true;
    [SerializeField] bool hideSurfaceItems = true;
    [SerializeField] bool hideSurfacePowerLines = true;
    [SerializeField] bool includeInactiveMachines = true;
    [SerializeField] bool keepPowerSourcesVisible = true;
    [SerializeField] Color machineTint = new Color(1f, 0.9f, 0.2f, 0.5f);

    [Header("Backgrounds")]
    [SerializeField] GameObject dayBackground;
    [SerializeField] GameObject undergroundBackground;

    GridService grid;
    BuildModeController buildModeController;
    bool isUndergroundActive;
    bool manualOverride;
    string lastSelectionName;
    readonly Dictionary<GameObject, bool> surfaceStates = new();
    readonly Dictionary<GameObject, bool> undergroundStates = new();
    readonly List<GameObject> markerPool = new();
    readonly List<Vector3> markerBaseScales = new();
    readonly List<RendererState> hiddenRenderers = new();
    readonly List<RendererState> hiddenBeltRenderers = new();
    readonly List<RendererState> hiddenPowerLineRenderers = new();
    readonly List<TintState> tintedRenderers = new();
    bool warnedMissingMarker;

    struct RendererState
    {
        public Renderer renderer;
        public bool enabled;

        public RendererState(Renderer renderer, bool enabled)
        {
            this.renderer = renderer;
            this.enabled = enabled;
        }
    }

    struct TintState
    {
        public SpriteRenderer renderer;
        public Color color;

        public TintState(SpriteRenderer renderer, Color color)
        {
            this.renderer = renderer;
            this.color = color;
        }
    }

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
    }

    void Start()
    {
        if (hideSurfacePowerLines)
            HideSurfacePowerLineRenderers();
        SetBackgroundState(isUndergroundActive);
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
        lastSelectionName = selectionName;
        if (manualOverride) return;
        if (ShouldShowForSelection(selectionName))
            ShowUnderground();
        else
            HideUnderground();
    }

    void HandleBuildModeExit()
    {
        if (manualOverride) return;
        HideUnderground();
    }

    public void ToggleUndergroundView()
    {
        manualOverride = !manualOverride;
        if (manualOverride)
            ShowUnderground();
        else
            HandleSelectionChanged(lastSelectionName);
    }

    public void ShowUndergroundView()
    {
        manualOverride = true;
        ShowUnderground();
    }

    public void HideUndergroundView()
    {
        manualOverride = true;
        HideUnderground();
    }

    public void ClearManualOverride()
    {
        manualOverride = false;
        HandleSelectionChanged(lastSelectionName);
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
        if (tintSurfaceMachines)
            TintSurfaceMachineRenderers();
        else if (hideSurfaceMachineRenderers)
            HideSurfaceMachineRenderers();
        if (hideSurfaceBelts)
            HideSurfaceBeltRenderers();
        if (hideSurfaceItems)
            HideSurfaceItemContainer();
        SetBackgroundState(true);
        if (hideSurfacePowerLines)
            RestoreSurfacePowerLineRenderers();
        DisableMarkers();
    }

    void HideUnderground()
    {
        if (!isUndergroundActive) return;
        isUndergroundActive = false;
        RestoreStates(surfaceStates);
        RestoreStates(undergroundStates);
        RestoreSurfaceMachineTint();
        RestoreSurfaceMachineRenderers();
        EnsureSurfaceMachineRenderersEnabled();
        RestoreSurfaceBeltRenderers();
        EnsureSurfaceBeltRenderersEnabled();
        SetBackgroundState(false);
        if (hideSurfacePowerLines)
            HideSurfacePowerLineRenderers();
        else
            RestoreSurfacePowerLineRenderers();
        if (!hideSurfacePowerLines)
            EnsureSurfacePowerLineRenderersEnabled();
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
                ScaleMarker(marker, used - 1);
                marker.SetActive(true);
            }
        }
        DisableMarkers(used);
    }

    void HideSurfaceMachineRenderers()
    {
        hiddenRenderers.Clear();
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);

        var seen = new HashSet<Renderer>();
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!includeInactiveMachines && !behaviour.gameObject.activeInHierarchy) continue;
            if (keepPowerSourcesVisible && behaviour is IPowerSource) continue;
            if (!(behaviour is IMachine) && !(behaviour is IPowerConsumer)) continue;
            var renderers = behaviour.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null || !seen.Add(r)) continue;
                hiddenRenderers.Add(new RendererState(r, r.enabled));
                r.enabled = false;
            }
        }
    }

    void RestoreSurfaceMachineRenderers()
    {
        foreach (var state in hiddenRenderers)
        {
            if (state.renderer == null) continue;
            state.renderer.enabled = state.enabled;
        }
        hiddenRenderers.Clear();
    }

    void EnsureSurfaceMachineRenderersEnabled()
    {
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!behaviour.gameObject.activeInHierarchy) continue;
            if (!(behaviour is IMachine) && !(behaviour is IPowerConsumer)) continue;

            var renderers = behaviour.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!r.enabled) r.enabled = true;
            }
        }
    }

    void HideSurfaceBeltRenderers()
    {
        hiddenBeltRenderers.Clear();
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        var seen = new HashSet<Renderer>();
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!includeInactiveMachines && !behaviour.gameObject.activeInHierarchy) continue;
            if (!(behaviour is IConveyor)) continue;
            var renderers = behaviour.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null || !seen.Add(r)) continue;
                hiddenBeltRenderers.Add(new RendererState(r, r.enabled));
                r.enabled = false;
            }
        }
    }

    void RestoreSurfaceBeltRenderers()
    {
        foreach (var state in hiddenBeltRenderers)
        {
            if (state.renderer == null) continue;
            state.renderer.enabled = state.enabled;
        }
        hiddenBeltRenderers.Clear();
    }

    void EnsureSurfaceBeltRenderersEnabled()
    {
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!behaviour.gameObject.activeInHierarchy) continue;
            if (!(behaviour is IConveyor)) continue;

            var renderers = behaviour.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!r.enabled) r.enabled = true;
            }
        }
    }

    void HideSurfaceItemContainer()
    {
        var container = ContainerLocator.GetItemContainer();
        if (container == null) return;
        CacheAndSetActive(new[] { container.gameObject }, surfaceStates, false);
    }

    void SetBackgroundState(bool undergroundActive)
    {
        if (dayBackground != null) dayBackground.SetActive(!undergroundActive);
        if (undergroundBackground != null) undergroundBackground.SetActive(undergroundActive);
    }

    void HideSurfacePowerLineRenderers()
    {
        hiddenPowerLineRenderers.Clear();
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        var seen = new HashSet<Renderer>();
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!includeInactiveMachines && !behaviour.gameObject.activeInHierarchy) continue;
            if (!(behaviour is PowerCable) && !(behaviour is PowerPole)) continue;
            var renderers = behaviour.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null || !seen.Add(r)) continue;
                hiddenPowerLineRenderers.Add(new RendererState(r, r.enabled));
                r.enabled = false;
            }
        }
    }

    void RestoreSurfacePowerLineRenderers()
    {
        foreach (var state in hiddenPowerLineRenderers)
        {
            if (state.renderer == null) continue;
            state.renderer.enabled = state.enabled;
        }
        hiddenPowerLineRenderers.Clear();
    }

    void EnsureSurfacePowerLineRenderersEnabled()
    {
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!behaviour.gameObject.activeInHierarchy) continue;
            if (!(behaviour is PowerCable) && !(behaviour is PowerPole)) continue;

            var renderers = behaviour.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!r.enabled) r.enabled = true;
            }
        }
    }

    void TintSurfaceMachineRenderers()
    {
        tintedRenderers.Clear();
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        var seen = new HashSet<SpriteRenderer>();
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!includeInactiveMachines && !behaviour.gameObject.activeInHierarchy) continue;
            if (keepPowerSourcesVisible && behaviour is IPowerSource) continue;
            if (!(behaviour is IMachine) && !(behaviour is IPowerConsumer)) continue;
            var renderers = behaviour.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in renderers)
            {
                if (sr == null || !seen.Add(sr)) continue;
                tintedRenderers.Add(new TintState(sr, sr.color));
                sr.color = new Color(sr.color.r * machineTint.r, sr.color.g * machineTint.g, sr.color.b * machineTint.b, sr.color.a * machineTint.a);
            }
        }
    }

    void RestoreSurfaceMachineTint()
    {
        foreach (var state in tintedRenderers)
        {
            if (state.renderer == null) continue;
            state.renderer.color = state.color;
        }
        tintedRenderers.Clear();
    }

    GameObject GetMarker(int index)
    {
        if (index < markerPool.Count) return markerPool[index];
        var parent = markerParent != null ? markerParent : transform;
        var marker = Instantiate(markerPrefab, parent);
        markerPool.Add(marker);
        markerBaseScales.Add(marker.transform.localScale);
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

    void ScaleMarker(GameObject marker, int index)
    {
        if (!scaleMarkerToCell) return;
        if (marker == null) return;
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;

        var renderers = marker.GetComponentsInChildren<SpriteRenderer>(true);
        if (renderers == null || renderers.Length == 0) return;

        float maxSize = 0f;
        foreach (var sr in renderers)
        {
            if (sr == null || sr.sprite == null) continue;
            var size = sr.sprite.bounds.size;
            maxSize = Mathf.Max(maxSize, size.x, size.y);
        }

        if (maxSize <= 0.0001f) return;

        float targetSize = Mathf.Max(0.01f, grid.CellSize * Mathf.Clamp01(markerCellFill));
        float scaleFactor = targetSize / maxSize;
        var baseScale = index < markerBaseScales.Count ? markerBaseScales[index] : marker.transform.localScale;
        marker.transform.localScale = new Vector3(baseScale.x * scaleFactor, baseScale.y * scaleFactor, baseScale.z);
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
