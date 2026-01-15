using System.Collections.Generic;
using UnityEngine;

public class PowerConsumer : MonoBehaviour, IPowerConsumer
{
    [Header("Power Cells")]
    [SerializeField] bool useMachineCell = true;
    [SerializeField] Vector2Int[] powerOffsets = { new Vector2Int(0, 0) };

    [Header("Behavior")]
    [SerializeField] bool disableBehavioursWhenUnpowered = true;
    [SerializeField] bool includeChildren = true;
    [SerializeField] bool debugLogging = false;

    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;

    bool powered;
    readonly List<Behaviour> disabledBehaviours = new List<Behaviour>();
    IMachine cachedMachine;
    Repairable cachedRepairable;

    public bool IsPowered => powered;

    public IEnumerable<Vector2Int> PowerCells
    {
        get
        {
            var baseCell = ResolveBaseCell();
            if (powerOffsets == null || powerOffsets.Length == 0)
            {
                yield return baseCell;
                yield break;
            }

            for (int i = 0; i < powerOffsets.Length; i++)
                yield return baseCell + powerOffsets[i];
        }
    }

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.EnsureInstance();
        cachedRepairable = GetComponent<Repairable>();
    }

    void OnEnable()
    {
        if (powerService == null) powerService = PowerService.EnsureInstance();
        powerService?.RegisterConsumer(this);
    }

    void OnDisable()
    {
        if (powerService == null) powerService = PowerService.Instance;
        powerService?.UnregisterConsumer(this);
        EnableBehaviours();
    }

    void Update()
    {
        if (!powered) return;
        if (disabledBehaviours.Count == 0) return;
        if (IsBroken()) return;
        EnableBehaviours();
    }

    public void SetPowered(bool value)
    {
        if (powered == value) return;
        powered = value;

        if (debugLogging)
            Debug.Log($"[PowerConsumer] {gameObject.name} powered={powered}");

        if (!disableBehavioursWhenUnpowered) return;
        if (powered)
            EnableBehaviours();
        else
            DisableBehaviours();
    }

    Vector2Int ResolveBaseCell()
    {
        if (useMachineCell)
        {
            if (cachedMachine == null)
                cachedMachine = GetComponent<IMachine>() ?? GetComponentInParent<IMachine>();
            if (cachedMachine != null)
                return cachedMachine.Cell;
        }

        if (grid == null) grid = GridService.Instance;
        if (grid == null) return Vector2Int.zero;
        return grid.WorldToCell(transform.position);
    }

    void DisableBehaviours()
    {
        disabledBehaviours.Clear();
        var behaviours = includeChildren ? GetComponentsInChildren<Behaviour>(true) : GetComponents<Behaviour>();
        foreach (var behaviour in behaviours)
        {
            if (behaviour == null || behaviour == this) continue;
            if (behaviour is PowerConsumer) continue;
            if (!ShouldDisable(behaviour)) continue;
            if (!behaviour.enabled) continue;
            behaviour.enabled = false;
            disabledBehaviours.Add(behaviour);
        }
    }

    void EnableBehaviours()
    {
        if (IsBroken()) return;
        foreach (var behaviour in disabledBehaviours)
        {
            if (behaviour == null) continue;
            behaviour.enabled = true;
        }
        disabledBehaviours.Clear();
    }

    bool ShouldDisable(Behaviour behaviour)
    {
        if (behaviour is IMachine) return true;
        if (behaviour is SugarMine) return true;
        if (behaviour is WaterPipe) return true;
        return false;
    }

    bool IsBroken()
    {
        if (cachedRepairable == null) cachedRepairable = GetComponent<Repairable>();
        return cachedRepairable != null && cachedRepairable.IsBroken;
    }
}
