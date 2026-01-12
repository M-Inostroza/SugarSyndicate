using System;
using System.Collections.Generic;
using UnityEngine;

public class StorageContainerMachine : MonoBehaviour, IMachine, IMachineStorageWithCapacity
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] BeltSimulationService belt;

    [Header("Orientation")]
    [Tooltip("Output/facing vector. Input is the opposite side. Right=(1,0), Left=(-1,0), Up=(0,1), Down=(0,-1)")]
    public Vector2Int facingVec = new Vector2Int(1, 0);

    [Header("Capacity")]
    [SerializeField, Min(1)] int capacity = 40;

    [Header("Output")]
    [Tooltip("If true, releases items on GameTick; otherwise it uses frame time.")]
    [SerializeField] bool useGameTickForOutput = true;

    [Header("Debug")]
    [SerializeField] bool enableDebugLogs = false;

    [NonSerialized] public bool isGhost = false;

    public Vector2Int InputVec => new Vector2Int(-facingVec.x, -facingVec.y);
    public Vector2Int OutputVec => facingVec;
    public Vector2Int Cell => inputCell;
    public int StoredItemCount => stored.Count;
    public int Capacity => Mathf.Max(0, capacity);

    struct StoredEntry
    {
        public Item item;
        public Transform view;
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

    readonly Queue<StoredEntry> stored = new Queue<StoredEntry>();

    Vector2Int inputCell;
    Vector2Int outputFootprintCell;
    bool registered;
    FootprintBlocker outputBlocker;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (belt == null) belt = BeltSimulationService.Instance;
    }

    void Start()
    {
        if (isGhost) return;
        EnsureStorageDisplay();
        TryRegisterAsMachineAndSnap();
    }

    void OnEnable()
    {
        if (useGameTickForOutput)
        {
            try { GameTick.OnTickStart += OnTick; } catch { }
        }
    }

    void OnDisable()
    {
        if (useGameTickForOutput)
        {
            try { GameTick.OnTickStart -= OnTick; } catch { }
        }
    }

    void Update()
    {
        if (useGameTickForOutput) return;
        if (isGhost) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
        TryOutputOnce();
    }

    void OnTick()
    {
        if (!useGameTickForOutput) return;
        if (isGhost) return;
        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
        TryOutputOnce();
    }

    void OnDestroy()
    {
        try
        {
            if (useGameTickForOutput)
            {
                try { GameTick.OnTickStart -= OnTick; } catch { }
            }

            if (registered)
                MachineRegistry.Unregister(this);
            if (outputBlocker != null)
                MachineRegistry.Unregister(outputBlocker);

            if (registered)
            {
                if (grid == null) grid = GridService.Instance;
                if (grid != null)
                {
                    ClearMachineCell(inputCell);
                    ClearMachineCell(outputFootprintCell);
                }
            }
        }
        catch { }

        ClearStoredViews();
    }

    void EnsureStorageDisplay()
    {
        if (GetComponent<MachineStorageDisplay>() != null) return;
        gameObject.AddComponent<MachineStorageDisplay>();
    }

    void TryRegisterAsMachineAndSnap()
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;

        facingVec = NormalizeFacing(facingVec);
        inputCell = ComputeInputCell();
        outputFootprintCell = inputCell + OutputVec;

        grid.SetMachineCell(inputCell);
        grid.SetMachineCell(outputFootprintCell);

        transform.position = GetFootprintCenterWorld(inputCell, outputFootprintCell, transform.position.z);

        MachineRegistry.Register(this);
        outputBlocker = new FootprintBlocker(outputFootprintCell);
        MachineRegistry.Register(outputBlocker);
        registered = true;
    }

    Vector2Int ComputeInputCell()
    {
        float half = grid.CellSize * 0.5f;
        var offset = new Vector3(OutputVec.x * half, OutputVec.y * half, 0f);
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

    void ClearStoredViews()
    {
        while (stored.Count > 0)
        {
            var entry = stored.Dequeue();
            if (entry.view != null)
                ItemViewPool.Return(entry.view);
        }
    }

    public bool CanAcceptFrom(Vector2Int approachFromVec)
    {
        if (approachFromVec != InputVec) return false;
        int cap = Mathf.Max(0, capacity);
        if (cap <= 0) return false;
        return stored.Count < cap;
    }

    public bool TryStartProcess(Item item)
    {
        if (item == null) return false;
        int cap = Mathf.Max(0, capacity);
        if (stored.Count >= cap) return false;

        var view = item.view;
        if (view != null)
        {
            view.gameObject.SetActive(false);
            view.SetParent(transform, true);
        }
        item.view = null;
        stored.Enqueue(new StoredEntry { item = item, view = view });
        DLog($"[StorageContainerMachine] Stored item '{item.type}' ({stored.Count}/{cap}) at {inputCell}");
        return true;
    }

    void TryOutputOnce()
    {
        if (stored.Count == 0) return;
        if (grid == null) grid = GridService.Instance;
        if (belt == null) belt = BeltSimulationService.Instance;
        if (grid == null || belt == null) return;

        var outCell = inputCell + OutputVec * 2;
        var entry = stored.Peek();
        if (entry.item == null)
        {
            stored.Dequeue();
            return;
        }

        if (TryResolveOutputMachine(outCell, out var targetMachine))
        {
            var view = entry.view;
            if (view != null)
            {
                var parent = ContainerLocator.GetItemContainer();
                view.SetParent(parent != null ? parent : null, true);
                var world = grid.CellToWorld(outCell, view.position.z);
                view.position = world;
                view.gameObject.SetActive(true);
            }
            entry.item.view = view;

            bool ok = false;
            try { ok = targetMachine.TryStartProcess(entry.item); } catch { ok = false; }
            if (!ok)
            {
                entry.item.view = null;
                if (view != null)
                {
                    view.gameObject.SetActive(false);
                    if (view.parent != transform)
                        view.SetParent(transform, true);
                }
                return;
            }

            stored.Dequeue();
            if (entry.item.view != null)
            {
                ItemViewPool.Return(entry.item.view);
                entry.item.view = null;
            }
            return;
        }

        var cellData = grid.GetCell(outCell);
        if (cellData == null) return;
        if (cellData.type == GridService.CellType.Machine) return;
        if (cellData.hasItem) return;
        bool beltLike = cellData.type == GridService.CellType.Belt
                        || cellData.type == GridService.CellType.Junction
                        || cellData.hasConveyor
                        || cellData.conveyor != null;
        if (!beltLike) return;
        if (belt.IsVisualNearCell(outCell)) return;

        var beltView = entry.view;
        if (beltView != null)
        {
            beltView.gameObject.SetActive(false);
            var parent = ContainerLocator.GetItemContainer();
            beltView.SetParent(parent != null ? parent : null, true);
        }

        entry.item.view = beltView;
        bool spawned = belt.TrySpawnItem(outCell, entry.item);
        if (!spawned)
        {
            entry.item.view = null;
            if (beltView != null)
            {
                beltView.gameObject.SetActive(false);
                if (beltView.parent != transform)
                    beltView.SetParent(transform, true);
            }
            return;
        }

        stored.Dequeue();

        if (beltView != null)
        {
            beltView.gameObject.SetActive(true);
            var world = grid.CellToWorld(outCell, beltView.position.z);
            beltView.position = world;
        }

        belt.TryAdvanceSpawnedItem(outCell);
    }

    bool TryResolveOutputMachine(Vector2Int outCell, out IMachine targetMachine)
    {
        targetMachine = null;
        try
        {
            if (MachineRegistry.TryGet(outCell, out var machine) && machine != null)
            {
                var approachFromVec = -OutputVec;
                bool accepts = false;
                try { accepts = machine.CanAcceptFrom(approachFromVec); } catch { return false; }
                if (!accepts) return false;
                targetMachine = machine;
                return true;
            }
        }
        catch { }
        return false;
    }

    void DLog(string msg)
    {
        if (enableDebugLogs) Debug.Log(msg);
    }
}
