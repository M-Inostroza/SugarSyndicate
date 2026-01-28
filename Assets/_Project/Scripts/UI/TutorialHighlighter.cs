using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class TutorialHighlighter : MonoBehaviour
{
    [Header("Canvas")]
    [SerializeField] Canvas overlayCanvas;
    [SerializeField] RectTransform overlayRoot;
    [SerializeField] int sortingOrder = 6200;

    [Header("Highlight")]
    [SerializeField] Sprite highlightSprite;
    [SerializeField] Color highlightColor = new Color(1f, 1f, 1f, 0.9f);
    [SerializeField] Vector2 padding = new Vector2(16f, 16f);
    [SerializeField] bool keepAspect = false;

    [Header("Pulse")]
    [SerializeField] bool pulse = true;
    [SerializeField, Min(1f)] float pulseSpeed = 2f;
    [SerializeField, Range(1f, 1.5f)] float pulseScale = 1.06f;
    [SerializeField] bool useUnscaledTime = true;

    readonly List<RectTransform> targets = new List<RectTransform>();
    readonly List<Image> visuals = new List<Image>();
    Vector3[] corners = new Vector3[4];
    bool warnedMissingSprite;

    void Awake()
    {
        EnsureCanvas();
        ApplyVisualDefaults();
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
        UpdateVisibility();
    }

    void EnsureCanvas()
    {
        if (overlayCanvas == null)
        {
            var go = new GameObject("TutorialHighlightCanvas");
            go.transform.SetParent(transform, false);
            overlayCanvas = go.AddComponent<Canvas>();
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            overlayCanvas.sortingOrder = sortingOrder;
            go.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            go.AddComponent<GraphicRaycaster>();
        }
        if (overlayRoot == null && overlayCanvas != null)
            overlayRoot = overlayCanvas.GetComponent<RectTransform>();
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
        while (visuals.Count < count)
        {
            var go = new GameObject("Highlight");
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

        float pulseT = pulse ? Mathf.Sin((useUnscaledTime ? Time.unscaledTime : Time.time) * pulseSpeed) : 0f;
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
            rt.anchoredPosition = center;
            rt.sizeDelta = new Vector2(Mathf.Abs(size.x) + padding.x * 2f, Mathf.Abs(size.y) + padding.y * 2f);
            rt.localScale = Vector3.one * scale;
            img.enabled = true;
        }
    }
}
