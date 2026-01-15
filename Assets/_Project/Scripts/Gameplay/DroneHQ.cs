using UnityEngine;

public class DroneHQ : MonoBehaviour
{
    public static DroneHQ Instance { get; private set; }

    [SerializeField] Transform dockPoint;

    [System.NonSerialized] public bool isGhost = false;

    public Vector3 DockPosition => dockPoint != null ? dockPoint.position : transform.position;
    public bool HasPower => true;

    void Awake()
    {
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
        DroneTaskService.Instance?.RegisterHQ(this);
    }

    void OnDestroy()
    {
        if (isGhost) return;
        if (Instance == this) Instance = null;
        DroneTaskService.Instance?.UnregisterHQ(this);
    }
}
