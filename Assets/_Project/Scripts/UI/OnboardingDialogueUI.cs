using System;
using System.Collections;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class OnboardingDialogueUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] GameObject uiRoot;
    [SerializeField] CanvasGroup canvasGroup;
    [SerializeField] RectTransform dialogueBoxTransform;
    [SerializeField] RectTransform avatarTransform;
    [SerializeField] CanvasGroup backgroundCanvasGroup;
    [SerializeField] CanvasGroup dialogueBoxCanvasGroup;
    [SerializeField] CanvasGroup avatarCanvasGroup;
    [SerializeField] TMP_Text speakerText;
    [SerializeField] TMP_Text messageText;

    [Header("Content")]
    [SerializeField] string defaultSpeaker = "Pig Boss";
    [SerializeField] bool toggleRootActive = false;
    [SerializeField] bool closeOnAnyClick = false;
    [SerializeField] bool blockRaycastsWhenVisible = true;

    [Header("Container Animation")]
    [SerializeField] bool animateContainer = true;
    [SerializeField] bool delayFirstShowOnStartup = true;
    [SerializeField, Min(0f)] float firstShowDelaySeconds = 1f;
    [SerializeField, Min(0f)] float containerEnterDuration = 0.35f;
    [SerializeField, Min(0f)] float containerExitDuration = 0.25f;
    [SerializeField] float dialogueBoxEnterOffsetY = -180f;
    [SerializeField] float avatarEnterOffsetX = 220f;
    [SerializeField, Range(0f, 1f)] float containerStartAlpha = 0f;
    [SerializeField, Range(0f, 1f)] float containerFadeStartNormalized = 0.65f;
    [SerializeField, Range(0f, 1f)] float backgroundStartAlpha = 0f;
    [SerializeField, Range(0f, 1f)] float backgroundTargetAlpha = 1f;
    [SerializeField, Range(0f, 1f)] float backgroundFadeStartNormalized = 0.2f;
    [SerializeField] AnimationCurve containerMoveCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Message Animation")]
    [SerializeField] bool animateMessageText = true;
    [SerializeField, Min(0f)] float messageEnterDuration = 0.3f;
    [SerializeField] float messageEnterOffsetY = -24f;
    [SerializeField, Range(0f, 1f)] float messageEnterStartAlpha = 0f;
    [SerializeField, Range(0f, 1f)] float messageFadeStartNormalized = 0.75f;
    [SerializeField] bool useUnscaledAnimationTime = true;
    [SerializeField] AnimationCurve messageMoveCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    bool isVisible;
    bool warnedMissing;
    Coroutine messageAnimationRoutine;
    RectTransform cachedMessageRect;
    Vector2 messageTargetAnchoredPosition;
    bool messageTargetPositionCached;
    Vector2 dialogueBoxTargetAnchoredPosition;
    bool dialogueBoxTargetPositionCached;
    Vector2 avatarTargetAnchoredPosition;
    bool avatarTargetPositionCached;
    CanvasGroup messageCanvasGroup;
    Color messageBaseColor = Color.white;
    Coroutine containerAnimationRoutine;
    Coroutine delayedShowRoutine;
    bool hasAppliedStartupDelay;

    public event Action Clicked;

    void Awake()
    {
        CacheReferences();
        SetVisibleImmediate(false);
    }

    void OnDisable()
    {
        StopHideOrShowRoutines();
        StopMessageAnimation(restoreToFinalState: true);
    }

    public void ShowMessage(string message)
    {
        Show(defaultSpeaker, message);
    }

    public void Show(string speaker, string message)
    {
        CacheReferences();
        if (!warnedMissing && messageText == null)
        {
            warnedMissing = true;
            Debug.LogWarning("[OnboardingDialogueUI] Assign messageText in the inspector.");
        }
        if (speakerText != null)
            speakerText.gameObject.SetActive(false);
        if (messageText != null)
        {
            messageText.text = message ?? string.Empty;
            messageText.gameObject.SetActive(!string.IsNullOrWhiteSpace(messageText.text));
        }

        bool wasVisible = isVisible;
        if (wasVisible)
        {
            StopHideOrShowRoutines();
            SetVisibleImmediate(true);
            ApplyContainerVisualState(1f);
            PlayMessageAnimationIfNeeded();
            return;
        }

        PrepareMessageForContainerEnterIfNeeded();
        BeginShow();
    }

    public void Hide()
    {
        if (!isVisible)
        {
            StopHideOrShowRoutines();
            SetVisibleImmediate(false);
            return;
        }

        StopHideOrShowRoutines();
        if (!ShouldAnimateContainer())
        {
            SetVisibleImmediate(false);
            return;
        }

        containerAnimationRoutine = StartCoroutine(AnimateContainerVisibility(false));
    }

    void CacheReferences()
    {
        if (uiRoot == null) uiRoot = gameObject;
        if (canvasGroup == null && uiRoot != null)
            canvasGroup = uiRoot.GetComponent<CanvasGroup>();
        CacheContainerAnimationReferences();
        CacheMessageAnimationReferences();
    }

    void CacheContainerAnimationReferences()
    {
        if (dialogueBoxTransform != null && !dialogueBoxTargetPositionCached)
        {
            dialogueBoxTargetAnchoredPosition = dialogueBoxTransform.anchoredPosition;
            dialogueBoxTargetPositionCached = true;
        }
        if (dialogueBoxTransform != null && dialogueBoxCanvasGroup == null)
        {
            dialogueBoxCanvasGroup = dialogueBoxTransform.GetComponent<CanvasGroup>();
            if (dialogueBoxCanvasGroup == null)
                dialogueBoxCanvasGroup = dialogueBoxTransform.gameObject.AddComponent<CanvasGroup>();
        }

        if (avatarTransform != null && !avatarTargetPositionCached)
        {
            avatarTargetAnchoredPosition = avatarTransform.anchoredPosition;
            avatarTargetPositionCached = true;
        }
        if (avatarTransform != null && avatarCanvasGroup == null)
        {
            avatarCanvasGroup = avatarTransform.GetComponent<CanvasGroup>();
            if (avatarCanvasGroup == null)
                avatarCanvasGroup = avatarTransform.gameObject.AddComponent<CanvasGroup>();
        }
    }

    void CacheMessageAnimationReferences()
    {
        if (messageText == null)
        {
            cachedMessageRect = null;
            messageCanvasGroup = null;
            messageTargetPositionCached = false;
            return;
        }

        if (cachedMessageRect == null || cachedMessageRect != messageText.rectTransform)
        {
            cachedMessageRect = messageText.rectTransform;
            messageTargetPositionCached = false;
        }

        if (!messageTargetPositionCached && cachedMessageRect != null)
        {
            messageTargetAnchoredPosition = cachedMessageRect.anchoredPosition;
            messageTargetPositionCached = true;
        }

        if (messageCanvasGroup == null || messageCanvasGroup.gameObject != messageText.gameObject)
        {
            messageCanvasGroup = messageText.GetComponent<CanvasGroup>();
            if (messageCanvasGroup == null)
                messageCanvasGroup = messageText.gameObject.AddComponent<CanvasGroup>();
        }
    }

    void SetVisibleImmediate(bool visible)
    {
        isVisible = visible;
        if (canvasGroup != null)
        {
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.blocksRaycasts = visible && blockRaycastsWhenVisible;
            canvasGroup.interactable = visible;
        }

        if (toggleRootActive && uiRoot != null && uiRoot != gameObject)
            uiRoot.SetActive(visible);

        if (!visible)
        {
            // Reset nested animated elements so the next show starts from a clean visible state.
            ApplyContainerVisualState(1f);
            StopMessageAnimation(restoreToFinalState: true);
        }
    }

    void Update()
    {
        if (!isVisible) return;

        if (Input.GetMouseButtonDown(0))
        {
            Clicked?.Invoke();
            if (closeOnAnyClick) Hide();
            return;
        }

        if (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)
        {
            Clicked?.Invoke();
            if (closeOnAnyClick) Hide();
        }
    }

    public void SetAutoHideOnClick(bool enabled)
    {
        closeOnAnyClick = enabled;
    }

    void PlayMessageAnimationIfNeeded()
    {
        CacheMessageAnimationReferences();
        if (messageText == null || cachedMessageRect == null || messageCanvasGroup == null)
            return;

        if (!messageText.gameObject.activeInHierarchy)
        {
            StopMessageAnimation(restoreToFinalState: true);
            return;
        }

        messageBaseColor = messageText.color;
        if (!animateMessageText || messageEnterDuration <= 0f)
        {
            StopMessageAnimation(restoreToFinalState: true);
            return;
        }

        StopMessageAnimation(restoreToFinalState: false);
        messageAnimationRoutine = StartCoroutine(AnimateMessageTextEnter());
    }

    void PrepareMessageForContainerEnterIfNeeded()
    {
        if (!ShouldAnimateContainer()) return;
        if (!animateMessageText || messageEnterDuration <= 0f) return;

        CacheMessageAnimationReferences();
        if (messageText == null || cachedMessageRect == null || messageCanvasGroup == null) return;
        if (!messageText.gameObject.activeSelf) return;

        StopMessageAnimation(restoreToFinalState: false);
        messageBaseColor = messageText.color;
        ApplyMessageAnimationState(0f);
    }

    void StopMessageAnimation(bool restoreToFinalState)
    {
        if (messageAnimationRoutine != null)
        {
            StopCoroutine(messageAnimationRoutine);
            messageAnimationRoutine = null;
        }

        if (!restoreToFinalState)
            return;

        CacheMessageAnimationReferences();
        ApplyMessageAnimationState(1f);
    }

    void BeginShow()
    {
        StopHideOrShowRoutines();

        if (!ShouldAnimateContainer())
        {
            SetVisibleImmediate(true);
            ApplyContainerVisualState(1f);
            PlayMessageAnimationIfNeeded();
            return;
        }

        if (delayFirstShowOnStartup && !hasAppliedStartupDelay && firstShowDelaySeconds > 0f)
        {
            delayedShowRoutine = StartCoroutine(DelayedFirstShowRoutine());
            return;
        }

        containerAnimationRoutine = StartCoroutine(AnimateContainerVisibility(true));
    }

    IEnumerator DelayedFirstShowRoutine()
    {
        float delay = Mathf.Max(0f, firstShowDelaySeconds);
        while (delay > 0f)
        {
            float dt = useUnscaledAnimationTime ? Time.unscaledDeltaTime : Time.deltaTime;
            delay -= Mathf.Max(0f, dt);
            yield return null;
        }

        delayedShowRoutine = null;
        hasAppliedStartupDelay = true;
        containerAnimationRoutine = StartCoroutine(AnimateContainerVisibility(true));
    }

    IEnumerator AnimateContainerVisibility(bool show)
    {
        SetVisibleImmediate(true);
        CacheReferences();

        float duration = show ? containerEnterDuration : containerExitDuration;
        if (duration <= 0f)
        {
            ApplyContainerVisualState(show ? 1f : 0f);
            if (show)
                PlayMessageAnimationIfNeeded();
            else
                SetVisibleImmediate(false);
            containerAnimationRoutine = null;
            yield break;
        }

        if (show)
            ApplyContainerVisualState(0f);
        else
            ApplyContainerVisualState(1f);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += useUnscaledAnimationTime ? Time.unscaledDeltaTime : Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            ApplyContainerVisualState(show ? t : (1f - t));
            yield return null;
        }

        if (show)
        {
            ApplyContainerVisualState(1f);
            PlayMessageAnimationIfNeeded();
        }
        else
        {
            ApplyContainerVisualState(0f);
            SetVisibleImmediate(false);
        }

        containerAnimationRoutine = null;
    }

    void StopHideOrShowRoutines()
    {
        if (delayedShowRoutine != null)
        {
            StopCoroutine(delayedShowRoutine);
            delayedShowRoutine = null;
        }
        if (containerAnimationRoutine != null)
        {
            StopCoroutine(containerAnimationRoutine);
            containerAnimationRoutine = null;
        }
    }

    bool ShouldAnimateContainer()
    {
        if (!animateContainer) return false;
        return dialogueBoxTransform != null || avatarTransform != null || backgroundCanvasGroup != null;
    }

    void ApplyContainerVisualState(float t)
    {
        CacheContainerAnimationReferences();
        float visibilityT = Mathf.Clamp01(t);
        float moveT = containerMoveCurve != null ? Mathf.Clamp01(containerMoveCurve.Evaluate(visibilityT)) : visibilityT;
        float fadeStart = Mathf.Clamp01(containerFadeStartNormalized);
        float fadeT = fadeStart >= 1f ? (visibilityT >= 1f ? 1f : 0f) : Mathf.InverseLerp(fadeStart, 1f, visibilityT);
        float alpha = Mathf.Lerp(containerStartAlpha, 1f, fadeT);
        float bgFadeStart = Mathf.Clamp01(backgroundFadeStartNormalized);
        float bgFadeT = bgFadeStart >= 1f ? (visibilityT >= 1f ? 1f : 0f) : Mathf.InverseLerp(bgFadeStart, 1f, visibilityT);
        float backgroundAlpha = Mathf.Lerp(backgroundStartAlpha, backgroundTargetAlpha, bgFadeT);

        if (dialogueBoxTransform != null && dialogueBoxTargetPositionCached)
        {
            var start = dialogueBoxTargetAnchoredPosition + new Vector2(0f, dialogueBoxEnterOffsetY);
            dialogueBoxTransform.anchoredPosition = Vector2.LerpUnclamped(start, dialogueBoxTargetAnchoredPosition, moveT);
        }
        if (backgroundCanvasGroup != null)
            backgroundCanvasGroup.alpha = backgroundAlpha;
        if (dialogueBoxCanvasGroup != null)
            dialogueBoxCanvasGroup.alpha = alpha;

        if (avatarTransform != null && avatarTargetPositionCached)
        {
            var start = avatarTargetAnchoredPosition + new Vector2(avatarEnterOffsetX, 0f);
            avatarTransform.anchoredPosition = Vector2.LerpUnclamped(start, avatarTargetAnchoredPosition, moveT);
        }
        if (avatarCanvasGroup != null)
            avatarCanvasGroup.alpha = alpha;
    }

    IEnumerator AnimateMessageTextEnter()
    {
        ApplyMessageAnimationState(0f);
        float duration = Mathf.Max(0.0001f, messageEnterDuration);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += useUnscaledAnimationTime ? Time.unscaledDeltaTime : Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            ApplyMessageAnimationState(t);
            yield return null;
        }

        ApplyMessageAnimationState(1f);
        messageAnimationRoutine = null;
    }

    void ApplyMessageAnimationState(float t)
    {
        if (cachedMessageRect == null || messageCanvasGroup == null || messageText == null)
            return;

        float moveT = messageMoveCurve != null ? Mathf.Clamp01(messageMoveCurve.Evaluate(t)) : t;
        var startPos = messageTargetAnchoredPosition + new Vector2(0f, messageEnterOffsetY);
        cachedMessageRect.anchoredPosition = Vector2.LerpUnclamped(startPos, messageTargetAnchoredPosition, moveT);

        float fadeStart = Mathf.Clamp01(messageFadeStartNormalized);
        float fadeT = fadeStart >= 1f ? (t >= 1f ? 1f : 0f) : Mathf.InverseLerp(fadeStart, 1f, t);
        messageCanvasGroup.alpha = Mathf.Lerp(messageEnterStartAlpha, 1f, fadeT);
        messageText.color = new Color(messageBaseColor.r, messageBaseColor.g, messageBaseColor.b, messageBaseColor.a);
    }
}
