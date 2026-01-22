using UnityEngine;
using UnityEngine.Rendering;

public interface IMachineJammed
{
    bool IsJammed { get; }
}

public interface IMachineStoppable
{
    bool IsStopped { get; }
}

public interface IGhostState
{
    bool IsGhost { get; }
}

[ExecuteAlways]
[DisallowMultipleComponent]
public class MachineStatusIndicator : MonoBehaviour
{
    [SerializeField] Vector3 localOffset = new Vector3(0.35f, 0.45f, 0f);
    [SerializeField] Vector2 size = new Vector2(0.18f, 0.18f);
    [SerializeField] Color runningColor = new Color(0.156f, 0.922f, 0.396f, 1f);
    [SerializeField] Color jammedColor = new Color(0.922f, 0.765f, 0.165f, 1f);
    [SerializeField] Color stoppedColor = new Color(0.922f, 0.267f, 0.255f, 1f);
    [SerializeField] int sortingOrderOffset = 12;
    [SerializeField] string sortingLayerName = "Default";
    [SerializeField] Sprite indicatorSprite;

    IMachineJammed jammedProvider;
    IMachineStoppable stoppableProvider;
    IGhostState ghostState;
    IPowerConsumer powerConsumer;
    PowerPole powerPole;
    Repairable repairable;
    GridService grid;
    PowerService powerService;
    SpriteRenderer sprite;
    Color lastColor = new Color(0f, 0f, 0f, 0f);
    bool hookedPower;

    static Sprite whiteSprite;

    void Awake()
    {
        ResolveTargets();
        EnsureSprite();
        UpdateVisual(true);
        UpdateTransform();
    }

    void OnEnable()
    {
        if (!Application.isPlaying) return;
        HookPowerService();
    }

    void OnDisable()
    {
        if (!Application.isPlaying) return;
        UnhookPowerService();
    }

    void LateUpdate()
    {
        if (sprite == null) return;
        if (!Application.isPlaying) return;
        if (jammedProvider == null || stoppableProvider == null || powerConsumer == null || repairable == null)
            ResolveTargets();
        UpdateVisual(false);
        UpdateTransform();
        ApplySorting(sprite);
    }

    void OnValidate()
    {
        if (sprite == null) EnsureSprite();
        ResolveTargets();
        UpdateVisual(true);
        UpdateTransform();
        ApplySorting(sprite);
    }

    void ResolveTargets()
    {
        jammedProvider = GetComponent<IMachineJammed>() ?? GetComponentInParent<IMachineJammed>();
        stoppableProvider = GetComponent<IMachineStoppable>() ?? GetComponentInParent<IMachineStoppable>();
        ghostState = GetComponent<IGhostState>() ?? GetComponentInParent<IGhostState>();
        powerConsumer = GetComponent<IPowerConsumer>() ?? GetComponentInParent<IPowerConsumer>();
        powerPole = GetComponent<PowerPole>() ?? GetComponentInParent<PowerPole>();
        repairable = GetComponent<Repairable>() ?? GetComponentInParent<Repairable>();
        grid = GridService.Instance;
        powerService = PowerService.Instance;
    }

    void EnsureSprite()
    {
        var child = transform.Find("StatusIndicator");
        if (child == null)
        {
            var go = new GameObject("StatusIndicator");
            child = go.transform;
            child.SetParent(transform, false);
        }

        sprite = child.GetComponent<SpriteRenderer>();
        if (sprite == null) sprite = child.gameObject.AddComponent<SpriteRenderer>();
        sprite.sprite = indicatorSprite != null ? indicatorSprite : GetWhiteSprite();
        ApplySorting(sprite);
    }

    void UpdateTransform()
    {
        if (sprite == null) return;
        var t = sprite.transform;
        t.localPosition = localOffset;
        t.localRotation = Quaternion.identity;
        t.localScale = new Vector3(size.x, size.y, 1f);
    }

