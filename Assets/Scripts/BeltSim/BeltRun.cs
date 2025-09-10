using System;
using System.Collections.Generic;
using UnityEngine;

// A contiguous belt run as a polyline (cell centers). Items are tracked by offset along the run length.
public class BeltRun
{
    // Immutable geometry
    public readonly List<Vector3> points = new List<Vector3>(32); // world positions of segment vertices
    public readonly List<float> segLen = new List<float>(32);
    public float totalLen;

    // Runtime items in ascending offset order (no boxing/alloc in hot path)
    public readonly List<BeltItem> items = new List<BeltItem>(32);

    public float speed = 2f;      // units per second
    public float minSpacing = 0.6f; // minimal distance between item noses

    // Build polyline from grid cells with optional converter (recommended). points must contain at least two points.
    public void BuildFromCells(IReadOnlyList<Vector2Int> cells, Func<Vector2Int, Vector3> cellToWorld)
    {
        points.Clear(); segLen.Clear(); items.Clear(); totalLen = 0f;
        for (int i = 0; i < cells.Count; i++)
        {
            Vector3 p = cellToWorld != null
                ? cellToWorld(cells[i])
                : new Vector3(cells[i].x + 0.5f, cells[i].y + 0.5f, 0f);
            points.Add(p);
        }
        for (int i = 1; i < points.Count; i++)
        {
            float d = Vector3.Distance(points[i - 1], points[i]);
            segLen.Add(d);
            totalLen += d;
        }
        // Make sure spacing is not larger than run length so at least one item can be admitted
        if (totalLen > 0f && minSpacing > totalLen) minSpacing = totalLen;
    }

    // Legacy helper for callers that don't pass a converter (assumes cell size=1 at origin)
    public void BuildFromCells(IReadOnlyList<Vector2Int> cells)
        => BuildFromCells(cells, null);

    // Get world position/forward at arc offset (clamped inside run)
    public void PositionAt(float offset, out Vector3 pos, out Vector3 fwd)
    {
        offset = Mathf.Clamp(offset, 0, Mathf.Max(0.0001f, totalLen));
        float t = offset;
        for (int i = 0; i < segLen.Count; i++)
        {
            float s = segLen[i];
            if (t <= s)
            {
                var a = points[i];
                var b = points[i + 1];
                float u = s > 0 ? t / s : 0f;
                pos = Vector3.Lerp(a, b, u);
                fwd = (b - a).normalized;
                return;
            }
            t -= s;
        }
        pos = points[points.Count - 1];
        fwd = (points[points.Count - 1] - points[points.Count - 2]).normalized;
    }

    // Admission: try to add an item at head (offset=0). Respects spacing to first item.
    public bool TryEnqueue(int itemId)
    {
        float headClear = items.Count == 0 ? float.MaxValue : items[0].offset;
        if (headClear < minSpacing) return false;
        items.Insert(0, new BeltItem { id = itemId, offset = 0f });
        return true;
    }

    // Attempt to advance items by dt*speed while respecting spacing; returns ejected items at tail (offset>=totalLen)
    public void Advance(float dt, bool tailBlocked, List<BeltItem> ejected)
    {
        if (items.Count == 0) return;
        // forward pass: push by kinematics, backward pass: enforce spacing by clamping
        float delta = speed * dt;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            it.offset += delta;
            items[i] = it;
        }
        // If tail is blocked, clamp last item to totalLen (can't go past end)
        if (tailBlocked)
        {
            int last = items.Count - 1;
            var tail = items[last];
            if (tail.offset > totalLen) { tail.offset = totalLen; items[last] = tail; }
        }
        // enforce spacing from tail back to head: ensure next.offset - cur.offset >= minSpacing
        for (int i = items.Count - 2; i >= 0; i--)
        {
            var a = items[i];
            var b = items[i + 1];
            float maxA = b.offset - minSpacing;
            if (a.offset > maxA)
            {
                a.offset = maxA;
                items[i] = a;
            }
        }
        // collect ejected from the end (only if not blocked)
        if (!tailBlocked)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (items[i].offset >= totalLen)
                {
                    ejected.Add(items[i]);
                    items.RemoveAt(i);
                }
                else break;
            }
        }
    }
}
