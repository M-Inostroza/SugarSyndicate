using System.Collections.Generic;
using UnityEngine;

// Simple renderer that spawns views and updates them from runs each LateUpdate.
[AddComponentMenu("Belts/Belt Item View Renderer")]
public class BeltItemViewRenderer : MonoBehaviour
{
    [Header("Prefab")]
    [Tooltip("Prefab with a SpriteRenderer to visualize items.")]
    [SerializeField] GameObject itemViewPrefab;

    [Header("Pool")]
    [SerializeField] int poolSize = 64;

    [Header("Debug")]
    [SerializeField] bool debugLogs = false;

    readonly Queue<BeltItemView> pool = new Queue<BeltItemView>();
    readonly Dictionary<int, BeltItemView> live = new Dictionary<int, BeltItemView>();

    static Sprite fallbackSprite;

    void OnEnable()
    {
        EnsureGraphService();
        if (itemViewPrefab == null)
        {
            Debug.LogWarning("BeltItemViewRenderer: No itemViewPrefab assigned. Using a simple square as a fallback. Assign a prefab with a SpriteRenderer for custom visuals.");
        }
        Prewarm();
        if (BeltGraphService.Instance != null)
            BeltGraphService.Instance.OnGraphRebuilt += OnGraphRebuilt;
        if (debugLogs) Debug.Log("[BeltItemViewRenderer] Enabled");
    }

    void OnDisable()
    {
        if (BeltGraphService.Instance != null)
            BeltGraphService.Instance.OnGraphRebuilt -= OnGraphRebuilt;
    }

    void EnsureGraphService()
    {
        if (BeltGraphService.Instance != null) return;
        var go = new GameObject("BeltGraphService");
        go.AddComponent<BeltGraphService>();
        if (debugLogs) Debug.Log("[BeltItemViewRenderer] Auto-created BeltGraphService");
        // BeltGraphService will auto-create BeltTickService in Start
    }

    void Prewarm()
    {
        if (pool.Count > 0) return;
        for (int i = 0; i < poolSize; i++)
        {
            var go = CreateItemGO();
            go.SetActive(false);
            pool.Enqueue(go.GetComponent<BeltItemView>() ?? go.AddComponent<BeltItemView>());
        }
        if (debugLogs) Debug.Log($"[BeltItemViewRenderer] Prewarmed pool size={pool.Count}");
    }

    GameObject CreateItemGO()
    {
        if (itemViewPrefab != null)
        {
            return Instantiate(itemViewPrefab, transform);
        }
        // Create a simple visible fallback square
        var go = new GameObject("BeltItemView");
        go.transform.SetParent(transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = GetFallbackSprite();
        sr.color = new Color(1f, 0.9f, 0.2f, 1f);
        sr.sortingOrder = 100; // ensure on top of background
        go.transform.localScale = Vector3.one * 0.5f;
        return go;
    }

    static Sprite GetFallbackSprite()
    {
        if (fallbackSprite != null) return fallbackSprite;
        const int size = 16;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.white;
        tex.SetPixels(pixels);
        tex.Apply(false);
        fallbackSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return fallbackSprite;
    }

    void OnGraphRebuilt(BeltGraph _)
    {
        if (debugLogs) Debug.Log("[BeltItemViewRenderer] Graph rebuilt event");
        // no special handling needed; views are driven from runs each frame
    }

    BeltItemView GetView()
    {
        if (pool.Count > 0)
        {
            var v = pool.Dequeue();
            v.gameObject.SetActive(true);
            return v;
        }
        var go = CreateItemGO();
        return go.GetComponent<BeltItemView>() ?? go.AddComponent<BeltItemView>();
    }

    void RecycleMissing(HashSet<int> seen)
    {
        var toRecycle = new List<int>();
        foreach (var kv in live)
            if (!seen.Contains(kv.Key)) toRecycle.Add(kv.Key);
        foreach (var id in toRecycle)
        {
            var v = live[id];
            live.Remove(id);
            v.gameObject.SetActive(false);
            pool.Enqueue(v);
        }
    }

    void LateUpdate()
    {
        var svc = BeltGraphService.Instance; if (svc == null) { if (debugLogs) Debug.LogWarning("[BeltItemViewRenderer] No BeltGraphService"); return; }
        var runs = svc.Runs; if (runs == null) { if (debugLogs) Debug.LogWarning("[BeltItemViewRenderer] No Runs"); return; }
        var seen = new HashSet<int>();
        int totalItems = 0;

        for (int r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            var items = run.items;
            totalItems += items.Count;
            for (var node = items.First; node != null; node = node.Next)
            {
                var it = node.Value;
                seen.Add(it.id);
                if (!live.TryGetValue(it.id, out var view))
                {
                    view = GetView();
                    view.id = it.id;
                    live[it.id] = view;
                }
                run.PositionAt(it.offset, out var pos, out var fwd);
                view.transform.position = pos;
                var ang = Mathf.Atan2(fwd.y, fwd.x) * Mathf.Rad2Deg;
                view.transform.rotation = Quaternion.Euler(0,0,ang);
            }
        }

        if (debugLogs) Debug.Log($"[BeltItemViewRenderer] runs={runs.Count} items={totalItems} liveViews={live.Count} pool={pool.Count}");
        RecycleMissing(seen);
    }
}
