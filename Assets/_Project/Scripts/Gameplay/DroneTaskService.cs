using System.Collections.Generic;
using UnityEngine;

public class DroneTaskService : MonoBehaviour
{
    public static DroneTaskService Instance { get; private set; }

    const int MaxDronesAllowed = 3;

    [Header("Drones")]
    [SerializeField] DroneWorker dronePrefab;
    [SerializeField, Range(0, MaxDronesAllowed)] int startingDrones = 0;
    [SerializeField, Range(1, MaxDronesAllowed)] int maxDrones = 3;
    [SerializeField] Transform droneParent;

    [Header("Crawlers")]
    [SerializeField] CrawlerWorker crawlerPrefab;
    [SerializeField, Range(0, MaxDronesAllowed)] int startingCrawlers = 0;
    [SerializeField, Range(1, MaxDronesAllowed)] int maxCrawlers = 3;
    [SerializeField] Transform crawlerParent;

    [Header("HQ Bootstrap (First HQ Build)")]
    [SerializeField, Min(0)] int hqBootstrapDrones = 2;
    [SerializeField, Min(0.1f)] float hqBootstrapOffscreenMargin = 2f;
    [SerializeField, Min(0.1f)] float hqBootstrapSpeedMultiplier = 1.75f;

    readonly List<DroneWorker> drones = new();
    readonly List<CrawlerWorker> crawlers = new();
    readonly List<DroneTaskTarget> tasks = new();
    readonly List<DroneWorker> bootstrapDrones = new();

    DroneHQ hq;
    DroneTaskTarget hqBootstrapTask;
    bool hqBootstrapSpawned;

    public bool HasHq => hq != null;
    public bool IsPowered => hq != null && hq.HasPower;
    public int TotalDrones => drones.Count;
    public int MaxDrones => Mathf.Max(0, maxDrones);
    public int TotalCrawlers => crawlers.Count;
    public int MaxCrawlers => Mathf.Max(0, maxCrawlers);
    public bool CanAddDrone => TotalDrones < MaxDrones;
    public bool CanAddCrawler => TotalCrawlers < MaxCrawlers;

    public event System.Action<int> OnDroneCountChanged;

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

