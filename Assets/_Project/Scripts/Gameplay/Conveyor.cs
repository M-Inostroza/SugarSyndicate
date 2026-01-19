using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(Transform))]
public class Conveyor : MonoBehaviour, IConveyor
{
    public Direction direction = Direction.Right;
    [Min(1)] public int ticksPerCell = 4;

    Vector2Int lastCell;
    GridService grid;
    
    // Flag to mark this conveyor as a ghost (visual only, should not be registered)
    [System.NonSerialized]
    public bool isGhost = false;

    public Vector2Int DirVec() => DirectionUtil.DirVec(direction);

    void Awake()
    {
        // Don't do any registration work if this is a ghost conveyor
        if (isGhost) return;
        
        // Cache initial cell; avoid mutating runtime services while not playing
        lastCell = GetCellForPosition(transform.position);
        // Do NOT touch GridService here; its Awake may not have warmed the grid yet when scene loads
        // Registration is done in Start to avoid race with GridService.Awake
    }

    void Start()
    {
        if (Application.isPlaying)
        {
            // Don't register ghost conveyors with GridService or simulation
            if (isGhost)
            {
                return;
            }
            
            // Check if there's already a conveyor at this position
            var gs = GetGridService();
            if (gs != null)
            {
                var currentCell = gs.WorldToCell(transform.position);
                var existing = gs.GetConveyor(currentCell);
                if (existing != null && existing != this && !existing.isGhost)
                {
                    // There's already a real conveyor here, destroy this one
                    Debug.Log($"[Conveyor] Duplicate conveyor detected at {currentCell}, destroying duplicate.");
                    Destroy(gameObject);
                    return;
                }
            }
            
            // First, register with GridService now that all Awakes have run
            RegisterWithGridService();
            // Then register with the belt simulation so it can tick us
            BeltSimulationService.Instance?.RegisterConveyor(this);
        }
    }

    void OnEnable()
    {
        if (!Application.isPlaying) return;
        UndergroundVisibilityRegistry.RegisterBelt(this);
    }

    void OnDisable()
    {
        if (!Application.isPlaying) return;
        UndergroundVisibilityRegistry.UnregisterBelt(this);
    }

    void Update()
    {
        // Don't update grid registration for ghost conveyors
        if (isGhost) return;
        
        if (!Application.isPlaying)
        {
            // Editor-time grid snapping while moving in Scene view
            var gs = GetGridService();
            if (gs == null) return;

            float z = transform.position.z;
            var currentCell = gs.WorldToCell(transform.position);
            var world = gs.CellToWorld(currentCell, z);
            if ((world - transform.position).sqrMagnitude > 1e-6f)
                transform.position = world;
            return;
        }

        // Runtime: track cell changes and update grid + belt graph incrementally
        var rgs = GetGridService();
        if (rgs == null) return;

        var current = rgs.WorldToCell(transform.position);
        if (current != lastCell)
        {
            SetConveyorAtCell(lastCell, null);
            TrySetConveyorSafe(current, this);
            lastCell = current;
            BeltSimulationService.Instance?.RegisterConveyor(this);
        }
    }

    void OnDestroy()
    {
        if (Application.isPlaying && !isGhost)
        {
            SetConveyorAtCell(lastCell, null);
            BeltSimulationService.Instance?.UnregisterConveyor(this);
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
        var gs = GetGridService();
        if (gs == null) return;
        var cell = gs.WorldToCell(transform.position);
        var world = gs.CellToWorld(cell, transform.position.z);
        if ((world - transform.position).sqrMagnitude > 1e-6f)
            transform.position = world;
    }
#endif

    void RegisterWithGridService()
    {
        var gs = GetGridService();
        if (gs == null) return;

        var currentCell = gs.WorldToCell(transform.position);

        // Only register if not a ghost
        if (!isGhost)
        {
            TrySetConveyorSafe(currentCell, this);
            lastCell = currentCell;
        }
    }

    void TrySetConveyorSafe(Vector2Int cell, Conveyor conveyor)
    {
        // check for an existing conveyor in the cell
        var gs = GetGridService();
        if (gs == null) return;
        var existing = gs.GetConveyor(cell);
        if (existing != null && existing != this)
        {
            // Check if existing is a ghost and this is not
            if (existing.isGhost && !this.isGhost)
            {
                // Replace ghost with real conveyor
                Destroy(existing.gameObject);
                gs.SetConveyor(cell, conveyor);
                return;
            }
            
            // Check if this is a ghost and existing is real
            if (this.isGhost && !existing.isGhost)
            {
                // Don't replace real conveyor with ghost, destroy this ghost instead
                Debug.Log($"Ghost conveyor trying to replace real conveyor at {cell}, destroying ghost.");
                Destroy(gameObject);
                return;
            }
            
            // if placing opposite direction (head-on), keep the earlier one and destroy the new one to avoid a crashy cycle
            var dvExisting = existing.DirVec();
            var dvNew = this.DirVec();
            if (dvExisting == -dvNew)
            {
                Debug.LogWarning($"Conveyor collision head-on at {cell}, destroying the new one to avoid cycle.");
                Destroy(gameObject);
                return;
            }
            // otherwise, replace existing with this (simple policy) or keep existing; here we keep existing
            Debug.LogWarning($"Conveyor already present at {cell}, keeping existing.");
            return;
        }
        gs.SetConveyor(cell, conveyor);
    }

    void SetConveyorAtCell(Vector2Int cell, Conveyor conveyor)
    {
        var gs = GetGridService();
        if (gs == null) return;
        gs.SetConveyor(cell, conveyor);
    }

    Vector2Int GetCellForPosition(Vector3 pos)
    {
        var gs = GetGridService();
        if (gs == null) return default;
        return gs.WorldToCell(pos);
    }

    GridService GetGridService()
    {
        if (grid != null) return grid;
        grid = GridService.Instance;
        if (grid == null)
            grid = FindAnyObjectByType<GridService>();
        return grid;
    }
}
