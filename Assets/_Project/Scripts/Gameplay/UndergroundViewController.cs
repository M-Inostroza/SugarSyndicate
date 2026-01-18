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

    [Header("Surface Hiding")]
    [SerializeField] bool tintSurfaceMachines = true;
    [SerializeField] bool hideSurfaceMachineRenderers = true;
    [SerializeField] bool hideSurfaceBelts = true;
    [SerializeField] bool hideSurfaceItems = true;
    [SerializeField] bool hideSurfacePowerLines = true;
    [SerializeField] bool includeInactiveMachines = true;
    [SerializeField] bool keepPowerSourcesVisible = true;
    [SerializeField] Color machineTint = new Color(1f, 0.9f, 0.2f, 0.5f);
    [SerializeField] Color powerSourceTint = new Color(1f, 0.95f, 0.35f, 0.6f);
    [SerializeField] Color disconnectedTint = new Color(0.65f, 0.65f, 0.65f, 0.45f);

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
    readonly List<RendererState> hiddenRenderers = new();
    readonly List<RendererState> hiddenBeltRenderers = new();
    readonly List<RendererState> hiddenPowerLineRenderers = new();
    readonly List<RendererState> hiddenDroneRenderers = new();
    readonly List<RendererState> hiddenCrawlerRenderers = new();
    readonly List<TintState> tintedRenderers = new();
    readonly Dictionary<SpriteRenderer, Color> originalTintColors = new();
    readonly List<SpriteRenderer> tintCleanup = new();

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

    void Update()
    {
        if (!isUndergroundActive) return;
        if (tintSurfaceMachines)
            RefreshSurfaceMachineTintRealtime();
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
        HideDroneRenderers();
        RestoreCrawlerRenderers();
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
        RestoreDroneRenderers();
        HideCrawlerRenderers();
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


    void HideSurfaceMachineRenderers()
    {
        hiddenRenderers.Clear();
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        var seen = new HashSet<Renderer>();
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!includeInactiveMachines && !behaviour.gameObject.activeInHierarchy) continue;
            if (keepPowerSourcesVisible && behaviour is IPowerSource) continue;
            if (!IsOverlayTarget(behaviour)) continue;
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
            if (!IsOverlayTarget(behaviour)) continue;

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
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
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
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
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

    void HideDroneRenderers()
    {
        hiddenDroneRenderers.Clear();
        var found = FindObjectsByType<DroneWorker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var seen = new HashSet<Renderer>();
        foreach (var drone in found)
        {
            if (drone == null) continue;
            var renderers = drone.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null || !seen.Add(r)) continue;
                hiddenDroneRenderers.Add(new RendererState(r, r.enabled));
                r.enabled = false;
            }
        }
    }

    void RestoreDroneRenderers()
    {
        foreach (var state in hiddenDroneRenderers)
        {
            if (state.renderer == null) continue;
            state.renderer.enabled = state.enabled;
        }
        hiddenDroneRenderers.Clear();
    }

    void HideCrawlerRenderers()
    {
        hiddenCrawlerRenderers.Clear();
        var found = FindObjectsByType<CrawlerWorker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var seen = new HashSet<Renderer>();
        foreach (var crawler in found)
        {
            if (crawler == null) continue;
            var renderers = crawler.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r == null || !seen.Add(r)) continue;
                hiddenCrawlerRenderers.Add(new RendererState(r, r.enabled));
                r.enabled = false;
            }
        }
    }

    void RestoreCrawlerRenderers()
    {
        foreach (var state in hiddenCrawlerRenderers)
        {
            if (state.renderer == null) continue;
            state.renderer.enabled = state.enabled;
        }
        hiddenCrawlerRenderers.Clear();
    }

    void TintSurfaceMachineRenderers()
    {
        RefreshSurfaceMachineTintRealtime();
    }

    void RefreshSurfaceMachineTintRealtime()
    {
        tintedRenderers.Clear();
        var found = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var seen = new HashSet<SpriteRenderer>();
        foreach (var behaviour in found)
        {
            if (behaviour == null) continue;
            if (!includeInactiveMachines && !behaviour.gameObject.activeInHierarchy) continue;
            if (!IsOverlayTarget(behaviour)) continue;
            var tint = GetOverlayTint(behaviour);
            var renderers = behaviour.GetComponentsInChildren<SpriteRenderer>(true);
            foreach (var sr in renderers)
            {
                if (sr == null || !seen.Add(sr)) continue;
                if (!originalTintColors.ContainsKey(sr))
                    originalTintColors[sr] = sr.color;
                var baseCol = originalTintColors[sr];
                tintedRenderers.Add(new TintState(sr, baseCol));
                sr.color = new Color(baseCol.r * tint.r, baseCol.g * tint.g, baseCol.b * tint.b, baseCol.a * tint.a);
            }
        }

        if (originalTintColors.Count > 0)
        {
            tintCleanup.Clear();
            foreach (var kv in originalTintColors)
            {
                if (kv.Key == null || !seen.Contains(kv.Key))
                    tintCleanup.Add(kv.Key);
            }
            for (int i = 0; i < tintCleanup.Count; i++)
                originalTintColors.Remove(tintCleanup[i]);
        }
    }

    bool IsOverlayTarget(MonoBehaviour behaviour)
    {
        if (behaviour == null) return false;
        if (behaviour is DroneHQ) return true;
        if (behaviour is IPowerSource) return true;
        if (behaviour is IPowerConsumer) return true;
        if (behaviour is IMachine) return true;
        return false;
    }

    Color GetOverlayTint(MonoBehaviour behaviour)
    {
        if (behaviour is IPowerSource) return powerSourceTint;
        bool connected = IsConnectedToPower(behaviour);
        if (!connected) return disconnectedTint;
        return machineTint;
    }

    bool IsConnectedToPower(MonoBehaviour behaviour)
    {
        if (behaviour == null) return false;
        var power = PowerService.Instance;
        if (power == null) return true;

        if (behaviour is IPowerConsumer consumer)
        {
            return IsConsumerConnected(consumer, behaviour);
        }

        if (behaviour is IPowerTerminal terminal)
        {
            foreach (var cell in terminal.PowerCells)
            {
                if (power.IsCellPoweredOrAdjacent(cell)) return true;
            }
            return false;
        }

        if (behaviour is DroneHQ)
        {
            var grid = GridService.Instance;
            if (grid == null) return true;
            var cell = grid.WorldToCell(behaviour.transform.position);
            return power.IsCellPoweredOrAdjacent(cell);
        }

        return true;
    }

    bool IsConsumerConnected(IPowerConsumer consumer, MonoBehaviour behaviour)
    {
        if (consumer == null) return false;
        var power = PowerService.Instance;
        if (power == null) return true;

        if (consumer is IMachine machine)
            return power.IsCellPoweredOrAdjacent(machine.Cell);

        var grid = GridService.Instance;
        if (grid == null) return true;
        var cell = grid.WorldToCell(behaviour.transform.position);
        return power.IsCellPoweredOrAdjacent(cell);
    }

    void RestoreSurfaceMachineTint()
    {
        foreach (var kv in originalTintColors)
        {
            if (kv.Key == null) continue;
            kv.Key.color = kv.Value;
        }
        tintedRenderers.Clear();
        originalTintColors.Clear();
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
