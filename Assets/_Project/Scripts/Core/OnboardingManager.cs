using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class OnboardingManager : MonoBehaviour
{
    public enum StepTrigger
    {
        None,
        DroneHqBlueprintPlaced,
        DroneHqBuilt,
        SolarPanelBuilt,
        BuyDrones
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

    [Header("Steps")]
    [SerializeField] List<Step> steps = new List<Step>();

    int currentStepIndex = -1;
    readonly HashSet<string> completedSteps = new HashSet<string>();
    bool isActive;
    int messageIndex;
    int completionIndex;
    DialogueMode dialogueMode = DialogueMode.None;

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
    }

    void Reset()
    {
        FillDefaultSteps();
    }

    [ContextMenu("Fill Default Steps")]
    void FillDefaultSteps()
    {
        steps = new List<Step>
        {
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
                    "Select the Drone HQ and buy three drones."
                },
                completionMessages = new List<string>
                {
                    "Perfect. That should keep the place running."
                },
                trigger = StepTrigger.BuyDrones,
                requiredDroneCount = 3,
                allowedCategories = new List<string> { "Essential" }
            }
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
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        BlueprintTask.BlueprintPlaced -= HandleBlueprintPlaced;
        BlueprintTask.BlueprintCompleted -= HandleBlueprintCompleted;
        SubscribeDialogue(false);
        SubscribeDroneService(false);
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
        }
        else
        {
            droneService.OnDroneCountChanged -= HandleDroneCountChanged;
        }
    }

    void EnsureHighlighter()
    {
        if (highlighter == null && autoFindReferences)
            highlighter = FindAnyObjectByType<TutorialHighlighter>();
    }

    void AdvanceToFirstIncompleteStep()
    {
        if (!isActive || steps == null || steps.Count == 0)
        {
            HideUi();
            return;
        }

        int nextIndex = FindNextIncompleteIndex(0);
        if (nextIndex < 0)
        {
            HideUi();
            return;
        }

        StartStep(nextIndex);
    }

    int FindNextIncompleteIndex(int startIndex)
    {
        for (int i = Mathf.Max(0, startIndex); i < steps.Count; i++)
        {
            var step = steps[i];
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
            case StepTrigger.BuyDrones:
                return GetDroneCount() >= Mathf.Max(0, step.requiredDroneCount);
            case StepTrigger.None:
                return false;
        }
        return false;
    }

    void StartStep(int index)
    {
        if (index < 0 || steps == null || index >= steps.Count) return;
        currentStepIndex = index;
        var step = steps[index];
        messageIndex = 0;
        completionIndex = 0;
        dialogueMode = DialogueMode.Intro;
        ShowIntroMessage(step, messageIndex);
        ApplyStepUi(step);
    }

    void CompleteCurrentStep()
    {
        var step = GetCurrentStep();
        if (step == null) return;

        if (!string.IsNullOrWhiteSpace(step.id))
            completedSteps.Add(step.id);

        if (highlighter != null)
            highlighter.ClearTargets();
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
        if (currentStepIndex < 0 || steps == null || currentStepIndex >= steps.Count)
            return null;
        return steps[currentStepIndex];
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
    }

    void HandleDroneCountChanged(int count)
    {
        if (!isActive) return;
        var step = GetCurrentStep();
        if (step == null) return;
        if (step.trigger == StepTrigger.BuyDrones && count >= Mathf.Max(0, step.requiredDroneCount))
            CompleteCurrentStep();
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

    int GetDroneCount()
    {
        if (droneService == null && autoFindReferences)
            droneService = FindAnyObjectByType<DroneTaskService>();
        return droneService != null ? droneService.TotalDrones : 0;
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
