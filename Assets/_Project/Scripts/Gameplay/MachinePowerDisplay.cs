using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public class MachinePowerDisplay : MonoBehaviour
{
    [SerializeField] Vector3 worldOffset = new Vector3(0f, 0.7f, 0f);
    [SerializeField, Min(0.1f)] float fontSize = 2.5f;
    [SerializeField] Color textColor = Color.white;
    [SerializeField] bool showOnlyUnderground = true;
    [SerializeField] bool showSourceWhenZero = true;
    [SerializeField] bool showConsumerWhenZero = false;
    [SerializeField] bool showConsumerWhenUnpowered = false;
    [SerializeField] int sortingOrderOffset = 10;
    [SerializeField] string sortingLayerName = "Default";

    IPowerSource source;
    IPowerConsumer consumer;
    TextMeshPro text;
    float lastValue = float.NaN;
    bool lastVisible;
    TimeManager timeManager;
    PowerService powerService;
    bool undergroundVisible = true;

    void Awake()
    {
        ResolveTargets();
        if (source == null && consumer == null)
        {
            enabled = false;
            return;
        }
        EnsureText();
        UpdateText(true);
        UpdateTransform();
        if (showOnlyUnderground)
        {
            undergroundVisible = false;
            if (text != null) text.gameObject.SetActive(false);
        }
    }

    void LateUpdate()
    {
        if (text == null) return;
        if (showOnlyUnderground && !undergroundVisible)
        {
            if (text.gameObject.activeSelf) text.gameObject.SetActive(false);
            return;
        }
        UpdateText(false);
        UpdateTransform();
    }

    void ResolveTargets()
    {
        source = GetComponent<IPowerSource>() ?? GetComponentInParent<IPowerSource>();
        consumer = GetComponent<IPowerConsumer>() ?? GetComponentInParent<IPowerConsumer>();
        timeManager = TimeManager.Instance;
        powerService = PowerService.Instance;
    }

    void EnsureText()
    {
        text = GetComponentInChildren<TextMeshPro>(true);
        if (text == null)
        {
            var go = new GameObject("PowerText");
            go.transform.SetParent(transform, false);
            text = go.AddComponent<TextMeshPro>();
        }

        text.text = string.Empty;
        text.fontSize = fontSize;
        text.color = textColor;
        text.alignment = TextAlignmentOptions.Center;
        text.richText = false;
        text.enableAutoSizing = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.transform.rotation = Quaternion.identity;

        ApplySorting(text);
    }

    void UpdateTransform()
    {
        text.transform.position = transform.position + worldOffset;
        text.transform.rotation = Quaternion.identity;
    }

    void UpdateText(bool force)
    {
        if (showOnlyUnderground && !undergroundVisible) return;
        float value = 0f;
        bool visible = true;

        if (source != null)
        {
            if (powerService == null) powerService = PowerService.Instance;
            if (powerService != null && powerService.TryGetNetworkIdForSource(source, out var networkId))
                value = powerService.GetNetworkNetWatts(networkId);
            else
            {
                var phase = timeManager != null ? timeManager.CurrentPhase
                    : (TimeManager.Instance != null ? TimeManager.Instance.CurrentPhase : TimePhase.Day);
                value = Mathf.Max(0f, source.GetOutputWatts(phase));
            }
            if (!showSourceWhenZero && Mathf.Abs(value) <= 0.001f)
                visible = false;
        }
        else if (consumer != null)
        {
            float usage = Mathf.Max(0f, consumer.GetConsumptionWatts());
            bool powered = showConsumerWhenUnpowered || PowerConsumerUtil.IsConsumerPowered(consumer);
            if (!powered) usage = 0f;
            if (!showConsumerWhenZero && usage <= 0.001f)
                visible = false;
            value = -usage;
        }
        else
        {
            visible = false;
        }

        if (!force && visible == lastVisible && Mathf.Abs(value - lastValue) < 0.01f)
            return;

        lastVisible = visible;
        lastValue = value;

        if (!visible)
        {
            if (text.gameObject.activeSelf) text.gameObject.SetActive(false);
            return;
        }

        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        text.text = PowerService.FormatPower(value);
    }

    void ApplySorting(TextMeshPro target)
    {
        if (target == null) return;
        var renderer = target.GetComponent<Renderer>();
        if (renderer == null) return;

        int layerId = SortingLayer.NameToID(sortingLayerName);
        int order = 0;

        var group = GetComponentInChildren<SortingGroup>();
        if (group != null)
        {
            layerId = group.sortingLayerID;
            order = group.sortingOrder;
        }
        else
        {
            var sr = GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
            {
                layerId = sr.sortingLayerID;
                order = sr.sortingOrder;
            }
        }

        renderer.sortingLayerID = layerId;
        renderer.sortingOrder = order + sortingOrderOffset;
    }

    public void SetUndergroundVisible(bool visible)
    {
        if (!showOnlyUnderground) return;
        undergroundVisible = visible;
        if (text == null) return;
        if (!visible)
        {
            if (text.gameObject.activeSelf) text.gameObject.SetActive(false);
            return;
        }
        if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        var renderer = text.GetComponent<Renderer>();
        if (renderer != null && !renderer.enabled) renderer.enabled = true;
        UpdateText(true);
        UpdateTransform();
    }
}
