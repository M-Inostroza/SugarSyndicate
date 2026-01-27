using System;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class OnboardingDialogueUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] GameObject uiRoot;
    [SerializeField] CanvasGroup canvasGroup;
    [SerializeField] TMP_Text speakerText;
    [SerializeField] TMP_Text messageText;

    [Header("Content")]
    [SerializeField] string defaultSpeaker = "Pig Boss";
    [SerializeField] bool toggleRootActive = false;
    [SerializeField] bool closeOnAnyClick = false;
    [SerializeField] bool blockRaycastsWhenVisible = true;

    bool isVisible;
    bool warnedMissing;

    public event Action Clicked;

    void Awake()
    {
        CacheReferences();
        SetVisible(false);
    }

    public void ShowMessage(string message)
    {
        Show(defaultSpeaker, message);
    }

    public void Show(string speaker, string message)
    {
        CacheReferences();
        if (!warnedMissing && (speakerText == null || messageText == null))
        {
            warnedMissing = true;
            Debug.LogWarning("[OnboardingDialogueUI] Assign speakerText and messageText in the inspector.");
        }
        if (speakerText != null)
        {
            var finalSpeaker = string.IsNullOrWhiteSpace(speaker) ? defaultSpeaker : speaker;
            speakerText.text = finalSpeaker ?? string.Empty;
            speakerText.gameObject.SetActive(!string.IsNullOrWhiteSpace(speakerText.text));
        }
        if (messageText != null)
        {
            messageText.text = message ?? string.Empty;
            messageText.gameObject.SetActive(!string.IsNullOrWhiteSpace(messageText.text));
        }
        SetVisible(true);
    }

    public void Hide()
    {
        SetVisible(false);
    }

    void CacheReferences()
    {
        if (uiRoot == null) uiRoot = gameObject;
        if (canvasGroup == null && uiRoot != null)
            canvasGroup = uiRoot.GetComponent<CanvasGroup>();
    }

    void SetVisible(bool visible)
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
}
