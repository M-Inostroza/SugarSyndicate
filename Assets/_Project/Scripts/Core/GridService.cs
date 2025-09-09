using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class GridService : MonoBehaviour
{
    public static GridService Instance { get; private set; }

    [SerializeField] Vector2 origin = Vector2.zero;
    [SerializeField, Min(0.01f)] float cellSize = 1f;
    [SerializeField] Vector2Int gridSize = new Vector2Int(20, 12);

    [Header("Debug")]
    [SerializeField] bool debugLogging = false;

    readonly Dictionary<Vector2Int, Cell> cells = new();
    readonly Dictionary<Vector2Int, List<Intent>> intents = new();
    readonly Dictionary<Vector2Int, Direction> lastServed = new();

    public class Cell
    {
        public bool hasFloor;
        public bool hasConveyor;
        public bool hasMachine;
        public Conveyor conveyor;
    }

    public struct Intent
    {
        public ItemAgent agent;
        public Vector2Int from;
        public Vector2Int to;
        public Direction incoming;
    }

    void Awake()
    {
        if (Instance) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        WarmCells();

        GameTick.OnTickStart += OnTickStart;
        GameTick.OnTickEnd += OnTickEnd;
    }

    void OnDestroy()
    {
        GameTick.OnTickStart -= OnTickStart;
        GameTick.OnTickEnd -= OnTickEnd;
    }

    void OnTickStart()
    {
        intents.Clear();
    }

    void OnTickEnd()
    {
        ResolveIntents();
    }

    void WarmCells()
    {
        cells.Clear();
        for (int y = 0; y < gridSize.y; y++)
            for (int x = 0; x < gridSize.x; x++)
                cells[new Vector2Int(x, y)] = new Cell();
    }

    public Vector2Int WorldToCell(Vector3 world)
    {
        Vector2 p = (Vector2)world - origin;
        return new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.y / cellSize));
    }

    public Vector3 CellToWorld(Vector2Int cell, float z = 0)
    {
        Vector2 w = origin + (Vector2)cell * cellSize + new Vector2(cellSize * 0.5f, cellSize * 0.5f);
        return new Vector3(w.x, w.y, z);
    }

    public bool InBounds(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < gridSize.x && c.y < gridSize.y;

    public Cell GetCell(Vector2Int c) => cells.TryGetValue(c, out var cell) ? cell : null;

    public void SetConveyor(Vector2Int c, Conveyor conveyor)
    {
        if (!InBounds(c)) return;
        var cell = GetCell(c);
        if (cell == null) return;
        cell.conveyor = conveyor;
        cell.hasConveyor = conveyor != null;
    }

    public Conveyor GetConveyor(Vector2Int c)
    {
        var cell = GetCell(c);
        return cell != null ? cell.conveyor : null;
    }

    public void SubmitIntent(ItemAgent agent, Vector2Int from, Vector2Int to, Direction incoming)
    {
        if (!InBounds(to)) return;
        if (!intents.TryGetValue(to, out var list))
        {
            list = new List<Intent>(2);
            intents[to] = list;
        }
        list.Add(new Intent { agent = agent, from = from, to = to, incoming = incoming });
    }

    void ResolveIntents()
    {
        // 1. SETUP: Collect all initial state
        var allAgents = Object.FindObjectsByType<ItemAgent>(FindObjectsSortMode.None);

        var cellOccupants = new Dictionary<Vector2Int, ItemAgent>();
        foreach (var agent in allAgents)
        {
            if (!cellOccupants.ContainsKey(agent.CurrentCell))
            {
                cellOccupants[agent.CurrentCell] = agent;
            }
            else
            {
                if(debugLogging) Debug.LogWarning($"Merge detected at {agent.CurrentCell} during setup. Ignoring duplicate agent.");
            }
        }

        var agentIntents = new Dictionary<ItemAgent, Intent>();
        var allSubmittedAgents = new HashSet<ItemAgent>();
        foreach (var intentList in intents.Values)
        {
            foreach (var intent in intentList)
            {
                allSubmittedAgents.Add(intent.agent);
                if (cellOccupants.TryGetValue(intent.from, out var occupant) && occupant == intent.agent)
                {
                    agentIntents[intent.agent] = intent;
                }
            }
        }

        var grants = new Dictionary<ItemAgent, Vector2Int>();
        var processedAgents = new HashSet<ItemAgent>();

        // 2. PHASE 1: Detect and resolve cycles
        var path = new List<ItemAgent>();
        var visited = new HashSet<ItemAgent>();
        foreach (var agent in agentIntents.Keys)
        {
            if (!visited.Contains(agent))
            {
                FindCycles(agent, agentIntents, cellOccupants, path, visited, processedAgents, grants);
            }
        }

        // 3. PHASE 2: Resolve chains iteratively
        bool changedInPass;
        do
        {
            changedInPass = false;
            foreach (var agent in agentIntents.Keys)
            {
                if (processedAgents.Contains(agent)) continue;

                var intent = agentIntents[agent];
                var targetCell = intent.to;

                bool targetIsAvailable = !cellOccupants.ContainsKey(targetCell) ||
                                         (cellOccupants.TryGetValue(targetCell, out var occupant) && grants.ContainsKey(occupant));

                if (targetIsAvailable)
                {
                    var contenders = new List<Intent>();
                    if (intents.TryGetValue(targetCell, out var intentListForTarget))
                    {
                        foreach (var contenderIntent in intentListForTarget)
                        {
                            if (!processedAgents.Contains(contenderIntent.agent) && agentIntents.ContainsKey(contenderIntent.agent))
                            {
                                contenders.Add(contenderIntent);
                            }
                        }
                    }

                    if (contenders.Count > 0)
                    {
                        ItemAgent winner;
                        if (contenders.Count == 1)
                        {
                            winner = contenders[0].agent;
                        }
                        else
                        {
                            Direction lastDir = lastServed.TryGetValue(targetCell, out var d) ? d : Direction.Left;
                            winner = contenders.FirstOrDefault(c => c.incoming != lastDir).agent ?? contenders[0].agent;
                        }

                        grants[winner] = targetCell;
                        lastServed[targetCell] = agentIntents[winner].incoming;

                        foreach (var contender in contenders)
                        {
                            processedAgents.Add(contender.agent);
                        }
                        changedInPass = true;
                    }
                }
            }
        } while (changedInPass);

        // 4. FINALIZATION: Notify all agents
        foreach (var agent in allSubmittedAgents)
        {
            if (grants.TryGetValue(agent, out var grantedCell))
            {
                agent.ApplyGrantedMove(grantedCell);
            }
            else
            {
                agent.OnDecisionProcessed();
            }
        }
    }

    private void FindCycles(ItemAgent currentAgent, Dictionary<ItemAgent, Intent> agentIntents,
        Dictionary<Vector2Int, ItemAgent> cellOccupants, List<ItemAgent> path, HashSet<ItemAgent> visited,
        HashSet<ItemAgent> processedAgents, Dictionary<ItemAgent, Vector2Int> grants)
    {
        path.Add(currentAgent);
        visited.Add(currentAgent);

        if (agentIntents.TryGetValue(currentAgent, out var intent))
        {
            var targetCell = intent.to;
            if (cellOccupants.TryGetValue(targetCell, out var nextAgent))
            {
                int cycleStartIndex = path.IndexOf(nextAgent);
                if (cycleStartIndex != -1) // Cycle detected
                {
                    for (int i = cycleStartIndex; i < path.Count; i++)
                    {
                        var agentInCycle = path[i];
                        if (processedAgents.Contains(agentInCycle)) continue;

                        var cycleIntent = agentIntents[agentInCycle];
                        grants[agentInCycle] = cycleIntent.to;
                        processedAgents.Add(agentInCycle);
                    }
                }
                else if (!visited.Contains(nextAgent))
                {
                    FindCycles(nextAgent, agentIntents, cellOccupants, path, visited, processedAgents, grants);
                }
            }
        }
        path.RemoveAt(path.Count - 1);
    }
}
