using UnityEngine;

public class DroneWorker : MonoBehaviour
{
    [Tooltip("Base movement speed used to compute flight duration (distance / speed).")]
    [SerializeField, Min(0.01f)] float moveSpeed = 3.5f;
    [Tooltip("Distance threshold to consider the drone at its target (stops/restarts tweening).")]
    [SerializeField, Min(0.01f)] float arriveDistance = 0.05f;
    [Tooltip("If true, use the custom flight curve for movement; otherwise linear.")]
    [SerializeField] bool useFlightCurve = true;
    [Tooltip("Ease curve for the flight (0..1 time to 0..1 progress).")]
    [SerializeField] AnimationCurve flightCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Rendering")]
    [Tooltip("Raise drone sorting order so it stays above machines.")]
    [SerializeField] bool enforceSortingOrder = true;
    [Tooltip("Sorting order offset applied to the drone visuals.")]
    [SerializeField] int sortingOrderOffset = 2000;

    [Header("Progress Bar")]
    [Tooltip("Show a small progress bar below the drone while it is working.")]
    [SerializeField] bool showProgressBar = true;
    [Tooltip("Size of the progress bar in world units (width, height).")]
    [SerializeField] Vector2 barSize = new Vector2(0.5f, 0.06f);
    [Tooltip("Vertical offset below the drone in world units.")]
    [SerializeField, Min(0f)] float barYOffset = 0.25f;
    [Tooltip("Fill color of the progress bar.")]
    [SerializeField] Color barFillColor = new Color(0.2f, 0.9f, 0.4f, 1f);
    [Tooltip("Background color of the progress bar.")]
    [SerializeField] Color barBackgroundColor = new Color(0f, 0f, 0f, 0.4f);

    DroneTaskTarget currentTask;
    Vector3 currentTarget;
    bool hasTarget;
    bool isMoving;
    bool isPaused;
    Vector3 moveStart;
    float moveElapsed;
    float moveDuration;
    float lastMoveSpeed;
    bool lastUseCurve;
    int lastCurveHash;
    float curveStart;
    float curveEnd;
    bool hasCurveRange;
    Transform barRoot;
    SpriteRenderer barBackground;
    SpriteRenderer barFill;
    bool barVisible;
    UnityEngine.Rendering.SortingGroup sortingGroup;
    SpriteRenderer[] cachedRenderers;

    static Sprite barSprite;

    void Awake()
    {
        CacheRenderers();
        ApplySortingOrder();
    }

    void Update()
    {
        var service = DroneTaskService.Instance;
        if (service == null)
        {
            UpdateProgressBar(false);
            return;
        }

        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play)
        {
            isPaused = true;
            UpdateProgressBar(false);
            return;
        }

        isPaused = false;
        RefreshMovementSettings();

        if (currentTask == null)
        {
            if (service.HasHq && service.TryAssignTask(this, out var task))
            {
                currentTask = task;
            }
            else
            {
                if (service.HasHq)
                    SetMoveTarget(service.GetHqPosition());
                else
                    StopMovement();
                UpdateProgressBar(false);
                return;
            }
        }

        if (currentTask == null)
        {
            UpdateProgressBar(false);
            return;
        }

        var target = currentTask.WorkPosition;
        if (!IsValidTarget(target))
        {
            currentTask.ClearAssignment(this);
            currentTask = null;
            StopMovement();
            UpdateProgressBar(false);
            return;
        }
        if (!IsAt(target))
        {
            SetMoveTarget(target);
            UpdateProgressBar(false);
            return;
        }

