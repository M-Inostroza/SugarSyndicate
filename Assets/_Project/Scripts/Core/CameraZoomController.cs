using UnityEngine;
using DG.Tweening;

/// <summary>
/// Handles simple stepped zoom (Farthest <-> Far <-> Neutral <-> Close) on an orthographic camera.
/// Public methods are intended to be wired to UI buttons.
/// </summary>
public class CameraZoomController : MonoBehaviour
{
    public static event System.Action ZoomedIn;
    public static event System.Action ZoomedOut;

    [Header("Zoom Sizes (Orthographic)")]
    [SerializeField] float closeSize = 2.8f;   // all in
    [SerializeField] float neutralSize = 3.6f; // middle
    [SerializeField] float farSize = 4.8f;     // zoomed out
    [SerializeField] float farthestSize = 6.2f; // extra zoomed out

    [Header("Tween")] 
    [SerializeField, Min(0.01f)] float zoomDuration = 0.35f; 
    [SerializeField] Ease ease = Ease.InOutSine;

    Camera cam;
    Tweener currentTween;
    float targetSize; // last commanded target size

    public enum ZoomLevel { Close, Neutral, Far, Farthest }

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null) cam = Camera.main; // fallback
        if (cam != null && !cam.orthographic)
        {
            Debug.LogWarning("CameraZoomController: camera is not orthographic – switching to orthographic.");
            cam.orthographic = true;
        }
        if (cam != null)
        {
            // Initialize to neutral if within tolerance, else clamp to one of the defined sizes.
            float start = cam.orthographicSize;
            targetSize = ClosestPreset(start);
            cam.orthographicSize = targetSize;
        }
    }

    float ClosestPreset(float value)
    {
        float dClose = Mathf.Abs(value - closeSize);
        float dNeutral = Mathf.Abs(value - neutralSize);
        float dFar = Mathf.Abs(value - farSize);
        float dFarthest = Mathf.Abs(value - farthestSize);
        if (dClose <= dNeutral && dClose <= dFar && dClose <= dFarthest) return closeSize;
        if (dNeutral <= dFar && dNeutral <= dFarthest) return neutralSize;
        if (dFar <= dFarthest) return farSize;
        return farthestSize;
    }

    ZoomLevel CurrentLevel
    {
        get
        {
            if (Mathf.Approximately(targetSize, closeSize)) return ZoomLevel.Close;
            if (Mathf.Approximately(targetSize, neutralSize)) return ZoomLevel.Neutral;
            if (Mathf.Approximately(targetSize, farSize)) return ZoomLevel.Far;
            return ZoomLevel.Farthest;
        }
    }

    bool TweenTo(float size)
    {
        if (cam == null) return false;
        if (Mathf.Approximately(targetSize, size)) return false; // already targeting
        targetSize = size;
        currentTween?.Kill();
        currentTween = DOTween.To(() => cam.orthographicSize, s => cam.orthographicSize = s, size, zoomDuration)
            .SetEase(ease)
            .SetUpdate(false) // normal time scale
            .SetTarget(this);
        return true;
    }

    /// <summary>
    /// Zoom out one step (Close->Neutral, Neutral->Far, Far->Farthest, Farthest stays).
    /// </summary>
    public void ZoomOut()
    {
        bool changed = false;
        switch (CurrentLevel)
        {
            case ZoomLevel.Close:
                changed = TweenTo(neutralSize);
                break;
            case ZoomLevel.Neutral:
                changed = TweenTo(farSize);
                break;
            case ZoomLevel.Far:
                changed = TweenTo(farthestSize);
                break;
            case ZoomLevel.Farthest:
                break; // already max
        }
        if (changed) ZoomedOut?.Invoke();
    }

    /// <summary>
    /// Zoom in one step (Farthest->Far, Far->Neutral, Neutral->Close, Close stays).
    /// </summary>
    public void ZoomIn()
    {
        bool changed = false;
        switch (CurrentLevel)
        {
            case ZoomLevel.Farthest:
                changed = TweenTo(farSize);
                break;
            case ZoomLevel.Far:
                changed = TweenTo(neutralSize);
                break;
            case ZoomLevel.Neutral:
                changed = TweenTo(closeSize);
                break;
            case ZoomLevel.Close:
                break; // already min
        }
        if (changed) ZoomedIn?.Invoke();
    }
}
