using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Serialization;
using DG.Tweening;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class OnboardingManager : MonoBehaviour
{
    const string DefaultBuildControlUnlockStepId = "camera-move-zoom";
    const string DefaultBuildButtonName = "Build Btn";
    const string DefaultDeleteButtonName = "Delete btn";
    const string DefaultSolarStepId = "place-solar-panel";
    const string DefaultSolarButtonName = "Solar Panel";
    const string DefaultCableButtonName = "Cable";
    const string DefaultPoleButtonName = "Pole";

    public enum StepTrigger
    {
        None = 0,
        DroneHqBlueprintPlaced = 1,
        DroneHqBuilt = 2,
        SolarPanelBuilt = 3,
        BuyDrones = 4,
        SugarMineBuilt = 5,
        MinesPowered = 6,
        PressBuilt = 7,
        CameraMoveZoom = 8,
        MinesConnectedToPresses = 9,
        PressesPowered = 10
    }

    [System.Serializable]
    public class Step
    {
        public string id = "place-drone-hq";
        public string speaker = "Pig Boss";
        public StepTrigger trigger = StepTrigger.DroneHqBuilt;
        public bool hideUiAfterCompletion = false;

        [Header("Guidance")]
        public List<string> messages = new List<string>();
        public List<string> completionMessages = new List<string>();
        public int requiredDroneCount = 3;
        public int requiredCrawlerCount = 0;
        public int requiredMineCount = 1;
        public int requiredPressCount = 1;
        public bool requireCameraMove = false;
        public bool requireZoomIn = false;
        public bool requireZoomOut = false;
        public List<RectTransform> highlightTargets = new List<RectTransform>();
        public string openCategory;
        public List<string> allowedCategories = new List<string>();
        public bool hideDisallowedCategories = true;
        public bool disableDisallowedCategories = true;
        public List<Selectable> enableSelectables = new List<Selectable>();
        public List<Selectable> disableSelectables = new List<Selectable>();
        public List<GameObject> showObjects = new List<GameObject>();
        public List<GameObject> hideObjects = new List<GameObject>();
    }

    public static OnboardingManager Instance { get; private set; }

    [Header("Setup")]
    [SerializeField] bool autoStart = true;
    [SerializeField] bool onlyInTutorialScene = true;
    [SerializeField] int tutorialSceneBuildIndex = 0;
    [SerializeField] bool requireGridService = true;
    [SerializeField] bool autoFindReferences = true;
    [SerializeField] OnboardingDialogueUI dialogueUi;
    [SerializeField] TutorialHighlighter highlighter;
    [SerializeField] BuildMenuController buildMenu;
    [SerializeField] DroneTaskService droneService;
    [SerializeField] PowerService powerService;
    [SerializeField] TutorialGoalUI goalUi;

    [Header("Steps")]
    [SerializeField] TutorialStepsAsset stepsAsset;
    [SerializeField, HideInInspector] List<Step> steps = new List<Step>();

    [Header("Tutorial Overrides")]
    [SerializeField] bool forceDroneHqPlacement = true;
    [SerializeField] string droneHqRequiredCell = "O7";
    [SerializeField, TextArea(2, 4)] string droneHqWrongCellMessage =
        "Can you follow orders or do I need to find another engineer? Place it where I tell you, you'll have more freedom later on to play around.";
    [SerializeField] bool highlightDroneHqCell = true;
    [SerializeField] bool forceSolarPanelPlacement = true;
    [SerializeField] string solarPanelRequiredCells = "O8,N8";
    [SerializeField] bool highlightSolarPanelCells = true;
    [SerializeField] bool focusCameraOnMineSelection = true;
    [SerializeField, Min(0.05f)] float mineFocusDuration = 0.6f;
    [SerializeField] Ease mineFocusEase = Ease.InOutSine;
    [SerializeField, TextArea(2, 4)] string mineWrongCellMessage =
        "Sugar Mines can only be placed on sugar deposits.";
    [SerializeField] TutorialCellOverlay cellOverlay;
    [SerializeField] bool highlightDroneHqDuringBuyStep = true;

    [Header("Build Category Restrictions")]
    [Tooltip("If enabled, each step uses its own allowed categories instead of cumulative unlocks.")]
    [SerializeField] bool useStepSpecificCategoryLocks = true;
    [Tooltip("Force only Extraction category on sugar-mine placement step.")]
    [SerializeField] bool extractionOnlyOnSugarMineStep = true;
    [Tooltip("Force only Power category on mine-powering (cable) step.")]
    [SerializeField] bool powerOnlyOnMinesPoweredStep = true;

    [Header("Tutorial UI Locks")]
    [Tooltip("If enabled, build/delete controls stay hidden until the configured tutorial step is completed.")]
    [SerializeField] bool gateBuildControlsDuringTutorial = true;
    [Tooltip("Step id that unlocks build/delete controls after completion.")]
    [SerializeField] string unlockBuildControlsAfterStepId = DefaultBuildControlUnlockStepId;
    [Tooltip("Optional explicit reference. If empty, fallback name lookup is used.")]
    [SerializeField] GameObject buildButtonObject;
    [Tooltip("Optional explicit highlight target for the Build button. Falls back to Build button RectTransform.")]
    [SerializeField] RectTransform buildButtonHighlightTarget;
    [Tooltip("Optional explicit reference. If empty, fallback name lookup is used.")]
    [SerializeField] GameObject deleteButtonObject;
    [Tooltip("Fallback scene object name used when Build Button reference is not set.")]
    [SerializeField] string buildButtonFallbackName = DefaultBuildButtonName;
    [Tooltip("Fallback scene object name used when Delete Button reference is not set.")]
    [SerializeField] string deleteButtonFallbackName = DefaultDeleteButtonName;

    [Header("Step Timing")]
    [Tooltip("Delay before completing a tutorial step after its objectives are satisfied.")]
    [FormerlySerializedAs("cameraStepCompletionDelaySeconds")]
    [SerializeField, Min(0f)] float stepCompletionDelaySeconds = 1f;

    [Header("Solar Step UI Restriction")]
    [Tooltip("If enabled, only the Solar Panel button is visible during the solar-panel tutorial step.")]
    [SerializeField] bool showOnlySolarPanelOnSolarStep = true;
    [SerializeField] string solarPanelStepId = DefaultSolarStepId;
    [SerializeField] GameObject solarPanelBuildButtonObject;
    [SerializeField] GameObject cableBuildButtonObject;
    [SerializeField] GameObject poleBuildButtonObject;
    [SerializeField] string solarPanelButtonFallbackName = DefaultSolarButtonName;
    [SerializeField] string cableButtonFallbackName = DefaultCableButtonName;
    [SerializeField] string poleButtonFallbackName = DefaultPoleButtonName;

    [Header("Buy Crew Step")]
    [Tooltip("If enabled, the buy-drones step completes only after the machine overview panel is closed.")]
    [SerializeField] bool requireMachineOverviewClosedForBuyStep = true;
    [SerializeField] MachineInspectUI machineInspectUi;

    int currentStepIndex = -1;
    readonly HashSet<string> completedSteps = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    bool isActive;
    int messageIndex;
    int completionIndex;
    DialogueMode dialogueMode = DialogueMode.None;
    readonly List<SugarMine> trackedMines = new List<SugarMine>();
    readonly List<PressMachine> trackedPresses = new List<PressMachine>();
    readonly HashSet<string> unlockedCategories = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    readonly List<string> effectiveCategories = new List<string>(4);
    bool cameraMoved;
    bool zoomedIn;
    bool zoomedOut;
    bool oneOffDialogueActive;
    Tweener mineFocusTween;
    readonly List<RectTransform> resolvedHighlightTargets = new List<RectTransform>(4);
    Coroutine pendingStepCompletionRoutine;
    int pendingStepCompletionIndex = -1;
    string pendingStepCompletionId;
    readonly Dictionary<GameObject, bool> cachedPowerButtonVisibility = new Dictionary<GameObject, bool>();

    enum DialogueMode
    {
        None,
        Intro,
        Completion
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        EnsureStepsInitialized();
    }

    List<Step> GetSteps()
    {
        if (stepsAsset != null)
        {
            if (stepsAsset.steps == null)
                stepsAsset.steps = new List<Step>();
            return stepsAsset.steps;
        }
        if (steps == null)
            steps = new List<Step>();
        return steps;
    }

    void EnsureStepsInitialized()
    {
        var list = GetSteps();
        if (list == null || list.Count == 0)
        {
            var defaults = BuildDefaultSteps();
            if (stepsAsset != null)
                stepsAsset.steps = defaults;
            else
                steps = defaults;
            list = GetSteps();
        }
        EnsureRequiredSteps(list);
    }

    void EnsureRequiredSteps(List<Step> list)
    {
        if (list == null) return;
        if (!HasStepId(list, "power-presses"))
        {
            var insertIndex = FindStepIndex(list, "connect-mines-presses");
            if (insertIndex < 0) insertIndex = list.Count - 1;
            if (insertIndex < 0) insertIndex = 0;
            list.Insert(Mathf.Clamp(insertIndex + 1, 0, list.Count), CreatePressesPoweredStep());
        }
    }

    static bool HasStepId(List<Step> list, string id)
    {
        if (list == null || string.IsNullOrWhiteSpace(id)) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var step = list[i];
            if (step == null || string.IsNullOrWhiteSpace(step.id)) continue;
            if (step.id == id) return true;
        }
        return false;
    }

    static int FindStepIndex(List<Step> list, string id)
    {
        if (list == null || string.IsNullOrWhiteSpace(id)) return -1;
        for (int i = 0; i < list.Count; i++)
        {
            var step = list[i];
            if (step == null || string.IsNullOrWhiteSpace(step.id)) continue;
            if (step.id == id) return i;
        }
        return -1;
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (Application.isPlaying) return;
        TryAutoCreateStepsAsset();
    }

    void TryAutoCreateStepsAsset()
    {
        if (stepsAsset != null) return;

        const string assetPath = "Assets/_Project/Data/TutorialSteps.asset";
        var existing = AssetDatabase.LoadAssetAtPath<TutorialStepsAsset>(assetPath);
        if (existing == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/_Project/Data"))
            {
                if (!AssetDatabase.IsValidFolder("Assets/_Project"))
                    AssetDatabase.CreateFolder("Assets", "_Project");
                AssetDatabase.CreateFolder("Assets/_Project", "Data");
            }

            existing = ScriptableObject.CreateInstance<TutorialStepsAsset>();
            var source = (steps != null && steps.Count > 0) ? steps : BuildDefaultSteps();
            existing.steps = new List<Step>(source);
            AssetDatabase.CreateAsset(existing, assetPath);
            AssetDatabase.SaveAssets();
        }
        else if ((existing.steps == null || existing.steps.Count == 0) && steps != null && steps.Count > 0)
        {
            existing.steps = new List<Step>(steps);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
        }

        if (existing != null)
        {
            EnsureRequiredSteps(existing.steps);
            EditorUtility.SetDirty(existing);
            AssetDatabase.SaveAssets();
        }

        stepsAsset = existing;
        EditorUtility.SetDirty(this);
    }
