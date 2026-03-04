using System;
using System.Collections.Generic;
using UnityEngine;

public class SolarPanelMachine : MonoBehaviour, IMachine, IPowerSourceNode, IPowerSourceDirectional, IGhostState
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;

    [Header("Orientation")]
    [Tooltip("Facing vector. Horizontal only: Right=(1,0), Left=(-1,0).")]
    public Vector2Int facingVec = new Vector2Int(1, 0);

    public enum PowerOutputFace
    {
        BaseCell,
        FacingCell
    }

    [Header("Power Output")]
    [SerializeField] PowerOutputFace outputFace = PowerOutputFace.FacingCell;

    [Header("Power")]
    [SerializeField, Min(0f)] float dayOutputWatts = 40f;
    [SerializeField, Range(0f, 1f)] float nightOutputMultiplier = 0.2f;

    [Header("Power Ramp")]
    [SerializeField, Min(0f)] float outputRampSeconds = 10f;

    [NonSerialized] public bool isGhost = false;
    public bool IsGhost => isGhost;

    public Vector2Int InputVec => Vector2Int.zero;
    public Vector2Int Cell => baseCell;
    public IEnumerable<Vector2Int> PowerCells
    {
        get
        {
            yield return outputFace == PowerOutputFace.BaseCell ? baseCell : frontCell;
        }
    }

    public bool TryGetOutputDirection(Vector2Int cell, out Vector2Int direction)
    {
        direction = Vector2Int.zero;

        if (outputFace == PowerOutputFace.BaseCell)
        {
            if (cell != baseCell) return false;
            direction = new Vector2Int(-facingVec.x, -facingVec.y);
        }
        else
        {
            if (cell != frontCell) return false;
            direction = facingVec;
        }

        return direction != Vector2Int.zero;
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
    Vector2Int midCell;
    Vector2Int frontCell;
    bool registered;
    readonly List<FootprintBlocker> footprintBlockers = new();
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

    void OnEnable()
    {
        UndergroundVisibilityRegistry.RegisterOverlay(this);
    }

    void OnDisable()
    {
        UndergroundVisibilityRegistry.UnregisterOverlay(this);
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
            for (int i = 0; i < footprintBlockers.Count; i++)
            {
                if (footprintBlockers[i] != null)
                    MachineRegistry.Unregister(footprintBlockers[i]);
            }
            footprintBlockers.Clear();
        }

        if (timeManager != null)
            timeManager.OnPhaseChanged -= HandlePhaseChanged;

        if (powerService == null) powerService = PowerService.Instance;
        powerService?.UnregisterSource(this);

        if (grid == null) grid = GridService.Instance;
        if (grid != null)
        {
            ClearMachineCell(baseCell);
            ClearMachineCell(midCell);
            ClearMachineCell(frontCell);
        }
    }

    void TryRegisterAsMachineAndSnap()
    {
        if (grid == null) return;
        facingVec = NormalizeFacing(facingVec);
        baseCell = ComputeBaseCell();
        midCell = baseCell + facingVec;
        frontCell = baseCell + (facingVec * 2);

        grid.SetMachineCell(baseCell);
        grid.SetMachineCell(midCell);
        grid.SetMachineCell(frontCell);

        transform.position = GetFootprintCenterWorld(baseCell, midCell, frontCell, transform.position.z);

        MachineRegistry.Register(this);
        RegisterFootprintBlocker(midCell);
        RegisterFootprintBlocker(frontCell);
        registered = true;
    }

    Vector2Int ComputeBaseCell()
    {
        float oneCell = grid.CellSize;
        var offset = new Vector3(facingVec.x * oneCell, facingVec.y * oneCell, 0f);
        return grid.WorldToCell(transform.position - offset);
    }

    static Vector2Int NormalizeFacing(Vector2Int dir)
    {
        return dir.x < 0 ? Vector2Int.left : Vector2Int.right;
    }

    void RegisterFootprintBlocker(Vector2Int cell)
    {
        var blocker = new FootprintBlocker(cell);
        footprintBlockers.Add(blocker);
        MachineRegistry.Register(blocker);
    }

    Vector3 GetFootprintCenterWorld(Vector2Int a, Vector2Int b, Vector2Int c, float z)
    {
        if (grid == null) return transform.position;
        var w1 = grid.CellToWorld(a, z);
        var w2 = grid.CellToWorld(b, z);
        var w3 = grid.CellToWorld(c, z);
        return (w1 + w2 + w3) / 3f;
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
