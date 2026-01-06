using System;
using UnityEngine;

public enum GameState { Play, Build, Delete }

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    public GameState State { get; private set; } = GameState.Play;

    [Header("Economy")]
    [SerializeField, Min(0)] int money = 0;

    public event Action<int> OnMoneyChanged;
    public int Money => money;

    // NOTE: Auto-spawn removed. Place a GameManager in each scene that needs it.

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
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

    public void SetMoney(int amount)
    {
        int next = Mathf.Max(0, amount);
        if (money == next) return;
        money = next;
        OnMoneyChanged?.Invoke(money);
    }

    public void AddMoney(int amount)
    {
        if (amount == 0) return;
        SetMoney(money + amount);
    }

    public bool TrySpendMoney(int amount)
    {
        if (amount <= 0) return true;
        if (money < amount) return false;
        SetMoney(money - amount);
        return true;
    }

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
