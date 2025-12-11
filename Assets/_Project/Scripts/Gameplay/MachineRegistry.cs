using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared contract + registry for belt-fed machines so the belt simulation
/// can hand off items without knowing concrete machine types.
/// </summary>
public interface IMachine
{
    Vector2Int Cell { get; }
    Vector2Int InputVec { get; }
    bool CanAcceptFrom(Vector2Int approachFromVec);
    bool TryStartProcess(Item item);
}

public static class MachineRegistry
{
    static readonly Dictionary<Vector2Int, IMachine> machines = new();

    public static bool TryGet(Vector2Int cell, out IMachine machine)
        => machines.TryGetValue(cell, out machine);

    public static void Register(IMachine machine)
    {
        if (machine == null) return;
        machines[machine.Cell] = machine;
    }

    public static void Unregister(IMachine machine)
    {
        if (machine == null) return;
        if (machines.TryGetValue(machine.Cell, out var existing) && ReferenceEquals(existing, machine))
        {
            machines.Remove(machine.Cell);
        }
    }
}
