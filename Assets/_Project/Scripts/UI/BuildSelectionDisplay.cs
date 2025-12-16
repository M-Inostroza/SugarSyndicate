using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small HUD badge that shows which machine is currently armed for placement.
/// </summary>
public class BuildSelectionDisplay : MonoBehaviour
{
    [Header("Visuals")]
    [SerializeField] Color backgroundColor = new Color(0.1f, 0.12f, 0.18f, 0.9f);
    [SerializeField] Color textColor = Color.white;
    [SerializeField] string prefix = "Selected: ";
    [SerializeField] int sortingOrder = 6000;

    Canvas canvas;
    Image bg;
    Text label;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (FindAnyObjectByType<BuildSelectionDisplay>() != null) return;
        var go = new GameObject("BuildSelectionDisplay");
        DontDestroyOnLoad(go);
        go.AddComponent<BuildSelectionDisplay>();
    }

    void Awake()
    {
        CreateUIIfNeeded();
        ApplyColors();
        HandleSelectionChanged(null);
    }

    void OnEnable()
    {
        Subscribe(true);
    }

    void OnDisable()
    {
        Subscribe(false);
    }

    void OnDestroy()
    {
        Subscribe(false);
    }

    void Subscribe(bool add)
    {
        if (add) BuildSelectionNotifier.OnSelectionChanged += HandleSelectionChanged;
        else BuildSelectionNotifier.OnSelectionChanged -= HandleSelectionChanged;
    }

    void HandleSelectionChanged(string name)
    {
        bool has = !string.IsNullOrEmpty(name);
        if (label != null) label.text = has ? $"{prefix}{name}" : string.Empty;
        if (bg != null) bg.enabled = has;
        if (label != null) label.enabled = has;
    }

    void CreateUIIfNeeded()
    {
        if (canvas != null && bg != null && label != null) return;

        var canvasGO = new GameObject("BuildSelectionCanvas");
        canvasGO.transform.SetParent(transform, false);
        canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        canvasGO.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasGO.AddComponent<GraphicRaycaster>();

        var bgGO = new GameObject("Badge");
        bgGO.transform.SetParent(canvasGO.transform, false);
        bg = bgGO.AddComponent<Image>();
        bg.raycastTarget = false;

        var textGO = new GameObject("Label");
        textGO.transform.SetParent(bgGO.transform, false);
        label = textGO.AddComponent<Text>();
        label.raycastTarget = false;
        label.alignment = TextAnchor.MiddleLeft;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.resizeTextForBestFit = true;
        label.resizeTextMinSize = 14;
        label.resizeTextMaxSize = 24;

        var bgRect = bg.rectTransform;
        bgRect.anchorMin = new Vector2(0f, 1f);
        bgRect.anchorMax = new Vector2(0f, 1f);
        bgRect.pivot = new Vector2(0f, 1f);
        bgRect.sizeDelta = new Vector2(220f, 48f);
        bgRect.anchoredPosition = new Vector2(20f, -20f);

        var textRect = label.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.offsetMin = new Vector2(14f, 6f);
        textRect.offsetMax = new Vector2(-14f, -6f);
    }

    void ApplyColors()
    {
        if (bg != null) bg.color = backgroundColor;
        if (label != null) label.color = textColor;
    }
}
