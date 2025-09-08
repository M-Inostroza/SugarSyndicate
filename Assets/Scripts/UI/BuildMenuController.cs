using UnityEngine;
using UnityEngine.UIElements;

public class BuildMenuController : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] UIDocument uiDocument;
    [SerializeField] StyleSheet styleSheet; // assign BuildMenu.uss
    [SerializeField] BuildModeController buildMode;

    // cached
    VisualElement root;
    Button fab;
    VisualElement menu;
    Button btnConveyor;

    const string ClassOpen = "open";
    const string ClassBuilding = "building";

    void Awake()
    {
        if (uiDocument == null) uiDocument = GetComponent<UIDocument>();
    }

    void OnEnable()
    {
        root = uiDocument != null ? uiDocument.rootVisualElement : null;
        if (root == null) return;

        if (styleSheet != null && !root.styleSheets.Contains(styleSheet))
            root.styleSheets.Add(styleSheet);

        fab = root.Q<Button>("BuildFab");
        menu = root.Q("BuildMenu");
        btnConveyor = root.Q<Button>("BtnConveyor");

        if (fab != null) fab.clicked += ToggleMenu;
        if (btnConveyor != null) btnConveyor.clicked += OnConveyor;

        // Close menu on background click
        root.RegisterCallback<MouseDownEvent>(OnRootMouseDown);

        // ensure initial menu visibility matches USS (hidden)
        if (menu != null)
        {
            menu.style.display = DisplayStyle.None;
            menu.style.visibility = Visibility.Hidden;
        }
    }

    void OnDisable()
    {
        if (root == null) return;
        if (fab != null) fab.clicked -= ToggleMenu;
        if (btnConveyor != null) btnConveyor.clicked -= OnConveyor;
        root.UnregisterCallback<MouseDownEvent>(OnRootMouseDown);
    }

    void ToggleMenu()
    {
        if (menu == null) return;
        bool isOpen = menu.ClassListContains(ClassOpen);
        if (isOpen)
        {
            menu.RemoveFromClassList(ClassOpen);
            menu.style.display = DisplayStyle.None;
            menu.style.visibility = Visibility.Hidden;
        }
        else
        {
            menu.AddToClassList(ClassOpen);
            menu.style.display = DisplayStyle.Flex;
            menu.style.visibility = Visibility.Visible;
        }
    }

    void CloseMenu()
    {
        if (menu == null) return;
        menu.RemoveFromClassList(ClassOpen);
        menu.style.display = DisplayStyle.None;
        menu.style.visibility = Visibility.Hidden;
    }

    void OnRootMouseDown(MouseDownEvent evt)
    {
        // close if clicking outside menu+fab
        if (fab == null || menu == null) return;
        if (fab.worldBound.Contains(evt.mousePosition)) return;
        if (menu.worldBound.Contains(evt.mousePosition)) return;
        CloseMenu();
    }

    void OnConveyor()
    {
        CloseMenu();
        if (buildMode != null)
        {
            buildMode.StartBuildMode(BuildableType.Conveyor);
            root.AddToClassList(ClassBuilding);
            buildMode.onExitBuildMode -= OnExitBuild;
            buildMode.onExitBuildMode += OnExitBuild;
        }
    }

    void OnExitBuild()
    {
        if (root != null) root.RemoveFromClassList(ClassBuilding);
    }
}
