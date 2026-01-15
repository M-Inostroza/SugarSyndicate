using System;
using System.Collections.Generic;
using UnityEngine;

public interface IPowerSource
{
    float GetOutputWatts(TimePhase phase);
}

public interface IPowerTerminal
{
    IEnumerable<Vector2Int> PowerCells { get; }
}

public interface IPowerSourceNode : IPowerTerminal, IPowerSource
{
}

public interface IPowerConsumer : IPowerTerminal
{
    void SetPowered(bool powered);
}

public class PowerService : MonoBehaviour
{
    public static PowerService Instance { get; private set; }

    [Header("Debug")]
    [SerializeField] bool logPowerChanges = false;

    readonly HashSet<IPowerSource> sources = new();
    readonly HashSet<IPowerSourceNode> networkSources = new();
    TimeManager timeManager;
    TimePhase cachedPhase = TimePhase.Day;
    bool hasPhase;

    float totalWatts;
    public float TotalWatts => totalWatts;
    public event Action<float> OnPowerChanged;

    [Header("Network Rules")]
    [SerializeField, Min(0)] int maxCableLength = 8;

    [Header("Network Debug")]
    [SerializeField] bool logNetworkChanges = false;

    readonly HashSet<Vector2Int> cables = new();
    readonly HashSet<Vector2Int> poles = new();
    readonly HashSet<Vector2Int> poweredCables = new();
    readonly HashSet<Vector2Int> poweredPoles = new();
    readonly HashSet<IPowerConsumer> consumers = new();
    readonly Dictionary<Vector2Int, int> bestDistance = new();
    readonly Dictionary<Vector2Int, Vector2Int> poleInputs = new();

