using UnityEngine;

public static class PowerConsumerUtil
{
    public static bool IsMachinePowered(IMachine machine)
    {
        if (machine == null) return false;
        if (machine is Component component)
        {
            var consumer = component.GetComponent<PowerConsumer>() ?? component.GetComponentInParent<PowerConsumer>();
            if (consumer != null)
                return consumer.IsPowered;
        }
        return true;
    }
}
