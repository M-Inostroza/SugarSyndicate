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
    [SerializeField] GameObject nightBackground;
    [SerializeField] GameObject undergroundBackground;
    [SerializeField] Color nightTint = new Color(0.2235294f, 0.454902f, 1f, 1f);

    BuildModeController buildModeController;
    bool isUndergroundActive;
    bool manualOverride;
    string lastSelectionName;
    readonly Dictionary<GameObject, bool> surfaceStates = new();
    readonly Dictionary<GameObject, bool> undergroundStates = new();
    readonly Dictionary<Renderer, bool> hiddenMachineRenderers = new();
    readonly Dictionary<Renderer, bool> hiddenBeltRenderers = new();
    readonly Dictionary<Renderer, bool> hiddenPowerLineRenderers = new();
    readonly Dictionary<Renderer, bool> hiddenDroneRenderers = new();
    readonly Dictionary<Renderer, bool> hiddenCrawlerRenderers = new();
    readonly Dictionary<SpriteRenderer, Color> originalTintColors = new();
    readonly List<SpriteRenderer> tintCleanup = new();
    PowerService powerService;
    bool tintDirty;
    TimeManager timeManager;
    SpriteRenderer[] dayBackgroundRenderers;
    Color[] dayBackgroundColors;
    SpriteRenderer[] nightBackgroundRenderers;
    Color[] nightBackgroundColors;
    float lastNightBlend = -1f;
    bool backgroundDirty;

    void Awake()
    {
    }

    void Start()
    {
        if (hideSurfacePowerLines)
            HideSurfacePowerLineRenderers();
        CacheBackgroundRenderers();
        SetBackgroundState(isUndergroundActive);
        if (!isUndergroundActive)
            HideCrawlerRenderers();
        TryHookPowerService();
        TryHookTimeManager();
        UpdateBackgroundFromTime(true);
    }

    void LateUpdate()
    {
        if (tintSurfaceMachines && isUndergroundActive && tintDirty)
        {
            RefreshSurfaceMachineTintRealtime();
            tintDirty = false;
        }
        if (backgroundDirty)
        {
            UpdateBackgroundFromTime(false);
            backgroundDirty = false;
        }
    }

    void OnEnable()
    {
        BuildSelectionNotifier.OnSelectionChanged += HandleSelectionChanged;
        TryHookBuildModeController();
        HookRegistryEvents(true);
        TryHookPowerService();
        TryHookTimeManager();
    }

    void OnDisable()
    {
        BuildSelectionNotifier.OnSelectionChanged -= HandleSelectionChanged;
        if (buildModeController != null)
            buildModeController.onExitBuildMode -= HandleBuildModeExit;
        HookRegistryEvents(false);
        UnhookPowerService();
        UnhookTimeManager();
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
        TryHookPowerService();
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
        UpdateBackgroundFromTime(true);
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
        RestoreSurfaceBeltRenderers();
        SetBackgroundState(false);
        UpdateBackgroundFromTime(true);
        if (hideSurfacePowerLines)
            HideSurfacePowerLineRenderers();
        else
            RestoreSurfacePowerLineRenderers();
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
            if (!IsSceneObject(root)) continue;
            if (!cache.ContainsKey(root)) cache[root] = root.activeSelf;
            root.SetActive(active);
        }
    }

    void RestoreStates(Dictionary<GameObject, bool> cache)
    {
        foreach (var kv in cache)
        {
            if (kv.Key == null) continue;
            if (!IsSceneObject(kv.Key)) continue;
            kv.Key.SetActive(kv.Value);
        }
        cache.Clear();
    }

    static bool IsSceneObject(GameObject go)
    {
        if (go == null) return false;
        var scene = go.scene;
        return scene.IsValid() && scene.isLoaded;
    }


    void HideSurfaceMachineRenderers()
    {
        hiddenMachineRenderers.Clear();
        foreach (var behaviour in UndergroundVisibilityRegistry.OverlayTargets)
            HideOverlayRenderers(behaviour);
    }

    void RestoreSurfaceMachineRenderers()
    {
        RestoreRenderers(hiddenMachineRenderers);
    }

    void HideSurfaceBeltRenderers()
    {
        hiddenBeltRenderers.Clear();
        foreach (var belt in UndergroundVisibilityRegistry.Belts)
            CacheAndDisableRenderers(belt, hiddenBeltRenderers);
    }

    void RestoreSurfaceBeltRenderers()
    {
        RestoreRenderers(hiddenBeltRenderers);
    }

    void HideSurfaceItemContainer()
    {
        var container = ContainerLocator.GetItemContainer();
        if (container == null) return;
        CacheAndSetActive(new[] { container.gameObject }, surfaceStates, false);
    }

    void SetBackgroundState(bool undergroundActive)
    {
        bool surfaceActive = !undergroundActive;
        if (dayBackground != null) dayBackground.SetActive(surfaceActive);
        if (nightBackground != null) nightBackground.SetActive(surfaceActive);
        if (undergroundBackground != null) undergroundBackground.SetActive(undergroundActive);
    }

    void HideSurfacePowerLineRenderers()
    {
        hiddenPowerLineRenderers.Clear();
        foreach (var cable in UndergroundVisibilityRegistry.PowerCables)
            CacheAndDisableRenderers(cable, hiddenPowerLineRenderers);
        foreach (var pole in UndergroundVisibilityRegistry.PowerPoles)
            CacheAndDisableRenderers(pole, hiddenPowerLineRenderers);
    }

    void RestoreSurfacePowerLineRenderers()
    {
        RestoreRenderers(hiddenPowerLineRenderers);
    }

    void HideDroneRenderers()
    {
        hiddenDroneRenderers.Clear();
        foreach (var drone in UndergroundVisibilityRegistry.Drones)
            CacheAndDisableRenderers(drone, hiddenDroneRenderers);
    }

    void RestoreDroneRenderers()
    {
        RestoreRenderers(hiddenDroneRenderers);
    }

    void HideCrawlerRenderers()
    {
        hiddenCrawlerRenderers.Clear();
        foreach (var crawler in UndergroundVisibilityRegistry.Crawlers)
            CacheAndDisableRenderers(crawler, hiddenCrawlerRenderers);
    }

    void RestoreCrawlerRenderers()
    {
        RestoreRenderers(hiddenCrawlerRenderers);
    }

    void TintSurfaceMachineRenderers()
    {
        tintDirty = true;
        RefreshSurfaceMachineTintRealtime();
        tintDirty = false;
    }

    void RefreshSurfaceMachineTintRealtime()
    {
        var seen = new HashSet<SpriteRenderer>();
        foreach (var behaviour in UndergroundVisibilityRegistry.OverlayTargets)
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
        var power = powerService ?? PowerService.Instance;
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

        if (behaviour is DroneHQ) return true;

        return true;
    }

    bool IsConsumerConnected(IPowerConsumer consumer, MonoBehaviour behaviour)
    {
        if (consumer == null) return false;
        var power = powerService ?? PowerService.Instance;
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
        originalTintColors.Clear();
        tintDirty = false;
    }

    void HideOverlayRenderers(MonoBehaviour behaviour)
    {
        if (behaviour == null) return;
        if (!includeInactiveMachines && !behaviour.gameObject.activeInHierarchy) return;
        if (keepPowerSourcesVisible && behaviour is IPowerSource) return;
        if (!IsOverlayTarget(behaviour)) return;
        CacheAndDisableRenderers(behaviour, hiddenMachineRenderers);
    }

    void CacheAndDisableRenderers(Component root, Dictionary<Renderer, bool> store)
    {
        if (root == null) return;
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (r == null || store.ContainsKey(r)) continue;
            store[r] = r.enabled;
            r.enabled = false;
        }
    }

    void RestoreRenderers(Dictionary<Renderer, bool> store)
    {
        foreach (var kv in store)
        {
            if (kv.Key == null) continue;
            kv.Key.enabled = kv.Value;
        }
        store.Clear();
    }

    void RestoreRenderersFor(Component root, Dictionary<Renderer, bool> store)
    {
        if (root == null) return;
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (store.TryGetValue(r, out var enabled))
            {
                r.enabled = enabled;
                store.Remove(r);
            }
        }
    }

    void ForgetRenderers(Component root, Dictionary<Renderer, bool> store)
    {
        if (root == null) return;
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        foreach (var r in renderers)
        {
            if (r == null) continue;
            store.Remove(r);
        }
    }

    void ForgetTintedRenderers(MonoBehaviour behaviour)
    {
        if (behaviour == null) return;
        var renderers = behaviour.GetComponentsInChildren<SpriteRenderer>(true);
        foreach (var sr in renderers)
        {
            if (sr == null) continue;
            if (originalTintColors.TryGetValue(sr, out var color))
                sr.color = color;
            originalTintColors.Remove(sr);
        }
    }

    void HookRegistryEvents(bool hook)
    {
        if (hook)
        {
            UndergroundVisibilityRegistry.OverlayRegistered += HandleOverlayRegistered;
            UndergroundVisibilityRegistry.OverlayUnregistered += HandleOverlayUnregistered;
            UndergroundVisibilityRegistry.BeltRegistered += HandleBeltRegistered;
            UndergroundVisibilityRegistry.BeltUnregistered += HandleBeltUnregistered;
            UndergroundVisibilityRegistry.PowerCableRegistered += HandlePowerCableRegistered;
            UndergroundVisibilityRegistry.PowerCableUnregistered += HandlePowerCableUnregistered;
            UndergroundVisibilityRegistry.PowerPoleRegistered += HandlePowerPoleRegistered;
            UndergroundVisibilityRegistry.PowerPoleUnregistered += HandlePowerPoleUnregistered;
            UndergroundVisibilityRegistry.DroneRegistered += HandleDroneRegistered;
            UndergroundVisibilityRegistry.DroneUnregistered += HandleDroneUnregistered;
            UndergroundVisibilityRegistry.CrawlerRegistered += HandleCrawlerRegistered;
            UndergroundVisibilityRegistry.CrawlerUnregistered += HandleCrawlerUnregistered;
            return;
        }

        UndergroundVisibilityRegistry.OverlayRegistered -= HandleOverlayRegistered;
        UndergroundVisibilityRegistry.OverlayUnregistered -= HandleOverlayUnregistered;
        UndergroundVisibilityRegistry.BeltRegistered -= HandleBeltRegistered;
        UndergroundVisibilityRegistry.BeltUnregistered -= HandleBeltUnregistered;
        UndergroundVisibilityRegistry.PowerCableRegistered -= HandlePowerCableRegistered;
        UndergroundVisibilityRegistry.PowerCableUnregistered -= HandlePowerCableUnregistered;
        UndergroundVisibilityRegistry.PowerPoleRegistered -= HandlePowerPoleRegistered;
        UndergroundVisibilityRegistry.PowerPoleUnregistered -= HandlePowerPoleUnregistered;
        UndergroundVisibilityRegistry.DroneRegistered -= HandleDroneRegistered;
        UndergroundVisibilityRegistry.DroneUnregistered -= HandleDroneUnregistered;
        UndergroundVisibilityRegistry.CrawlerRegistered -= HandleCrawlerRegistered;
        UndergroundVisibilityRegistry.CrawlerUnregistered -= HandleCrawlerUnregistered;
    }

    void HandleOverlayRegistered(MonoBehaviour behaviour)
    {
        if (!isUndergroundActive) return;
        if (tintSurfaceMachines)
        {
            tintDirty = true;
            return;
        }
        if (hideSurfaceMachineRenderers)
            HideOverlayRenderers(behaviour);
    }

    void HandleOverlayUnregistered(MonoBehaviour behaviour)
    {
        ForgetTintedRenderers(behaviour);
        ForgetRenderers(behaviour, hiddenMachineRenderers);
    }

    void HandleBeltRegistered(Conveyor belt)
    {
        if (!isUndergroundActive || !hideSurfaceBelts) return;
        CacheAndDisableRenderers(belt, hiddenBeltRenderers);
    }

    void HandleBeltUnregistered(Conveyor belt)
    {
        ForgetRenderers(belt, hiddenBeltRenderers);
    }

    void HandlePowerCableRegistered(PowerCable cable)
    {
        HandlePowerLineRegistered(cable);
    }

    void HandlePowerCableUnregistered(PowerCable cable)
    {
        ForgetRenderers(cable, hiddenPowerLineRenderers);
    }

    void HandlePowerPoleRegistered(PowerPole pole)
    {
        HandlePowerLineRegistered(pole);
    }

    void HandlePowerPoleUnregistered(PowerPole pole)
    {
        ForgetRenderers(pole, hiddenPowerLineRenderers);
    }

    void HandlePowerLineRegistered(Component line)
    {
        if (!hideSurfacePowerLines) return;
        if (isUndergroundActive)
        {
            RestoreRenderersFor(line, hiddenPowerLineRenderers);
            return;
        }
        CacheAndDisableRenderers(line, hiddenPowerLineRenderers);
    }

    void HandleDroneRegistered(DroneWorker drone)
    {
        if (!isUndergroundActive) return;
        CacheAndDisableRenderers(drone, hiddenDroneRenderers);
    }

    void HandleDroneUnregistered(DroneWorker drone)
    {
        ForgetRenderers(drone, hiddenDroneRenderers);
    }

    void HandleCrawlerRegistered(CrawlerWorker crawler)
    {
        if (isUndergroundActive)
        {
            RestoreRenderersFor(crawler, hiddenCrawlerRenderers);
            return;
        }
        CacheAndDisableRenderers(crawler, hiddenCrawlerRenderers);
    }

    void HandleCrawlerUnregistered(CrawlerWorker crawler)
    {
        ForgetRenderers(crawler, hiddenCrawlerRenderers);
    }

    void TryHookPowerService()
    {
        if (powerService != null) return;
        powerService = PowerService.Instance;
        if (powerService == null)
            powerService = FindAnyObjectByType<PowerService>();
        if (powerService == null) return;
        powerService.OnPowerChanged -= HandlePowerChanged;
        powerService.OnPowerChanged += HandlePowerChanged;
        powerService.OnNetworkChanged -= HandleNetworkChanged;
        powerService.OnNetworkChanged += HandleNetworkChanged;
        if (isUndergroundActive && tintSurfaceMachines)
            tintDirty = true;
    }

    void UnhookPowerService()
    {
        if (powerService == null) return;
        powerService.OnPowerChanged -= HandlePowerChanged;
        powerService.OnNetworkChanged -= HandleNetworkChanged;
        powerService = null;
    }

    void HandlePowerChanged(float _)
    {
        if (!isUndergroundActive || !tintSurfaceMachines) return;
        tintDirty = true;
    }

    void HandleNetworkChanged()
    {
        if (!isUndergroundActive || !tintSurfaceMachines) return;
        tintDirty = true;
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

    void CacheBackgroundRenderers()
    {
        CacheBackgroundRenderers(dayBackground, ref dayBackgroundRenderers, ref dayBackgroundColors);
        CacheBackgroundRenderers(nightBackground, ref nightBackgroundRenderers, ref nightBackgroundColors);
    }

    static void CacheBackgroundRenderers(GameObject root, ref SpriteRenderer[] renderers, ref Color[] baseColors)
    {
        if (root == null)
        {
            renderers = null;
            baseColors = null;
            return;
        }
        renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
        baseColors = new Color[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            var sr = renderers[i];
            baseColors[i] = sr != null ? sr.color : Color.white;
        }
    }

    void TryHookTimeManager()
    {
        if (timeManager != null) return;
        timeManager = TimeManager.Instance;
        if (timeManager == null) return;
        timeManager.OnPhaseChanged -= HandlePhaseChanged;
        timeManager.OnPhaseChanged += HandlePhaseChanged;
        timeManager.OnPhaseTimeChanged -= HandlePhaseTimeChanged;
        timeManager.OnPhaseTimeChanged += HandlePhaseTimeChanged;
        backgroundDirty = true;
    }

    void UnhookTimeManager()
    {
        if (timeManager == null) return;
        timeManager.OnPhaseChanged -= HandlePhaseChanged;
        timeManager.OnPhaseTimeChanged -= HandlePhaseTimeChanged;
        timeManager = null;
    }

    void HandlePhaseChanged(TimePhase _)
    {
        backgroundDirty = true;
    }

    void HandlePhaseTimeChanged(float _)
    {
        backgroundDirty = true;
    }

    void UpdateBackgroundFromTime(bool force)
    {
        if (isUndergroundActive) return;
        float blend = GetNightBlend();
        if (!force && Mathf.Abs(blend - lastNightBlend) < 0.001f) return;
        lastNightBlend = blend;

        var tint = nightBackground != null ? Color.white : Color.Lerp(Color.white, nightTint, blend);
        ApplyTint(dayBackgroundRenderers, dayBackgroundColors, tint);
        if (nightBackground != null)
            ApplyNightAlpha(nightBackgroundRenderers, nightBackgroundColors, blend);
    }

    float GetNightBlend()
    {
        if (timeManager == null) return 0f;
        float duration = Mathf.Max(0.01f, timeManager.PhaseDuration);
        float t = Mathf.Clamp01(timeManager.PhaseElapsed / duration);
        return timeManager.CurrentPhase == TimePhase.Night ? 1f - t : t;
    }

    static void ApplyTint(SpriteRenderer[] renderers, Color[] baseColors, Color tint)
    {
        if (renderers == null || baseColors == null) return;
        float alpha = Mathf.Clamp01(tint.a);
        for (int i = 0; i < renderers.Length; i++)
        {
            var sr = renderers[i];
            if (sr == null) continue;
            var baseCol = baseColors[i];
            sr.color = new Color(baseCol.r * tint.r, baseCol.g * tint.g, baseCol.b * tint.b, baseCol.a * alpha);
        }
    }

    static void ApplyNightAlpha(SpriteRenderer[] renderers, Color[] baseColors, float alpha01)
    {
        if (renderers == null || baseColors == null) return;
        float alpha = Mathf.Clamp01(alpha01);
        for (int i = 0; i < renderers.Length; i++)
        {
            var sr = renderers[i];
            if (sr == null) continue;
            var baseCol = baseColors[i];
            sr.color = new Color(baseCol.r, baseCol.g, baseCol.b, baseCol.a * alpha);
        }
    }
}