    static readonly Vector2Int[] NeighborDirs =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0),
    };

    bool networkDirty;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnEnable()
    {
        TryHookTimeManager();
    }

    void Update()
    {
        if (timeManager == null && TimeManager.Instance != null)
            TryHookTimeManager();
    }

    void LateUpdate()
    {
        if (networkDirty)
            RecalculateNetwork();
    }

    void OnDestroy()
    {
        if (timeManager != null)
            timeManager.OnPhaseChanged -= HandlePhaseChanged;
    }

    public static PowerService EnsureInstance()
    {
        if (Instance != null) return Instance;
        var existing = FindAnyObjectByType<PowerService>();
        if (existing != null) { Instance = existing; return Instance; }
        var go = new GameObject("PowerService");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<PowerService>();
        return Instance;
    }

    public void RegisterSource(IPowerSource source)
    {
        if (source == null) return;
        if (sources.Add(source))
            Recalculate();
        if (source is IPowerSourceNode node && networkSources.Add(node))
            MarkNetworkDirty();
    }

    public void UnregisterSource(IPowerSource source)
    {
        if (source == null) return;
        if (sources.Remove(source))
            Recalculate();
        if (source is IPowerSourceNode node && networkSources.Remove(node))
            MarkNetworkDirty();
    }

    void TryHookTimeManager()
    {
        timeManager = TimeManager.Instance;
        if (timeManager == null) return;
        timeManager.OnPhaseChanged -= HandlePhaseChanged;
        timeManager.OnPhaseChanged += HandlePhaseChanged;
        cachedPhase = timeManager.CurrentPhase;
        hasPhase = true;
        Recalculate();
        MarkNetworkDirty();
    }

    void HandlePhaseChanged(TimePhase phase)
    {
        cachedPhase = phase;
        hasPhase = true;
        Recalculate();
        MarkNetworkDirty();
    }

    void Recalculate()
    {
        var phase = hasPhase ? cachedPhase : (TimeManager.Instance != null ? TimeManager.Instance.CurrentPhase : TimePhase.Day);
        float sum = 0f;
        foreach (var source in sources)
        {
            if (source == null) continue;
            sum += Mathf.Max(0f, source.GetOutputWatts(phase));
        }
        if (Mathf.Abs(sum - totalWatts) < 0.001f) return;
        totalWatts = sum;
        if (logPowerChanges) Debug.Log($"[PowerService] Power = {totalWatts:0.##} W");
        OnPowerChanged?.Invoke(totalWatts);
    }

    public static string FormatPower(float watts)
    {
        float abs = Mathf.Abs(watts);
        if (abs >= 1000000f) return $"{watts / 1000000f:0.##} MW";
        if (abs >= 1000f) return $"{watts / 1000f:0.##} kW";
        return $"{watts:0.##} W";
    }

    public int MaxCableLength => maxCableLength;

    public void SetMaxCableLength(int length)
    {
        maxCableLength = Mathf.Max(0, length);
        MarkNetworkDirty();
    }

    public bool IsCellOccupied(Vector2Int cell) => cables.Contains(cell) || poles.Contains(cell);
    public bool IsCellPowered(Vector2Int cell) => poweredCables.Contains(cell) || poweredPoles.Contains(cell);
    public bool IsCablePowered(Vector2Int cell) => poweredCables.Contains(cell);
    public bool IsPolePowered(Vector2Int cell) => poweredPoles.Contains(cell);

    public bool RegisterCable(Vector2Int cell)
    {
        if (poles.Contains(cell)) return false;
        if (cables.Add(cell))
        {
            MarkNetworkDirty();
            return true;
        }
        return false;
    }

    public void UnregisterCable(Vector2Int cell)
    {
        if (cables.Remove(cell))
            MarkNetworkDirty();
    }

    public bool RegisterPole(Vector2Int cell)
    {
        if (cables.Contains(cell)) return false;
        if (poles.Add(cell))
        {
            MarkNetworkDirty();
            return true;
        }
        return false;
    }

    public void UnregisterPole(Vector2Int cell)
    {
        if (poles.Remove(cell))
            MarkNetworkDirty();
    }

    public void RegisterConsumer(IPowerConsumer consumer)
    {
        if (consumer == null) return;
        if (consumers.Add(consumer))
            MarkNetworkDirty();
    }

    public void UnregisterConsumer(IPowerConsumer consumer)
    {
        if (consumer == null) return;
        if (consumers.Remove(consumer))
            MarkNetworkDirty();
    }

    void MarkNetworkDirty()
    {
        networkDirty = true;
    }

    void RecalculateNetwork()
    {
        networkDirty = false;
        networkSources.RemoveWhere(source => source == null);
        consumers.RemoveWhere(consumer => consumer == null);

        poweredCables.Clear();
        poweredPoles.Clear();
        bestDistance.Clear();
        poleInputs.Clear();

        var phase = hasPhase ? cachedPhase : (TimeManager.Instance != null ? TimeManager.Instance.CurrentPhase : TimePhase.Day);

        var queue = new Queue<Step>();
        foreach (var source in networkSources)
        {
            if (source == null) continue;
            if (source.GetOutputWatts(phase) <= 0f) continue;
            foreach (var cell in source.PowerCells)
                SeedFromSourceCell(cell, queue);
        }

        while (queue.Count > 0)
        {
            var step = queue.Dequeue();
            poweredCables.Add(step.cell);

            foreach (var dir in NeighborDirs)
            {
                var next = step.cell + dir;
                if (poles.Contains(next))
                    PowerPole(next, step.cell, queue);

                if (step.distance >= maxCableLength)
                    continue;

                if (cables.Contains(next))
                    EnqueueCable(next, step.distance + 1, queue);
            }
        }

        UpdateConsumers();

        if (logNetworkChanges)
            Debug.Log($"[PowerService] Powered cables={poweredCables.Count}, poles={poweredPoles.Count}");
    }

    struct Step
    {
        public Vector2Int cell;
        public int distance;

        public Step(Vector2Int cell, int distance)
        {
            this.cell = cell;
            this.distance = distance;
        }
    }

    void SeedFromSourceCell(Vector2Int cell, Queue<Step> queue)
    {
        if (poles.Contains(cell))
            PowerPole(cell, cell, queue);
        if (cables.Contains(cell))
            EnqueueCable(cell, 1, queue);

        foreach (var dir in NeighborDirs)
        {
            var next = cell + dir;
            if (poles.Contains(next))
                PowerPole(next, cell, queue);
            if (cables.Contains(next))
                EnqueueCable(next, 1, queue);
        }
    }

    void PowerPole(Vector2Int cell, Vector2Int inputCell, Queue<Step> queue)
    {
        if (!poles.Contains(cell)) return;
        if (!poweredPoles.Add(cell)) return;
        poleInputs[cell] = inputCell;

        foreach (var dir in NeighborDirs)
        {
            var next = cell + dir;
            if (next == inputCell) continue;
            if (cables.Contains(next))
                EnqueueCable(next, 1, queue);
        }
    }

    void EnqueueCable(Vector2Int cell, int distance, Queue<Step> queue)
    {
        if (distance > maxCableLength) return;
        if (!cables.Contains(cell)) return;
        if (bestDistance.TryGetValue(cell, out var best) && best <= distance) return;
        bestDistance[cell] = distance;
        queue.Enqueue(new Step(cell, distance));
    }

    void UpdateConsumers()
    {
        foreach (var consumer in consumers)
        {
            if (consumer == null) continue;
            bool powered = IsPoweredForConsumer(consumer);
            consumer.SetPowered(powered);
        }
    }

    bool IsPoweredForConsumer(IPowerConsumer consumer)
    {
        foreach (var cell in consumer.PowerCells)
        {
            if (IsCellPowered(cell)) return true;
            foreach (var dir in NeighborDirs)
            {
                if (IsCellPowered(cell + dir))
                    return true;
            }
        }
        return false;
    }
}
