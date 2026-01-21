using TMPro;
using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public class PowerDisplay : MonoBehaviour
{
    [SerializeField] TMP_Text powerText;
    [SerializeField] Text legacyText;
    [SerializeField] string prefix = "Power: ";
    [SerializeField] string suffix = "";
    [SerializeField] bool showBreakdown = true;

    PowerService powerService;
    bool warnedMissingText;

    void Awake()
    {
        if (powerText == null)
            powerText = GetComponent<TMP_Text>();
        if (legacyText == null)
            legacyText = GetComponent<Text>();
    }

    void OnEnable()
    {
        HookPowerService();
    }

    void Start()
    {
        if (powerService == null)
            HookPowerService();
    }

    void OnDisable()
    {
        UnhookPowerService();
    }

    void HookPowerService()
    {
        if (powerService != null) return;
        powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        if (powerService == null) return;

        powerService.OnPowerChanged -= HandlePowerChanged;
        powerService.OnPowerChanged += HandlePowerChanged;
        HandlePowerChanged(powerService.TotalWatts);
    }

    void UnhookPowerService()
    {
        if (powerService == null) return;
        powerService.OnPowerChanged -= HandlePowerChanged;
        powerService = null;
    }

    void HandlePowerChanged(float watts)
    {
        if (powerText == null && legacyText == null)
        {
            if (!warnedMissingText)
            {
                warnedMissingText = true;
                Debug.LogWarning("[PowerDisplay] No text component assigned.", this);
            }
            return;
        }
        string netText = PowerService.FormatPower(watts);
        string text = $"{prefix}{netText}{suffix}";
        if (showBreakdown && powerService != null)
        {
            string genText = PowerService.FormatPower(powerService.TotalGeneratedWatts);
            string useText = PowerService.FormatPower(powerService.TotalConsumedWatts);
            text = $"{text} (Gen {genText} / Use {useText})";
        }
        if (powerText != null) powerText.text = text;
        if (legacyText != null) legacyText.text = text;
    }
}
