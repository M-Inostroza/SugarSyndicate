using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Simple category toggle for the build menu.
/// Attach to a GameObject and assign category button + content roots.
/// Clicking a category shows its content and hides the others.
/// </summary>
public class BuildMenuController : MonoBehaviour
{
    [Serializable]
    public class Category
    {
        public string name;
        public Button button;
        public GameObject contentRoot;
        [Tooltip("Optional: if set, only this child is toggled. Leave null to toggle contentRoot.")]
        public GameObject contentPanel;
    }

    [Header("Containers")]
    [Tooltip("Optional: parent for the main category buttons. Hidden when a category is active if hideButtonsWhileOpen is true.")]
    [SerializeField] GameObject buttonContainer;

    [Header("Setup")]
    [SerializeField] List<Category> categories = new();
    [Tooltip("If true, no category is shown on start until the player picks one.")]
    [SerializeField] bool startHidden = true;
    [Tooltip("If true, clicking the active category hides it (no category visible).")]
    [SerializeField] bool toggleOffActive = true;
    [Tooltip("If true, hides the buttonContainer while a category panel is open.")]
    [SerializeField] bool hideButtonsWhileOpen = true;

    string activeCategory;
    readonly Dictionary<Category, bool> cachedButtonActive = new Dictionary<Category, bool>();
    readonly Dictionary<Category, bool> cachedButtonInteractable = new Dictionary<Category, bool>();

    void OnEnable()
    {
        CacheInitialStates();
        RegisterButtons();
        // Always start with panels hidden, then optionally show the first category
        HideAll();
        if (!startHidden && categories.Count > 0)
        {
            ShowCategory(categories[0].name);
        }
    }

    void OnDisable()
    {
        UnregisterButtons();
    }

    void RegisterButtons()
    {
        foreach (var cat in categories)
        {
            if (cat?.button == null) continue;
            cat.button.onClick.AddListener(() => OnCategoryClicked(cat));
        }
    }

    void UnregisterButtons()
    {
        foreach (var cat in categories)
        {
            if (cat?.button == null) continue;
            cat.button.onClick.RemoveAllListeners();
        }
    }

    void OnCategoryClicked(Category cat)
    {
        if (cat == null) return;
        if (cat.button != null && !cat.button.interactable) return;
        if (activeCategory == cat.name && toggleOffActive)
        {
            HideAll();
            activeCategory = null;
            return;
        }

        ShowCategory(cat.name);
    }

    public void ShowCategory(string categoryName)
    {
        activeCategory = categoryName;
        if (hideButtonsWhileOpen && buttonContainer != null) buttonContainer.SetActive(false);
        foreach (var cat in categories)
        {
            if (cat == null || cat.contentRoot == null) continue;
            bool show = string.Equals(cat.name, categoryName, StringComparison.OrdinalIgnoreCase);
            var target = ResolveToggleTarget(cat);
            if (target != null) target.SetActive(show);
        }
    }

    public void ShowCategoryIfNoneOpen(string categoryName)
    {
        if (!string.IsNullOrWhiteSpace(activeCategory)) return;
        ShowCategory(categoryName);
    }

    public void HideAll()
    {
        foreach (var cat in categories)
        {
            if (cat == null || cat.contentRoot == null) continue;
            var target = ResolveToggleTarget(cat);
            if (target != null) target.SetActive(false);
        }
        activeCategory = null;
        if (hideButtonsWhileOpen && buttonContainer != null) buttonContainer.SetActive(true);
    }

    public void SetAllowedCategories(IReadOnlyList<string> allowed, bool hideDisallowed, bool disableDisallowed)
    {
        if (allowed == null || allowed.Count == 0) return;
        foreach (var cat in categories)
        {
            if (cat == null) continue;
            bool isAllowed = ContainsName(allowed, cat.name);
            if (isAllowed)
            {
                SetCategoryVisible(cat, true);
                SetCategoryEnabled(cat, true);
            }
            else
            {
                if (hideDisallowed) SetCategoryVisible(cat, false);
                if (disableDisallowed) SetCategoryEnabled(cat, false);
            }
        }
    }

    public void ResetCategoryStates()
    {
        foreach (var cat in categories)
        {
            if (cat == null || cat.button == null) continue;
            if (cachedButtonActive.TryGetValue(cat, out var active))
                cat.button.gameObject.SetActive(active);
            if (cachedButtonInteractable.TryGetValue(cat, out var interactable))
                cat.button.interactable = interactable;
        }
    }

    void SetCategoryVisible(Category cat, bool visible)
    {
        if (cat?.button == null) return;
        cat.button.gameObject.SetActive(visible);
        if (!visible)
        {
            var target = ResolveToggleTarget(cat);
            if (target != null) target.SetActive(false);
        }
    }

    void SetCategoryEnabled(Category cat, bool enabled)
    {
        if (cat?.button == null) return;
        cat.button.interactable = enabled;
        if (!enabled)
        {
            var target = ResolveToggleTarget(cat);
            if (target != null) target.SetActive(false);
        }
    }

    void CacheInitialStates()
    {
        if (cachedButtonActive.Count > 0 || cachedButtonInteractable.Count > 0) return;
        foreach (var cat in categories)
        {
            if (cat?.button == null) continue;
            cachedButtonActive[cat] = cat.button.gameObject.activeSelf;
            cachedButtonInteractable[cat] = cat.button.interactable;
        }
    }

    static bool ContainsName(IReadOnlyList<string> list, string name)
    {
        if (list == null || string.IsNullOrWhiteSpace(name)) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (string.Equals(entry.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // UI hook for "Back" buttons: hide panels and clear the active build tool.
    public void OnBackPressed()
    {
        HideAll();
        ClearActiveBuildTool();
    }

    void ClearActiveBuildTool()
    {
        try
        {
            var bmc = FindAnyObjectByType<BuildModeController>();
            if (bmc != null)
            {
                bmc.ClearActiveTool();
                return;
            }
        }
        catch { }

        try
        {
            var mb = FindAnyObjectByType<MachineBuilder>();
            if (mb != null) mb.StopBuilding();
        }
        catch { }

        try
        {
            var jb = FindAnyObjectByType<JunctionBuilder>();
            if (jb != null) jb.StopBuilding();
        }
        catch { }

        try { BuildSelectionNotifier.Notify(null); } catch { }
        try
        {
            if (GameManager.Instance != null && GameManager.Instance.State == GameState.Delete)
                GameManager.Instance.SetState(GameState.Build);
        }
        catch { }
    }

    GameObject ResolveToggleTarget(Category cat)
    {
        if (cat == null) return null;
        if (cat.contentPanel != null) return cat.contentPanel;

        // If the button lives inside the contentRoot, try a child named "Content" to avoid hiding the button.
        if (cat.button != null && cat.contentRoot != null && cat.button.transform.IsChildOf(cat.contentRoot.transform))
        {
            var child = cat.contentRoot.transform.Find("Content");
            if (child != null) return child.gameObject;
        }

        return cat.contentRoot;
    }

}