    void UpdateVisual(bool force)
    {
        if (IsGhost())
        {
            if (sprite.enabled) sprite.enabled = false;
            lastColor = new Color(0f, 0f, 0f, 0f);
            return;
        }
        if (!sprite.enabled) sprite.enabled = true;

        if (powerPole != null)
        {
            Color poleColor = IsPoleConnected() ? runningColor : stoppedColor;
            if (!force && poleColor == lastColor) return;
            lastColor = poleColor;
            sprite.color = poleColor;
            return;
        }

        Color target = runningColor;
        if (IsStoppedOrDisconnected())
            target = stoppedColor;
        else if (jammedProvider != null && jammedProvider.IsJammed)
            target = jammedColor;

        if (!force && target == lastColor) return;
        lastColor = target;
        sprite.color = target;
    }

    bool IsStoppedOrDisconnected()
    {
        if (repairable != null && repairable.IsBroken) return true;
        if (stoppableProvider != null && stoppableProvider.IsStopped) return true;
        return IsConsumerDisconnected();
    }

    bool IsConsumerDisconnected()
    {
        if (powerConsumer == null) return false;
        if (powerConsumer.GetConsumptionWatts() <= 0f) return false;

        if (powerService == null) powerService = PowerService.Instance;
        if (grid == null) grid = GridService.Instance;
        if (powerService == null || grid == null) return true;

        Vector2Int cell;
        if (powerConsumer is IMachine machine)
        {
            cell = machine.Cell;
        }
        else if (powerConsumer is Component component)
        {
            cell = grid.WorldToCell(component.transform.position);
        }
        else
        {
            return true;
        }

        return !powerService.IsCellPoweredOrAdjacent(cell);
    }

    bool IsPoleConnected()
    {
        if (powerPole == null) return true;
        if (powerService == null) powerService = PowerService.Instance;
        if (grid == null) grid = GridService.Instance;
        if (powerService == null || grid == null) return false;
        var cell = grid.WorldToCell(powerPole.transform.position);
        return powerService.IsPolePowered(cell);
    }

    bool IsGhost()
    {
        if (ghostState != null) return ghostState.IsGhost;
        if (powerPole != null) return powerPole.isGhost;
        return false;
    }

    void HookPowerService()
    {
        if (powerService == null) powerService = PowerService.Instance;
        if (powerService == null) return;
        if (hookedPower) return;
        powerService.OnNetworkChanged -= HandleNetworkChanged;
        powerService.OnNetworkChanged += HandleNetworkChanged;
        hookedPower = true;
    }

    void UnhookPowerService()
    {
        if (powerService == null) return;
        powerService.OnNetworkChanged -= HandleNetworkChanged;
        hookedPower = false;
    }

    void HandleNetworkChanged()
    {
        if (!Application.isPlaying) return;
        if (sprite == null) return;
        ResolveTargets();
        UpdateVisual(true);
    }

    void ApplySorting(SpriteRenderer target)
    {
        if (target == null) return;
        int layerId = SortingLayer.NameToID(sortingLayerName);
        int order = 0;

        var group = GetComponentInParent<SortingGroup>(true) ?? GetComponentInChildren<SortingGroup>(true);
        if (group != null)
        {
            layerId = group.sortingLayerID;
            order = group.sortingOrder;
        }
        else
        {
            var sr = FindBestSortingRenderer(target);
            if (sr != null)
            {
                layerId = sr.sortingLayerID;
                order = sr.sortingOrder;
            }
        }

        target.sortingLayerID = layerId;
        target.sortingOrder = order + sortingOrderOffset;
    }

    SpriteRenderer FindBestSortingRenderer(SpriteRenderer target)
    {
        SpriteRenderer best = null;
        int bestOrder = int.MinValue;

        var parentRenderers = GetComponentsInParent<SpriteRenderer>(true);
        for (int i = 0; i < parentRenderers.Length; i++)
        {
            var sr = parentRenderers[i];
            if (sr == null || sr == target) continue;
            if (sr.sortingOrder > bestOrder)
            {
                bestOrder = sr.sortingOrder;
                best = sr;
            }
        }

        var childRenderers = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < childRenderers.Length; i++)
        {
            var sr = childRenderers[i];
            if (sr == null || sr == target) continue;
            if (sr.sortingOrder > bestOrder)
            {
                bestOrder = sr.sortingOrder;
                best = sr;
            }
        }

        return best;
    }

    static Sprite GetWhiteSprite()
    {
        if (whiteSprite == null)
        {
            var tex = Texture2D.whiteTexture;
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
        }
        return whiteSprite;
    }
}
