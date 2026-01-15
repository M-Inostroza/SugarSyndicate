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

public interface IPowerConsumer
{
    float GetConsumptionWatts();
}

public interface IPowerSourceNode : IPowerTerminal, IPowerSource
{
}

public interface IPowerNetworkConsumer : IPowerTerminal
{
    void SetPowered(bool powered);
}

public class PowerService : MonoBehaviour
{
    public static PowerService Instance { get; private set; }

    [Header("Debug")]
    [SerializeField] bool logPowerChanges = false;
    [SerializeField, Min(0f)] float powerTransitionSeconds = 2f;

    readonly HashSet<IPowerSource> sources = new();
    readonly HashSet<IPowerConsumer> consumers = new();
    readonly HashSet<IPowerSourceNode> networkSources = new();
    readonly HashSet<IPowerNetworkConsumer> networkConsumers = new();
    TimeManager timeManager;
    TimePhase cachedPhase = TimePhase.Day;
    bool hasPhase;

    float totalWatts;
    float totalGeneratedWatts;
    float totalConsumedWatts;
    float targetGeneratedWatts;
    public float TotalWatts => totalWatts;
    public float TotalGeneratedWatts => totalGeneratedWatts;
    public float TotalConsumedWatts => totalConsumedWatts;
    public event Action<float> OnPowerChanged;

    [Header("Network Rules")]
    [SerializeField, Min(0)] int maxCableLength = 8;

    [Header("Network Debug")]
    [SerializeField] bool logNetworkChanges = false;

    readonly HashSet<Vector2Int> cables = new();
    readonly HashSet<Vector2Int> poles = new();
    readonly HashSet<Vector2Int> cableBlueprints = new();
    readonly HashSet<Vector2Int> poleBlueprints = new();
    readonly HashSet<Vector2Int> poweredCables = new();
    readonly HashSet<Vector2Int> poweredPoles = new();
    readonly Dictionary<Vector2Int, int> bestDistance = new();
    readonly Dictionary<Vector2Int, Vector2Int> poleInputs = new();
    readonly Dictionary<Vector2Int, int> placementDistance = new();
    readonly HashSet<Vector2Int> placementSourceCells = new();
    readonly HashSet<Vector2Int> connectedPlacementPoles = new();
    readonly Dictionary<IPowerConsumer, float> consumerCharge = new();

    static readonly Vector2Int[] NeighborDirs =
    {
        new Vector2Int(0, 1),
        new Vector2Int(1, 0),
        new Vector2Int(0, -1),
        new Vector2Int(-1, 0),
    };

