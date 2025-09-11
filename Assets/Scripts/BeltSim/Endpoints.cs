using System.Collections.Generic;
using UnityEngine;

// Base endpoint with a small queue
public abstract class BeltEndpoint
{
    protected readonly Queue<int> queue = new Queue<int>(16);

    // Called by graph/tick to try admit an item onto a run
    public virtual bool TryOutputTo(BeltRun run)
    {
        if (queue.Count == 0) return false;
        if (!run.TryEnqueue(queue.Peek())) return false;
        queue.Dequeue();
        return true;
    }

    // Called when an item reaches the end of a run
    public virtual void OnInputItem(int itemId)
    {
        queue.Enqueue(itemId);
    }

    // Producers push here
    public void Produce(int itemId) => queue.Enqueue(itemId);
}

// N->1 merger with round-robin fairness handled internally
public class MergerEndpoint : BeltEndpoint
{
    // per-source queues for fairness
    readonly Dictionary<int, Queue<int>> sources = new Dictionary<int, Queue<int>>();
    readonly List<int> round = new List<int>();
    int turn;

    // Optional: include base queue as a source to prevent starvation
    const int BASE_SRC = -1;

    // Braiding with the belt stream (used for self-loops): when enabled, only output on alternate ticks
    bool braidWithStream = false;
    bool queueTurn = true;         // when false, it's stream's turn so we hold
    long lastTickChecked = -1;

    public void EnableStreamBraiding()
    {
        braidWithStream = true;
        queueTurn = true;
        lastTickChecked = -1;
    }

    // Optional: when caller doesn't provide a source, route to base-source so it participates in fairness
    public override void OnInputItem(int itemId)
    {
        if (!sources.TryGetValue(BASE_SRC, out var q))
        {
            q = new Queue<int>(8);
            sources[BASE_SRC] = q;
            round.Add(BASE_SRC);
        }
        q.Enqueue(itemId);
    }

    // Source-aware admission used by TickService
    public void OnInputItemFrom(int sourceRunIndex, int itemId)
    {
        if (!sources.TryGetValue(sourceRunIndex, out var q))
        {
            q = new Queue<int>(8);
            sources[sourceRunIndex] = q;
            round.Add(sourceRunIndex);
        }
        q.Enqueue(itemId);
    }

    public override bool TryOutputTo(BeltRun run)
    {
        // Braided junction handling: alternate with stream when enabled.
        if (braidWithStream)
        {
            // Update once per game tick
            long t = GameTick.TickIndex;
            if (t != lastTickChecked)
            {
                lastTickChecked = t;
                // If it was stream's turn and the stream exists, hand turn back to queue this tick
                if (!queueTurn && run != null && run.items.Count > 0)
                {
                    queueTurn = true;
                }
                // If no items on belt, keep queueTurn as-is (defaults true on start)
            }
            if (!queueTurn)
            {
                return false; // wait for the stream to pass this tick
            }
        }

        // If we only have the base queue, we still use the round list (it contains BASE_SRC)
        if (sources.Count == 0)
        {
            // Fallback to base queue behavior if no sources were ever registered
            return base.TryOutputTo(run);
        }

        if (round.Count == 0) return false;
        int n = round.Count;
        for (int k = 0; k < n; k++)
        {
            int idx = (turn + k) % n;
            int s = round[idx];
            if (!sources.TryGetValue(s, out var q) || q.Count == 0) continue;
            if (run.TryEnqueue(q.Peek()))
            {
                q.Dequeue();
                turn = (idx + 1) % n; // advance turn only on success
                if (braidWithStream)
                {
                    // We just consumed the queue slot; give next turn to the stream
                    queueTurn = false;
                }
                return true;
            }
        }
        return false;
    }
}

// 1->N splitter alternating outputs; if target blocked, falls back to others
public class SplitterEndpoint : BeltEndpoint
{
    int lastIndex = -1;
    readonly List<int> weights = new List<int>();
    int weightCounter = 0;

    // Configure weighted outputs (defaults to round-robin when not set)
    public void SetWeights(IEnumerable<int> w)
    {
        weights.Clear();
        foreach (var x in w) weights.Add(Mathf.Max(1, x));
        lastIndex = -1; weightCounter = 0;
    }

    public bool TrySplitTo(IReadOnlyList<BeltRun> outs)
    {
        if (queue.Count == 0) return false;
        int n = outs.Count;
        for (int k = 0; k < n; k++)
        {
            int i;
            if (weights.Count == n && n > 0)
            {
                i = (lastIndex + n) % n;
                if (outs[i].TryEnqueue(queue.Peek()))
                {
                    queue.Dequeue();
                    weightCounter++;
                    if (weightCounter >= weights[i])
                    {
                        weightCounter = 0;
                        lastIndex = (i + 1) % n;
                    }
                    return true;
                }
                lastIndex = (i + 1) % n;
                weightCounter = 0;
            }
            else
            {
                i = (lastIndex + 1 + k) % n;
                if (outs[i].TryEnqueue(queue.Peek()))
                {
                    queue.Dequeue();
                    lastIndex = i;
                    return true;
                }
            }
        }
        return false;
    }
}

// Tunnel to move items across gaps (no rendering rules here; just pass-through)
public class TunnelEndpoint : BeltEndpoint
{
    public TunnelEndpoint peer;
    public override void OnInputItem(int itemId)
    {
        if (peer != null) peer.Produce(itemId);
        else base.OnInputItem(itemId);
    }
}
