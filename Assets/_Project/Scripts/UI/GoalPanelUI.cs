using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class GoalPanelUI : MonoBehaviour
{
    [System.Serializable]
    struct ItemSprite
    {
        public string itemType;
        public Sprite sprite;
    }

    [Header("Goal")]
    [SerializeField] GoalManager goalManager;

    [Header("UI References")]
    [SerializeField] GameObject uiRoot;
    [SerializeField] CanvasGroup backdropGroup;
    [SerializeField] Button backdropButton;
    [SerializeField] CanvasGroup panelGroup;
    [SerializeField] Button closeButton;
    [SerializeField] Button openButton;

    [Header("Goal Text")]
    [SerializeField] TMP_Text goalText;
    [SerializeField] TMP_Text progressText;
    [SerializeField] TMP_Text secondaryGoalText;
    [SerializeField] TMP_Text secondaryProgressText;
    [SerializeField] TMP_Text secondaryGoalText2;
    [SerializeField] TMP_Text secondaryProgressText2;
    [SerializeField] Image goalItemImage;
    [SerializeField] Sprite defaultItemSprite;
    [SerializeField] ItemSprite[] itemSprites;
    [SerializeField] bool overrideGoalText = true;
    [SerializeField] string overrideGoalTextValue = "confidential";

    [Header("Behavior")]
    [SerializeField] bool startOpen = false;
    [SerializeField] bool closeOnBackdrop = true;
    [SerializeField] bool setRootActive = false;

    bool isOpen;

    void Awake()
    {
        if (uiRoot == null) uiRoot = gameObject;
        if (goalManager == null)
            goalManager = FindAnyObjectByType<GoalManager>();
        CacheUiReferences();
        WireUi(true);
        SetVisibleImmediate(startOpen);
        RefreshGoalText();
    }

    void OnEnable()
    {
        if (goalManager == null)
            goalManager = FindAnyObjectByType<GoalManager>();
        if (goalManager != null)
            goalManager.OnGoalUiChanged += RefreshGoalText;
        WireUi(true);
        RefreshGoalText();
    }

    void OnDisable()
    {
        if (goalManager != null)
            goalManager.OnGoalUiChanged -= RefreshGoalText;
        WireUi(false);
    }

    public void Open() => SetVisible(true);

    public void Close() => SetVisible(false);

    public void Toggle() => SetVisible(!isOpen);

    void SetVisible(bool visible)
    {
        if (visible) EnsureRootActive();
        isOpen = visible;
        SetGroupVisible(backdropGroup, visible);
        SetGroupVisible(panelGroup, visible);
        if (visible) RefreshGoalText();
        if (visible) EnsurePanelOnTop();

        if (!visible && setRootActive && uiRoot != null && uiRoot != gameObject)
            uiRoot.SetActive(visible);
    }

    void SetVisibleImmediate(bool visible)
    {
        if (visible) EnsureRootActive();
        isOpen = visible;
        SetGroupVisible(backdropGroup, visible);
        SetGroupVisible(panelGroup, visible);
        if (visible) RefreshGoalText();
        if (visible) EnsurePanelOnTop();

        if (!visible && setRootActive && uiRoot != null && uiRoot != gameObject)
            uiRoot.SetActive(visible);
    }

    void EnsurePanelOnTop()
    {
        if (panelGroup == null || backdropGroup == null) return;
        var panelTransform = panelGroup.transform;
        var backdropTransform = backdropGroup.transform;
        if (panelTransform == null || backdropTransform == null) return;
        if (panelTransform.parent != backdropTransform.parent) return;

        backdropTransform.SetAsFirstSibling();
        panelTransform.SetAsLastSibling();
    }

    void EnsureRootActive()
    {
        if (uiRoot == null || uiRoot == gameObject) return;
        if (!uiRoot.activeSelf)
            uiRoot.SetActive(true);
    }

    void RefreshGoalText()
    {
        if (goalManager == null)
        {
            SetText(goalText, string.Empty);
            SetText(progressText, string.Empty);
            SetText(secondaryGoalText, string.Empty);
            SetText(secondaryProgressText, string.Empty);
            SetText(secondaryGoalText2, string.Empty);
            SetText(secondaryProgressText2, string.Empty);
            SetGoalItemSprite(null);
            return;
        }

        SetText(goalText, overrideGoalText ? overrideGoalTextValue : goalManager.GetGoalText());
        SetText(progressText, goalManager.GetProgressText());
        SetText(secondaryGoalText, goalManager.GetBonusObjectiveText(0));
        SetText(secondaryProgressText, goalManager.GetBonusObjectiveProgressText(0));
        SetText(secondaryGoalText2, goalManager.GetBonusObjectiveText(1));
        SetText(secondaryProgressText2, goalManager.GetBonusObjectiveProgressText(1));

        if (goalManager.TryGetGoalItemType(out var itemType))
            SetGoalItemSprite(itemType);
        else
            SetGoalItemSprite(null);
    }

    static void SetText(TMP_Text text, string value)
    {
        if (text == null) return;
        text.text = value ?? string.Empty;
    }

    void SetGoalItemSprite(string itemType)
    {
        if (goalItemImage == null) return;
        var sprite = FindSpriteForItem(itemType) ?? defaultItemSprite;
        goalItemImage.sprite = sprite;
        goalItemImage.enabled = sprite != null;
    }

    Sprite FindSpriteForItem(string itemType)
    {
        if (string.IsNullOrWhiteSpace(itemType) || itemSprites == null) return null;
        var trimmed = itemType.Trim();
        for (int i = 0; i < itemSprites.Length; i++)
        {
            var entry = itemSprites[i];
            if (string.IsNullOrWhiteSpace(entry.itemType) || entry.sprite == null) continue;
            if (string.Equals(entry.itemType.Trim(), trimmed, System.StringComparison.OrdinalIgnoreCase))
                return entry.sprite;
        }
        return null;
    }

    void SetGroupVisible(CanvasGroup group, bool visible)
    {
        if (group == null) return;
        group.alpha = visible ? 1f : 0f;
        group.blocksRaycasts = visible;
        group.interactable = visible;
    }

    void CacheUiReferences()
    {
        if (backdropGroup == null && backdropButton != null)
            backdropGroup = backdropButton.GetComponent<CanvasGroup>();
        if (panelGroup == null && uiRoot != null)
        {
            var groups = uiRoot.GetComponentsInChildren<CanvasGroup>(true);
            if (groups.Length == 1)
            {
                panelGroup = groups[0];
            }
            else if (groups.Length > 1)
            {
                for (int i = 0; i < groups.Length; i++)
                {
                    if (groups[i] == null || groups[i] == backdropGroup) continue;
                    panelGroup = groups[i];
                    break;
                }
            }
        }
    }

    void WireUi(bool add)
    {
        if (openButton != null)
        {
            if (add)
            {
                openButton.onClick.RemoveListener(Open);
                openButton.onClick.AddListener(Open);
            }
            else
            {
                openButton.onClick.RemoveListener(Open);
            }
        }

        if (closeButton != null)
        {
            if (add)
            {
                closeButton.onClick.RemoveListener(Close);
                closeButton.onClick.AddListener(Close);
            }
            else
            {
                closeButton.onClick.RemoveListener(Close);
            }
        }

        if (closeOnBackdrop && backdropButton != null)
        {
            if (add)
            {
                backdropButton.onClick.RemoveListener(Close);
                backdropButton.onClick.AddListener(Close);
            }
            else
            {
                backdropButton.onClick.RemoveListener(Close);
            }
        }
    }
}