    bool networkDirty;
    bool placementDirty;

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
        if (GameManager.Instance != null)
        {
            var state = GameManager.Instance.State;
            if (state == GameState.Build || state == GameState.Delete)
                return;
        }
        UpdatePowerSmoothing();
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
        {
            MarkNetworkDirty();
            MarkPlacementDirty();
        }
    }

    public void UnregisterSource(IPowerSource source)
    {
        if (source == null) return;
        if (sources.Remove(source))
            Recalculate();
        if (source is IPowerSourceNode node && networkSources.Remove(node))
        {
            MarkNetworkDirty();
            MarkPlacementDirty();
        }
    }

    public void RegisterConsumer(IPowerConsumer consumer)
    {
        if (consumer == null) return;
        if (consumers.Add(consumer))
        {
            consumerCharge[consumer] = 0f;
            Recalculate();
        }
    }

    public void UnregisterConsumer(IPowerConsumer consumer)
    {
        if (consumer == null) return;
        if (consumers.Remove(consumer))
        {
            consumerCharge.Remove(consumer);
            Recalculate();
        }
    }

    public void RegisterNetworkConsumer(IPowerNetworkConsumer consumer)
    {
        if (consumer == null) return;
        if (networkConsumers.Add(consumer))
            MarkNetworkDirty();
    }

    public void UnregisterNetworkConsumer(IPowerNetworkConsumer consumer)
    {
        if (consumer == null) return;
        if (networkConsumers.Remove(consumer))
            MarkNetworkDirty();
    }

    public void RequestRecalculate()
    {
        Recalculate();
    }

    public bool HasPowerFor(float watts)
    {
        if (watts <= 0f) return true;
        return totalGeneratedWatts > 0f && totalWatts >= 0f;
    }

    public bool IsConsumerFullyCharged(IPowerConsumer consumer, float threshold = 0.999f)
    {
        if (consumer == null) return false;
        if (consumer.GetConsumptionWatts() <= 0f) return true;
        if (!consumerCharge.TryGetValue(consumer, out var charge)) return false;
        return charge >= threshold;
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
        float generated = 0f;
        foreach (var source in sources)
        {
            if (source == null) continue;
            generated += Mathf.Max(0f, source.GetOutputWatts(phase));
        }
        targetGeneratedWatts = generated;
    }

    public static string FormatPower(float watts)
    {
        float abs = Mathf.Abs(watts);
        if (abs >= 1000000f) return $"{watts / 1000000f:0.##} MW";
        if (abs >= 1000f) return $"{watts / 1000f:0.##} kW";
        return $"{watts:0.##} W";
    }

    void UpdatePowerSmoothing()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float transition = Mathf.Max(0f, powerTransitionSeconds);
        totalGeneratedWatts = SmoothTowards(totalGeneratedWatts, targetGeneratedWatts, transition, dt);
        totalConsumedWatts = ComputeSmoothedConsumption(transition, dt);

        float net = totalGeneratedWatts - totalConsumedWatts;
        if (Mathf.Abs(net - totalWatts) < 0.001f) return;
        totalWatts = net;
        if (logPowerChanges) Debug.Log($"[PowerService] Power = {totalWatts:0.##} W");
        OnPowerChanged?.Invoke(totalWatts);
    }

    float ComputeSmoothedConsumption(float transition, float dt)
    {
        float consumed = 0f;
        List<IPowerConsumer> toRemove = null;
        foreach (var consumer in consumers)
        {
            if (consumer == null)
            {
                (toRemove ??= new List<IPowerConsumer>()).Add(consumer);
                continue;
            }

            float usage = Mathf.Max(0f, consumer.GetConsumptionWatts());
            if (usage <= 0f)
            {
                consumerCharge[consumer] = 1f;
                continue;
            }

            bool connected = IsConsumerNetworkPowered(consumer);
            float target = connected ? 1f : 0f;
            float current = consumerCharge.TryGetValue(consumer, out var charge) ? charge : 0f;
            float next = SmoothTowards(current, target, transition, dt);
            consumerCharge[consumer] = next;
            consumed += usage * next;
        }

        if (toRemove != null)
        {
            foreach (var consumer in toRemove)
            {
                consumers.Remove(consumer);
                consumerCharge.Remove(consumer);
            }
        }

        return consumed;
    }

    static float SmoothTowards(float current, float target, float duration, float dt)
    {
        if (duration <= 0f) return target;
        float diff = Mathf.Abs(target - current);
        if (diff <= 0.0001f) return target;
        float rate = diff / duration;
        return Mathf.MoveTowards(current, target, rate * dt);
    }

    bool IsConsumerNetworkPowered(IPowerConsumer consumer)
    {
        if (consumer == null) return false;
        if (consumer.GetConsumptionWatts() <= 0f) return true;
        if (consumer is IMachine machine)
            return IsCellPoweredOrAdjacent(machine.Cell);
        if (consumer is Component component)
        {
            var grid = GridService.Instance;
            if (grid != null)
            {
                var cell = grid.WorldToCell(component.transform.position);
                return IsCellPoweredOrAdjacent(cell);
            }
        }
        return false;
    }

    public int MaxCableLength => maxCableLength;

    public void SetMaxCableLength(int length)
    {
        maxCableLength = Mathf.Max(0, length);
        MarkNetworkDirty();
        MarkPlacementDirty();
    }

    public bool IsCellOccupied(Vector2Int cell) => cables.Contains(cell) || poles.Contains(cell);
    public bool IsCellPowered(Vector2Int cell) => poweredCables.Contains(cell) || poweredPoles.Contains(cell);
    public bool IsCablePowered(Vector2Int cell) => poweredCables.Contains(cell);
    public bool IsPolePowered(Vector2Int cell) => poweredPoles.Contains(cell);
    public bool IsCableBlueprint(Vector2Int cell) => cableBlueprints.Contains(cell);
    public bool IsPoleBlueprint(Vector2Int cell) => poleBlueprints.Contains(cell);
    public bool IsCellOccupiedOrBlueprint(Vector2Int cell) => IsCellOccupied(cell) || cableBlueprints.Contains(cell) || poleBlueprints.Contains(cell);
    public bool IsCellPoweredOrAdjacent(Vector2Int cell)
    {
        if (IsCellPowered(cell)) return true;
        foreach (var dir in NeighborDirs)
        {
            if (IsCellPowered(cell + dir))
                return true;
        }
        return false;
    }

    public bool RegisterCable(Vector2Int cell)
    {
        if (poles.Contains(cell) || poleBlueprints.Contains(cell)) return false;
        cableBlueprints.Remove(cell);
        if (cables.Add(cell))
        {
            MarkNetworkDirty();
            MarkPlacementDirty();
            return true;
        }
        return false;
    }

    public void UnregisterCable(Vector2Int cell)
    {
        if (cables.Remove(cell))
        {
            MarkNetworkDirty();
            MarkPlacementDirty();
        }
    }

    public bool RegisterPole(Vector2Int cell)
    {
        if (cables.Contains(cell) || cableBlueprints.Contains(cell)) return false;
        poleBlueprints.Remove(cell);
        if (poles.Add(cell))
        {
            MarkNetworkDirty();
            MarkPlacementDirty();
            return true;
        }
        return false;
    }

    public void UnregisterPole(Vector2Int cell)
    {
        if (poles.Remove(cell))
        {
            MarkNetworkDirty();
            MarkPlacementDirty();
        }
    }

    public bool RegisterCableBlueprint(Vector2Int cell)
    {
        if (cables.Contains(cell) || poles.Contains(cell) || poleBlueprints.Contains(cell)) return false;
        if (cableBlueprints.Add(cell))
        {
            MarkPlacementDirty();
            return true;
        }
        return false;
    }

    public void UnregisterCableBlueprint(Vector2Int cell)
    {
        if (cableBlueprints.Remove(cell))
            MarkPlacementDirty();
    }

    public bool RegisterPoleBlueprint(Vector2Int cell)
    {
        if (poles.Contains(cell) || cables.Contains(cell) || cableBlueprints.Contains(cell)) return false;
        if (poleBlueprints.Add(cell))
        {
            MarkPlacementDirty();
            return true;
        }
        return false;
    }

    public void UnregisterPoleBlueprint(Vector2Int cell)
    {
        if (poleBlueprints.Remove(cell))
            MarkPlacementDirty();
    }

    public bool PromoteCableBlueprint(Vector2Int cell)
    {
        if (cables.Contains(cell)) return true;
        if (poles.Contains(cell) || poleBlueprints.Contains(cell)) return false;
        if (cableBlueprints.Remove(cell))
        {
            cables.Add(cell);
            MarkNetworkDirty();
            MarkPlacementDirty();
            return true;
        }
        return RegisterCable(cell);
    }

    public bool PromotePoleBlueprint(Vector2Int cell)
    {
        if (poles.Contains(cell)) return true;
        if (cables.Contains(cell) || cableBlueprints.Contains(cell)) return false;
        if (poleBlueprints.Remove(cell))
        {
            poles.Add(cell);
            MarkNetworkDirty();
            MarkPlacementDirty();
            return true;
        }
        return RegisterPole(cell);
    }

    void MarkNetworkDirty()
    {
        networkDirty = true;
    }

    void MarkPlacementDirty()
    {
        placementDirty = true;
    }

    void RecalculateNetwork()
    {
        networkDirty = false;
        networkSources.RemoveWhere(source => source == null);
        networkConsumers.RemoveWhere(consumer => consumer == null);

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

        UpdateNetworkConsumers();

        if (logNetworkChanges)
            Debug.Log($"[PowerService] Powered cables={poweredCables.Count}, poles={poweredPoles.Count}");
    }

    void EnsurePlacementDistances()
    {
        if (!placementDirty) return;
        RecalculatePlacementDistances();
    }

    void RecalculatePlacementDistances()
    {
        placementDirty = false;
        placementDistance.Clear();
        placementSourceCells.Clear();
        connectedPlacementPoles.Clear();

        networkSources.RemoveWhere(source => source == null);

        var queue = new Queue<Step>();
        foreach (var source in networkSources)
        {
            if (source == null) continue;
            foreach (var cell in source.PowerCells)
            {
                placementSourceCells.Add(cell);
                SeedPlacementFromSourceCell(cell, queue);
            }
        }

        while (queue.Count > 0)
        {
            var step = queue.Dequeue();
            foreach (var dir in NeighborDirs)
            {
                var next = step.cell + dir;
                if (IsAnyPole(next))
                    SeedPlacementFromPole(next, queue);

                if (step.distance >= maxCableLength)
                    continue;

                if (IsAnyCable(next))
                    EnqueuePlacementCable(next, step.distance + 1, queue);
            }
        }
    }

    void SeedPlacementFromSourceCell(Vector2Int cell, Queue<Step> queue)
    {
        foreach (var dir in NeighborDirs)
        {
            var next = cell + dir;
            if (IsAnyPole(next))
                SeedPlacementFromPole(next, queue);
            if (IsAnyCable(next))
                EnqueuePlacementCable(next, 1, queue);
        }
    }

    void SeedPlacementFromPole(Vector2Int cell, Queue<Step> queue)
    {
        if (!IsAnyPole(cell)) return;
        if (!connectedPlacementPoles.Add(cell)) return;
        foreach (var dir in NeighborDirs)
        {
            var next = cell + dir;
            if (IsAnyCable(next))
                EnqueuePlacementCable(next, 1, queue);
        }
    }

    void EnqueuePlacementCable(Vector2Int cell, int distance, Queue<Step> queue)
    {
        if (distance > maxCableLength) return;
        if (!IsAnyCable(cell)) return;
        if (placementDistance.TryGetValue(cell, out var best) && best <= distance) return;
        placementDistance[cell] = distance;
        queue.Enqueue(new Step(cell, distance));
    }

    bool IsAnyCable(Vector2Int cell) => cables.Contains(cell) || cableBlueprints.Contains(cell);
    bool IsAnyPole(Vector2Int cell) => poles.Contains(cell) || poleBlueprints.Contains(cell);

    public bool TryGetPlacementDistance(Vector2Int cell, out int distance)
    {
        EnsurePlacementDistances();
        return placementDistance.TryGetValue(cell, out distance);
    }

    public bool IsAdjacentToSourceCell(Vector2Int cell)
    {
        EnsurePlacementDistances();
        foreach (var dir in NeighborDirs)
        {
            if (placementSourceCells.Contains(cell + dir))
                return true;
        }
        return false;
    }

    public bool IsAdjacentToConnectedPole(Vector2Int cell)
    {
        EnsurePlacementDistances();
        foreach (var dir in NeighborDirs)
        {
            if (connectedPlacementPoles.Contains(cell + dir))
                return true;
        }
        return false;
    }

    public bool CanPlaceCableAt(Vector2Int cell)
    {
        if (IsCellOccupiedOrBlueprint(cell)) return false;
        EnsurePlacementDistances();
        int best = int.MaxValue;
        if (IsAdjacentToSourceCell(cell) || IsAdjacentToConnectedPole(cell))
            best = 1;

        foreach (var dir in NeighborDirs)
        {
            var next = cell + dir;
            if (placementDistance.TryGetValue(next, out var dist))
                best = Mathf.Min(best, dist + 1);
        }

        return best <= maxCableLength;
    }

    public bool CanPlacePoleAt(Vector2Int cell)
    {
        if (IsCellOccupiedOrBlueprint(cell)) return false;
        EnsurePlacementDistances();
        if (IsAdjacentToSourceCell(cell)) return true;

        foreach (var dir in NeighborDirs)
        {
            var next = cell + dir;
            if (placementDistance.TryGetValue(next, out var dist) && dist <= maxCableLength)
                return true;
        }

        return false;
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

    void UpdateNetworkConsumers()
    {
        foreach (var consumer in networkConsumers)
        {
            if (consumer == null) continue;
            bool powered = IsPoweredForConsumer(consumer);
            consumer.SetPowered(powered);
        }
    }

    bool IsPoweredForConsumer(IPowerNetworkConsumer consumer)
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
