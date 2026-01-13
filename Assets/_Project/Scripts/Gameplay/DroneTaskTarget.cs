using UnityEngine;

public enum DroneTaskPriority { Normal, Priority }
public enum DroneTaskType { Build, Repair }

public class DroneTaskTarget : MonoBehaviour
{
    [SerializeField] DroneTaskType taskType = DroneTaskType.Build;
    [SerializeField] DroneTaskPriority priority = DroneTaskPriority.Normal;
    [SerializeField, Min(0.1f)] float workSeconds = 1f;

    float remainingSeconds;
    bool registered;
    bool hasWorkPosition;
    Vector3 workPosition;
    DroneWorker assignedDrone;

    public DroneTaskType TaskType => taskType;
    public DroneTaskPriority Priority => priority;
    public bool IsAssigned => assignedDrone != null;
    public bool IsComplete => remainingSeconds <= 0f;
    public Vector3 WorkPosition => hasWorkPosition ? workPosition : transform.position;
    public float Progress01
    {
        get
        {
            if (workSeconds <= 0f) return 1f;
            if (remainingSeconds <= 0f) return 1f;
            return 1f - Mathf.Clamp01(remainingSeconds / Mathf.Max(0.0001f, workSeconds));
        }
    }

    protected void BeginTask(DroneTaskType type, float seconds, DroneTaskPriority setPriority, Vector3 targetPosition)
    {
        taskType = type;
        priority = setPriority;
        workSeconds = Mathf.Max(0.01f, seconds);
        remainingSeconds = workSeconds;
        workPosition = targetPosition;
        hasWorkPosition = true;

        if (!registered)
        {
            registered = true;
            DroneTaskService.Instance?.RegisterTask(this);
        }
    }

    protected void CancelTask()
    {
        if (registered)
        {
            registered = false;
            DroneTaskService.Instance?.UnregisterTask(this);
        }
        assignedDrone = null;
    }

    public bool TryAssign(DroneWorker drone)
    {
        if (drone == null || assignedDrone != null) return false;
        assignedDrone = drone;
        return true;
    }

    public void ClearAssignment(DroneWorker drone)
    {
        if (assignedDrone == drone) assignedDrone = null;
    }

    public void ApplyWork(float deltaSeconds)
    {
        if (!registered || remainingSeconds <= 0f) return;
        remainingSeconds -= Mathf.Max(0f, deltaSeconds);
        if (remainingSeconds <= 0f)
        {
            remainingSeconds = 0f;
            CompleteTask();
        }
    }

    public void TogglePriority()
    {
        priority = priority == DroneTaskPriority.Priority ? DroneTaskPriority.Normal : DroneTaskPriority.Priority;
    }

    protected virtual void CompleteTask()
    {
        CancelTask();
        OnTaskCompleted();
    }

    protected virtual void OnTaskCompleted() { }

    protected virtual void OnDestroy()
    {
        CancelTask();
    }
}
