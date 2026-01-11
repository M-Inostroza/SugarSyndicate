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

        LoadSucra();
        TryApplyTutorialStartMoney(SceneManager.GetActiveScene());
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
        try
        {
            sucra = Mathf.Max(0, PlayerPrefs.GetInt(sucraPlayerPrefsKey, sucra));
        }
        catch { }
    }

    void SaveSucra()
    {
        if (!persistSucra) return;
        try
        {
            PlayerPrefs.SetInt(sucraPlayerPrefsKey, sucra);
            PlayerPrefs.Save();
        }
        catch { }
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
