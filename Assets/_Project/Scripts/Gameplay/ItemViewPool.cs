using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Very small generic object pool for item view GameObjects to reduce
/// runtime allocations and Instantiate/Destroy spikes.
/// </summary>
public class ItemViewPool : MonoBehaviour
{
    public static ItemViewPool Instance { get; private set; }

    [SerializeField] GameObject itemPrefab; // fallback assign in Inspector or first Initialize call
    [SerializeField, Min(0)] int prewarm = 32;
    [SerializeField, Min(0)] int maxPoolSize = 1024; // hard safety cap

    readonly Queue<Transform> pool = new Queue<Transform>();
    Transform poolRoot;
    bool initialized;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        poolRoot = new GameObject("ItemViewPool_Container").transform;
        poolRoot.SetParent(transform, false);
        if (itemPrefab != null)
            Prewarm();
        DontDestroyOnLoad(gameObject);
    }

    void Prewarm()
    {
        if (initialized) return;
        initialized = true;
        int count = Mathf.Max(0, prewarm);
        for (int i = 0; i < count; i++)
        {
            var t = CreateNew();
            Return(t);
        }
    }

    Transform CreateNew()
    {
        if (itemPrefab == null)
        {
            Debug.LogError("ItemViewPool: itemPrefab not set.");
            return null;
        }
        var go = Instantiate(itemPrefab, poolRoot);
        go.name = itemPrefab.name + "(Pooled)";
        return go.transform;
    }

    public static void Ensure(GameObject prefab, int optionalPrewarm = 0)
    {
        if (Instance == null)
        {
            var go = new GameObject("ItemViewPool");
            var comp = go.AddComponent<ItemViewPool>();
            comp.itemPrefab = prefab;
            comp.prewarm = optionalPrewarm;
        }
        else if (Instance.itemPrefab == null && prefab != null)
        {
            Instance.itemPrefab = prefab;
        }
        if (!Instance.initialized && Instance.itemPrefab != null)
        {
            Instance.prewarm = Mathf.Max(Instance.prewarm, optionalPrewarm);
            Instance.Prewarm();
        }
    }

    public static Transform Get(Vector3 position, Quaternion rotation, Transform parent = null)
    {
        if (Instance == null)
        {
            Debug.LogError("ItemViewPool.Get called before Ensure/awake.");
            return null;
        }
        Transform t = null;
        while (Instance.pool.Count > 0 && t == null)
            t = Instance.pool.Dequeue();
        if (t == null)
            t = Instance.CreateNew();
        if (t == null) return null;
        var go = t.gameObject;
        if (!go.activeSelf) go.SetActive(true);
        if (parent != null) t.SetParent(parent, false);
        t.SetPositionAndRotation(position, rotation);
        return t;
    }

    public static void Return(Transform t)
    {
        if (t == null || Instance == null) return;
        if (Instance.pool.Count >= Instance.maxPoolSize)
        {
            Destroy(t.gameObject); // exceed cap
            return;
        }
        t.SetParent(Instance.poolRoot, false);
        t.gameObject.SetActive(false);
        Instance.pool.Enqueue(t);
    }
}
