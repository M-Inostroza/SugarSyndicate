using UnityEngine;

public class DroneHQ : MonoBehaviour, IPowerConsumer
{
    public static DroneHQ Instance { get; private set; }

    [SerializeField] Transform dockPoint;
    [SerializeField] PowerService powerService;

    [Header("Power")]
    [SerializeField, Min(0f)] float powerUsageWatts = 0f;

    [System.NonSerialized] public bool isGhost = false;

    public Vector3 DockPosition => dockPoint != null ? dockPoint.position : transform.position;
    public bool HasPower => powerUsageWatts <= 0f || (powerService != null && powerService.HasPowerFor(powerUsageWatts));

    void Awake()
    {
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
    }

    void Start()
    {
        if (isGhost) return;
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        powerService?.RegisterConsumer(this);
        DroneTaskService.Instance?.RegisterHQ(this);
    }

    void OnDestroy()
    {
        if (isGhost) return;
        if (powerService == null) powerService = PowerService.Instance;
        powerService?.UnregisterConsumer(this);
        if (Instance == this) Instance = null;
        DroneTaskService.Instance?.UnregisterHQ(this);
    }

    public float GetConsumptionWatts()
    {
        if (isGhost) return 0f;
        return Mathf.Max(0f, powerUsageWatts);
    }
}
