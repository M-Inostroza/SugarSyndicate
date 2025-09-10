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

// N->1 merger with round-robin fairness handled in TickService
public class MergerEndpoint : BeltEndpoint
{
}

// 1->N splitter alternating outputs; if target blocked, falls back to others
public class SplitterEndpoint : BeltEndpoint
{
    int lastIndex = -1;
    public bool TrySplitTo(IReadOnlyList<BeltRun> outs)
    {
        if (queue.Count == 0) return false;
        int n = outs.Count;
        for (int k = 0; k < n; k++)
        {
            int i = (lastIndex + 1 + k) % n;
            if (outs[i].TryEnqueue(queue.Peek()))
            {
                queue.Dequeue();
                lastIndex = i;
                return true;
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
