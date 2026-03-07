using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(PressMachine))]
public class PressMachineVisuals : MonoBehaviour
{
    const string UpperGroupName = "Press Up";
    const string LowerGroupName = "Press Down";
    const string UpperLeftPressName = "Press up left";
    const string UpperRightPressName = "Press up right";
    const string LowerLeftPressName = "Press down left";
    const string LowerRightPressName = "Press down right";
    const string SugarName = "Sugar";
    const string CubeName = "Cube";

    enum FillSide
    {
        Upper,
        Lower
    }

    [Header("References")]
    [SerializeField] PressMachine pressMachine;
    [SerializeField] Transform upperGroup;
    [SerializeField] Transform lowerGroup;
    [SerializeField] Transform upperLeftPress;
    [SerializeField] Transform upperRightPress;
    [SerializeField] Transform lowerLeftPress;
    [SerializeField] Transform lowerRightPress;
    [SerializeField] Transform sugarTransform;
    [SerializeField] Transform cubeTransform;

    [Header("Press X Positions")]
    [SerializeField] float upperLeftIdleX = -1.1f;
    [SerializeField] float upperLeftPressedX = -0.76f;
    [SerializeField] float upperRightIdleX = 0.14f;
    [SerializeField] float upperRightPressedX = -0.24f;
    [SerializeField] float lowerLeftIdleX = -1.1f;
    [SerializeField] float lowerLeftPressedX = -0.76f;
    [SerializeField] float lowerRightIdleX = 0.14f;
    [SerializeField] float lowerRightPressedX = -0.24f;