#endif

    static List<Step> BuildDefaultSteps()
    {
        return new List<Step>
        {
            new Step
            {
                id = "camera-move-zoom",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "First, learn to move the camera.",
                    "Drag with your finger to pan around, then use the zoom buttons to zoom in and out."
                },
                completionMessages = new List<string>
                {
                    "Good. Now you can move around."
                },
                trigger = StepTrigger.CameraMoveZoom,
                requireCameraMove = true,
                requireZoomIn = true,
                requireZoomOut = true
            },
            new Step
            {
                id = "place-drone-hq",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "Glad you're here. The last engineer had a few issues and we had to get rid of h... I mean, he found a better opportunity.",
                    "Anyway, I'm Pig Boss. I'll teach you the basics.",
                    "First task: place your Drone HQ. It's on me.",
                    "Drop it on O7 so the crew has a home base."
                },
                completionMessages = new List<string>
                {
                    "Nice. HQ online and drones are ready.",
                    "They will handle construction from here."
                },
                trigger = StepTrigger.DroneHqBuilt,
                openCategory = "Essential",
                allowedCategories = new List<string> { "Essential" }
            },
            new Step
            {
                id = "buy-drones",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "Good. Now we need more hands.",
                    "Select the Drone HQ and buy three drones and three crawlers."
                },
                completionMessages = new List<string>
                {
                    "Perfect. That should keep the place running."
                },
                trigger = StepTrigger.BuyDrones,
                requiredDroneCount = 3,
                requiredCrawlerCount = 3,
                allowedCategories = new List<string> { "Essential" }
            },
            new Step
            {
                id = "place-solar-panel",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "Now we need power.",
                    "Open the Power category and place a Solar Panel."
                },
                completionMessages = new List<string>
                {
                    "Great. You can build more power sources later."
                },
                trigger = StepTrigger.SolarPanelBuilt,
                openCategory = "Power",
                allowedCategories = new List<string> { "Essential", "Power" }
            },
            new Step
            {
                id = "place-sugar-mine",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "Now we need a steady sugar supply.",
                    "Open the Extraction category and place a Sugar Mine.",
                    "Sugar Mines can only be placed on sugar deposits."
                },
                completionMessages = new List<string>
                {
                    "Nice. That'll keep the sweets flowing."
                },
                trigger = StepTrigger.SugarMineBuilt,
                requiredMineCount = 2,
                openCategory = "Extraction",
                allowedCategories = new List<string> { "Extraction" }
            },
            new Step
            {
                id = "power-mines",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "Now connect the generator to your mines with power cables.",
                    "All current mines need electricity."
                },
                completionMessages = new List<string>
                {
                    "Good. The mines are powered."
                },
                trigger = StepTrigger.MinesPowered,
                openCategory = "Power",
                allowedCategories = new List<string> { "Power" }
            },
            new Step
            {
                id = "build-presses",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "Time to make sugar cubes.",
                    "Build two Presses."
                },
                completionMessages = new List<string>
                {
                    "Nice. You're ready to scale production."
                },
                trigger = StepTrigger.PressBuilt,
                requiredPressCount = 2,
                openCategory = "Processing",
                allowedCategories = new List<string> { "Essential", "Processing" }
            },
            new Step
            {
                id = "connect-mines-presses",
                speaker = "Pig Boss",
                messages = new List<string>
                {
                    "Now connect your mines to the presses with conveyor belts.",
                    "Every mine should connect to every press."
                },
                completionMessages = new List<string>
                {
                    "Perfect. The presses can finally chew through sugar."
                },
                trigger = StepTrigger.MinesConnectedToPresses,
                openCategory = "Essential",
                allowedCategories = new List<string> { "Essential" }
            },
            CreatePressesPoweredStep()
        };
    }

    static Step CreatePressesPoweredStep()
    {
        return new Step
        {
            id = "power-presses",
            speaker = "Pig Boss",
            messages = new List<string>
            {
                "Now provide electricity to your presses.",
                "All current presses need power."
            },
            completionMessages = new List<string>
            {
                "Great. The presses are powered."
            },
            trigger = StepTrigger.PressesPowered,
            openCategory = "Power",
            allowedCategories = new List<string> { "Essential", "Power" }
        };
    }

    void Start()
    {
        if (autoStart)
            TryActivateForScene(SceneManager.GetActiveScene());
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        if (autoFindReferences) EnsureDialogue();
        BlueprintTask.BlueprintPlaced += HandleBlueprintPlaced;
        BlueprintTask.BlueprintCompleted += HandleBlueprintCompleted;
        SubscribeDialogue(true);
        SubscribeDroneService(true);
        SubscribePowerService(true);
        CameraDragPan.CameraDragged += HandleCameraDragged;
        CameraZoomController.ZoomedIn += HandleZoomedIn;
        CameraZoomController.ZoomedOut += HandleZoomedOut;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        BlueprintTask.BlueprintPlaced -= HandleBlueprintPlaced;
        BlueprintTask.BlueprintCompleted -= HandleBlueprintCompleted;
        SubscribeDialogue(false);
        SubscribeDroneService(false);
        SubscribePowerService(false);
        CameraDragPan.CameraDragged -= HandleCameraDragged;
        CameraZoomController.ZoomedIn -= HandleZoomedIn;
        CameraZoomController.ZoomedOut -= HandleZoomedOut;
        CancelPendingStepCompletion();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;

        if (step.trigger == StepTrigger.BuyDrones)
        {
            EvaluateBuyDronesStep(step);
            UpdateGoalUi(step);
        }

        if (showOnlySolarPanelOnSolarStep && IsSolarPanelTutorialStep(step))
            ApplySolarStepPowerButtonVisibility(step);
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (autoStart)
            TryActivateForScene(scene);
    }

    void TryActivateForScene(Scene scene)
    {
        if (!ShouldRunInScene(scene))
        {
            Deactivate();
            return;
        }

        if (autoFindReferences) EnsureDialogue();
        if (autoFindReferences) EnsureDroneService();
        if (autoFindReferences) EnsurePowerService();
        if (autoFindReferences) EnsureGoalUi();
        isActive = true;
        ApplyTutorialUiLocks();
        AdvanceToFirstIncompleteStep();
    }

    bool ShouldRunInScene(Scene scene)
    {
        if (onlyInTutorialScene && scene.buildIndex != tutorialSceneBuildIndex)
            return false;
        if (requireGridService && FindAnyObjectByType<GridService>() == null)
            return false;
        return true;
    }

    void Deactivate()
    {
        isActive = false;
        CancelPendingStepCompletion();
        currentStepIndex = -1;
        dialogueMode = DialogueMode.None;
        trackedMines.Clear();
        unlockedCategories.Clear();
        mineFocusTween?.Kill();
        mineFocusTween = null;
        cameraMoved = false;
        zoomedIn = false;
        zoomedOut = false;
        if (goalUi != null) goalUi.Hide();
        HidePlacementHighlight();
        if (buildMenu == null && autoFindReferences)
            buildMenu = FindAnyObjectByType<BuildMenuController>();
        if (buildMenu != null)
            buildMenu.ResetCategoryStates();
        ApplySolarStepPowerButtonVisibility(null);
        cachedPowerButtonVisibility.Clear();
        HideUi();
        ApplyTutorialUiLocks();
    }

    void EnsureDialogue()
    {
        if (dialogueUi == null)
            dialogueUi = FindAnyObjectByType<OnboardingDialogueUI>();
        if (dialogueUi == null) return;
        dialogueUi.SetAutoHideOnClick(false);
        SubscribeDialogue(true);
    }

    void EnsureDroneService()
    {
        if (droneService == null)
            droneService = FindAnyObjectByType<DroneTaskService>();
        SubscribeDroneService(true);
    }

    void EnsurePowerService()
    {
        if (powerService == null)
            powerService = PowerService.Instance ?? FindAnyObjectByType<PowerService>();
        SubscribePowerService(true);
    }

    void EnsureGoalUi()
    {
        if (goalUi == null && autoFindReferences)
            goalUi = FindAnyObjectByType<TutorialGoalUI>();
        if (goalUi == null && autoFindReferences)
            goalUi = TutorialGoalUI.CreateDefault(transform);
    }

    void SubscribeDialogue(bool add)
    {
        if (dialogueUi == null) return;
        if (add)
        {
            dialogueUi.Clicked -= HandleDialogueClicked;
            dialogueUi.Clicked += HandleDialogueClicked;
        }
        else
        {
            dialogueUi.Clicked -= HandleDialogueClicked;
        }
    }

    void SubscribeDroneService(bool add)
    {
        if (droneService == null) return;
        if (add)
        {
            droneService.OnDroneCountChanged -= HandleDroneCountChanged;
            droneService.OnDroneCountChanged += HandleDroneCountChanged;
            droneService.OnCrawlerCountChanged -= HandleCrawlerCountChanged;
            droneService.OnCrawlerCountChanged += HandleCrawlerCountChanged;
        }
        else
        {
            droneService.OnDroneCountChanged -= HandleDroneCountChanged;
            droneService.OnCrawlerCountChanged -= HandleCrawlerCountChanged;
        }
    }

    void SubscribePowerService(bool add)
    {
        if (powerService == null)
            powerService = PowerService.Instance ?? FindAnyObjectByType<PowerService>();
        if (powerService == null) return;

        if (add)
        {
            powerService.OnPowerChanged -= HandlePowerChanged;
            powerService.OnPowerChanged += HandlePowerChanged;
            powerService.OnNetworkChanged -= HandlePowerNetworkChanged;
            powerService.OnNetworkChanged += HandlePowerNetworkChanged;
        }
        else
        {
            powerService.OnPowerChanged -= HandlePowerChanged;
            powerService.OnNetworkChanged -= HandlePowerNetworkChanged;
        }
    }

    void EnsureHighlighter()
    {
        if (highlighter == null && autoFindReferences)
            highlighter = FindAnyObjectByType<TutorialHighlighter>();
    }

    void EnsureCellOverlay()
    {
        if (cellOverlay == null && autoFindReferences)
        {
            cellOverlay = FindAnyObjectByType<TutorialCellOverlay>();
            if (cellOverlay == null)
            {
                var grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
                if (grid != null)
                    cellOverlay = TutorialCellOverlay.FindOrCreate(grid);
            }
        }
    }

    void AdvanceToFirstIncompleteStep()
    {
        var list = GetSteps();
        if (!isActive || list == null || list.Count == 0)
        {
            HideUi();
            if (goalUi != null) goalUi.Hide();
            HidePlacementHighlight();
            ApplySolarStepPowerButtonVisibility(null);
            ApplyTutorialUiLocks();
            return;
        }

        int nextIndex = FindNextIncompleteIndex(0);
        if (nextIndex < 0)
        {
            HideUi();
            if (goalUi != null) goalUi.Hide();
            HidePlacementHighlight();
            ApplySolarStepPowerButtonVisibility(null);
            ApplyTutorialUiLocks();
            return;
        }

        StartStep(nextIndex);
    }

    int FindNextIncompleteIndex(int startIndex)
    {
        var list = GetSteps();
        if (list == null) return -1;
        for (int i = Mathf.Max(0, startIndex); i < list.Count; i++)
        {
            var step = list[i];
            if (step == null) continue;
            if (IsStepComplete(step)) continue;
            return i;
        }
        return -1;
    }

    bool IsStepComplete(Step step)
    {
        if (step == null) return true;
        if (!string.IsNullOrWhiteSpace(step.id) && completedSteps.Contains(step.id))
            return true;

        switch (step.trigger)
        {
            case StepTrigger.DroneHqBlueprintPlaced:
                return DroneHQ.Instance != null || BlueprintTask.HasHqBlueprint;
            case StepTrigger.DroneHqBuilt:
                return DroneHQ.Instance != null;
            case StepTrigger.SolarPanelBuilt:
                return HasBuiltSolarPanel();
            case StepTrigger.SugarMineBuilt:
                return GetMineCount() >= Mathf.Max(0, step.requiredMineCount);
            case StepTrigger.BuyDrones:
                return IsBuyDronesStepSatisfied(step);
            case StepTrigger.MinesPowered:
                return AreAllMinesPowered(GetCurrentMines(), true);
            case StepTrigger.PressBuilt:
                return GetPressCount() >= Mathf.Max(0, step.requiredPressCount);
            case StepTrigger.MinesConnectedToPresses:
                return AreAllMinesConnectedToPresses(GetCurrentMines(), GetCurrentPresses(), true);
            case StepTrigger.PressesPowered:
                return AreAllPressesPowered(GetCurrentPresses(), true);
            case StepTrigger.CameraMoveZoom:
                return IsCameraStepComplete(step);
            case StepTrigger.None:
                return false;
        }
        return false;
    }

    void StartStep(int index)
    {
        var list = GetSteps();
        if (index < 0 || list == null || index >= list.Count) return;
        CancelPendingStepCompletion();
        currentStepIndex = index;
        var step = list[index];
        messageIndex = 0;
        completionIndex = 0;
        dialogueMode = DialogueMode.Intro;
        if (step.trigger == StepTrigger.MinesPowered)
            CaptureTrackedMines();
        if (step.trigger == StepTrigger.MinesConnectedToPresses)
            CaptureTrackedMinesAndPresses();
        if (step.trigger == StepTrigger.PressesPowered)
            CaptureTrackedPresses();
        if (step.trigger == StepTrigger.CameraMoveZoom)
            ResetCameraStepState(step);
        ShowIntroMessage(step, messageIndex);
        ApplyStepUi(step);
        if (GetIntroMessages(step).Count == 0)
        {
            dialogueMode = DialogueMode.None;
            UpdateGoalUi(step);
        }
        else if (goalUi != null)
        {
            goalUi.Hide();
        }
        if (step.trigger == StepTrigger.MinesPowered)
            TryCompleteMinesPowered(step);
        if (step.trigger == StepTrigger.MinesConnectedToPresses)
            TryCompleteMinesConnected(step);
        if (step.trigger == StepTrigger.PressesPowered)
            TryCompletePressesPowered(step);
        ApplyTutorialUiLocks();
    }

    void CompleteCurrentStepImmediate()
    {
        var step = GetCurrentStep();
        if (step == null) return;
        CancelPendingStepCompletion();

        if (!string.IsNullOrWhiteSpace(step.id))
            completedSteps.Add(step.id);
        ApplyTutorialUiLocks();

        if (highlighter != null)
            highlighter.ClearTargets();
        if (IsDroneHqPlacementStep(step) || IsSolarPanelPlacementStep(step))
            HidePlacementHighlight();
        if (goalUi != null) goalUi.Hide();
        var completion = GetCompletionMessages(step);
        if (completion.Count > 0)
        {
            completionIndex = 0;
            dialogueMode = DialogueMode.Completion;
            ShowMessage(step.speaker, completion[completionIndex]);
        }
        else
        {
            AdvanceToNextIncompleteStep();
        }
    }

    void RequestCompleteCurrentStep(Step step = null)
    {
        step ??= GetCurrentStep();
        if (step == null || !isActive) return;

        if (pendingStepCompletionRoutine != null)
        {
            bool sameStep = pendingStepCompletionIndex == currentStepIndex
                && string.Equals(pendingStepCompletionId, step.id, System.StringComparison.OrdinalIgnoreCase);
            if (sameStep) return;
            CancelPendingStepCompletion();
        }

        if (stepCompletionDelaySeconds <= 0f)
        {
            CompleteCurrentStepImmediate();
            return;
        }

        pendingStepCompletionIndex = currentStepIndex;
        pendingStepCompletionId = step.id;
        pendingStepCompletionRoutine = StartCoroutine(CompleteCurrentStepAfterDelay(stepCompletionDelaySeconds));
    }

    IEnumerator CompleteCurrentStepAfterDelay(float delaySeconds)
    {
        if (delaySeconds > 0f)
            yield return new WaitForSecondsRealtime(delaySeconds);

        pendingStepCompletionRoutine = null;
        int expectedIndex = pendingStepCompletionIndex;
        string expectedId = pendingStepCompletionId;
        pendingStepCompletionIndex = -1;
        pendingStepCompletionId = null;

        if (!isActive) yield break;
        if (dialogueMode != DialogueMode.None) yield break;
        if (currentStepIndex != expectedIndex) yield break;

        var current = GetCurrentStep();
        if (current == null) yield break;
        if (!string.IsNullOrWhiteSpace(expectedId)
            && !string.Equals(current.id, expectedId, System.StringComparison.OrdinalIgnoreCase))
            yield break;
        if (!IsStepComplete(current)) yield break;

        CompleteCurrentStepImmediate();
    }

    void CancelPendingStepCompletion()
    {
        if (pendingStepCompletionRoutine != null)
            StopCoroutine(pendingStepCompletionRoutine);
        pendingStepCompletionRoutine = null;
        pendingStepCompletionIndex = -1;
        pendingStepCompletionId = null;
    }

    void ApplyStepUi(Step step)
    {
        if (step == null) return;

        EnsureHighlighter();
        ApplyHighlighterTargets(step);
        UpdatePlacementHighlight(step);

        if (buildMenu == null)
            buildMenu = FindAnyObjectByType<BuildMenuController>();
        if (buildMenu != null)
        {
            var allowedCategories = BuildEffectiveAllowedCategories(step);
            if (allowedCategories.Count > 0)
            {
                if (useStepSpecificCategoryLocks)
                {
                    buildMenu.SetAllowedCategories(allowedCategories, step.hideDisallowedCategories, step.disableDisallowedCategories);
                }
                else
                {
                    UnlockCategories(allowedCategories);
                    if (unlockedCategories.Count > 0)
                        buildMenu.SetAllowedCategories(GetUnlockedCategoryList(), step.hideDisallowedCategories, step.disableDisallowedCategories);
                }
            }
            else if (useStepSpecificCategoryLocks)
            {
                buildMenu.ResetCategoryStates();
            }
            if (!string.IsNullOrWhiteSpace(step.openCategory))
                buildMenu.ShowCategory(step.openCategory);
        }

        SetSelectablesInteractable(step.enableSelectables, true);
        SetSelectablesInteractable(step.disableSelectables, false);
        SetObjectsActive(step.showObjects, true);
        SetObjectsActive(step.hideObjects, false);
        ApplySolarStepPowerButtonVisibility(step);
        ApplyTutorialUiLocks();
    }

    List<string> BuildEffectiveAllowedCategories(Step step)
    {
        effectiveCategories.Clear();
        if (step == null) return effectiveCategories;

        if (extractionOnlyOnSugarMineStep && step.trigger == StepTrigger.SugarMineBuilt)
        {
            effectiveCategories.Add("Extraction");
            return effectiveCategories;
        }

        if (powerOnlyOnMinesPoweredStep && step.trigger == StepTrigger.MinesPowered)
        {
            effectiveCategories.Add("Power");
            return effectiveCategories;
        }

        if (step.allowedCategories == null || step.allowedCategories.Count == 0)
            return effectiveCategories;

        for (int i = 0; i < step.allowedCategories.Count; i++)
        {
            var category = step.allowedCategories[i];
            if (string.IsNullOrWhiteSpace(category)) continue;
            var trimmed = category.Trim();
            if (!effectiveCategories.Contains(trimmed))
                effectiveCategories.Add(trimmed);
        }
        return effectiveCategories;
    }

    void ApplyHighlighterTargets(Step step)
    {
        if (highlighter == null)
            return;

        resolvedHighlightTargets.Clear();
        bool useBuildHighlight = ShouldHighlightBuildButton(step) && ShouldShowBuildControls();
        if (useBuildHighlight && TryGetBuildButtonHighlightTarget(out var buildTarget))
        {
            resolvedHighlightTargets.Add(buildTarget);
        }
        else
        {
            AddHighlightTargets(resolvedHighlightTargets, step.highlightTargets);
        }

        if (resolvedHighlightTargets.Count > 0)
            highlighter.SetTargets(resolvedHighlightTargets);
        else
            highlighter.ClearTargets();
    }

    static void AddHighlightTargets(List<RectTransform> destination, IReadOnlyList<RectTransform> source)
    {
        if (destination == null || source == null) return;
        for (int i = 0; i < source.Count; i++)
        {
            var target = source[i];
            if (target == null) continue;
            if (!destination.Contains(target))
                destination.Add(target);
        }
    }

    bool ShouldHighlightBuildButton(Step step)
    {
        if (step == null) return false;
        switch (step.trigger)
        {
            case StepTrigger.DroneHqBlueprintPlaced:
            case StepTrigger.DroneHqBuilt:
            case StepTrigger.SolarPanelBuilt:
            case StepTrigger.SugarMineBuilt:
            case StepTrigger.MinesPowered:
            case StepTrigger.PressBuilt:
            case StepTrigger.MinesConnectedToPresses:
            case StepTrigger.PressesPowered:
                return true;
            default:
                return false;
        }
    }

    bool TryGetBuildButtonHighlightTarget(out RectTransform target)
    {
        if (buildButtonHighlightTarget != null)
        {
            target = buildButtonHighlightTarget;
            return true;
        }

        EnsureBuildControlObjects();
        if (buildButtonObject != null && buildButtonObject.TryGetComponent<RectTransform>(out var rect))
        {
            target = rect;
            return true;
        }

        target = null;
        return false;
    }

    void ApplySolarStepPowerButtonVisibility(Step step)
    {
        EnsureSolarStepPowerButtons();
        CachePowerButtonVisibility(solarPanelBuildButtonObject);
        CachePowerButtonVisibility(cableBuildButtonObject);
        CachePowerButtonVisibility(poleBuildButtonObject);

        if (!showOnlySolarPanelOnSolarStep)
        {
            RestoreCachedPowerButtonVisibility();
            return;
        }

        bool isSolarStep = IsSolarPanelTutorialStep(step);
        if (isSolarStep)
        {
            SetPowerButtonActive(solarPanelBuildButtonObject, true);
            SetPowerButtonActive(cableBuildButtonObject, false);
            SetPowerButtonActive(poleBuildButtonObject, false);
        }
        else
        {
            RestoreCachedPowerButtonVisibility();
        }
    }

    bool IsSolarPanelTutorialStep(Step step)
    {
        if (step == null) return false;
        if (!string.IsNullOrWhiteSpace(solarPanelStepId))
            return string.Equals(step.id, solarPanelStepId, System.StringComparison.OrdinalIgnoreCase);
        return IsSolarPanelPlacementStep(step);
    }

    void EnsureSolarStepPowerButtons()
    {
        if (solarPanelBuildButtonObject == null && !string.IsNullOrWhiteSpace(solarPanelButtonFallbackName))
            solarPanelBuildButtonObject = FindSceneObjectByName(solarPanelButtonFallbackName);
        if (cableBuildButtonObject == null && !string.IsNullOrWhiteSpace(cableButtonFallbackName))
            cableBuildButtonObject = FindSceneObjectByName(cableButtonFallbackName);
        if (poleBuildButtonObject == null && !string.IsNullOrWhiteSpace(poleButtonFallbackName))
            poleBuildButtonObject = FindSceneObjectByName(poleButtonFallbackName);
    }

    void CachePowerButtonVisibility(GameObject go)
    {
        if (go == null) return;
        if (!cachedPowerButtonVisibility.ContainsKey(go))
            cachedPowerButtonVisibility[go] = go.activeSelf;
    }

    void RestoreCachedPowerButtonVisibility()
    {
        RestoreCachedPowerButtonVisibility(solarPanelBuildButtonObject);
        RestoreCachedPowerButtonVisibility(cableBuildButtonObject);
        RestoreCachedPowerButtonVisibility(poleBuildButtonObject);
    }

    void RestoreCachedPowerButtonVisibility(GameObject go)
    {
        if (go == null) return;
        if (cachedPowerButtonVisibility.TryGetValue(go, out var wasActive))
        {
            if (go.activeSelf != wasActive)
                go.SetActive(wasActive);
            return;
        }
        if (!go.activeSelf)
            go.SetActive(true);
    }

    static void SetPowerButtonActive(GameObject go, bool active)
    {
        if (go == null) return;
        if (go.activeSelf != active)
            go.SetActive(active);
    }

    static void SetSelectablesInteractable(IReadOnlyList<Selectable> selectables, bool interactable)
    {
        if (selectables == null) return;
        for (int i = 0; i < selectables.Count; i++)
        {
            var selectable = selectables[i];
            if (selectable != null) selectable.interactable = interactable;
        }
    }

    static void SetObjectsActive(IReadOnlyList<GameObject> objects, bool active)
    {
        if (objects == null) return;
        for (int i = 0; i < objects.Count; i++)
        {
            var go = objects[i];
            if (go != null) go.SetActive(active);
        }
    }

    void UpdatePlacementHighlight(Step step)
    {
        if (IsDroneHqPlacementStep(step) && highlightDroneHqCell && forceDroneHqPlacement)
        {
            if (TryGetDroneHqRequiredCell(out var cell))
            {
                ShowPlacementHighlight(new[] { cell });
                return;
            }
        }

        if (IsSolarPanelPlacementStep(step) && highlightSolarPanelCells && forceSolarPanelPlacement)
        {
            if (TryGetSolarPanelRequiredCells(out var cells))
            {
                ShowPlacementHighlight(cells);
                return;
            }
        }

        if (IsBuyDronesStep(step) && highlightDroneHqDuringBuyStep)
        {
            if (TryGetDroneHqCell(out var hqCell))
            {
                ShowPlacementHighlight(new[] { hqCell });
                return;
            }
        }

        HidePlacementHighlight();
    }

    void ShowPlacementHighlight(System.Collections.Generic.IReadOnlyList<Vector2Int> cells)
    {
        EnsureCellOverlay();
        if (cellOverlay != null)
            cellOverlay.ShowCells(cells);
    }

    void HidePlacementHighlight()
    {
        if (cellOverlay != null)
            cellOverlay.Hide();
    }

    bool IsDroneHqPlacementStep(Step step)
    {
        return step != null && step.trigger == StepTrigger.DroneHqBuilt;
    }

    bool IsSolarPanelPlacementStep(Step step)
    {
        return step != null && step.trigger == StepTrigger.SolarPanelBuilt;
    }

    static bool IsBuyDronesStep(Step step)
    {
        return step != null && step.trigger == StepTrigger.BuyDrones;
    }

    void UnlockCategories(IReadOnlyList<string> categories)
    {
        if (categories == null) return;
        for (int i = 0; i < categories.Count; i++)
        {
            var name = categories[i];
            if (string.IsNullOrWhiteSpace(name)) continue;
            unlockedCategories.Add(name.Trim());
        }
    }

    List<string> GetUnlockedCategoryList()
    {
        var list = new List<string>();
        foreach (var name in unlockedCategories)
            list.Add(name);
        return list;
    }

    bool IsSugarMinePlacementStep(Step step)
    {
        return step != null && step.trigger == StepTrigger.SugarMineBuilt;
    }

    bool TryGetDroneHqRequiredCell(out Vector2Int cell)
    {
        cell = default;
        if (!forceDroneHqPlacement) return false;
        if (!TryParseCellLabel(droneHqRequiredCell, out cell)) return false;

        var grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (grid != null && !grid.InBounds(cell)) return false;
        return true;
    }

    bool TryGetSolarPanelRequiredCells(out List<Vector2Int> cells)
    {
        cells = null;
        if (!forceSolarPanelPlacement) return false;
        if (!TryParseCellList(solarPanelRequiredCells, out cells)) return false;

        var grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (grid != null)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (!grid.InBounds(cells[i])) return false;
            }
        }
        return cells != null && cells.Count > 0;
    }

    bool TryGetDroneHqCell(out Vector2Int cell)
    {
        cell = default;
        var hq = DroneHQ.Instance;
        var grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (hq == null || grid == null) return false;
        cell = grid.WorldToCell(hq.transform.position);
        return grid.InBounds(cell);
    }

    static bool TryParseCellLabel(string label, out Vector2Int cell)
    {
        cell = default;
        if (string.IsNullOrWhiteSpace(label)) return false;

        label = label.Trim().ToUpperInvariant();
        int i = 0;
        int col = 0;
        while (i < label.Length && char.IsLetter(label[i]))
        {
            int value = label[i] - 'A' + 1;
            if (value < 1 || value > 26) return false;
            col = col * 26 + value;
            i++;
        }
        if (i == 0) return false;
        if (i >= label.Length) return false;
        if (!int.TryParse(label.Substring(i), out int row)) return false;
        if (row < 1) return false;

        cell = new Vector2Int(col - 1, row - 1);
        return true;
    }

    public bool ShouldBlockDroneHqPlacement(Vector2Int cell)
    {
        if (!isActive || !forceDroneHqPlacement) return false;
        var step = GetCurrentStep();
        if (!IsDroneHqPlacementStep(step)) return false;
        if (!TryGetDroneHqRequiredCell(out var requiredCell)) return false;
        if (cell == requiredCell) return false;

        ShowOneOffMessage(droneHqWrongCellMessage);
        if (highlightDroneHqCell)
            ShowPlacementHighlight(new[] { requiredCell });
        return true;
    }

    public bool ShouldBlockSolarPanelPlacement(IReadOnlyList<Vector2Int> footprint)
    {
        if (!isActive || !forceSolarPanelPlacement) return false;
        var step = GetCurrentStep();
        if (!IsSolarPanelPlacementStep(step)) return false;
        if (footprint == null || footprint.Count == 0) return false;
        if (!TryGetSolarPanelRequiredCells(out var requiredCells)) return false;
        if (MatchesRequiredCells(footprint, requiredCells)) return false;

        ShowOneOffMessage(droneHqWrongCellMessage);
        if (highlightSolarPanelCells)
            ShowPlacementHighlight(requiredCells);
        return true;
    }

    public void HandleMineBuildSelected()
    {
        if (!isActive || !focusCameraOnMineSelection) return;
        var step = GetCurrentStep();
        if (!IsSugarMinePlacementStep(step)) return;
        if (step != null && GetMineCount() >= Mathf.Max(0, step.requiredMineCount)) return;
        TryFocusCameraOnSugarDeposit();
    }

    public void NotifyMinePlacementInvalid(Vector2Int cell)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (!IsSugarMinePlacementStep(step)) return;
        ShowOneOffMessage(mineWrongCellMessage);
    }

    bool TryFocusCameraOnSugarDeposit()
    {
        var grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (grid == null) return false;
        var zones = grid.SugarZones;
        if (zones == null || zones.Count == 0) return false;
        var cam = Camera.main;
        if (cam == null) cam = FindAnyObjectByType<Camera>();
        if (cam == null) return false;

        var camPos = cam.transform.position;
        Vector2Int bestCell = zones[0].center;
        float bestDist = float.MaxValue;
        for (int i = 0; i < zones.Count; i++)
        {
            var world = grid.CellToWorld(zones[i].center, camPos.z);
            var dx = world.x - camPos.x;
            var dy = world.y - camPos.y;
            float d = dx * dx + dy * dy;
            if (d < bestDist)
            {
                bestDist = d;
                bestCell = zones[i].center;
            }
        }

        var targetWorld = grid.CellToWorld(bestCell, camPos.z);
        var targetPos = new Vector3(targetWorld.x, targetWorld.y, camPos.z);
        mineFocusTween?.Kill();
        mineFocusTween = cam.transform.DOMove(targetPos, mineFocusDuration)
            .SetEase(mineFocusEase)
            .SetUpdate(true)
            .SetTarget(this);
        return true;
    }

    static bool TryParseCellList(string value, out List<Vector2Int> cells)
    {
        cells = new List<Vector2Int>();
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryParseCellLabel(parts[i], out var cell)) continue;
            if (!cells.Contains(cell))
                cells.Add(cell);
        }
        return cells.Count > 0;
    }

    static bool MatchesRequiredCells(IReadOnlyList<Vector2Int> footprint, List<Vector2Int> required)
    {
        if (required == null || required.Count == 0) return false;
        if (footprint == null || footprint.Count != required.Count) return false;

        var remaining = new HashSet<Vector2Int>(required);
        for (int i = 0; i < footprint.Count; i++)
        {
            if (!remaining.Remove(footprint[i]))
                return false;
        }
        return remaining.Count == 0;
    }

    Step GetCurrentStep()
    {
        var list = GetSteps();
        if (currentStepIndex < 0 || list == null || currentStepIndex >= list.Count)
            return null;
        return list[currentStepIndex];
    }

    void HandleBlueprintPlaced(BlueprintTask.BlueprintType type, BlueprintTask task)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.DroneHqBlueprintPlaced && type == BlueprintTask.BlueprintType.DroneHQ)
            RequestCompleteCurrentStep(step);
    }

    void HandleBlueprintCompleted(BlueprintTask.BlueprintType type, BlueprintTask task)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.DroneHqBuilt && type == BlueprintTask.BlueprintType.DroneHQ)
            RequestCompleteCurrentStep(step);
        else if (step.trigger == StepTrigger.SolarPanelBuilt && type == BlueprintTask.BlueprintType.Machine && IsSolarPanelTask(task))
            RequestCompleteCurrentStep(step);
        else if (step.trigger == StepTrigger.SugarMineBuilt && type == BlueprintTask.BlueprintType.Machine && IsSugarMineTask(task))
        {
            if (GetMineCount() >= Mathf.Max(0, step.requiredMineCount))
                RequestCompleteCurrentStep(step);
        }
        else if (step.trigger == StepTrigger.PressBuilt && type == BlueprintTask.BlueprintType.Machine && IsPressTask(task))
        {
            if (GetPressCount() >= Mathf.Max(0, step.requiredPressCount))
                RequestCompleteCurrentStep(step);
        }
        else if (step.trigger == StepTrigger.MinesConnectedToPresses)
        {
            if (type == BlueprintTask.BlueprintType.Belt
                || type == BlueprintTask.BlueprintType.Junction
                || (type == BlueprintTask.BlueprintType.Machine && IsPressTask(task)))
            {
                TryCompleteMinesConnected(step);
            }
        }
        else if (step.trigger == StepTrigger.MinesPowered)
        {
            TryCompleteMinesPowered(step);
        }
        UpdateGoalUi(step);
    }

    void HandleDroneCountChanged(int count)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.BuyDrones)
            EvaluateBuyDronesStep(step);
        UpdateGoalUi(step);
    }

    void HandleCrawlerCountChanged(int count)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.BuyDrones)
            EvaluateBuyDronesStep(step);
        UpdateGoalUi(step);
    }

    void HandlePowerChanged(float _)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.MinesPowered)
            TryCompleteMinesPowered(step);
        if (step.trigger == StepTrigger.PressesPowered)
            TryCompletePressesPowered(step);
        UpdateGoalUi(step);
    }

    void HandlePowerNetworkChanged()
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.MinesPowered)
            TryCompleteMinesPowered(step);
        if (step.trigger == StepTrigger.PressesPowered)
            TryCompletePressesPowered(step);
        UpdateGoalUi(step);
    }

    void HandleCameraDragged()
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null || step.trigger != StepTrigger.CameraMoveZoom) return;
        if (!cameraMoved)
        {
            cameraMoved = true;
            UpdateGoalUi(step);
        }
        TryCompleteCameraStep(step);
    }

    void HandleZoomedIn()
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null || step.trigger != StepTrigger.CameraMoveZoom) return;
        if (!zoomedIn)
        {
            zoomedIn = true;
            UpdateGoalUi(step);
        }
        TryCompleteCameraStep(step);
    }

    void HandleZoomedOut()
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null || step.trigger != StepTrigger.CameraMoveZoom) return;
        if (!zoomedOut)
        {
            zoomedOut = true;
            UpdateGoalUi(step);
        }
        TryCompleteCameraStep(step);
    }

    void HandleDialogueClicked()
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;

        if (oneOffDialogueActive)
        {
            oneOffDialogueActive = false;
            dialogueMode = DialogueMode.None;
            StartCoroutine(ResetDialogueAutoHideNextFrame());
            return;
        }

        if (dialogueMode == DialogueMode.Intro)
        {
            var intro = GetIntroMessages(step);
            if (intro.Count == 0) return;
            messageIndex++;
            if (messageIndex < intro.Count)
            {
                ShowIntroMessage(step, messageIndex);
            }
            else
            {
                dialogueMode = DialogueMode.None;
                HideUi();
                ApplyStepUi(step);
                UpdateGoalUi(step);
                if (step.trigger == StepTrigger.CameraMoveZoom)
                    TryCompleteCameraStep(step);
            }
            return;
        }

        if (dialogueMode == DialogueMode.Completion)
        {
            var completion = GetCompletionMessages(step);
            if (completion.Count == 0)
            {
                dialogueMode = DialogueMode.None;
                AdvanceToNextIncompleteStep();
                return;
            }

            completionIndex++;
            if (completionIndex < completion.Count)
            {
                ShowMessage(step.speaker, completion[completionIndex]);
            }
            else
            {
                dialogueMode = DialogueMode.None;
                HideUi();
                AdvanceToNextIncompleteStep();
            }
        }
    }

    void AdvanceToNextIncompleteStep()
    {
        int nextIndex = FindNextIncompleteIndex(currentStepIndex + 1);
        if (nextIndex >= 0) StartStep(nextIndex);
        else
        {
            var step = GetCurrentStep();
            if (step != null && step.hideUiAfterCompletion) HideUi();
            if (goalUi != null) goalUi.Hide();
            HidePlacementHighlight();
            ApplySolarStepPowerButtonVisibility(null);
            ApplyTutorialUiLocks();
        }
    }

    List<string> GetIntroMessages(Step step)
    {
        if (step == null) return new List<string>();
        return step.messages != null ? step.messages : new List<string>();
    }

    List<string> GetCompletionMessages(Step step)
    {
        if (step == null) return new List<string>();
        return step.completionMessages != null ? step.completionMessages : new List<string>();
    }

    void ShowIntroMessage(Step step, int index)
    {
        var intro = GetIntroMessages(step);
        if (index < 0 || index >= intro.Count)
        {
            HideUi();
            return;
        }
        ShowMessage(step.speaker, intro[index]);
    }

    void ShowOneOffMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        EnsureDialogue();
        if (dialogueUi == null) return;

        if (!oneOffDialogueActive)
            oneOffDialogueActive = true;

        dialogueUi.SetAutoHideOnClick(true);
        dialogueUi.ShowMessage(message);
    }

    IEnumerator ResetDialogueAutoHideNextFrame()
    {
        yield return null;
        if (dialogueUi != null)
            dialogueUi.SetAutoHideOnClick(false);
    }

    bool IsSolarPanelTask(BlueprintTask task)
    {
        if (task == null || task.BuildPrefab == null) return false;
        return task.BuildPrefab.GetComponent<SolarPanelMachine>() != null;
    }

    bool IsSugarMineTask(BlueprintTask task)
    {
        if (task == null || task.BuildPrefab == null) return false;
        return task.BuildPrefab.GetComponent<SugarMine>() != null;
    }

    bool IsPressTask(BlueprintTask task)
    {
        if (task == null || task.BuildPrefab == null) return false;
        return task.BuildPrefab.GetComponent<PressMachine>() != null;
    }

    bool HasBuiltSolarPanel()
    {
        var panels = FindObjectsByType<SolarPanelMachine>(FindObjectsSortMode.None);
        if (panels == null || panels.Length == 0) return false;
        for (int i = 0; i < panels.Length; i++)
        {
            var panel = panels[i];
            if (panel != null && !panel.isGhost) return true;
        }
        return false;
    }

    int GetMineCount()
    {
        var mines = FindObjectsByType<SugarMine>(FindObjectsSortMode.None);
        if (mines == null || mines.Length == 0) return 0;
        int count = 0;
        for (int i = 0; i < mines.Length; i++)
        {
            var mine = mines[i];
            if (mine != null && !mine.isGhost) count++;
        }
        return count;
    }

    int GetPressCount()
    {
        var presses = FindObjectsByType<PressMachine>(FindObjectsSortMode.None);
        if (presses == null || presses.Length == 0) return 0;
        int count = 0;
        for (int i = 0; i < presses.Length; i++)
        {
            var press = presses[i];
            if (press != null && !press.isGhost) count++;
        }
        return count;
    }

    List<SugarMine> GetCurrentMines()
    {
        var mines = FindObjectsByType<SugarMine>(FindObjectsSortMode.None);
        var list = new List<SugarMine>();
        if (mines == null || mines.Length == 0) return list;
        for (int i = 0; i < mines.Length; i++)
        {
            var mine = mines[i];
            if (mine != null && !mine.isGhost) list.Add(mine);
        }
        return list;
    }

    List<PressMachine> GetCurrentPresses()
    {
        var presses = FindObjectsByType<PressMachine>(FindObjectsSortMode.None);
        var list = new List<PressMachine>();
        if (presses == null || presses.Length == 0) return list;
        for (int i = 0; i < presses.Length; i++)
        {
            var press = presses[i];
            if (press != null && !press.isGhost) list.Add(press);
        }
        return list;
    }

    int GetDroneCount()
    {
        if (droneService == null && autoFindReferences)
            droneService = FindAnyObjectByType<DroneTaskService>();
        return droneService != null ? droneService.TotalDrones : 0;
    }

    int GetCrawlerCount()
    {
        if (droneService == null && autoFindReferences)
            droneService = FindAnyObjectByType<DroneTaskService>();
        return droneService != null ? droneService.TotalCrawlers : 0;
    }

    bool MeetsBuyCrewStep(Step step)
    {
        if (step == null) return false;
        int requiredDrones = Mathf.Max(0, step.requiredDroneCount);
        int requiredCrawlers = Mathf.Max(0, step.requiredCrawlerCount);
        return GetDroneCount() >= requiredDrones && GetCrawlerCount() >= requiredCrawlers;
    }

    bool IsBuyDronesStepSatisfied(Step step)
    {
        if (!MeetsBuyCrewStep(step)) return false;
        if (!requireMachineOverviewClosedForBuyStep) return true;
        return IsMachineOverviewClosed();
    }

    void EvaluateBuyDronesStep(Step step)
    {
        if (step == null || step.trigger != StepTrigger.BuyDrones) return;
        if (IsBuyDronesStepSatisfied(step))
            RequestCompleteCurrentStep(step);
        else
            CancelPendingStepCompletion();
    }

    bool IsMachineOverviewClosed()
    {
        if (machineInspectUi == null)
            machineInspectUi = FindAnyObjectByType<MachineInspectUI>();
        if (machineInspectUi == null) return true;
        return !machineInspectUi.IsPanelOpen;
    }

    void UpdateGoalUi(Step step)
    {
        if (goalUi == null || !isActive || dialogueMode != DialogueMode.None) return;
        var items = BuildChecklist(step);
        if (items == null || items.Count == 0)
            goalUi.Hide();
        else
            goalUi.ShowChecklist(items);
    }

    List<TutorialGoalUI.ChecklistItem> BuildChecklist(Step step)
    {
        var items = new List<TutorialGoalUI.ChecklistItem>();
        if (step == null) return items;

        switch (step.trigger)
        {
            case StepTrigger.CameraMoveZoom:
                if (step.requireCameraMove) items.Add(new TutorialGoalUI.ChecklistItem("Move the camera", cameraMoved));
                if (step.requireZoomIn) items.Add(new TutorialGoalUI.ChecklistItem("Zoom in", zoomedIn));
                if (step.requireZoomOut) items.Add(new TutorialGoalUI.ChecklistItem("Zoom out", zoomedOut));
                break;
            case StepTrigger.DroneHqBlueprintPlaced:
            case StepTrigger.DroneHqBuilt:
                items.Add(new TutorialGoalUI.ChecklistItem("Build the Drone HQ", DroneHQ.Instance != null));
                break;
            case StepTrigger.BuyDrones:
                {
                    int required = Mathf.Max(0, step.requiredDroneCount);
                    int current = GetDroneCount();
                    string label = FormatCountLabel("Buy", "drone", current, required);
                    bool dronesDone = required <= 0 || current >= required;
                    items.Add(new TutorialGoalUI.ChecklistItem(label, dronesDone));
                    int requiredCrawlers = Mathf.Max(0, step.requiredCrawlerCount);
                    int currentCrawlers = GetCrawlerCount();
                    bool crawlersDone = requiredCrawlers <= 0 || currentCrawlers >= requiredCrawlers;
                    if (requiredCrawlers > 0)
                    {
                        string crawlerLabel = FormatCountLabel("Buy", "crawler", currentCrawlers, requiredCrawlers);
                        items.Add(new TutorialGoalUI.ChecklistItem(crawlerLabel, crawlersDone));
                    }
                    if (requireMachineOverviewClosedForBuyStep)
                    {
                        bool crewDone = dronesDone && crawlersDone;
                        items.Add(new TutorialGoalUI.ChecklistItem("Close the overview panel", crewDone && IsMachineOverviewClosed()));
                    }
                }
                break;
            case StepTrigger.SolarPanelBuilt:
                items.Add(new TutorialGoalUI.ChecklistItem("Build a Solar Panel", HasBuiltSolarPanel()));
                break;
            case StepTrigger.SugarMineBuilt:
                {
                    int required = Mathf.Max(0, step.requiredMineCount);
                    int current = GetMineCount();
                    string label = FormatCountLabel("Build", "sugar mine", current, required);
                    items.Add(new TutorialGoalUI.ChecklistItem(label, required <= 0 || current >= required));
                }
                break;
            case StepTrigger.MinesPowered:
                {
                    GetMinePowerProgress(trackedMines, out var powered, out var total);
                    string label = total > 0
                        ? $"Power all current mines ({powered}/{total})"
                        : "Power all current mines";
                    items.Add(new TutorialGoalUI.ChecklistItem(label, total > 0 && powered >= total));
                }
                break;
            case StepTrigger.PressBuilt:
                {
                    int required = Mathf.Max(0, step.requiredPressCount);
                    int current = GetPressCount();
                    string label = FormatCountLabel("Build", "press", current, required);
                    items.Add(new TutorialGoalUI.ChecklistItem(label, required <= 0 || current >= required));
                }
                break;
            case StepTrigger.MinesConnectedToPresses:
                {
                    GetMinePressConnectionProgress(trackedMines, trackedPresses, out var connected, out var total);
                    string label = total > 0
                        ? $"Connect mines to presses ({connected}/{total})"
                        : "Connect mines to presses";
                    items.Add(new TutorialGoalUI.ChecklistItem(label, total > 0 && connected >= total));
                }
                break;
            case StepTrigger.PressesPowered:
                {
                    GetPressPowerProgress(trackedPresses, out var powered, out var total);
                    string label = total > 0
                        ? $"Power all current presses ({powered}/{total})"
                        : "Power all current presses";
                    items.Add(new TutorialGoalUI.ChecklistItem(label, total > 0 && powered >= total));
                }
                break;
        }

        return items;
    }

    static string FormatCountLabel(string verb, string singular, int current, int required)
    {
        int safe = Mathf.Max(0, required);
        string noun = safe == 1 ? singular : $"{singular}s";
        if (safe > 0)
        {
            int clamped = Mathf.Clamp(current, 0, safe);
            return $"{verb} {safe} {noun} ({clamped}/{safe})";
        }
        return $"{verb} {noun}";
    }

    void CaptureTrackedMines()
    {
        trackedMines.Clear();
        trackedMines.AddRange(GetCurrentMines());
    }

    void CaptureTrackedMinesAndPresses()
    {
        trackedMines.Clear();
        trackedPresses.Clear();
        trackedMines.AddRange(GetCurrentMines());
        trackedPresses.AddRange(GetCurrentPresses());
    }

    void CaptureTrackedPresses()
    {
        trackedPresses.Clear();
        trackedPresses.AddRange(GetCurrentPresses());
    }

    void TryCompleteMinesPowered(Step step)
    {
        if (step == null || step.trigger != StepTrigger.MinesPowered) return;
        if (trackedMines.Count == 0) return;
        if (AreAllMinesPowered(trackedMines, true))
            RequestCompleteCurrentStep(step);
    }

    void TryCompleteMinesConnected(Step step)
    {
        if (step == null || step.trigger != StepTrigger.MinesConnectedToPresses) return;
        if (trackedMines.Count == 0 || trackedPresses.Count == 0) return;
        if (AreAllMinesConnectedToPresses(trackedMines, trackedPresses, true))
            RequestCompleteCurrentStep(step);
    }

    void TryCompletePressesPowered(Step step)
    {
        if (step == null || step.trigger != StepTrigger.PressesPowered) return;
        if (trackedPresses.Count == 0) return;
        if (AreAllPressesPowered(trackedPresses, true))
            RequestCompleteCurrentStep(step);
    }

    void ResetCameraStepState(Step step)
    {
        cameraMoved = step == null || !step.requireCameraMove;
        zoomedIn = step == null || !step.requireZoomIn;
        zoomedOut = step == null || !step.requireZoomOut;
    }

    bool IsCameraStepComplete(Step step)
    {
        if (step == null) return false;
        if (step.requireCameraMove && !cameraMoved) return false;
        if (step.requireZoomIn && !zoomedIn) return false;
        if (step.requireZoomOut && !zoomedOut) return false;
        return step.requireCameraMove || step.requireZoomIn || step.requireZoomOut;
    }

    void TryCompleteCameraStep(Step step)
    {
        if (step == null || step.trigger != StepTrigger.CameraMoveZoom) return;
        if (dialogueMode != DialogueMode.None) return;
        if (IsCameraStepComplete(step))
            RequestCompleteCurrentStep(step);
        else
            CancelPendingStepCompletion();
    }

    bool AreAllMinesPowered(List<SugarMine> mines, bool requireAny)
    {
        GetMinePowerProgress(mines, out var powered, out var total);
        if (total == 0) return !requireAny;
        return powered >= total;
    }

    bool AreAllMinesConnectedToPresses(List<SugarMine> mines, List<PressMachine> presses, bool requireAny)
    {
        GetMinePressConnectionProgress(mines, presses, out var connected, out var total);
        if (total == 0) return !requireAny;
        return connected >= total;
    }

    bool AreAllPressesPowered(List<PressMachine> presses, bool requireAny)
    {
        GetPressPowerProgress(presses, out var powered, out var total);
        if (total == 0) return !requireAny;
        return powered >= total;
    }

    void GetMinePowerProgress(List<SugarMine> mines, out int powered, out int total)
    {
        powered = 0;
        total = 0;
        if (mines == null || mines.Count == 0) return;
        var grid = GridService.Instance;
        var power = PowerService.Instance;
        if (grid == null || power == null) return;
        for (int i = 0; i < mines.Count; i++)
        {
            var mine = mines[i];
            if (mine == null || mine.isGhost) continue;
            total++;
            var cell = grid.WorldToCell(mine.transform.position);
            if (power.IsCellPoweredOrAdjacent(cell))
                powered++;
        }
    }

    void GetPressPowerProgress(List<PressMachine> presses, out int powered, out int total)
    {
        powered = 0;
        total = 0;
        if (presses == null || presses.Count == 0) return;
        var grid = GridService.Instance;
        var power = PowerService.Instance;
        if (grid == null || power == null) return;
        for (int i = 0; i < presses.Count; i++)
        {
            var press = presses[i];
            if (press == null || press.isGhost) continue;
            total++;
            if (power.IsCellPoweredOrAdjacent(press.Cell))
                powered++;
        }
    }

    void GetMinePressConnectionProgress(List<SugarMine> mines, List<PressMachine> presses, out int connected, out int total)
    {
        connected = 0;
        total = 0;
        if (mines == null || presses == null || mines.Count == 0 || presses.Count == 0) return;
        var grid = GridService.Instance;
        if (grid == null) return;

        var pressByCell = BuildPressLookup(presses);
        if (pressByCell.Count == 0) return;

        int mineCount = 0;
        for (int i = 0; i < mines.Count; i++)
        {
            var mine = mines[i];
            if (mine == null || mine.isGhost) continue;
            mineCount++;
        }

        total = mineCount * pressByCell.Count;
        if (total == 0) return;

        for (int i = 0; i < mines.Count; i++)
        {
            var mine = mines[i];
            if (mine == null || mine.isGhost) continue;
            connected += CountReachablePresses(grid, mine, pressByCell);
        }
    }

    Dictionary<Vector2Int, PressMachine> BuildPressLookup(List<PressMachine> presses)
    {
        var lookup = new Dictionary<Vector2Int, PressMachine>();
        if (presses == null) return lookup;
        for (int i = 0; i < presses.Count; i++)
        {
            var press = presses[i];
            if (press == null || press.isGhost) continue;
            lookup[press.Cell] = press;
        }
        return lookup;
    }

    static Direction DirectionFromVecOrNone(Vector2Int dir)
    {
        if (dir == Vector2Int.up) return Direction.Up;
        if (dir == Vector2Int.right) return Direction.Right;
        if (dir == Vector2Int.down) return Direction.Down;
        if (dir == Vector2Int.left) return Direction.Left;
        return Direction.None;
    }

    static bool IsBeltLike(GridService.Cell c)
        => c != null && !c.isBlueprint && !c.isBroken
           && (c.type == GridService.CellType.Belt || c.type == GridService.CellType.Junction || c.hasConveyor || c.conveyor != null);

    int CountReachablePresses(GridService grid, SugarMine mine, Dictionary<Vector2Int, PressMachine> pressByCell)
    {
        if (grid == null || mine == null || pressByCell == null || pressByCell.Count == 0) return 0;
        var baseCell = grid.WorldToCell(mine.transform.position);
        var startCell = baseCell + DirectionUtil.DirVec(mine.outputDirection);
        var start = grid.GetCell(startCell);
        if (!IsBeltLike(start)) return 0;

        var visited = new HashSet<Vector2Int>();
        var queue = new Queue<Vector2Int>();
        visited.Add(startCell);
        queue.Enqueue(startCell);
        var reached = new HashSet<Vector2Int>();

        bool TryStep(Vector2Int cellPos, Direction dir)
        {
            if (!DirectionUtil.IsCardinal(dir)) return false;
            var nextPos = cellPos + DirectionUtil.DirVec(dir);
            if (!grid.InBounds(nextPos)) return false;

            if (pressByCell.TryGetValue(nextPos, out var press) && press != null && !press.isGhost)
            {
                var approachFromVec = cellPos - nextPos;
                if (approachFromVec == press.InputVec)
                {
                    reached.Add(nextPos);
                    if (reached.Count == pressByCell.Count) return true;
                }
            }

            var nextCell = grid.GetCell(nextPos);
            if (IsBeltLike(nextCell) && visited.Add(nextPos))
                queue.Enqueue(nextPos);
            return false;
        }

        while (queue.Count > 0)
        {
            var cellPos = queue.Dequeue();
            var cell = grid.GetCell(cellPos);
            if (cell == null) continue;

            if (cell.type == GridService.CellType.Belt)
            {
                if (TryStep(cellPos, cell.outA)) break;
            }
            else if (cell.type == GridService.CellType.Junction)
            {
                if (TryStep(cellPos, cell.outA)) break;
                if (TryStep(cellPos, cell.outB)) break;
            }
            else if (cell.conveyor != null)
            {
                var dir = DirectionFromVecOrNone(cell.conveyor.DirVec());
                if (TryStep(cellPos, dir)) break;
            }
        }

        return reached.Count;
    }


    void ShowMessage(string speaker, string message)
    {
        if (dialogueUi == null) return;
        dialogueUi.Show(speaker, message);
    }

    void ApplyTutorialUiLocks()
    {
        EnsureBuildControlObjects();
        bool visible = ShouldShowBuildControls();
        SetObjectVisible(buildButtonObject, visible);
        SetObjectVisible(deleteButtonObject, visible);
    }

    bool ShouldShowBuildControls()
    {
        if (!gateBuildControlsDuringTutorial) return true;
        if (!isActive) return true;
        var unlockStepId = string.IsNullOrWhiteSpace(unlockBuildControlsAfterStepId)
            ? DefaultBuildControlUnlockStepId
            : unlockBuildControlsAfterStepId;
        if (completedSteps.Contains(unlockStepId)) return true;

        var list = GetSteps();
        int unlockIndex = FindStepIndex(list, unlockStepId);
        if (unlockIndex < 0) return true;
        return currentStepIndex > unlockIndex;
    }

    void EnsureBuildControlObjects()
    {
        var buildName = string.IsNullOrWhiteSpace(buildButtonFallbackName) ? DefaultBuildButtonName : buildButtonFallbackName;
        var deleteName = string.IsNullOrWhiteSpace(deleteButtonFallbackName) ? DefaultDeleteButtonName : deleteButtonFallbackName;

        if (buildButtonObject == null)
            buildButtonObject = FindSceneObjectByName(buildName);
        if (deleteButtonObject == null)
            deleteButtonObject = FindSceneObjectByName(deleteName);
    }

    static GameObject FindSceneObjectByName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName)) return null;
        var transforms = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int i = 0; i < transforms.Length; i++)
        {
            var tr = transforms[i];
            if (tr == null) continue;
            if (string.Equals(tr.name, objectName, System.StringComparison.OrdinalIgnoreCase))
                return tr.gameObject;
        }
        return null;
    }

    static void SetObjectVisible(GameObject go, bool visible)
    {
        if (go == null) return;
        var animator = go.GetComponent<UIElementAnimator>();
        if (animator != null)
        {
            if (visible) animator.Show();
            else animator.Hide();
            return;
        }

        if (go.activeSelf != visible)
            go.SetActive(visible);
    }

    void HideUi()
    {
        if (dialogueUi != null)
        {
            dialogueUi.SetAutoHideOnClick(false);
            dialogueUi.Hide();
        }
        if (highlighter != null)
            highlighter.ClearTargets();
        if (oneOffDialogueActive)
        {
            oneOffDialogueActive = false;
        }
    }
}
