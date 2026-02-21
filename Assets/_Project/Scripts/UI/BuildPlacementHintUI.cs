using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class BuildPlacementHintUI : MonoBehaviour
{
    static BuildPlacementHintUI instance;

    [Header("Visuals")]
    [SerializeField] Color backgroundColor = new Color(0f, 0f, 0f, 0.85f);
    [SerializeField] Color textColor = new Color(1f, 0.86f, 0.25f, 1f);
    [SerializeField] int sortingOrder = 6300;
    [SerializeField] Vector2 screenOffset = new Vector2(0f, 42f);
    [SerializeField, Min(0f)] float worldYOffset = 0.8f;
    [SerializeField, Min(0.1f)] float displaySeconds = 1.15f;
    [SerializeField, Min(0.01f)] float fadeSeconds = 0.2f;

    Canvas canvas;
    RectTransform root;
    RectTransform panelRect;
    CanvasGroup panelGroup;
    TMP_Text textLabel;
    float hideAtUnscaledTime = -1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (instance != null) return;
        var existing = FindAnyObjectByType<BuildPlacementHintUI>();
        if (existing != null)
        {
            instance = existing;
            return;
        }

        var go = new GameObject("BuildPlacementHintUI");
        instance = go.AddComponent<BuildPlacementHintUI>();
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
        EnsureUi();
        SetVisible(false);
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    void Update()
    {
        if (panelGroup == null || panelGroup.alpha <= 0f) return;

        if (hideAtUnscaledTime <= 0f || Time.unscaledTime <= hideAtUnscaledTime)
            return;

        float alpha = Mathf.MoveTowards(panelGroup.alpha, 0f, Time.unscaledDeltaTime / fadeSeconds);
        panelGroup.alpha = alpha;
        panelGroup.blocksRaycasts = false;
        panelGroup.interactable = false;
        if (alpha <= 0.001f)
            SetVisible(false);
    }

    public static void ShowAtCell(Vector2Int cell, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (instance == null) Bootstrap();
        if (instance == null) return;
        instance.ShowAtCellInternal(cell, message);
    }

    void ShowAtCellInternal(Vector2Int cell, string message)
    {
        EnsureUi();
        if (root == null || panelRect == null || textLabel == null) return;

        var grid = GridService.Instance;
        var cam = Camera.main;
        if (grid == null || cam == null) return;

        var world = grid.CellToWorld(cell, 0f);
        world.y += worldYOffset;
        var screen = cam.WorldToScreenPoint(world);
        if (screen.z < 0f) return;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, null, out var localPoint);
        panelRect.anchoredPosition = localPoint + screenOffset;
        textLabel.text = message;

        SetVisible(true);
        hideAtUnscaledTime = Time.unscaledTime + displaySeconds;
    }

    void SetVisible(bool visible)
    {
        if (panelRect != null && panelRect.gameObject.activeSelf != visible)
            panelRect.gameObject.SetActive(visible);
        if (panelGroup == null) return;

        panelGroup.alpha = visible ? 1f : 0f;
        panelGroup.blocksRaycasts = false;
        panelGroup.interactable = false;
        if (!visible)
            hideAtUnscaledTime = -1f;
    }

    void EnsureUi()
    {
        if (canvas == null)
        {
            canvas = GetComponentInChildren<Canvas>(true);
            if (canvas == null)
            {
                var canvasGo = new GameObject("HintCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvasGo.transform.SetParent(transform, false);
                canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = sortingOrder;

                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
            }
        }

        if (root == null && canvas != null)
            root = canvas.GetComponent<RectTransform>();

        if (panelRect == null && root != null)
        {
            var panelGo = new GameObject("HintPanel", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            panelGo.transform.SetParent(root, false);
            panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(260f, 46f);

            panelGroup = panelGo.GetComponent<CanvasGroup>();
            panelGroup.alpha = 0f;
            panelGroup.blocksRaycasts = false;
            panelGroup.interactable = false;

            var bg = panelGo.GetComponent<Image>();
            bg.color = backgroundColor;
            bg.raycastTarget = false;

            var textGo = new GameObject("HintText", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(panelGo.transform, false);
            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.offsetMin = new Vector2(12f, 8f);
            textRect.offsetMax = new Vector2(-12f, -8f);

            textLabel = textGo.GetComponent<TextMeshProUGUI>();
            textLabel.text = string.Empty;
            textLabel.color = textColor;
            textLabel.alignment = TextAlignmentOptions.Center;
            textLabel.enableAutoSizing = true;
            textLabel.fontSizeMin = 16f;
            textLabel.fontSizeMax = 26f;
            textLabel.textWrappingMode = TextWrappingModes.Normal;
        }
        else if (panelRect != null)
        {
            if (panelGroup == null) panelGroup = panelRect.GetComponent<CanvasGroup>();
            if (textLabel == null) textLabel = panelRect.GetComponentInChildren<TMP_Text>(true);
        }

        if (canvas != null)
            canvas.sortingOrder = sortingOrder;
        if (panelRect != null && panelRect.gameObject.TryGetComponent<Image>(out var panelImage))
            panelImage.color = backgroundColor;
        if (textLabel != null)
            textLabel.color = textColor;
    }
}