    [Header("Material Motion")]
    [SerializeField, Min(0f)] float materialInsetFromPress = 0.42f;
    [SerializeField, Min(0.01f)] float inputMoveDuration = 0.16f;
    [SerializeField] AnimationCurve inputMoveCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [SerializeField] AnimationCurve cubeMoveCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Process Timing")]
    [SerializeField, Range(0.05f, 0.85f)] float pressPhasePortion = 0.58f;
    [SerializeField, Range(0.05f, 0.5f)] float cubeTravelPortion = 0.24f;
    [SerializeField, Min(1)] int pressCycles = 2;
    [SerializeField] AnimationCurve pressStrokeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    Transform[] presses;
    Vector3[] idlePressPositions;
    Vector3[] pressedPressPositions;
    SpriteRenderer sugarRenderer;
    SpriteRenderer cubeRenderer;
    Vector3 sugarCenterLocalPos;
    Vector3 cubeCenterLocalPos;
    Vector3 upperMaterialLocalPos;
    Vector3 lowerMaterialLocalPos;
    FillSide bufferedFillSide = FillSide.Upper;
    FillSide cycleFillSide = FillSide.Upper;
    bool hasActiveCycleVisual;
    bool subscribed;
    Coroutine inputArrivalRoutine;

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
        Subscribe();
        if (Application.isPlaying)
            ApplyIdleVisualState();
    }

    void OnDisable()
    {
        Unsubscribe();
        StopInputArrival();
        if (Application.isPlaying)
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

        if (pressMachine.IsProcessing || pressMachine.IsJammed || hasActiveCycleVisual)
        {
            StopInputArrival();
            RenderProcessingVisual(Mathf.Clamp01(pressMachine.Progress01));
        }
    }

    void HandleInputBuffered(int slotIndex)
    {
        if (!Application.isPlaying || pressMachine == null)
            return;

        bufferedFillSide = ResolveFillSide(slotIndex);

        if (pressMachine.IsProcessing || pressMachine.IsJammed || hasActiveCycleVisual)
            return;

        StopInputArrival();
        inputArrivalRoutine = StartCoroutine(PlayInputArrival(bufferedFillSide));
    }

    void HandleProcessingStarted(float _)
    {
        if (!Application.isPlaying)
            return;

        hasActiveCycleVisual = true;
        cycleFillSide = bufferedFillSide;
        StopInputArrival();
        RenderProcessingVisual(0f);
    }

    void HandleOutputProduced()
    {
        if (!Application.isPlaying)
            return;

        hasActiveCycleVisual = false;
        ApplyIdleVisualState();
    }

    IEnumerator PlayInputArrival(FillSide side)
    {
        if (sugarTransform == null)
        {
            inputArrivalRoutine = null;
            yield break;
        }

        SetSugarVisible(true);
        SetCubeVisible(false);
        ApplyPressStroke(0f);

        Vector3 target = GetMaterialTarget(side);
        sugarTransform.localPosition = sugarCenterLocalPos;

        float elapsed = 0f;
        while (elapsed < inputMoveDuration)
        {
            elapsed += Time.deltaTime;
            float t = inputMoveDuration <= 0f ? 1f : Mathf.Clamp01(elapsed / inputMoveDuration);
            sugarTransform.localPosition = Vector3.LerpUnclamped(sugarCenterLocalPos, target, EvaluateCurve(inputMoveCurve, t));
            yield return null;
        }

        sugarTransform.localPosition = target;
        inputArrivalRoutine = null;
    }

    void RenderProcessingVisual(float progress01)
    {
        if (sugarTransform == null || cubeTransform == null)
            return;

        float pressCutoff = Mathf.Clamp01(pressPhasePortion);
        float cubeCutoff = Mathf.Clamp01(pressPhasePortion + cubeTravelPortion);
        Vector3 materialTarget = GetMaterialTarget(cycleFillSide);

        if (progress01 < pressCutoff)
        {
            float normalized = SafeNormalize(progress01, pressCutoff);
            float stroke = Mathf.PingPong(normalized * Mathf.Max(1, pressCycles) * 2f, 1f);
            ApplyPressStroke(EvaluateCurve(pressStrokeCurve, stroke));
            SetSugarVisible(true);
            SetCubeVisible(false);
            sugarTransform.localPosition = materialTarget;
            return;
        }

        ApplyPressStroke(0f);

        if (progress01 < cubeCutoff)
        {
            float normalized = SafeNormalize(progress01 - pressCutoff, Mathf.Max(0.0001f, cubeCutoff - pressCutoff));
            SetSugarVisible(false);
            SetCubeVisible(true);
            cubeTransform.localPosition = Vector3.LerpUnclamped(materialTarget, cubeCenterLocalPos, EvaluateCurve(cubeMoveCurve, normalized));
            return;
        }

        SetSugarVisible(false);
        SetCubeVisible(true);
        cubeTransform.localPosition = cubeCenterLocalPos;
    }

    void ApplyIdleVisualState()
    {
        ApplyPressStroke(0f);
        if (sugarTransform != null)
            sugarTransform.localPosition = sugarCenterLocalPos;
        if (cubeTransform != null)
            cubeTransform.localPosition = cubeCenterLocalPos;
        SetSugarVisible(false);
        SetCubeVisible(false);
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

    void StopInputArrival()
    {
        if (inputArrivalRoutine == null)
            return;

        StopCoroutine(inputArrivalRoutine);
        inputArrivalRoutine = null;
    }

    FillSide ResolveFillSide(int slotIndex)
    {
        int upperSlots = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, pressMachine != null ? pressMachine.InputsPerProcess : 4) * 0.5f));
        return slotIndex <= upperSlots ? FillSide.Upper : FillSide.Lower;
    }

    Vector3 GetMaterialTarget(FillSide side)
    {
        return side == FillSide.Upper ? upperMaterialLocalPos : lowerMaterialLocalPos;
    }

    void SetSugarVisible(bool visible)
    {
        if (sugarRenderer != null)
            sugarRenderer.enabled = visible;
    }

    void SetCubeVisible(bool visible)
    {
        if (cubeRenderer != null)
            cubeRenderer.enabled = visible;
    }

    void Subscribe()
    {
        if (subscribed || pressMachine == null)
            return;

        pressMachine.InputBuffered += HandleInputBuffered;
        pressMachine.ProcessingStarted += HandleProcessingStarted;
        pressMachine.OutputProduced += HandleOutputProduced;
        subscribed = true;
    }

    void Unsubscribe()
    {
        if (!subscribed || pressMachine == null)
            return;

        pressMachine.InputBuffered -= HandleInputBuffered;
        pressMachine.ProcessingStarted -= HandleProcessingStarted;
        pressMachine.OutputProduced -= HandleOutputProduced;
        subscribed = false;
    }

    void AutoAssignReferences()
    {
        if (pressMachine == null)
            pressMachine = GetComponent<PressMachine>();

        if (upperGroup == null)
            upperGroup = transform.Find(UpperGroupName);
        if (lowerGroup == null)
            lowerGroup = transform.Find(LowerGroupName);

        if (upperLeftPress == null)
            upperLeftPress = transform.Find($"{UpperGroupName}/{UpperLeftPressName}") ?? transform.Find(UpperLeftPressName);
        if (upperRightPress == null)
            upperRightPress = transform.Find($"{UpperGroupName}/{UpperRightPressName}") ?? transform.Find(UpperRightPressName);
        if (lowerLeftPress == null)
            lowerLeftPress = transform.Find($"{LowerGroupName}/{LowerLeftPressName}") ?? transform.Find(LowerLeftPressName);
        if (lowerRightPress == null)
            lowerRightPress = transform.Find($"{LowerGroupName}/{LowerRightPressName}") ?? transform.Find(LowerRightPressName);

        if (sugarTransform == null)
            sugarTransform = transform.Find(SugarName);
        if (cubeTransform == null)
            cubeTransform = transform.Find(CubeName);

        sugarRenderer = sugarTransform != null ? sugarTransform.GetComponent<SpriteRenderer>() : null;
        cubeRenderer = cubeTransform != null ? cubeTransform.GetComponent<SpriteRenderer>() : null;
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

        if (sugarTransform != null)
            sugarCenterLocalPos = sugarTransform.localPosition;
        if (cubeTransform != null)
            cubeCenterLocalPos = cubeTransform.localPosition;

        float upperPressY = AverageY(upperLeftPress, upperRightPress);
        float lowerPressY = AverageY(lowerLeftPress, lowerRightPress);

        upperMaterialLocalPos = sugarCenterLocalPos;
        upperMaterialLocalPos.y = upperPressY - materialInsetFromPress;

        lowerMaterialLocalPos = sugarCenterLocalPos;
        lowerMaterialLocalPos.y = lowerPressY + materialInsetFromPress;
    }

    void CachePressPose(int index, Transform target, float idleX, float pressedX)
    {
        if (target == null)
            return;

        Vector3 idle = target.localPosition;
        idle.x = idleX;
        Vector3 pressed = idle;
        pressed.x = pressedX;
        idlePressPositions[index] = idle;
        pressedPressPositions[index] = pressed;
    }

    static float AverageY(Transform a, Transform b)
    {
        int count = 0;
        float sum = 0f;

        if (a != null)
        {
            sum += a.localPosition.y;
            count++;
        }

        if (b != null)
        {
            sum += b.localPosition.y;
            count++;
        }

        return count > 0 ? sum / count : 0f;
    }

    static float SafeNormalize(float value, float max)
    {
        if (max <= 0.0001f)
            return 1f;
        return Mathf.Clamp01(value / max);
    }

    static float EvaluateCurve(AnimationCurve curve, float t)
    {
        return curve != null ? curve.Evaluate(Mathf.Clamp01(t)) : Mathf.Clamp01(t);
    }
}
