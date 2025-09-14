using UnityEngine;

public static class ContainerLocator
{
    static Transform itemContainerCache;
    static Transform beltContainerCache;

    static readonly string[] itemNames = new[] { "Item Container", "Item container", "ItemContainer", "Items", "ItemParent" };
    static readonly string[] beltNames = new[] { "Belt Container", "Belt container", "BeltContainer", "Belts", "BeltParent" };

    public static Transform GetItemContainer()
    {
        if (itemContainerCache != null) return itemContainerCache;
        foreach (var n in itemNames)
        {
            var go = GameObject.Find(n);
            if (go != null) { itemContainerCache = go.transform; return itemContainerCache; }
        }
        return null;
    }

    public static Transform GetBeltContainer()
    {
        if (beltContainerCache != null) return beltContainerCache;
        foreach (var n in beltNames)
        {
            var go = GameObject.Find(n);
            if (go != null) { beltContainerCache = go.transform; return beltContainerCache; }
        }
        return null;
    }
}