using DG.Tweening;
using UnityEngine;

[DisallowMultipleComponent]
public class UIElementAnimator : MonoBehaviour
{
    [Header("Targets")]
    [SerializeField] RectTransform targetRect;
    [SerializeField] CanvasGroup targetGroup;
    [SerializeField] bool autoCreateCanvasGroup = true;

    [Header("Initial State")]
    [SerializeField] bool startHidden = true;
    [SerializeField] bool setInactiveWhenHidden = true;
    [SerializeField] bool playOnEnable = false;

    [Header("Position")]
    [SerializeField] bool animatePosition = true;
    [SerializeField] Vector2 hiddenOffset = new Vector2(0f, -200f);

    [Header("Fade")]
    [SerializeField] bool animateFade = true;
    [SerializeField, Range(0f, 1f)] float hiddenAlpha = 0f;
    [SerializeField, Range(0f, 1f)] float shownAlpha = 1f;

    [Header("Scale")]
    [SerializeField] bool animateScale = false;
    [SerializeField] Vector3 hiddenScale = new Vector3(0.95f, 0.95f, 1f);
    [SerializeField] Vector3 shownScale = Vector3.one;

    [Header("Timing")]
    [SerializeField, Min(0f)] float showDelay = 0f;
    [SerializeField, Min(0.01f)] float showDuration = 0.25f;
    [SerializeField] Ease showEase = Ease.OutCubic;
    [SerializeField, Min(0f)] float hideDelay = 0f;
    [SerializeField, Min(0.01f)] float hideDuration = 0.2f;
    [SerializeField] Ease hideEase = Ease.InCubic;
    [SerializeField] bool useUnscaledTime = true;

    Vector2 shownPosition;
    Vector3 shownLocalScale;
    bool hasCachedShown;
    bool isVisible;
    bool handledInitialState;
    Sequence tween;

    void Awake()
    {
        CacheTargets();
        CacheShownState();
    }

    void Start()
    {
        if (handledInitialState) return;
        if (startHidden)
            SetVisibleImmediate(false);
        else
            SetVisibleImmediate(true);
    }

    void OnDisable()
    {
        tween?.Kill();
        tween = null;
    }

    void OnEnable()
    {
        CacheTargets();
        if (!hasCachedShown)
            CacheShownState();
        if (playOnEnable)
        {
            Show();
            handledInitialState = true;
        }
    }

    void CacheTargets()
    {
        if (targetRect == null)
            targetRect = GetComponent<RectTransform>();
        if (targetGroup == null)
            targetGroup = GetComponent<CanvasGroup>();
        if (targetGroup == null && autoCreateCanvasGroup && animateFade)
            targetGroup = gameObject.AddComponent<CanvasGroup>();
    }

    void CacheShownState()
    {
        if (targetRect != null)
        {
            shownPosition = targetRect.anchoredPosition;
            shownLocalScale = targetRect.localScale;
            hasCachedShown = true;
        }
    }

    public void Show() => Animate(true);

    public void Hide() => Animate(false);

    public void Toggle() => Animate(!isVisible);

    public void SetVisibleImmediate(bool visible)
    {
        tween?.Kill();
        tween = null;
        if (visible && setInactiveWhenHidden && !gameObject.activeSelf)
            gameObject.SetActive(true);
        ApplyState(visible);
        if (!visible && setInactiveWhenHidden && gameObject.activeSelf)
            gameObject.SetActive(false);
        isVisible = visible;
    }

    void Animate(bool visible)
    {
        CacheTargets();
        if (!hasCachedShown)
            CacheShownState();
        if (targetRect == null)
            return;

        tween?.Kill();
        tween = DOTween.Sequence().SetUpdate(useUnscaledTime);

        if (visible)
        {
            if (setInactiveWhenHidden && !gameObject.activeSelf)
                gameObject.SetActive(true);
            if (targetGroup != null)
            {
                targetGroup.blocksRaycasts = true;
                targetGroup.interactable = true;
            }

            ApplyStateImmediateForAnimation(true);

            if (showDelay > 0f) tween.AppendInterval(showDelay);
            if (animatePosition)
            {
                var pos = tween.Join(targetRect.DOAnchorPos(shownPosition, showDuration).SetEase(showEase));
                if (useUnscaledTime) pos.SetUpdate(true);
            }
            if (animateFade && targetGroup != null)
            {
                var fade = tween.Join(targetGroup.DOFade(shownAlpha, showDuration).SetEase(showEase));
                if (useUnscaledTime) fade.SetUpdate(true);
            }
            if (animateScale)
            {
                var scale = tween.Join(targetRect.DOScale(shownLocalScale, showDuration).SetEase(showEase));
                if (useUnscaledTime) scale.SetUpdate(true);
            }
        }
        else
        {
            if (hideDelay > 0f) tween.AppendInterval(hideDelay);
            if (animatePosition)
            {
                var pos = tween.Join(targetRect.DOAnchorPos(shownPosition + hiddenOffset, hideDuration).SetEase(hideEase));
                if (useUnscaledTime) pos.SetUpdate(true);
            }
            if (animateFade && targetGroup != null)
            {
                var fade = tween.Join(targetGroup.DOFade(hiddenAlpha, hideDuration).SetEase(hideEase));
                if (useUnscaledTime) fade.SetUpdate(true);
            }
            if (animateScale)
            {
                var scale = tween.Join(targetRect.DOScale(hiddenScale, hideDuration).SetEase(hideEase));
                if (useUnscaledTime) scale.SetUpdate(true);
            }
            tween.OnComplete(() =>
            {
                ApplyState(false);
                if (setInactiveWhenHidden)
                    gameObject.SetActive(false);
            });
        }

        isVisible = visible;
    }

    void ApplyStateImmediateForAnimation(bool visible)
    {
        if (!animatePosition && !animateFade && !animateScale)
            return;

        if (animatePosition)
            targetRect.anchoredPosition = visible ? shownPosition + hiddenOffset : shownPosition;
        if (animateFade && targetGroup != null)
            targetGroup.alpha = visible ? hiddenAlpha : shownAlpha;
        if (animateScale)
            targetRect.localScale = visible ? hiddenScale : shownLocalScale;
    }

    void ApplyState(bool visible)
    {
        if (targetRect == null) return;
        if (animatePosition)
            targetRect.anchoredPosition = visible ? shownPosition : shownPosition + hiddenOffset;
        if (animateFade && targetGroup != null)
            targetGroup.alpha = visible ? shownAlpha : hiddenAlpha;
        if (animateScale)
            targetRect.localScale = visible ? shownLocalScale : hiddenScale;
        if (targetGroup != null)
        {
            targetGroup.blocksRaycasts = visible;
            targetGroup.interactable = visible;
        }
    }
}
