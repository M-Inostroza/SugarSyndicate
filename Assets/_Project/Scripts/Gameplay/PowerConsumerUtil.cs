using UnityEngine;

public static class PowerConsumerUtil
{
    public static bool IsMachinePowered(IMachine machine)
    {
        if (machine == null) return false;
        if (machine is IPowerConsumer powerConsumer)
            return IsConsumerPowered(powerConsumer);
        return true;
    }

    public static bool IsConsumerPowered(IPowerConsumer consumer)
    {
        if (consumer == null) return false;
        if (consumer.GetConsumptionWatts() <= 0f) return true;
        var power = PowerService.Instance;
        if (power == null) return false;

        Vector2Int cell;
        if (consumer is IMachine machine)
        {
            cell = machine.Cell;
        }
        else if (consumer is Component component)
        {
            var grid = GridService.Instance;
            if (grid == null) return false;
            cell = grid.WorldToCell(component.transform.position);
        }
        else
        {
            return false;
        }

        if (!power.IsCellPoweredOrAdjacent(cell) || !power.IsConsumerFullyCharged(consumer))
            return false;
        return power.HasPowerFor(consumer, consumer.GetConsumptionWatts());
    }
}
