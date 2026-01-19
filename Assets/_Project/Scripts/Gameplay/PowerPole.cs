using UnityEngine;

public class PowerPole : MonoBehaviour
{
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;

    [System.NonSerialized] public bool isGhost = false;

    Vector2Int cell;
    bool registered;
    bool registeredAsBlueprint;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.EnsureInstance();
    }

    void OnEnable()
    {
        UndergroundVisibilityRegistry.RegisterPowerPole(this);
        Register();
        if (registered)
            PowerCable.RefreshAround(cell);
    }

    void OnDisable()
    {
        bool wasRegistered = registered;
        UndergroundVisibilityRegistry.UnregisterPowerPole(this);
        Unregister();
        if (wasRegistered)
            PowerCable.RefreshAround(cell);
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
    }
}
