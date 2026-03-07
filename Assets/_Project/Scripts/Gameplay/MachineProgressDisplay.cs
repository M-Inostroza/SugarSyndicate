using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

public interface IMachineProgress
{
    float Progress01 { get; }
    bool IsProcessing { get; }
}

[ExecuteAlways]
[DisallowMultipleComponent]
public class MachineProgressDisplay : MonoBehaviour
{
    [SerializeField] Vector3 worldOffset = new Vector3(0f, -0.55f, 0f);
    [SerializeField] Vector2 barSize = new Vector2(0.7f, 0.08f);
    [SerializeField] Color fillColor = new Color(0.2f, 0.9f, 0.2f, 1f);
    [SerializeField] Color backgroundColor = new Color(0f, 0f, 0f, 0.45f);
    [SerializeField] bool hideWhenIdle = true;
    [SerializeField] bool showPreviewInEditMode = true;
    [SerializeField, Range(0f, 1f)] float editModePreviewProgress = 0.65f;
    [SerializeField] int sortingOrderOffset = 9;
    [SerializeField] string sortingLayerName = "Default";

    IMachineProgress progress;
    Transform bg;
    Transform fill;
    SpriteRenderer bgRenderer;
    SpriteRenderer fillRenderer;
    float lastProgress = -1f;
    bool editorRefreshQueued;
    bool editorRefreshForce;

    static Sprite whiteSprite;

    void Awake()
    {
        RequestRefresh(force: true);
    }

    void OnEnable()
    {
        RequestRefresh(force: true);
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            EditorApplication.delayCall -= FlushEditorRefresh;
            editorRefreshQueued = false;
            editorRefreshForce = false;
        }
#endif
    }

    void LateUpdate()
    {
        if (!Application.isPlaying)
            return;

        Refresh(force: false);
    }

    void OnValidate()
    {
        RequestRefresh(force: true);
    }

    void RequestRefresh(bool force)
    {
        if (Application.isPlaying)
        {
            Refresh(force);
            return;
        }

#if UNITY_EDITOR
        editorRefreshForce |= force;
        if (editorRefreshQueued)
            return;

        editorRefreshQueued = true;
        EditorApplication.delayCall -= FlushEditorRefresh;
        EditorApplication.delayCall += FlushEditorRefresh;
#endif
    }

    void Refresh(bool force)
    {
        ResolveTarget();
        EnsureRenderers();
        if (bgRenderer == null || fillRenderer == null) return;
        UpdateVisual(force);
        UpdateTransform();
        ApplySorting(bgRenderer, fillRenderer);
    }

    void ResolveTarget()
    {
        progress = GetComponent<IMachineProgress>() ?? GetComponentInParent<IMachineProgress>();
    }

#if UNITY_EDITOR
    void FlushEditorRefresh()
    {
        EditorApplication.delayCall -= FlushEditorRefresh;
        if (this == null)
            return;

        editorRefreshQueued = false;
        bool force = editorRefreshForce;
        editorRefreshForce = false;
        Refresh(force);
    }
#endif

    void EnsureRenderers()
    {
        bg = transform.Find("ProgressBar_BG");
        if (bg == null)
        {
            var go = new GameObject("ProgressBar_BG");
            bg = go.transform;
            bg.SetParent(transform, false);
        }
        bgRenderer = bg.GetComponent<SpriteRenderer>();
        if (bgRenderer == null) bgRenderer = bg.gameObject.AddComponent<SpriteRenderer>();
        bgRenderer.sprite = GetWhiteSprite();
        bgRenderer.drawMode = SpriteDrawMode.Sliced;
        bgRenderer.color = backgroundColor;

        fill = transform.Find("ProgressBar_Fill");
        if (fill == null)
        {
            var go = new GameObject("ProgressBar_Fill");
            fill = go.transform;
            fill.SetParent(transform, false);
        }
        fillRenderer = fill.GetComponent<SpriteRenderer>();
        if (fillRenderer == null) fillRenderer = fill.gameObject.AddComponent<SpriteRenderer>();
        fillRenderer.sprite = GetWhiteSprite();
        fillRenderer.drawMode = SpriteDrawMode.Sliced;
        fillRenderer.color = fillColor;

        ApplySorting(bgRenderer, fillRenderer);
    }

    void UpdateTransform()
    {
        var basePos = worldOffset;
        bg.localPosition = basePos;
        bg.localRotation = Quaternion.identity;
        bg.localScale = Vector3.one;
        if (bgRenderer != null)
            bgRenderer.size = barSize;

        float p = Mathf.Clamp01(lastProgress);
        float width = barSize.x * p;
        fill.localPosition = basePos + new Vector3(-barSize.x * 0.5f + width * 0.5f, 0f, 0f);
        fill.localRotation = Quaternion.identity;
        fill.localScale = Vector3.one;
        if (fillRenderer != null)
            fillRenderer.size = new Vector2(width, barSize.y);
    }

    void UpdateVisual(bool force)
    {
        float p = GetDisplayProgress();

        if (!force && Mathf.Abs(p - lastProgress) < 0.001f)
            return;

        lastProgress = p;

        bool show = !hideWhenIdle || p > 0f;
        if (bgRenderer != null) bgRenderer.enabled = show;
        if (fillRenderer != null) fillRenderer.enabled = show;
    }

    float GetDisplayProgress()
    {
        if (Application.isPlaying)
        {
            if (progress != null && progress.IsProcessing)
                return Mathf.Clamp01(progress.Progress01);
            return 0f;
        }

        if (showPreviewInEditMode)
            return Mathf.Clamp01(editModePreviewProgress);

        return 0f;
    }

    void ApplySorting(SpriteRenderer bgSr, SpriteRenderer fillSr)
    {
        if (bgSr == null || fillSr == null) return;
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

        bgSr.sortingLayerID = layerId;
        bgSr.sortingOrder = order + sortingOrderOffset;
        fillSr.sortingLayerID = layerId;
        fillSr.sortingOrder = order + sortingOrderOffset + 1;
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