    public int IdleCrawlers
    {
        get
        {
            int idle = 0;
            for (int i = 0; i < crawlers.Count; i++)
            {
                var crawler = crawlers[i];
                if (crawler != null && !crawler.IsBusy) idle++;
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

    public float CrawlerSpeed
    {
        get
        {
            for (int i = 0; i < crawlers.Count; i++)
            {
                var crawler = crawlers[i];
                if (crawler != null) return crawler.MoveSpeed;
            }
            return crawlerPrefab != null ? crawlerPrefab.MoveSpeed : 0f;
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
        EnsureStartingCrawlers();
    }

    public void RegisterHQ(DroneHQ newHq)
    {
        if (newHq == null) return;
        hq = newHq;
        ReleaseBootstrapDrones();
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
        TrySpawnBootstrapDrones(task);
    }

    public void UnregisterTask(DroneTaskTarget task)
    {
        if (task == null) return;
        tasks.Remove(task);
        if (hqBootstrapTask == task)
        {
            hqBootstrapTask = null;
            hqBootstrapSpawned = false;
            ReleaseBootstrapDrones();
        }
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
            if (IsCrawlerTask(t)) continue;
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

    public bool TryAssignCrawlerTask(CrawlerWorker crawler, out DroneTaskTarget task)
    {
        task = null;
        if (crawler == null || tasks.Count == 0) return false;

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
            if (!IsCrawlerTask(t)) continue;
            if (t.IsComplete || t.IsAssigned) continue;
            if (t.Priority == DroneTaskPriority.Priority)
            {
                if (t.TryAssign(crawler))
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

        if (bestNormal != null && bestNormal.TryAssign(crawler))
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

    void EnsureStartingCrawlers()
    {
        maxCrawlers = Mathf.Clamp(maxCrawlers, 1, MaxDronesAllowed);
        startingCrawlers = Mathf.Clamp(startingCrawlers, 0, maxCrawlers);
        int target = startingCrawlers;
        while (crawlers.Count < target)
        {
            if (crawlers.Count >= maxCrawlers) break;
            SpawnCrawler();
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
        if (drone != null)
        {
            drones.Add(drone);
            OnDroneCountChanged?.Invoke(drones.Count);
        }
    }

    void SpawnCrawler()
    {
        var pos = GetHqPosition();
        CrawlerWorker crawler = null;
        if (crawlerPrefab != null)
        {
            var parent = crawlerParent != null ? crawlerParent : null;
            crawler = parent != null ? Instantiate(crawlerPrefab, pos, Quaternion.identity, parent)
                                     : Instantiate(crawlerPrefab, pos, Quaternion.identity);
        }
        else
        {
            var go = new GameObject("Crawler");
            go.transform.position = pos;
            crawler = go.AddComponent<CrawlerWorker>();
        }
        if (crawler != null) crawlers.Add(crawler);
    }

    public bool TryBuyDrone(int cost)
    {
        if (!HasHq) return false;
        if (!CanAddDrone) return false;
        if (cost > 0 && GameManager.Instance != null && !GameManager.Instance.TrySpendSweetCredits(cost))
            return false;

        SpawnDrone();
        return true;
    }

    public bool TryBuyCrawler(int cost)
    {
        if (!HasHq) return false;
        if (!CanAddCrawler) return false;
        if (cost > 0 && GameManager.Instance != null && !GameManager.Instance.TrySpendSweetCredits(cost))
            return false;

        SpawnCrawler();
        return true;
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
        maxCrawlers = Mathf.Clamp(maxCrawlers, 1, MaxDronesAllowed);
        startingCrawlers = Mathf.Clamp(startingCrawlers, 0, maxCrawlers);
    }

    bool IsCrawlerTask(DroneTaskTarget task)
    {
        if (task is BlueprintTask bt)
            return bt.Type == BlueprintTask.BlueprintType.Cable || bt.Type == BlueprintTask.BlueprintType.Pole;
        return false;
    }

    void TrySpawnBootstrapDrones(DroneTaskTarget task)
    {
        if (task == null || hq != null) return;
        if (hqBootstrapDrones <= 0) return;

        if (task is BlueprintTask bt && bt.IsHqBlueprint)
        {
            if (hqBootstrapSpawned && hqBootstrapTask == task) return;
            hqBootstrapTask = task;
            hqBootstrapSpawned = true;

            for (int i = 0; i < hqBootstrapDrones; i++)
            {
                var spawnPos = GetRandomOffscreenPosition();
                var exitPos = GetRandomOffscreenPosition();
                var drone = SpawnBootstrapDrone(spawnPos);
                if (drone != null)
                {
                    drone.StartBootstrap(task, exitPos, hqBootstrapSpeedMultiplier);
                    bootstrapDrones.Add(drone);
                }
            }
        }
    }

    DroneWorker SpawnBootstrapDrone(Vector3 position)
    {
        DroneWorker drone = null;
        if (dronePrefab != null)
        {
            var parent = droneParent != null ? droneParent : null;
            drone = parent != null ? Instantiate(dronePrefab, position, Quaternion.identity, parent)
                                   : Instantiate(dronePrefab, position, Quaternion.identity);
        }
        else
        {
            var go = new GameObject("Drone");
            go.transform.position = position;
            drone = go.AddComponent<DroneWorker>();
        }
        return drone;
    }

    void ReleaseBootstrapDrones()
    {
        for (int i = 0; i < bootstrapDrones.Count; i++)
        {
            var drone = bootstrapDrones[i];
            if (drone == null) continue;
            drone.AbortBootstrap();
        }
        bootstrapDrones.Clear();
    }

    Vector3 GetRandomOffscreenPosition()
    {
        var cam = Camera.main;
        if (cam == null) return transform.position;

        var camPos = cam.transform.position;
        float planeZ = 0f;
        float planeDist = Mathf.Abs(camPos.z - planeZ);

        if (cam.orthographic)
        {
            float h = cam.orthographicSize;
            float w = h * cam.aspect;
            float margin = hqBootstrapOffscreenMargin;
            int side = Random.Range(0, 4);
            switch (side)
            {
                case 0:
                    return new Vector3(camPos.x - w - margin, camPos.y + Random.Range(-h, h), planeZ);
                case 1:
                    return new Vector3(camPos.x + w + margin, camPos.y + Random.Range(-h, h), planeZ);
                case 2:
                    return new Vector3(camPos.x + Random.Range(-w, w), camPos.y + h + margin, planeZ);
                default:
                    return new Vector3(camPos.x + Random.Range(-w, w), camPos.y - h - margin, planeZ);
            }
        }

        int sideIndex = Random.Range(0, 4);
        float marginPixels = hqBootstrapOffscreenMargin * 100f;
        float x = sideIndex == 0 ? -marginPixels : sideIndex == 1 ? Screen.width + marginPixels : Random.Range(0f, Screen.width);
        float y = sideIndex == 2 ? Screen.height + marginPixels : sideIndex == 3 ? -marginPixels : Random.Range(0f, Screen.height);
        var world = cam.ScreenToWorldPoint(new Vector3(x, y, planeDist));
        world.z = planeZ;
        return world;
    }
}
