using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class MoneyDisplay : MonoBehaviour
{
    [SerializeField] TMP_Text moneyText;
    [SerializeField] string prefix = "";
    [SerializeField] string suffix = "";

    GameManager gameManager;
    bool warnedMissingText;

    void Awake()
    {
        if (moneyText == null)
            moneyText = GetComponent<TMP_Text>();
    }

    void OnEnable()
    {
        HookGameManager();
    }

    void Start()
    {
        // Retry after all Awakes have run to ensure GameManager.Instance exists.
        if (gameManager == null)
            HookGameManager();
    }

    void OnDisable()
    {
        UnhookGameManager();
    }

    void HookGameManager()
    {
        if (gameManager != null) return;
        gameManager = GameManager.Instance;
        if (gameManager == null) return;

        gameManager.OnMoneyChanged += HandleMoneyChanged;
        HandleMoneyChanged(gameManager.Money);
    }

    void UnhookGameManager()
    {
        if (gameManager == null) return;
        gameManager.OnMoneyChanged -= HandleMoneyChanged;
        gameManager = null;
    }

    void HandleMoneyChanged(int amount)
    {
        if (moneyText == null)
        {
            if (!warnedMissingText)
            {
                warnedMissingText = true;
                Debug.LogWarning("[MoneyDisplay] No TMP_Text assigned.", this);
            }
            return;
        }

        moneyText.text = $"{prefix}{amount}{suffix}";
    }
}
