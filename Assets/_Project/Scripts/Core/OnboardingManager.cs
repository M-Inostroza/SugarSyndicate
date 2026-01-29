using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DisallowMultipleComponent]
public class OnboardingManager : MonoBehaviour
{
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

    int currentStepIndex = -1;
    readonly HashSet<string> completedSteps = new HashSet<string>();
    bool isActive;
    int messageIndex;
    int completionIndex;
    DialogueMode dialogueMode = DialogueMode.None;
    readonly List<SugarMine> trackedMines = new List<SugarMine>();
    readonly List<PressMachine> trackedPresses = new List<PressMachine>();
    bool cameraMoved;
    bool zoomedIn;
    bool zoomedOut;

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
                    "Drop it anywhere on the grid so the crew has a home base."
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
                allowedCategories = new List<string> { "Essential", "Extraction" }
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
                allowedCategories = new List<string> { "Essential", "Power" }
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
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
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
        currentStepIndex = -1;
        dialogueMode = DialogueMode.None;
        trackedMines.Clear();
        cameraMoved = false;
        zoomedIn = false;
        zoomedOut = false;
        if (goalUi != null) goalUi.Hide();
        HideUi();
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

    void AdvanceToFirstIncompleteStep()
    {
        var list = GetSteps();
        if (!isActive || list == null || list.Count == 0)
        {
            HideUi();
            if (goalUi != null) goalUi.Hide();
            return;
        }

        int nextIndex = FindNextIncompleteIndex(0);
        if (nextIndex < 0)
        {
            HideUi();
            if (goalUi != null) goalUi.Hide();
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
                return MeetsBuyCrewStep(step);
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
    }

    void CompleteCurrentStep()
    {
        var step = GetCurrentStep();
        if (step == null) return;

        if (!string.IsNullOrWhiteSpace(step.id))
            completedSteps.Add(step.id);

        if (highlighter != null)
            highlighter.ClearTargets();
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

    void ApplyStepUi(Step step)
    {
        if (step == null) return;

        EnsureHighlighter();
        if (highlighter != null)
        {
            if (step.highlightTargets != null && step.highlightTargets.Count > 0)
                highlighter.SetTargets(step.highlightTargets);
            else
                highlighter.ClearTargets();
        }

        if (buildMenu == null)
            buildMenu = FindAnyObjectByType<BuildMenuController>();
        if (buildMenu != null)
        {
            if (step.allowedCategories != null && step.allowedCategories.Count > 0)
                buildMenu.SetAllowedCategories(step.allowedCategories, step.hideDisallowedCategories, step.disableDisallowedCategories);
            if (!string.IsNullOrWhiteSpace(step.openCategory))
                buildMenu.ShowCategory(step.openCategory);
        }

        if (step.enableSelectables != null)
        {
            for (int i = 0; i < step.enableSelectables.Count; i++)
            {
                var sel = step.enableSelectables[i];
                if (sel != null) sel.interactable = true;
            }
        }
        if (step.disableSelectables != null)
        {
            for (int i = 0; i < step.disableSelectables.Count; i++)
            {
                var sel = step.disableSelectables[i];
                if (sel != null) sel.interactable = false;
            }
        }
        if (step.showObjects != null)
        {
            for (int i = 0; i < step.showObjects.Count; i++)
            {
                var go = step.showObjects[i];
                if (go != null) go.SetActive(true);
            }
        }
        if (step.hideObjects != null)
        {
            for (int i = 0; i < step.hideObjects.Count; i++)
            {
                var go = step.hideObjects[i];
                if (go != null) go.SetActive(false);
            }
        }
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
            CompleteCurrentStep();
    }

    void HandleBlueprintCompleted(BlueprintTask.BlueprintType type, BlueprintTask task)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.DroneHqBuilt && type == BlueprintTask.BlueprintType.DroneHQ)
            CompleteCurrentStep();
        else if (step.trigger == StepTrigger.SolarPanelBuilt && type == BlueprintTask.BlueprintType.Machine && IsSolarPanelTask(task))
            CompleteCurrentStep();
        else if (step.trigger == StepTrigger.SugarMineBuilt && type == BlueprintTask.BlueprintType.Machine && IsSugarMineTask(task))
        {
            if (GetMineCount() >= Mathf.Max(0, step.requiredMineCount))
                CompleteCurrentStep();
        }
        else if (step.trigger == StepTrigger.PressBuilt && type == BlueprintTask.BlueprintType.Machine && IsPressTask(task))
        {
            if (GetPressCount() >= Mathf.Max(0, step.requiredPressCount))
                CompleteCurrentStep();
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
        if (step.trigger == StepTrigger.BuyDrones && MeetsBuyCrewStep(step))
            CompleteCurrentStep();
        UpdateGoalUi(step);
    }

    void HandleCrawlerCountChanged(int count)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.BuyDrones && MeetsBuyCrewStep(step))
            CompleteCurrentStep();
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
                    items.Add(new TutorialGoalUI.ChecklistItem(label, required <= 0 || current >= required));
                    int requiredCrawlers = Mathf.Max(0, step.requiredCrawlerCount);
                    int currentCrawlers = GetCrawlerCount();
                    if (requiredCrawlers > 0)
                    {
                        string crawlerLabel = FormatCountLabel("Buy", "crawler", currentCrawlers, requiredCrawlers);
                        items.Add(new TutorialGoalUI.ChecklistItem(crawlerLabel, currentCrawlers >= requiredCrawlers));
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
            CompleteCurrentStep();
    }

    void TryCompleteMinesConnected(Step step)
    {
        if (step == null || step.trigger != StepTrigger.MinesConnectedToPresses) return;
        if (trackedMines.Count == 0 || trackedPresses.Count == 0) return;
        if (AreAllMinesConnectedToPresses(trackedMines, trackedPresses, true))
            CompleteCurrentStep();
    }

    void TryCompletePressesPowered(Step step)
    {
        if (step == null || step.trigger != StepTrigger.PressesPowered) return;
        if (trackedPresses.Count == 0) return;
        if (AreAllPressesPowered(trackedPresses, true))
            CompleteCurrentStep();
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
            CompleteCurrentStep();
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

    void HideUi()
    {
        if (dialogueUi != null)
            dialogueUi.Hide();
        if (highlighter != null)
            highlighter.ClearTargets();
    }
}
