using UnityEngine;

public class PowerCable : MonoBehaviour
{
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;

    [System.NonSerialized] public bool isGhost = false;

    Vector2Int cell;
    bool registered;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.EnsureInstance();
    }

    void OnEnable()
    {
        if (isGhost) return;
        Register();
    }

    void OnDisable()
    {
        Unregister();
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
            if (!powerService.RegisterCable(cell))
            {
                Debug.LogWarning($"[PowerCable] Cell {cell} already occupied by a pole/cable.");
                return;
            }
        }
        registered = true;
    }

    void Unregister()
    {
        if (!registered) return;
        if (powerService == null) powerService = PowerService.Instance;
        powerService?.UnregisterCable(cell);
        registered = false;
    }
}
