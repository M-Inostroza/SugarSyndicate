using UnityEngine;
using UnityEngine.UI;

// Lightweight full-screen overlay that tints the screen in Build/Delete modes.
// Drop this component into the scene; it auto-creates its own Canvas overlay.
public class BuildModeOverlay : MonoBehaviour
{
    static BuildModeOverlay instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        // Auto-spawn a singleton if none is present in the scene
        if (instance != null) return;
        var go = new GameObject("BuildModeOverlay");
        go.AddComponent<BuildModeOverlay>();
    }

    [Header("Visuals")]
    [SerializeField] Color overlayColor = new Color(1f, 0.97f, 0.72f, 0.25f);
    [SerializeField, Min(0.01f)] float fadeSpeed = 8f;
    [Tooltip("Sorting order for the overlay canvas (higher draws on top).")]
    [SerializeField] int sortingOrder = 5000;
    [Header("Vignette")]
    [SerializeField, Tooltip("Normalized center of the clear area (0-1).")]
    Vector2 center = new Vector2(0.5f, 0.5f);
    [SerializeField, Range(0.05f, 1f), Tooltip("Radius of the clear area. Higher = larger clear center.")]
    float radius = 0.45f;
    [SerializeField, Range(0.001f, 1f), Tooltip("Soft edge width for the vignette falloff.")]
    float softness = 0.25f;
    [SerializeField, Tooltip("Optional intensity multiplier for tinting the edges.")]
    float edgeIntensity = 1f;
    [Header("Delete Mode")]
    [SerializeField, Tooltip("Edge tint while in Delete mode.")]
    Color deleteOverlayColor = new Color(1f, 0.45f, 0.45f, 0.3f);
    [SerializeField, Tooltip("Intensity multiplier for delete mode vignette.")]
    float deleteEdgeIntensity = 1f;

    CanvasGroup canvasGroup;
    Image overlayImage;
    Material runtimeMat;
    bool targetVisible;
    bool isDeleteMode;
    GameState lastState = GameState.Play;
    static Sprite whiteSprite;

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject);
        CreateOverlayIfNeeded();
    }

    void Update()
    {
        var state = GameManager.Instance != null ? GameManager.Instance.State : GameState.Play;
        targetVisible = state == GameState.Build || state == GameState.Delete;
        isDeleteMode = state == GameState.Delete;
        lastState = state;

        if (canvasGroup != null)
        {
            float target = targetVisible ? 1f : 0f;
            canvasGroup.alpha = Mathf.MoveTowards(canvasGroup.alpha, target, fadeSpeed * Time.unscaledDeltaTime);
        }

        UpdateMaterialParams();
    }

    void OnDestroy()
    {
        if (runtimeMat != null) Destroy(runtimeMat);
    }

    void OnValidate()
    {
        UpdateMaterialParams();
    }

    void CreateOverlayIfNeeded()
    {
        // Try to reuse an existing CanvasGroup if present
        canvasGroup = GetComponentInChildren<CanvasGroup>();
        if (canvasGroup != null) return;

        // Canvas holder
        var canvasGO = new GameObject("BuildModeOverlayCanvas");
        canvasGO.transform.SetParent(transform, false);

        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        // Transparent raycast blocker is not needed; keep raycasts off to avoid interfering with UI input
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        canvasGO.AddComponent<GraphicRaycaster>();

        var overlayGO = new GameObject("Overlay");
        overlayGO.transform.SetParent(canvasGO.transform, false);
        overlayImage = overlayGO.AddComponent<Image>();
        overlayImage.color = overlayColor;
        overlayImage.sprite = GetWhiteSprite();
        overlayImage.raycastTarget = false;
        overlayImage.type = Image.Type.Simple;
        overlayImage.preserveAspect = false;

        var rect = overlayImage.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        canvasGroup = canvasGO.AddComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        SetupMaterial();
    }

    void SetupMaterial()
    {
        if (overlayImage == null) return;
        var shader = Shader.Find("UI/VignetteOverlay");
        if (shader != null)
        {
            runtimeMat = new Material(shader);
            overlayImage.material = runtimeMat;
            UpdateMaterialParams();
        }
        else
        {
            // fallback: keep default material but still show color
            runtimeMat = null;
        }
    }

    void UpdateMaterialParams()
    {
        if (runtimeMat == null)
        {
            SetupMaterial();
            if (runtimeMat == null) return;
        }

        if (runtimeMat == null)
        {
            if (overlayImage != null && overlayImage.material != null && overlayImage.material.shader != null &&
                overlayImage.material.shader.name == "UI/VignetteOverlay")
            {
                runtimeMat = overlayImage.material;
            }
            else
            {
                // fallback: ensure the Image still shows the tint even without the custom shader
                if (overlayImage != null)
                {
                    overlayImage.color = isDeleteMode ? deleteOverlayColor : overlayColor;
                }
                return;
            }
        }

        var col = isDeleteMode ? deleteOverlayColor : overlayColor;
        float intensity = isDeleteMode ? deleteEdgeIntensity : edgeIntensity;
        runtimeMat.SetColor("_Color", col * intensity);
        runtimeMat.SetVector("_Center", center);
        runtimeMat.SetFloat("_Radius", radius);
        runtimeMat.SetFloat("_Softness", Mathf.Max(0.0001f, softness));
        runtimeMat.SetTexture("_MainTex", overlayImage != null ? overlayImage.mainTexture : Texture2D.whiteTexture);

        if (overlayImage != null)
        {
            overlayImage.color = col;
        }
    }

    Sprite GetWhiteSprite()
    {
        if (whiteSprite == null)
        {
            var tex = Texture2D.whiteTexture;
            whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }
        return whiteSprite;
    }
}
