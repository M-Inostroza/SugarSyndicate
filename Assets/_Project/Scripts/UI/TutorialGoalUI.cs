using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class TutorialGoalUI : MonoBehaviour
{
    [System.Serializable]
    public struct ChecklistItem
    {
        public string label;
        public bool complete;

        public ChecklistItem(string label, bool complete)
        {
            this.label = label;
            this.complete = complete;
        }
    }

    [Header("UI References")]
    [SerializeField] GameObject uiRoot;
    [SerializeField] CanvasGroup canvasGroup;
    [SerializeField] RectTransform panelRect;
    [SerializeField] TMP_Text goalText;

    [Header("Content")]
    [SerializeField] string prefix = "Goal: ";
    [SerializeField] string title = "confidential";
    [SerializeField] bool includeTitleInChecklist = true;
    [SerializeField] string checkedPrefix = "[x] ";
    [SerializeField] string uncheckedPrefix = "[ ] ";
    [SerializeField] bool tintCompletedItems = true;
    [SerializeField] Color completedItemColor = new Color(0.35f, 0.9f, 0.35f, 1f);
    [SerializeField] bool toggleRootActive = false;
    [SerializeField] bool blockRaycastsWhenVisible = false;

    [Header("Layout")]
    [SerializeField] bool autoResizePanelHeight = true;
    [SerializeField, Min(0f)] float panelVerticalPadding = 16f;
    [SerializeField, Min(0f)] float panelMinimumHeight = 64f;

    bool isVisible;
    bool warnedMissing;

    void Awake()
    {
        CacheReferences();
        if (goalText != null)
            SetVisible(false);
    }

    public void Show(string goal)
    {
        ShowGoal(goal);
    }

    public void ShowGoal(string goal)
    {
        CacheReferences();
        if (!warnedMissing && goalText == null)
        {
            warnedMissing = true;
            Debug.LogWarning("[TutorialGoalUI] Assign goalText in the inspector.");
        }

        if (goalText != null)
        {
            goalText.text = string.IsNullOrWhiteSpace(goal) ? string.Empty : $"{prefix}{goal}";
            goalText.gameObject.SetActive(!string.IsNullOrWhiteSpace(goalText.text));
        }
        RefreshLayout();

        SetVisible(!string.IsNullOrWhiteSpace(goal));
    }

    public void ShowChecklist(IReadOnlyList<ChecklistItem> items)
    {
        CacheReferences();
        if (!warnedMissing && goalText == null)
        {
            warnedMissing = true;
            Debug.LogWarning("[TutorialGoalUI] Assign goalText in the inspector.");
        }

        string text = BuildChecklistText(items);
        if (goalText != null)
        {
            goalText.text = text;
            goalText.gameObject.SetActive(!string.IsNullOrWhiteSpace(text));
        }
        RefreshLayout();

        SetVisible(!string.IsNullOrWhiteSpace(text));
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
        if (goalText == null)
            goalText = GetComponentInChildren<TMP_Text>(true);
        if (panelRect == null && goalText != null)
            panelRect = goalText.transform.parent as RectTransform;
    }

    void RefreshLayout()
    {
        if (!autoResizePanelHeight || panelRect == null || goalText == null)
            return;

        Canvas.ForceUpdateCanvases();
        goalText.ForceMeshUpdate();

        float textHeight = goalText.GetRenderedValues(false).y;
        float targetHeight = Mathf.Max(panelMinimumHeight, textHeight + panelVerticalPadding);
        panelRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, targetHeight);
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

    string BuildChecklistText(IReadOnlyList<ChecklistItem> items)
    {
        if (items == null || items.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        string completeColorTag = tintCompletedItems ? $"<color=#{ColorUtility.ToHtmlStringRGBA(completedItemColor)}>" : string.Empty;
        string colorClose = tintCompletedItems ? "</color>" : string.Empty;
        if (includeTitleInChecklist && !string.IsNullOrWhiteSpace(title))
        {
            sb.Append(title);
            sb.Append(':');
            sb.Append('\n');
        }

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            string line = $"{(item.complete ? checkedPrefix : uncheckedPrefix)}{item.label ?? string.Empty}";
            if (tintCompletedItems && item.complete)
                line = $"{completeColorTag}{line}{colorClose}";
            sb.Append(line);
            if (i < items.Count - 1) sb.Append('\n');
        }

        return sb.ToString();
    }

    public static TutorialGoalUI CreateDefault(Transform parent)
    {
        var root = new GameObject("Tutorial Goal UI");
        if (parent != null) root.transform.SetParent(parent, false);

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;
        var scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        root.AddComponent<GraphicRaycaster>();
        root.AddComponent<CanvasGroup>();

        var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(root.transform, false);
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0f, 1f);
        panelRect.anchorMax = new Vector2(0f, 1f);
        panelRect.pivot = new Vector2(0f, 1f);
        panelRect.anchoredPosition = new Vector2(16f, -16f);
        panelRect.sizeDelta = new Vector2(360f, 64f);
        var panelImage = panel.GetComponent<Image>();
        panelImage.color = new Color(0f, 0f, 0f, 0.6f);

        var textGo = new GameObject("GoalText", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(panel.transform, false);
        var textRect = textGo.GetComponent<RectTransform>();
        textRect.anchorMin = new Vector2(0f, 0f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.offsetMin = new Vector2(12f, 8f);
        textRect.offsetMax = new Vector2(-12f, -8f);

        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.text = "Goal:";
        text.fontSize = 22f;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Left;
        text.textWrappingMode = TextWrappingModes.Normal;

        var ui = root.AddComponent<TutorialGoalUI>();
        ui.CacheReferences();
        ui.SetVisible(false);
        return ui;
    }
}
