using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

#if UNITY_EDITOR
using UnityEditor;
#endif

public enum GameState { Play, Build, Delete }

public class GameManager : MonoBehaviour
{
    const string SaveResetVersionKey = "Save.ResetVersion";
    const int SaveResetVersion = 1;

    public static GameManager Instance { get; private set; }

    public GameState State { get; private set; } = GameState.Play;

    [Header("Sweet Credits (Level Budget)")]
    [FormerlySerializedAs("money")]
    [SerializeField, Min(0)] int sweetCredits = 0;

    [FormerlySerializedAs("tutorialStartingMoney")]
    [SerializeField, Min(0)] int tutorialStartingSweetCredits = 1500;

    [FormerlySerializedAs("applyTutorialStartMoney")]
    [SerializeField] bool applyTutorialStartSweetCredits = true;

    public event Action<int> OnSweetCreditsChanged;
    public int SweetCredits => sweetCredits;

    [Header("Sucra (Global)")]
    [Tooltip("Global currency that persists across levels/sessions.")]
    [SerializeField, Min(0)] int sucra = 0;
    [SerializeField] bool persistSucra = true;
    [SerializeField] string sucraPlayerPrefsKey = "Currency.Sucra";

    public event Action<int> OnSucraChanged;
    public int Sucra => sucra;

    // Back-compat: existing code treats Money as the per-level budget.
    public event Action<int> OnMoneyChanged;
    public int Money => sweetCredits;

    // NOTE: Auto-spawn removed. Place a GameManager in each scene that needs it.

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        ResetAllMoneyOnce();
        LoadSucra();
        TryApplyTutorialStartMoney(SceneManager.GetActiveScene());
    }

    void ResetAllMoneyOnce()
    {
        // User-requested: start fresh after switching save systems.
        // This runs once and records the version in ES3.
        try
        {
            int applied = 0;
            try { applied = ES3.Load<int>(SaveResetVersionKey, 0); } catch { applied = 0; }
            if (applied == SaveResetVersion) return;

            // Clear global Sucra from ES3 and legacy PlayerPrefs.
            try { ES3.DeleteKey(sucraPlayerPrefsKey); } catch { }
            try { PlayerPrefs.DeleteKey(sucraPlayerPrefsKey); } catch { }

            // Reset runtime values.
            try { SetSweetCredits(0); } catch { }
            try { SetSucra(0); } catch { }

            // Mark reset complete.
            try { ES3.Save<int>(SaveResetVersionKey, SaveResetVersion); } catch { }
        }
        catch { }
    }

    void Update()
    {
        // Global quit shortcut. If a build tool is active, Esc should remain "cancel tool".
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (BuildModeController.HasActiveTool)
                return;
            QuitGame();
        }
    }

    public void QuitGame()
    {
#if UNITY_EDITOR
        EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Menu scenes should never inherit Build/Delete state from a previous level.
        // Otherwise persistent overlays/tools (DontDestroyOnLoad) can bleed into menus.
        bool isLevelSelection = string.Equals(scene.name, "Level selection", StringComparison.OrdinalIgnoreCase);
        bool isPlayableLevel = scene.name.StartsWith("Level", StringComparison.OrdinalIgnoreCase) && !isLevelSelection;
        if (isLevelSelection)
        {
            try { SetState(GameState.Play); } catch { }
            try { if (TimeManager.Instance != null) TimeManager.Instance.SetRunning(false); } catch { }
            try { BuildModeController.SetToolActive(false); } catch { }
            try { BuildSelectionNotifier.Notify(null); } catch { }
        }
        else if (isPlayableLevel)
        {
            try { if (TimeManager.Instance != null) TimeManager.Instance.SetRunning(true); } catch { }
        }

        TryApplyTutorialStartMoney(scene);
    }

    void TryApplyTutorialStartMoney(Scene scene)
    {
        if (!applyTutorialStartSweetCredits) return;
        if (scene.buildIndex != 0) return;
        SetSweetCredits(tutorialStartingSweetCredits);
    }

    public void SetState(GameState s)
    {
        // Avoid duplicate work/log when setting the same state again
        if (State == s) return;
        State = s;
        Debug.Log($"GameManager: state -> {s}");
    }

    public void ToggleState()
    {
        SetState(State == GameState.Play ? GameState.Build : GameState.Play);
    }

    public void SetSweetCredits(int amount)
    {
        int next = Mathf.Max(0, amount);
        if (sweetCredits == next) return;
        sweetCredits = next;
        OnSweetCreditsChanged?.Invoke(sweetCredits);
        OnMoneyChanged?.Invoke(sweetCredits);
    }

    public void AddSweetCredits(int amount)
    {
        if (amount == 0) return;
        SetSweetCredits(sweetCredits + amount);
    }

    public bool TrySpendSweetCredits(int amount)
    {
        if (amount <= 0) return true;
        if (sweetCredits < amount) return false;
        SetSweetCredits(sweetCredits - amount);
        return true;
    }

    public void SetSucra(int amount)
    {
        int next = Mathf.Max(0, amount);
        if (sucra == next) return;
        sucra = next;
        OnSucraChanged?.Invoke(sucra);
        SaveSucra();
    }

    public void AddSucra(int amount)
    {
        if (amount == 0) return;
        SetSucra(sucra + amount);
    }

    public bool TrySpendSucra(int amount)
    {
        if (amount <= 0) return true;
        if (sucra < amount) return false;
        SetSucra(sucra - amount);
        return true;
    }

    void LoadSucra()
    {
        if (!persistSucra) return;

        // Main save system: Easy Save 3.
        try
        {
            if (ES3.KeyExists(sucraPlayerPrefsKey))
                sucra = Mathf.Max(0, ES3.Load<int>(sucraPlayerPrefsKey));
        }
        catch { }
    }

    void SaveSucra()
    {
        if (!persistSucra) return;

        // Main save system: Easy Save 3.
        try { ES3.Save<int>(sucraPlayerPrefsKey, sucra); } catch { }
    }

    // Back-compat wrappers (existing scripts use these)
    public void SetMoney(int amount) => SetSweetCredits(amount);
    public void AddMoney(int amount) => AddSweetCredits(amount);
    public bool TrySpendMoney(int amount) => TrySpendSweetCredits(amount);

    // Enter build mode and start default build controller preview if available
    public void EnterBuildMode()
    {
        // Always enter Build globally (SetState early-outs if already Build)
        SetState(GameState.Build);

        // Let the player choose what to build; no default tool is auto-selected here.
    }

    // Public helper for a global UI button to exit Build/Delete and return to Play
    public void ExitBuildMode()
    {
        if (State == GameState.Play) return;
        TryCancelActiveBuildMode();
        if (State != GameState.Play)
            SetState(GameState.Play);
    }

    void TryCancelActiveBuildMode()
    {
        try
        {
            var bmc = FindFirstObjectByType<BuildModeController>();
            if (bmc != null)
            {
                bmc.CancelBuildMode();
            }
        }
        catch { }
    }
}
