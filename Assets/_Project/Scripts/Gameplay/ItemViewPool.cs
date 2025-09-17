using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Small object pool for item view GameObjects. Now supports multiple prefabs.
/// </summary>
public class ItemViewPool : MonoBehaviour
{
    public static ItemViewPool Instance { get; private set; }

    [SerializeField] GameObject itemPrefab; // default/fallback prefab for legacy Get()
    [SerializeField, Min(0)] int prewarm = 32;
    [SerializeField, Min(0)] int maxPoolSize = 1024; // hard safety cap per prefab pool

    // Marker added to pooled instances so we can return them to the right sub-pool
    class PooledItemMarker : MonoBehaviour { public GameObject sourcePrefab; }

    class PrefabPoolEntry
    {
        public GameObject prefab;
        public readonly Queue<Transform> pool = new Queue<Transform>();
        public Transform root;
        public int maxPoolSize;
        public bool initialized;
        public int prewarm;
    }

    // Multiple pools keyed by prefab
    readonly Dictionary<GameObject, PrefabPoolEntry> prefabPools = new Dictionary<GameObject, PrefabPoolEntry>();

    Transform poolRoot;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        poolRoot = new GameObject("ItemViewPool_Container").transform;
        poolRoot.SetParent(transform, false);
        DontDestroyOnLoad(gameObject);

        // If a default prefab was assigned in inspector, ensure its pool
        if (itemPrefab != null)
        {
            Ensure(itemPrefab, prewarm);
        }
    }

    PrefabPoolEntry GetOrCreateEntry(GameObject prefab)
    {
        if (prefab == null) return null;
        if (!prefabPools.TryGetValue(prefab, out var entry))
        {
            entry = new PrefabPoolEntry
            {
                prefab = prefab,
                root = new GameObject($"ItemPool_{prefab.name}").transform,
                maxPoolSize = maxPoolSize,
                prewarm = 0
            };
            entry.root.SetParent(poolRoot, false);
            prefabPools[prefab] = entry;
        }
        return entry;
    }

    void PrewarmEntry(PrefabPoolEntry entry, int count)
    {
        if (entry == null || entry.initialized) return;
        entry.initialized = true;
        int c = Mathf.Max(0, count);
        for (int i = 0; i < c; i++)
        {
            var t = CreateNew(entry);
            Return(t);
        }
    }

    Transform CreateNew(PrefabPoolEntry entry)
    {
        if (entry == null || entry.prefab == null)
        {
            Debug.LogError("ItemViewPool: prefab not set for pool.");
            return null;
        }
        var go = Instantiate(entry.prefab, entry.root);
        go.name = entry.prefab.name + "(Pooled)";
        var marker = go.GetComponent<PooledItemMarker>();
        if (marker == null) marker = go.AddComponent<PooledItemMarker>();
        marker.sourcePrefab = entry.prefab;
        return go.transform;
    }

    // Ensure a pool exists for this prefab and optionally prewarm
    public static void Ensure(GameObject prefab, int optionalPrewarm = 0)
    {
        if (prefab == null) return;
        if (Instance == null)
        {
            var go = new GameObject("ItemViewPool");
            var comp = go.AddComponent<ItemViewPool>();
            comp.itemPrefab = prefab; // set default
            comp.prewarm = optionalPrewarm;
        }
        var entry = Instance.GetOrCreateEntry(prefab);
        if (entry != null)
        {
            entry.prewarm = Mathf.Max(entry.prewarm, optionalPrewarm);
            if (!entry.initialized)
                Instance.PrewarmEntry(entry, entry.prewarm);
        }
    }

    // Backward compatible: uses default prefab set via Ensure/inspector
    public static Transform Get(Vector3 position, Quaternion rotation, Transform parent = null)
    {
        if (Instance == null)
        {
            Debug.LogError("ItemViewPool.Get called before Ensure/awake.");
            return null;
        }
        if (Instance.itemPrefab == null)
        {
            Debug.LogError("ItemViewPool.Get (legacy) requires a default prefab set via Ensure or inspector.");
            return null;
        }
        return Get(Instance.itemPrefab, position, rotation, parent);
    }

    // New: request a view for a specific prefab
    public static Transform Get(GameObject prefab, Vector3 position, Quaternion rotation, Transform parent = null)
    {
        if (prefab == null)
        {
            Debug.LogError("ItemViewPool.Get(prefab, ...) called with null prefab.");
            return null;
        }
        if (Instance == null)
        {
            Debug.LogError("ItemViewPool.Get(prefab, ...) called before Ensure/awake.");
            return null;
        }
        var entry = Instance.GetOrCreateEntry(prefab);
        if (entry == null)
            return null;

        Transform t = null;
        while (entry.pool.Count > 0 && t == null)
            t = entry.pool.Dequeue();
        if (t == null)
            t = Instance.CreateNew(entry);
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
        var marker = t.GetComponent<PooledItemMarker>();
        if (marker != null && marker.sourcePrefab != null)
        {
            var entry = Instance.GetOrCreateEntry(marker.sourcePrefab);
            if (entry != null)
            {
                if (entry.pool.Count >= Instance.maxPoolSize)
                {
                    Destroy(t.gameObject);
                    return;
                }
                t.SetParent(entry.root, false);
                t.gameObject.SetActive(false);
                entry.pool.Enqueue(t);
                return;
            }
        }
        // Fallback if no marker: just disable and destroy to avoid leaking into wrong pool
        Destroy(t.gameObject);
    }
}
