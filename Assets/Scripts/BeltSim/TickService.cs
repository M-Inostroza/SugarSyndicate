using System.Collections.Generic;
using UnityEngine;

// Central tick: advances all runs, handles ejection/admission
public class BeltTickService : MonoBehaviour
{
    [SerializeField] float fixedSpeed = 2f;
    readonly List<BeltRun> runs = new List<BeltRun>(64);
    readonly List<List<BeltItem>> ejectedPerRun = new List<List<BeltItem>>(64);

    // Optional endpoints per run end
    public readonly List<BeltEndpoint> heads = new List<BeltEndpoint>(); // feeders at head
    public readonly List<BeltEndpoint> tails = new List<BeltEndpoint>(); // consumers at tail

    public IReadOnlyList<BeltRun> Runs => runs;

    public void SetGraph(BeltGraph graph)
    {
        runs.Clear(); heads.Clear(); tails.Clear(); ejectedPerRun.Clear();
        foreach (var r in graph.runs)
        {
            r.speed = fixedSpeed;
            runs.Add(r);
            ejectedPerRun.Add(new List<BeltItem>(16));
            heads.Add(null);
            tails.Add(null);
        }
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        // 1) advance all runs, collect ejected
        for (int i = 0; i < runs.Count; i++)
        {
            var ej = ejectedPerRun[i]; ej.Clear();
            runs[i].Advance(dt, ej);
        }
        // 2) deliver ejected to tail endpoints
        for (int i = 0; i < runs.Count; i++)
        {
            var ep = tails[i];
            if (ep == null) continue;
            var ej = ejectedPerRun[i];
            for (int k = 0; k < ej.Count; k++) ep.OnInputItem(ej[k].id);
        }
        // 3) try admit from heads; keep pulling until blocked (so bursts are handled in one tick)
        for (int i = 0; i < runs.Count; i++)
        {
            var ep = heads[i]; if (ep == null) continue;
            // Pull multiple while spacing allows
            int guard = 32; // avoid infinite loop in case of misbehaving endpoints
            while (guard-- > 0 && ep.TryOutputTo(runs[i])) { }
        }
    }
}
