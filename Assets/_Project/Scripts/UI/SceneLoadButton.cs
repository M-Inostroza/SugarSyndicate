using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneLoadButton : MonoBehaviour
{
    [Tooltip("Scene name to load (must be in Build Settings).")]
    [SerializeField] string sceneName = "Level00";

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
