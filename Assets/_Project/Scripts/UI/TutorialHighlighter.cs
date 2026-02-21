using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
public class TutorialHighlighter : MonoBehaviour
{
    const string OverlayCanvasName = "TutorialHighlightCanvas";
    const string HighlightObjectName = "Highlight";

    [Header("Canvas")]
    [SerializeField] Canvas overlayCanvas;
    [SerializeField] RectTransform overlayRoot;
    [SerializeField] int sortingOrder = 6200;
    [SerializeField] bool drawBehindUi = true;

    [Header("Highlight")]
    [SerializeField] Sprite highlightSprite;
    [SerializeField] Color highlightColor = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] Vector2 padding = new Vector2(16f, 16f);
    [SerializeField] Vector2 positionOffset = Vector2.zero;
    [SerializeField] bool keepAspect = false;

    [Header("Pulse")]
    [SerializeField] bool pulse = true;
    [SerializeField, Min(1f)] float pulseSpeed = 2f;
    [SerializeField, Range(1f, 1.5f)] float pulseScale = 1.06f;
    [SerializeField] bool useUnscaledTime = true;

    [Header("Editor Preview")]
    [SerializeField] bool previewInEditMode = false;
    [SerializeField] List<RectTransform> previewTargets = new List<RectTransform>();

    readonly List<RectTransform> targets = new List<RectTransform>();
    readonly List<Image> visuals = new List<Image>();
    Vector3[] corners = new Vector3[4];
    bool warnedMissingSprite;

    void OnEnable()
    {
        if (Application.isPlaying)
        {
            EnsureCanvas();
            ApplyVisualDefaults();
            ClearTargets();
        }
        RefreshEditModePreview();
    }

    void OnValidate()
    {
        if (Application.isPlaying || previewInEditMode)
            EnsureCanvas();
        RebuildVisualPoolFromHierarchy();
        ApplyCanvasSorting();
        ApplyVisualDefaults();
        RefreshEditModePreview();
    }

    public void RefreshPreviewFromInspector()
    {
        RefreshEditModePreview();
    }

    void RefreshEditModePreview()
    {
        if (Application.isPlaying) return;
        if (previewInEditMode)
            SetTargets(previewTargets);
        else
        {
            ClearTargets();
            CleanupEditModePreviewObjects();
        }
    }

    public void SetTargets(IReadOnlyList<RectTransform> newTargets)
    {
        targets.Clear();
        if (newTargets != null)
        {
            for (int i = 0; i < newTargets.Count; i++)
            {
                var t = newTargets[i];
                if (t != null) targets.Add(t);
            }
        }
        EnsureVisualPool(targets.Count);
        UpdateVisibility();
    }

    public void ClearTargets()
    {
        targets.Clear();
        if (overlayCanvas == null && !Application.isPlaying && !previewInEditMode)
            return;
        EnsureCanvas();
        RebuildVisualPoolFromHierarchy();
        UpdateVisibility();
    }

    void CleanupEditModePreviewObjects()
    {
#if UNITY_EDITOR
        if (Application.isPlaying || previewInEditMode) return;
        if (overlayRoot != null)
        {
            for (int i = overlayRoot.childCount - 1; i >= 0; i--)
            {
                var child = overlayRoot.GetChild(i);
                if (child == null) continue;
                if (!child.name.StartsWith(HighlightObjectName, System.StringComparison.Ordinal)) continue;
                DestroyImmediate(child.gameObject);
            }
        }
        visuals.Clear();

        if (overlayCanvas != null
            && overlayCanvas.gameObject.name == OverlayCanvasName
            && overlayCanvas.transform.parent == transform)
        {
            DestroyImmediate(overlayCanvas.gameObject);
            overlayCanvas = null;
            overlayRoot = null;
        }
#endif
    }

    void EnsureCanvas()
    {
        if (overlayCanvas == null)
        {
            var existing = transform.Find(OverlayCanvasName);
            if (existing != null)
                overlayCanvas = existing.GetComponent<Canvas>();

            if (overlayCanvas == null)
            {
                var go = new GameObject(OverlayCanvasName);
                go.transform.SetParent(transform, false);
                overlayCanvas = go.AddComponent<Canvas>();
                overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                go.AddComponent<GraphicRaycaster>();
            }
        }
        ApplyCanvasSorting();
        if (overlayRoot == null && overlayCanvas != null)
            overlayRoot = overlayCanvas.GetComponent<RectTransform>();
    }

    void ApplyCanvasSorting()
    {
        if (overlayCanvas == null) return;
        int order = Mathf.Abs(sortingOrder);
        overlayCanvas.sortingOrder = drawBehindUi ? -order : order;
    }

    void ApplyVisualDefaults()
    {
        if (highlightSprite == null && !warnedMissingSprite)
        {
            warnedMissingSprite = true;
            Debug.LogWarning("[TutorialHighlighter] Assign a highlightSprite for the overlay.");
        }
        for (int i = 0; i < visuals.Count; i++)
        {
            if (visuals[i] == null) continue;
            visuals[i].sprite = highlightSprite;
            visuals[i].color = highlightColor;
            visuals[i].preserveAspect = keepAspect;
        }
    }

    void EnsureVisualPool(int count)
    {
        EnsureCanvas();
        RebuildVisualPoolFromHierarchy();
        while (visuals.Count < count)
        {
            var go = new GameObject(HighlightObjectName);
            go.transform.SetParent(overlayRoot, false);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            img.sprite = highlightSprite;
            img.color = highlightColor;
            img.preserveAspect = keepAspect;
            visuals.Add(img);
        }
        ApplyVisualDefaults();
    }

    void RebuildVisualPoolFromHierarchy()
    {
        visuals.RemoveAll(v => v == null);
        if (overlayRoot == null) return;

        for (int i = 0; i < overlayRoot.childCount; i++)
        {
            var child = overlayRoot.GetChild(i);
            if (child == null) continue;
            if (!child.name.StartsWith(HighlightObjectName, System.StringComparison.Ordinal)) continue;

            var img = child.GetComponent<Image>();
            if (img == null) continue;
            if (!visuals.Contains(img))
                visuals.Add(img);
        }

        visuals.Sort((a, b) =>
        {
            if (a == null && b == null) return 0;
            if (a == null) return 1;
            if (b == null) return -1;
            return a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex());
        });
    }

    void UpdateVisibility()
    {
        for (int i = 0; i < visuals.Count; i++)
        {
            if (visuals[i] == null) continue;
            visuals[i].enabled = i < targets.Count;
        }
    }

    void LateUpdate()
    {
        if (targets.Count == 0 || overlayRoot == null) return;

        float pulseT = pulse ? Mathf.Sin(GetCurrentPulseTime() * pulseSpeed) : 0f;
        float scale = pulse ? Mathf.Lerp(1f, pulseScale, (pulseT + 1f) * 0.5f) : 1f;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var img = i < visuals.Count ? visuals[i] : null;
            if (target == null || img == null) continue;

            if (!target.gameObject.activeInHierarchy)
            {
                img.enabled = false;
                continue;
            }

            target.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);

            RectTransformUtility.ScreenPointToLocalPointInRectangle(overlayRoot, min, null, out var minLocal);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(overlayRoot, max, null, out var maxLocal);

            var size = (maxLocal - minLocal);
            var center = (minLocal + maxLocal) * 0.5f;

            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = center + positionOffset;
            rt.sizeDelta = new Vector2(Mathf.Abs(size.x) + padding.x * 2f, Mathf.Abs(size.y) + padding.y * 2f);
            rt.localScale = Vector3.one * scale;
            img.enabled = true;
        }
    }

    float GetCurrentPulseTime()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
            return (float)EditorApplication.timeSinceStartup;
#endif
        return useUnscaledTime ? Time.unscaledTime : Time.time;
    }
}
