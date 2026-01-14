using System.Collections.Generic;
using UnityEngine;

public class DroneTaskService : MonoBehaviour
{
    public static DroneTaskService Instance { get; private set; }

    const int MaxDronesAllowed = 10;

    [Header("Drones")]
    [SerializeField] DroneWorker dronePrefab;
    [SerializeField, Range(0, MaxDronesAllowed)] int startingDrones = 1;
    [SerializeField, Range(1, MaxDronesAllowed)] int maxDrones = 10;
    [SerializeField] Transform droneParent;

    readonly List<DroneWorker> drones = new();
    readonly List<DroneTaskTarget> tasks = new();

    DroneHQ hq;

    public bool HasHq => hq != null;
    public bool IsPowered => hq != null && hq.HasPower;
    public int TotalDrones => drones.Count;
    public int MaxDrones => Mathf.Max(0, maxDrones);
    public int IdleDrones
    {
        get
        {
            int idle = 0;
            for (int i = 0; i < drones.Count; i++)
            {
                var drone = drones[i];
                if (drone != null && !drone.IsBusy) idle++;
            }
            return idle;
        }
    }
    public float DroneSpeed
    {
        get
        {
            for (int i = 0; i < drones.Count; i++)
            {
                var drone = drones[i];
                if (drone != null) return drone.MoveSpeed;
            }
            return dronePrefab != null ? dronePrefab.MoveSpeed : 0f;
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    void Start()
    {
        TryFindHq();
        if (hq != null)
            EnsureStartingDrones();
    }

    public void RegisterHQ(DroneHQ newHq)
    {
        if (newHq == null) return;
        hq = newHq;
        EnsureStartingDrones();
    }

    public void UnregisterHQ(DroneHQ oldHq)
    {
        if (hq == oldHq) hq = null;
    }

    public Vector3 GetHqPosition()
    {
        if (hq != null) return hq.DockPosition;
        return transform.position;
    }

    public void RegisterTask(DroneTaskTarget task)
    {
        if (task == null) return;
        if (!tasks.Contains(task)) tasks.Add(task);
    }

    public void UnregisterTask(DroneTaskTarget task)
    {
        if (task == null) return;
        tasks.Remove(task);
    }

    public bool TryAssignTask(DroneWorker drone, out DroneTaskTarget task)
    {
        task = null;
        if (drone == null || tasks.Count == 0) return false;

        DroneTaskTarget bestNormal = null;
        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            if (t == null)
            {
                tasks.RemoveAt(i);
                i--;
                continue;
            }
            if (t.IsComplete || t.IsAssigned) continue;
            if (t.Priority == DroneTaskPriority.Priority)
            {
                if (t.TryAssign(drone))
                {
                    task = t;
                    return true;
                }
            }
            else if (bestNormal == null)
            {
                bestNormal = t;
            }
        }

        if (bestNormal != null && bestNormal.TryAssign(drone))
        {
            task = bestNormal;
            return true;
        }

        return false;
    }

    void EnsureStartingDrones()
    {
        maxDrones = Mathf.Clamp(maxDrones, 1, MaxDronesAllowed);
        startingDrones = Mathf.Clamp(startingDrones, 0, maxDrones);
        int target = startingDrones;
        while (drones.Count < target)
        {
            if (drones.Count >= maxDrones) break;
            SpawnDrone();
        }
    }

    void SpawnDrone()
    {
        var pos = GetHqPosition();
        DroneWorker drone = null;
        if (dronePrefab != null)
        {
            var parent = droneParent != null ? droneParent : null;
            drone = parent != null ? Instantiate(dronePrefab, pos, Quaternion.identity, parent)
                                   : Instantiate(dronePrefab, pos, Quaternion.identity);
        }
        else
        {
            var go = new GameObject("Drone");
            go.transform.position = pos;
            drone = go.AddComponent<DroneWorker>();
        }
        if (drone != null) drones.Add(drone);
    }

    void TryFindHq()
    {
        if (hq != null) return;
        hq = FindAnyObjectByType<DroneHQ>();
    }

    void OnValidate()
    {
        maxDrones = Mathf.Clamp(maxDrones, 1, MaxDronesAllowed);
        startingDrones = Mathf.Clamp(startingDrones, 0, maxDrones);
    }
}
