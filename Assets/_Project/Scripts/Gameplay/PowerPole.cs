using UnityEngine;

public class PowerPole : MonoBehaviour, IGhostState
{
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;
    [Header("Hook Indicators")]
    [SerializeField] SpriteRenderer[] hookIndicators = new SpriteRenderer[4];

    [System.NonSerialized] public bool isGhost = false;
    public bool IsGhost => isGhost;

    Vector2Int cell;
    bool registered;
    bool registeredAsBlueprint;
    int lastHookCount = -1;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.EnsureInstance();
        CacheHookIndicators();
    }

    void OnEnable()
    {
        PowerLinkLine.ActiveLinksChanged += HandleActiveLinksChanged;
        UndergroundVisibilityRegistry.RegisterPowerPole(this);
        Register();
        RefreshHookIndicators(true);
        if (registered)
            PowerCable.RefreshAround(cell);
    }

    void OnDisable()
    {
        bool wasRegistered = registered;
        PowerLinkLine.ActiveLinksChanged -= HandleActiveLinksChanged;
        UndergroundVisibilityRegistry.UnregisterPowerPole(this);
        Unregister();
        if (wasRegistered)
            PowerCable.RefreshAround(cell);
    }

    void OnValidate()
    {
        CacheHookIndicators();
        if (Application.isPlaying)
            RefreshHookIndicators(true);
    }

    void Register()
    {
        if (registered) return;
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;
        cell = grid.WorldToCell(transform.position);
        if (powerService == null) powerService = PowerService.EnsureInstance();
        if (powerService != null)
        {
            bool ok = isGhost ? powerService.RegisterPoleBlueprint(cell) : powerService.RegisterPole(cell);
            if (!ok)
            {
                Debug.LogWarning($"[PowerPole] Cell {cell} already occupied by a cable/pole.");
                return;
            }
        }
        registered = true;
        registeredAsBlueprint = isGhost;
    }

    void Unregister()
    {
        if (!registered) return;
        if (powerService == null) powerService = PowerService.Instance;
        if (registeredAsBlueprint) powerService?.UnregisterPoleBlueprint(cell);
        else powerService?.UnregisterPole(cell);
        registered = false;
        registeredAsBlueprint = false;
    }

    public void SetGhost(bool ghost)
    {
        if (isGhost == ghost) return;
        isGhost = ghost;
        if (!registered) return;
        Unregister();
        Register();
        RefreshHookIndicators(true);
    }

    public void ActivateFromBlueprint()
    {
        isGhost = false;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        if (grid == null) grid = GridService.Instance;
        if (grid != null)
            cell = grid.WorldToCell(transform.position);
        if (powerService != null)
        {
            if (powerService.PromotePoleBlueprint(cell))
            {
                registered = true;
                registeredAsBlueprint = false;
            }
        }
        RefreshHookIndicators(true);
    }

    void HandleActiveLinksChanged()
    {
        RefreshHookIndicators(false);
    }

    void CacheHookIndicators()
    {
        bool hasAssignedIndicator = false;
        if (hookIndicators != null)
        {
            for (int i = 0; i < hookIndicators.Length; i++)
            {
                if (hookIndicators[i] == null) continue;
                hasAssignedIndicator = true;
                break;
            }
        }

        if (hasAssignedIndicator) return;

        var childIndicators = GetComponentsInChildren<SpriteRenderer>(true);
        if (childIndicators == null || childIndicators.Length == 0) return;

        int hookCount = 0;
        for (int i = 0; i < childIndicators.Length; i++)
        {
            var indicator = childIndicators[i];
            if (indicator == null || indicator.transform == transform) continue;
            if (!indicator.name.StartsWith("Hook")) continue;
            hookCount++;
        }

        if (hookCount <= 0) return;

        hookIndicators = new SpriteRenderer[hookCount];
        int index = 0;
        for (int i = 0; i < childIndicators.Length; i++)
        {
            var indicator = childIndicators[i];
            if (indicator == null || indicator.transform == transform) continue;
            if (!indicator.name.StartsWith("Hook")) continue;
            hookIndicators[index++] = indicator;
        }
    }

    void RefreshHookIndicators(bool force)
    {
        if (hookIndicators == null || hookIndicators.Length == 0) return;

        int activeHookCount = isGhost ? 0 : Mathf.Max(0, PowerLinkLine.CountLinksForNode(this));
        activeHookCount = Mathf.Min(activeHookCount, hookIndicators.Length);

        if (!force && activeHookCount == lastHookCount) return;
        lastHookCount = activeHookCount;

        for (int i = 0; i < hookIndicators.Length; i++)
        {
            var indicator = hookIndicators[i];
            if (indicator == null) continue;
            indicator.enabled = i < activeHookCount;
        }
    }
}