        currentTask.ApplyWork(Time.deltaTime);
        if (currentTask.IsComplete)
        {
            currentTask.ClearAssignment(this);
            currentTask = null;
        }
        UpdateProgressBar(true);
    }

    void SetMoveTarget(Vector3 target)
    {
        if (!IsValidTarget(target))
        {
            StopMovement();
            return;
        }

        if (IsAt(target))
        {
            StopMovement();
            return;
        }

        if (hasTarget && (currentTarget - target).sqrMagnitude <= 0.0001f && isMoving)
            return;

        currentTarget = target;
        hasTarget = true;
        StartMovement(target);
    }

    bool IsAt(Vector3 target)
    {
        return (transform.position - target).sqrMagnitude <= arriveDistance * arriveDistance;
    }

    void StartMovement(Vector3 target)
    {
        float distance = Vector3.Distance(transform.position, target);
        float duration = distance / Mathf.Max(0.01f, moveSpeed);
        moveStart = transform.position;
        moveElapsed = 0f;
        moveDuration = duration;
        isMoving = true;
    }

    void StopMovement()
    {
        hasTarget = false;
        isMoving = false;
        moveElapsed = 0f;
    }

    void RefreshMovementSettings()
    {
        int curveHash = GetCurveHash(flightCurve);
        bool changed = !Mathf.Approximately(lastMoveSpeed, moveSpeed)
                       || lastUseCurve != useFlightCurve
                       || lastCurveHash != curveHash;
        if (!changed) return;

        lastMoveSpeed = moveSpeed;
        lastUseCurve = useFlightCurve;
        lastCurveHash = curveHash;
        CacheCurveEndpoints();

        if (hasTarget && isMoving)
            StartMovement(currentTarget);
    }

    static int GetCurveHash(AnimationCurve curve)
    {
        if (curve == null) return 0;
        unchecked
        {
            int hash = 17;
            var keys = curve.keys;
            hash = hash * 31 + keys.Length;
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                hash = hash * 31 + k.time.GetHashCode();
                hash = hash * 31 + k.value.GetHashCode();
                hash = hash * 31 + k.inTangent.GetHashCode();
                hash = hash * 31 + k.outTangent.GetHashCode();
            }
            return hash;
        }
    }

    static bool IsFinite(Vector3 v)
    {
        return !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z)
            && !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
    }

    static bool IsValidTarget(Vector3 target)
    {
        if (!IsFinite(target)) return false;
        var grid = GridService.Instance;
        if (grid == null) return true;
        var cell = grid.WorldToCell(target);
        return grid.InBounds(cell);
    }

    void CacheCurveEndpoints()
    {
        if (flightCurve == null)
        {
            curveStart = 0f;
            curveEnd = 1f;
            hasCurveRange = false;
            return;
        }
        curveStart = flightCurve.Evaluate(0f);
        curveEnd = flightCurve.Evaluate(1f);
        hasCurveRange = !Mathf.Approximately(curveStart, curveEnd);
    }

    void UpdateProgressBar(bool hasWork)
    {
        if (!showProgressBar)
        {
            SetBarVisible(false);
            return;
        }

        if (!hasWork || currentTask == null)
        {
            SetBarVisible(false);
            return;
        }

        EnsureProgressBar();
        SetBarVisible(true);

        float progress = Mathf.Clamp01(currentTask.Progress01);
        if (barRoot != null)
            barRoot.localPosition = new Vector3(-barSize.x * 0.5f, -barYOffset, 0f);

        if (barBackground != null)
        {
            barBackground.color = barBackgroundColor;
            barBackground.transform.localScale = new Vector3(barSize.x, barSize.y, 1f);
        }

        if (barFill != null)
        {
            barFill.color = barFillColor;
            barFill.transform.localScale = new Vector3(barSize.x * progress, barSize.y, 1f);
        }
    }

    void EnsureProgressBar()
    {
        if (barRoot != null) return;

        var root = new GameObject("DroneProgressBar");
        root.transform.SetParent(transform, false);
        root.transform.localPosition = new Vector3(-barSize.x * 0.5f, -barYOffset, 0f);
        barRoot = root.transform;

        int baseOrder = 0;
        int baseLayer = 0;
        var sr = GetPrimaryRenderer();
        if (sr != null)
        {
            baseOrder = sr.sortingOrder;
            baseLayer = sr.sortingLayerID;
        }

        var bg = new GameObject("Background");
        bg.transform.SetParent(barRoot, false);
        barBackground = bg.AddComponent<SpriteRenderer>();
        barBackground.sprite = GetBarSprite();
        barBackground.color = barBackgroundColor;
        barBackground.sortingOrder = baseOrder + 1;
        barBackground.sortingLayerID = baseLayer;
        barBackground.transform.localScale = new Vector3(barSize.x, barSize.y, 1f);

        var fill = new GameObject("Fill");
        fill.transform.SetParent(barRoot, false);
        barFill = fill.AddComponent<SpriteRenderer>();
        barFill.sprite = GetBarSprite();
        barFill.color = barFillColor;
        barFill.sortingOrder = baseOrder + 2;
        barFill.sortingLayerID = baseLayer;
        barFill.transform.localScale = new Vector3(0f, barSize.y, 1f);
    }

    void SetBarVisible(bool visible)
    {
        if (barRoot == null)
        {
            barVisible = false;
            return;
        }
        if (barVisible == visible) return;
        barVisible = visible;
        barRoot.gameObject.SetActive(visible);
    }

    static Sprite GetBarSprite()
    {
        if (barSprite != null) return barSprite;
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Point;
        barSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0f, 0.5f), 1f);
        barSprite.name = "DroneProgressBarSprite";
        return barSprite;
    }

    void CacheRenderers()
    {
        sortingGroup = GetComponentInChildren<UnityEngine.Rendering.SortingGroup>(true);
        cachedRenderers = GetComponentsInChildren<SpriteRenderer>(true);
    }

    SpriteRenderer GetPrimaryRenderer()
    {
        if (cachedRenderers != null)
        {
            for (int i = 0; i < cachedRenderers.Length; i++)
            {
                if (cachedRenderers[i] != null) return cachedRenderers[i];
            }
        }
        return GetComponentInChildren<SpriteRenderer>();
    }

    void ApplySortingOrder()
    {
        if (!enforceSortingOrder || sortingOrderOffset == 0) return;
        if (sortingGroup != null)
        {
            sortingGroup.sortingOrder += sortingOrderOffset;
            return;
        }
        if (cachedRenderers == null || cachedRenderers.Length == 0) return;
        for (int i = 0; i < cachedRenderers.Length; i++)
        {
            var sr = cachedRenderers[i];
            if (sr == null) continue;
            sr.sortingOrder += sortingOrderOffset;
        }
    }

    void LateUpdate()
    {
        if (isPaused) return;
        if (!isMoving || !hasTarget) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        moveElapsed += dt;
        float t = moveDuration <= 0.0001f ? 1f : Mathf.Clamp01(moveElapsed / moveDuration);
        float eval = useFlightCurve && flightCurve != null ? flightCurve.Evaluate(t) : t;
        float progress = eval;
        if (useFlightCurve && flightCurve != null && hasCurveRange)
        {
            progress = Mathf.InverseLerp(curveStart, curveEnd, eval);
        }
        transform.position = Vector3.LerpUnclamped(moveStart, currentTarget, progress);

        if (t >= 1f || IsAt(currentTarget))
        {
            transform.position = currentTarget;
            StopMovement();
        }
    }

    void OnDisable()
    {
        StopMovement();
    }
}
