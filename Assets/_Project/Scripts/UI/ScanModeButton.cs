using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Auto-spawned UI button that triggers ScanModeController.
public class ScanModeButton : MonoBehaviour
{
    static ScanModeButton instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (instance != null) return;
        var existing = FindAnyObjectByType<ScanModeButton>();
        if (existing != null)
        {
            instance = existing;
            return;
        }
        var go = new GameObject("ScanModeButton");
        go.AddComponent<ScanModeButton>();
    }

    [Header("Setup")]
    [SerializeField] ScanModeController scanController;
    [SerializeField] bool autoFindController = true;

    [Header("Layout")]
    [SerializeField] Vector2 size = new Vector2(140f, 50f);
    [SerializeField] Vector2 anchoredOffset = new Vector2(-140f, 80f);
    [SerializeField] int sortingOrder = 4500;

    [Header("Visuals")]
    [SerializeField] string buttonText = "Scan";
    [SerializeField] Color buttonColor = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] Color textColor = Color.black;

    [Header("Behavior")]
    [SerializeField] bool hideWhenNoController = true;

    Canvas canvas;
    Button button;
    Text label;

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
        EnsureEventSystem();
        CreateButtonIfNeeded();
    }

    void Start()
    {
        ResolveController();
        HookButton();
        UpdateVisibility();
    }

    void Update()
    {
        if (scanController == null && autoFindController)
        {
            ResolveController();
            UpdateVisibility();
        }
    }

    void ResolveController()
    {
        if (scanController != null || !autoFindController) return;
        scanController = FindAnyObjectByType<ScanModeController>();
    }

    void HookButton()
    {
        if (button == null) return;
        button.onClick.RemoveAllListeners();
        button.onClick.AddListener(OnButtonClicked);
    }

    void OnButtonClicked()
    {
        if (scanController == null) ResolveController();
        if (scanController != null) scanController.OnScanButtonPressed();
    }

    void UpdateVisibility()
    {
        if (button == null) return;
        bool show = !(hideWhenNoController && scanController == null);
        button.gameObject.SetActive(show);
    }

    void CreateButtonIfNeeded()
    {
        if (canvas != null) return;

        var canvasGO = new GameObject("ScanModeButtonCanvas");
        canvasGO.transform.SetParent(transform, false);

        canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        canvasGO.AddComponent<GraphicRaycaster>();

        var buttonGO = new GameObject("ScanButton");
        buttonGO.transform.SetParent(canvasGO.transform, false);

        var img = buttonGO.AddComponent<Image>();
        img.color = buttonColor;

        button = buttonGO.AddComponent<Button>();

        var rect = buttonGO.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredOffset;

        var textGO = new GameObject("Label");
        textGO.transform.SetParent(buttonGO.transform, false);
        label = textGO.AddComponent<Text>();
        label.text = buttonText;
        label.color = textColor;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.alignment = TextAnchor.MiddleCenter;
        label.raycastTarget = false;

        var textRect = label.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
    }

    void EnsureEventSystem()
    {
        if (EventSystem.current != null) return;
        var es = new GameObject("EventSystem");
        es.AddComponent<EventSystem>();
        es.AddComponent<StandaloneInputModule>();
        DontDestroyOnLoad(es);
    }
}
