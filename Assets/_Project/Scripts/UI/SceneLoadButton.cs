using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SceneLoadButton : MonoBehaviour
{
    [Tooltip("Scene name to load (must be in Build Settings).")]
    [SerializeField] string sceneName = "Level00";

    Button cachedButton;

    void OnEnable()
    {
        // Convenience: if this script is placed on a UI Button, auto-wire it.
        // We only do this when there are no persistent OnClick calls, to avoid
        // double-loading on buttons already wired in the Inspector.
        if (cachedButton == null)
            cachedButton = GetComponent<Button>();

        if (cachedButton == null)
            return;

        if (cachedButton.onClick != null && cachedButton.onClick.GetPersistentEventCount() > 0)
            return;

        cachedButton.onClick.RemoveListener(LoadScene);
        cachedButton.onClick.AddListener(LoadScene);
    }

    void OnDisable()
    {
        if (cachedButton != null)
            cachedButton.onClick.RemoveListener(LoadScene);
    }

    public void LoadScene()
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            Debug.LogWarning("[SceneLoadButton] Scene name is empty.");
            return;
        }
        SceneManager.LoadScene(sceneName);
    }
}
