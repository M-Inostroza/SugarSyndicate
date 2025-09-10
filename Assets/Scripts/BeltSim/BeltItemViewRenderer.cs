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

    readonly Queue<BeltItemView> pool = new Queue<BeltItemView>();
    readonly Dictionary<int, BeltItemView> live = new Dictionary<int, BeltItemView>();

    void OnEnable()
    {
        if (itemViewPrefab == null)
        {
            Debug.LogWarning("BeltItemViewRenderer: No itemViewPrefab assigned. Items will move but not be visible. Assign a prefab with a SpriteRenderer.");
        }
        Prewarm();
        if (BeltGraphService.Instance != null)
            BeltGraphService.Instance.OnGraphRebuilt += OnGraphRebuilt;
    }

    void OnDisable()
    {
        if (BeltGraphService.Instance != null)
            BeltGraphService.Instance.OnGraphRebuilt -= OnGraphRebuilt;
    }

    void Prewarm()
    {
        if (itemViewPrefab == null || pool.Count > 0) return;
        for (int i = 0; i < poolSize; i++)
        {
            var go = Instantiate(itemViewPrefab, transform);
            go.SetActive(false);
            pool.Enqueue(go.GetComponent<BeltItemView>() ?? go.AddComponent<BeltItemView>());
        }
    }

    void OnGraphRebuilt(BeltGraph _)
    {
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
        var go = itemViewPrefab != null ? Instantiate(itemViewPrefab, transform) : new GameObject("BeltItemView");
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
        var svc = BeltGraphService.Instance; if (svc == null) return;
        var runs = svc.Runs; if (runs == null) return;
        var seen = new HashSet<int>();

        for (int r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            var items = run.items;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
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

        RecycleMissing(seen);
    }
}
