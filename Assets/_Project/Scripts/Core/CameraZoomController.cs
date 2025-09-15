using UnityEngine;
using DG.Tweening;

/// <summary>
/// Handles simple stepped zoom (Far <-> Neutral <-> Close) on an orthographic camera.
/// Public methods are intended to be wired to UI buttons.
/// </summary>
public class CameraZoomController : MonoBehaviour
{
    [Header("Zoom Sizes (Orthographic)")]
    [SerializeField] float closeSize = 2.8f;   // all in
    [SerializeField] float neutralSize = 3.6f; // middle
    [SerializeField] float farSize = 4.8f;     // zoomed out

    [Header("Tween")] 
    [SerializeField, Min(0.01f)] float zoomDuration = 0.35f; 
    [SerializeField] Ease ease = Ease.InOutSine;

    Camera cam;
    Tweener currentTween;
    float targetSize; // last commanded target size

    public enum ZoomLevel { Close, Neutral, Far }

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
        if (dClose <= dNeutral && dClose <= dFar) return closeSize;
        if (dNeutral <= dFar) return neutralSize;
        return farSize;
    }

    ZoomLevel CurrentLevel
    {
        get
        {
            if (Mathf.Approximately(targetSize, closeSize)) return ZoomLevel.Close;
            if (Mathf.Approximately(targetSize, neutralSize)) return ZoomLevel.Neutral;
            return ZoomLevel.Far;
        }
    }

    void TweenTo(float size)
    {
        if (cam == null) return;
        if (Mathf.Approximately(targetSize, size)) return; // already targeting
        targetSize = size;
        currentTween?.Kill();
        currentTween = DOTween.To(() => cam.orthographicSize, s => cam.orthographicSize = s, size, zoomDuration)
            .SetEase(ease)
            .SetUpdate(false) // normal time scale
            .SetTarget(this);
    }

    /// <summary>
    /// Zoom out one step (Close->Neutral, Neutral->Far, Far stays).
    /// </summary>
    public void ZoomOut()
    {
        switch (CurrentLevel)
        {
            case ZoomLevel.Close:
                TweenTo(neutralSize);
                break;
            case ZoomLevel.Neutral:
                TweenTo(farSize);
                break;
            case ZoomLevel.Far:
                break; // already max
        }
    }

    /// <summary>
    /// Zoom in one step (Far->Neutral, Neutral->Close, Close stays).
    /// </summary>
    public void ZoomIn()
    {
        switch (CurrentLevel)
        {
            case ZoomLevel.Far:
                TweenTo(neutralSize);
                break;
            case ZoomLevel.Neutral:
                TweenTo(closeSize);
                break;
            case ZoomLevel.Close:
                break; // already min
        }
    }
}
