using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(PressMachine))]
public class PressMachineVisuals : MonoBehaviour
{
    const string UpperLeftPressName = "Press up left";
    const string UpperRightPressName = "Press up right";
    const string LowerLeftPressName = "Press down left";
    const string LowerRightPressName = "Press down right";

    [Header("References")]
    [SerializeField] PressMachine pressMachine;
    [SerializeField] Transform upperLeftPress;
    [SerializeField] Transform upperRightPress;
    [SerializeField] Transform lowerLeftPress;
    [SerializeField] Transform lowerRightPress;

    [Header("Press X Positions")]
    [SerializeField] float upperLeftIdleX = -1.1f;
    [SerializeField] float upperLeftPressedX = -0.76f;
    [SerializeField] float upperRightIdleX = 0.14f;
    [SerializeField] float upperRightPressedX = -0.24f;
    [SerializeField] float lowerLeftIdleX = -1.1f;
    [SerializeField] float lowerLeftPressedX = -0.76f;
    [SerializeField] float lowerRightIdleX = 0.14f;
    [SerializeField] float lowerRightPressedX = -0.24f;

    [Header("Timing")]
    [SerializeField, Min(1)] int pressCycles = 2;

    Transform[] presses;
    Vector3[] idlePressPositions;
    Vector3[] pressedPressPositions;

    void Reset()
    {
        AutoAssignReferences();
        CacheVisualData();
        ApplyIdleVisualState();
    }

    void Awake()
    {
        AutoAssignReferences();
        CacheVisualData();
        ApplyIdleVisualState();
    }

    void OnEnable()
    {
        AutoAssignReferences();
        CacheVisualData();
        ApplyIdleVisualState();
    }

    void OnValidate()
    {
        AutoAssignReferences();
        CacheVisualData();
        if (!Application.isPlaying)
            ApplyIdleVisualState();
    }

    void Update()
    {
        if (!Application.isPlaying || pressMachine == null)
            return;

        if (pressMachine.IsProcessing || pressMachine.IsJammed)
            RenderProcessingVisual(Mathf.Clamp01(pressMachine.Progress01));
        else
            ApplyIdleVisualState();
    }

    void RenderProcessingVisual(float progress01)
    {
        float cycleCount = Mathf.Max(1, pressCycles);
        float cycle = Mathf.PingPong(progress01 * cycleCount * 2f, 1f);
        float stroke = Mathf.SmoothStep(0f, 1f, cycle);
        ApplyPressStroke(stroke);
    }

    void ApplyIdleVisualState()
    {
        ApplyPressStroke(0f);
    }

    void ApplyPressStroke(float stroke01)
    {
        if (presses == null || idlePressPositions == null || pressedPressPositions == null)
            return;

        float stroke = Mathf.Clamp01(stroke01);
        for (int i = 0; i < presses.Length; i++)
        {
            if (presses[i] == null)
                continue;

            presses[i].localPosition = Vector3.LerpUnclamped(idlePressPositions[i], pressedPressPositions[i], stroke);
        }
    }

    void AutoAssignReferences()
    {
        if (pressMachine == null)
            pressMachine = GetComponent<PressMachine>();

        if (upperLeftPress == null)
            upperLeftPress = FindNamedChild(UpperLeftPressName);
        if (upperRightPress == null)
            upperRightPress = FindNamedChild(UpperRightPressName);
        if (lowerLeftPress == null)
            lowerLeftPress = FindNamedChild(LowerLeftPressName);
        if (lowerRightPress == null)
            lowerRightPress = FindNamedChild(LowerRightPressName);
    }

    void CacheVisualData()
    {
        presses = new[]
        {
            upperLeftPress,
            upperRightPress,
            lowerLeftPress,
            lowerRightPress
        };

        idlePressPositions = new Vector3[presses.Length];
        pressedPressPositions = new Vector3[presses.Length];

        CachePressPose(0, upperLeftPress, upperLeftIdleX, upperLeftPressedX);
        CachePressPose(1, upperRightPress, upperRightIdleX, upperRightPressedX);
        CachePressPose(2, lowerLeftPress, lowerLeftIdleX, lowerLeftPressedX);
        CachePressPose(3, lowerRightPress, lowerRightIdleX, lowerRightPressedX);
    }

    void CachePressPose(int index, Transform target, float idleX, float pressedX)
    {
        if (target == null || index < 0 || index >= idlePressPositions.Length)
            return;

        idlePressPositions[index] = target.localPosition;
        idlePressPositions[index].x = idleX;

        pressedPressPositions[index] = target.localPosition;
        pressedPressPositions[index].x = pressedX;
    }

    Transform FindNamedChild(string childName)
    {
        if (string.IsNullOrWhiteSpace(childName))
            return null;

        var all = GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            var candidate = all[i];
            if (candidate == null || candidate == transform)
                continue;
            if (candidate.name == childName)
                return candidate;
        }

        return null;
    }
}
