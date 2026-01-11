using TMPro;
using UnityEngine;

public class GlobalSucraDisplay : MonoBehaviour
{
    [SerializeField] TMP_Text sucraText;
    [SerializeField] string prefix = "Total Sucra: ";

    GameManager subscribedGameManager;

    void Awake()
    {
        if (sucraText == null)
            sucraText = GetComponent<TMP_Text>();
    }

    void OnEnable()
    {
        Subscribe();
        Refresh();
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void Subscribe()
    {
        var gm = GameManager.Instance;
        if (gm == null) return;

        subscribedGameManager = gm;
        subscribedGameManager.OnSucraChanged -= HandleSucraChanged;
        subscribedGameManager.OnSucraChanged += HandleSucraChanged;
    }

    void Unsubscribe()
    {
        if (subscribedGameManager == null) return;
        subscribedGameManager.OnSucraChanged -= HandleSucraChanged;
        subscribedGameManager = null;
    }

    void HandleSucraChanged(int value)
    {
        SetText(value);
    }

    void Refresh()
    {
        int value = GameManager.Instance != null ? GameManager.Instance.Sucra : 0;
        SetText(value);
    }

    void SetText(int value)
    {
        if (sucraText != null)
            sucraText.text = prefix + Mathf.Max(0, value);
    }
}
