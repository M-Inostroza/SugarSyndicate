using System;
using UnityEngine;

public class ItemAgent : MonoBehaviour
{
    Vector2Int preferredDir;
    Action<ItemAgent> releaseCallback;

    // movement state
    Vector2Int currentCell;
    Vector2Int targetCell;
    Vector3 fromWorld;
    Vector3 toWorld;

    [SerializeField] int ticksPerCell = 4;
    int stepTicksRemaining;

    bool active;
    bool awaitingDecision;
    
    public string agentId { get; private set; }
    static int nextId = 1;

    public Vector2Int CurrentCell => currentCell;
    private Direction lastIncomingDirection;

    void OnEnable()
    {
        GameTick.OnTickStart += OnTickStart;
        GameTick.OnTick += OnGameTick;
        
        agentId = $"Item_{nextId++}";
        name = agentId;
    }

    void OnDisable()
    {
        GameTick.OnTickStart -= OnTickStart;
        GameTick.OnTick -= OnGameTick;
        releaseCallback = null;
    }

    public void SpawnAt(Vector3 worldPos, Vector2Int dir, Action<ItemAgent> release = null, int ticksPerCellOverride = 0)
    {
        transform.position = worldPos;
        preferredDir = dir;
        releaseCallback = release;
        gameObject.SetActive(true);

        var gs = GridService.Instance;
        if (gs != null)
        {
            currentCell = gs.WorldToCell(worldPos);
            fromWorld = gs.CellToWorld(currentCell, transform.position.z);
        }
        else
        {
            currentCell = Vector2Int.zero;
            fromWorld = worldPos;
        }
        toWorld = fromWorld;
        targetCell = currentCell;

        ticksPerCell = ticksPerCellOverride > 0 ? ticksPerCellOverride : ticksPerCell;
        stepTicksRemaining = 0;
        awaitingDecision = false;
        active = true;
    }

    void Update()
    {
        if (!active) return;
        if (ticksPerCell <= 0)
        {
            transform.position = toWorld;
            return;
        }
        // Interpolate from end to start based on remaining ticks
        float prog = (float)stepTicksRemaining / ticksPerCell;
        transform.position = Vector3.Lerp(toWorld, fromWorld, prog);
    }

    void OnGameTick()
    {
        if (!active) return;
        
        if (stepTicksRemaining > 0)
        {
            stepTicksRemaining--;
            if (stepTicksRemaining == 0)
            {
                // Arrived at destination
                currentCell = targetCell;
                fromWorld = toWorld;
            }
        }
    }

    void OnTickStart()
    {
        if (!active || GridService.Instance == null || stepTicksRemaining > 0 || awaitingDecision) return;

        var conv = GridService.Instance.GetConveyor(currentCell);
        if (conv == null)
        {
            // No conveyor, no move.
            return;
        }

        // Conveyor dictates direction
        var dir = DirectionUtil.DirVec(conv.direction);
        var desiredCell = currentCell + dir;

        // --- New Occupancy Check ---
        // Before submitting an intent, check if the target cell is occupied.
        // This moves the responsibility to the agent, simplifying the resolver.
        var allAgents = FindObjectsByType<ItemAgent>(FindObjectsSortMode.None);
        bool targetOccupied = false;
        foreach(var otherAgent in allAgents)
        {
            if (otherAgent.CurrentCell == desiredCell)
            {
                targetOccupied = true;
                break;
            }
        }

        if (targetOccupied)
        {
            // Target is occupied, so don't even try to move there.
            return;
        }
        
        lastIncomingDirection = VecToDirection(dir);
        GridService.Instance.SubmitIntent(this, currentCell, desiredCell, lastIncomingDirection);
        awaitingDecision = true;
    }

    public void ApplyGrantedMove(Vector2Int to)
    {
        if (!active) return;

        targetCell = to;
        fromWorld = GridService.Instance.CellToWorld(currentCell, transform.position.z);
        toWorld = GridService.Instance.CellToWorld(targetCell, transform.position.z);
        stepTicksRemaining = ticksPerCell;
        awaitingDecision = false;
    }

    public void OnDecisionProcessed()
    {
        awaitingDecision = false;
    }

    public Direction GetLastIncomingDirection()
    {
        return lastIncomingDirection;
    }

    public void ReleaseToPool()
    {
        if (!active) return;
        active = false;
        GameTick.OnTickStart -= OnTickStart;
        GameTick.OnTick -= OnGameTick;
        releaseCallback?.Invoke(this);
        releaseCallback = null;
        gameObject.SetActive(false);
    }

    static Direction VecToDirection(Vector2Int d)
    {
        if (d.x > 0) return Direction.Right;
        if (d.x < 0) return Direction.Left;
        if (d.y > 0) return Direction.Up;
        return Direction.Down;
    }
}
