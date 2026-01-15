using System;
using System.Collections.Generic;
using UnityEngine;

public class SolarPanelMachine : MonoBehaviour, IMachine, IPowerSourceNode
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;

    [Header("Orientation")]
    [Tooltip("Facing vector. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0);

    [Header("Power")]
    [SerializeField, Min(0f)] float dayOutputWatts = 40f;
    [SerializeField, Range(0f, 1f)] float nightOutputMultiplier = 0.2f;

    [Header("Power Ramp")]
    [SerializeField, Min(0f)] float outputRampSeconds = 10f;

    [NonSerialized] public bool isGhost = false;

    public Vector2Int InputVec => Vector2Int.zero;
    public Vector2Int Cell => baseCell;
    public IEnumerable<Vector2Int> PowerCells
    {
        get
        {
            yield return baseCell;
            if (extraCell != baseCell)
                yield return extraCell;
        }
    }

    class FootprintBlocker : IMachine
    {
        readonly Vector2Int cell;

        public FootprintBlocker(Vector2Int cellPos)
        {
            cell = cellPos;
        }

        public Vector2Int Cell => cell;
        public Vector2Int InputVec => Vector2Int.zero;
        public bool CanAcceptFrom(Vector2Int approachFromVec) => false;
        public bool TryStartProcess(Item item) => false;
    }

    Vector2Int baseCell;
    Vector2Int extraCell;
    bool registered;
    FootprintBlocker footprintBlocker;
    TimeManager timeManager;
    float currentOutputWatts;
    float targetOutputWatts;
    float rampStartWatts;
    float rampElapsed;
    bool ramping;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.EnsureInstance();
    }

    void Start()
    {
        if (isGhost) return;
        TryRegisterAsMachineAndSnap();
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        powerService?.RegisterSource(this);
        TryHookTimeManager();
        InitializeOutput();
    }

    void Update()
    {
        if (isGhost) return;
        if (timeManager == null && TimeManager.Instance != null)
        {
            TryHookTimeManager();
            if (timeManager != null)
                BeginRampTo(GetTargetWattsForPhase(timeManager.CurrentPhase));
        }
        if (IsPowerTransitionPaused()) return;
        if (!ramping) return;
        float delta = Time.deltaTime;
        if (delta <= 0f) return;
        rampElapsed += delta;
        float duration = Mathf.Max(0.001f, outputRampSeconds);
        float t = Mathf.Clamp01(rampElapsed / duration);
        float next = Mathf.Lerp(rampStartWatts, targetOutputWatts, t);
        if (Mathf.Abs(next - currentOutputWatts) > 0.001f)
        {
            currentOutputWatts = next;
            powerService?.RequestRecalculate();
        }
        if (t >= 1f || Mathf.Abs(currentOutputWatts - targetOutputWatts) <= 0.001f)
        {
            currentOutputWatts = targetOutputWatts;
            ramping = false;
            powerService?.RequestRecalculate();
        }
    }

    void OnDestroy()
    {
        if (isGhost) return;
        if (registered)
        {
            MachineRegistry.Unregister(this);
            if (footprintBlocker != null)
                MachineRegistry.Unregister(footprintBlocker);
        }

        if (timeManager != null)
            timeManager.OnPhaseChanged -= HandlePhaseChanged;

        if (powerService == null) powerService = PowerService.Instance;
        powerService?.UnregisterSource(this);

        if (grid == null) grid = GridService.Instance;
        if (grid != null)
        {
            ClearMachineCell(baseCell);
            ClearMachineCell(extraCell);
        }
    }

    void TryRegisterAsMachineAndSnap()
    {
        if (grid == null) return;
        facingVec = NormalizeFacing(facingVec);
        baseCell = ComputeBaseCell();
        extraCell = baseCell + facingVec;

        grid.SetMachineCell(baseCell);
        grid.SetMachineCell(extraCell);

        transform.position = GetFootprintCenterWorld(baseCell, extraCell, transform.position.z);

        MachineRegistry.Register(this);
        footprintBlocker = new FootprintBlocker(extraCell);
        MachineRegistry.Register(footprintBlocker);
        registered = true;
    }

    Vector2Int ComputeBaseCell()
    {
        float half = grid.CellSize * 0.5f;
        var offset = new Vector3(facingVec.x * half, facingVec.y * half, 0f);
        return grid.WorldToCell(transform.position - offset);
    }

    static Vector2Int NormalizeFacing(Vector2Int dir)
    {
        if (dir == Vector2Int.zero) return Vector2Int.right;
        if (Mathf.Abs(dir.x) >= Mathf.Abs(dir.y))
            return new Vector2Int(Math.Sign(dir.x), 0);
        return new Vector2Int(0, Math.Sign(dir.y));
    }

    Vector3 GetFootprintCenterWorld(Vector2Int a, Vector2Int b, float z)
    {
        if (grid == null) return transform.position;
        var w1 = grid.CellToWorld(a, z);
        var w2 = grid.CellToWorld(b, z);
        return (w1 + w2) * 0.5f;
    }

    void ClearMachineCell(Vector2Int cell)
    {
        if (grid == null) return;
        grid.ClearCell(cell);
        var c = grid.GetCell(cell);
        if (c != null) c.hasMachine = false;
    }

    public bool CanAcceptFrom(Vector2Int approachFromVec) => false;

    public bool TryStartProcess(Item item) => false;

    public float GetOutputWatts(TimePhase phase)
    {
        if (isGhost) return 0f;
        return Mathf.Max(0f, currentOutputWatts);
    }

    public string GetProcessSummary()
    {
        float day = Mathf.Max(0f, dayOutputWatts);
        float night = day * Mathf.Clamp01(nightOutputMultiplier);
        return $"Power: {PowerService.FormatPower(day)} day / {PowerService.FormatPower(night)} night";
    }

    void TryHookTimeManager()
    {
        var instance = TimeManager.Instance;
        if (instance == null) return;
        if (timeManager != null)
            timeManager.OnPhaseChanged -= HandlePhaseChanged;
        timeManager = instance;
        timeManager.OnPhaseChanged -= HandlePhaseChanged;
        timeManager.OnPhaseChanged += HandlePhaseChanged;
    }

    void HandlePhaseChanged(TimePhase phase)
    {
        if (isGhost) return;
        BeginRampTo(GetTargetWattsForPhase(phase));
    }

    void InitializeOutput()
    {
        currentOutputWatts = 0f;
        BeginRampTo(GetTargetWattsForPhase(GetCurrentPhase()));
    }

    TimePhase GetCurrentPhase()
    {
        if (timeManager != null) return timeManager.CurrentPhase;
        if (TimeManager.Instance != null) return TimeManager.Instance.CurrentPhase;
        return TimePhase.Day;
    }

    float GetTargetWattsForPhase(TimePhase phase)
    {
        float baseOutput = Mathf.Max(0f, dayOutputWatts);
        if (phase == TimePhase.Day) return baseOutput;
        return baseOutput * Mathf.Clamp01(nightOutputMultiplier);
    }

    void BeginRampTo(float watts)
    {
        targetOutputWatts = Mathf.Max(0f, watts);
        if (Mathf.Abs(targetOutputWatts - currentOutputWatts) < 0.001f)
        {
            ramping = false;
            return;
        }
        if (outputRampSeconds <= 0f)
        {
            if (IsPowerTransitionPaused())
            {
                rampStartWatts = currentOutputWatts;
                rampElapsed = 0f;
                ramping = true;
                return;
            }
            currentOutputWatts = targetOutputWatts;
            ramping = false;
            powerService?.RequestRecalculate();
            return;
        }
        rampStartWatts = currentOutputWatts;
        rampElapsed = 0f;
        ramping = true;
    }

    bool IsPowerTransitionPaused()
    {
        if (BuildModeController.HasActiveTool) return true;
        return GameManager.Instance != null && GameManager.Instance.State != GameState.Play;
    }
}
