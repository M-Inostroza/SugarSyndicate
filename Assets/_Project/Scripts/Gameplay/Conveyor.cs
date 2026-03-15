using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
[RequireComponent(typeof(Transform))]
public class Conveyor : MonoBehaviour, IConveyor
{
    public Direction direction = Direction.Right;
    [Min(1)] public int ticksPerCell = 4;
    [SerializeField] SpriteRenderer spriteRenderer;
    [SerializeField] Sprite straightSprite;
    [SerializeField] Sprite curveSprite;
    [SerializeField] CurveCorner curveBaseCorner = CurveCorner.LeftUp;
    [Header("Arrow Visual")]
    [SerializeField] Transform arrowVisual;
    [SerializeField] bool arrowUseBeltVisualTiming = true;
    [SerializeField, Min(0.01f)] float arrowCycleSeconds = 0.45f;
    [SerializeField] int arrowSortingOrderOffset = 1;
    [SerializeField, Min(0f)] float arrowCellPadding = 0.01f;
    [SerializeField, Min(0.1f)] float arrowSpacingMultiplier = 1f;
    [SerializeField, Min(0f)] float arrowSpacingWorldDistance = 0f;

    [System.NonSerialized] bool isCurve;
    [System.NonSerialized] Direction curveFrom = Direction.None;
    [System.NonSerialized] Direction curveTo = Direction.None;

    Vector2Int lastCell;
    GridService grid;
    SpriteRenderer arrowSpriteRenderer;
    Vector3 arrowBaseLocalPosition;
    bool arrowBaseCached;
    readonly List<Transform> arrowClones = new();
    readonly List<SpriteRenderer> arrowCloneSpriteRenderers = new();
    MaterialPropertyBlock arrowPropertyBlock;

    static readonly int ClipRectId = Shader.PropertyToID("_ClipRect");
    static Shader arrowClipShader;
    static Material arrowClipMaterial;
    
    // Flag to mark this conveyor as a ghost (visual only, should not be registered)
    [System.NonSerialized]
    public bool isGhost = false;

    public Vector2Int DirVec() => DirectionUtil.DirVec(direction);
    public bool IsCurve => isCurve;
    public Direction CurveFrom => curveFrom;
    public Direction CurveTo => curveTo;

    public enum CurveCorner
    {
        UpRight,
        RightDown,
        DownLeft,
        LeftUp,
    }

    void Awake()
    {
        ResolveArrowVisual();
        ResolveArrowSpriteRenderer();
        CacheArrowBaseLocalPosition();
        RefreshArrowVisualState(false);

        if (spriteRenderer == null)
            spriteRenderer = GetComponentInChildren<SpriteRenderer>(true);
        if (straightSprite == null && spriteRenderer != null)
            straightSprite = spriteRenderer.sprite;

        // Don't do any registration work if this is a ghost conveyor
        if (isGhost) return;
        
        // Cache initial cell; avoid mutating runtime services while not playing
        lastCell = GetCellForPosition(transform.position);
        // Do NOT touch GridService here; its Awake may not have warmed the grid yet when scene loads
        // Registration is done in Start to avoid race with GridService.Awake
    }

    public void SetStraight(Direction newDirection, float rotationOffset = 0f)
    {
        ApplyStraightSprite(newDirection);
        ApplyRotation(rotationOffset);
        RefreshArrowVisualState(false);
    }

    public void SetCurve(Direction fromDirection, Direction toDirection, float rotationOffset = 0f)
    {
        if (curveSprite == null || spriteRenderer == null)
        {
            ApplyStraightSprite(toDirection);
            ApplyRotation(rotationOffset);
            RefreshArrowVisualState(false);
            return;
        }
        ApplyCurveSprite(fromDirection, toDirection);
        ApplyCurveRotation(fromDirection, toDirection, rotationOffset);
        RefreshArrowVisualState(false);
    }

    public void ApplyStraightSprite(Direction newDirection)
    {
        direction = newDirection;
        isCurve = false;
        curveFrom = Direction.None;
        curveTo = Direction.None;
        if (spriteRenderer != null && straightSprite != null)
            spriteRenderer.sprite = straightSprite;
        RefreshArrowVisualState(false);
    }

    public void ApplyCurveSprite(Direction fromDirection, Direction toDirection)
    {
        if (curveSprite == null || spriteRenderer == null)
        {
            ApplyStraightSprite(toDirection);
            return;
        }

        direction = toDirection;
        isCurve = true;
        curveFrom = fromDirection;
        curveTo = toDirection;
        spriteRenderer.sprite = curveSprite;
        RefreshArrowVisualState(false);
    }

    void ApplyRotation(float rotationOffset)
    {
        float angle = direction switch
        {
            Direction.Right => 0f,
            Direction.Up => 90f,
            Direction.Left => 180f,
            Direction.Down => 270f,
            _ => 0f,
        };
        angle = Mathf.Repeat(angle + rotationOffset, 360f);
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    void ApplyCurveRotation(Direction fromDirection, Direction toDirection, float rotationOffset)
    {
        float angle;
        if (IsCurvePair(fromDirection, toDirection, Direction.Up, Direction.Right)) angle = 0f;
        else if (IsCurvePair(fromDirection, toDirection, Direction.Right, Direction.Down)) angle = 270f;
        else if (IsCurvePair(fromDirection, toDirection, Direction.Down, Direction.Left)) angle = 180f;
        else if (IsCurvePair(fromDirection, toDirection, Direction.Left, Direction.Up)) angle = 90f;
        else
        {
            ApplyStraightSprite(toDirection);
            ApplyRotation(rotationOffset);
            return;
        }

        angle = Mathf.Repeat(angle + GetCurveBaseOffset() + rotationOffset, 360f);
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    static bool IsCurvePair(Direction a, Direction b, Direction d1, Direction d2)
        => (a == d1 && b == d2) || (a == d2 && b == d1);

    float GetCurveBaseOffset()
    {
        float baseAngle = curveBaseCorner switch
        {
            CurveCorner.UpRight => 0f,
            CurveCorner.RightDown => 270f,
            CurveCorner.DownLeft => 180f,
            CurveCorner.LeftUp => 90f,
            _ => 0f,
        };
        return Mathf.Repeat(360f - baseAngle, 360f);
    }

    void Start()
    {
        if (Application.isPlaying)
        {
            // Don't register ghost conveyors with GridService or simulation
            if (isGhost)
            {
                return;
            }
            
            // Check if there's already a conveyor at this position
            var gs = GetGridService();
            if (gs != null)
            {
                var currentCell = gs.WorldToCell(transform.position);
                var existing = gs.GetConveyor(currentCell);
                if (existing != null && existing != this && !existing.isGhost)
                {
                    // There's already a real conveyor here, destroy this one
                    Debug.Log($"[Conveyor] Duplicate conveyor detected at {currentCell}, destroying duplicate.");
                    Destroy(gameObject);
                    return;
                }
            }
            
            // First, register with GridService now that all Awakes have run
            RegisterWithGridService();
            // Then register with the belt simulation so it can tick us
            BeltSimulationService.Instance?.RegisterConveyor(this);
        }
    }

    void OnEnable()
    {
        if (!Application.isPlaying) return;
        UndergroundVisibilityRegistry.RegisterBelt(this);
    }

    void OnDisable()
    {
        if (!Application.isPlaying) return;
        UndergroundVisibilityRegistry.UnregisterBelt(this);
    }

    void Update()
    {
        ResolveArrowVisual();
        ResolveArrowSpriteRenderer();
        CacheArrowBaseLocalPosition();
        RefreshArrowVisualState(Application.isPlaying);

        // Don't update grid registration for ghost conveyors
        if (isGhost) return;
        
        if (!Application.isPlaying)
        {
            // Editor-time grid snapping while moving in Scene view
            var gs = GetGridService();
            if (gs == null) return;

            float z = transform.position.z;
            var currentCell = gs.WorldToCell(transform.position);
            var world = gs.CellToWorld(currentCell, z);
            if ((world - transform.position).sqrMagnitude > 1e-6f)
                transform.position = world;
            return;
        }

        // Runtime: track cell changes and update grid + belt graph incrementally
        var rgs = GetGridService();
        if (rgs == null) return;

        var current = rgs.WorldToCell(transform.position);
        if (current != lastCell)
        {
            SetConveyorAtCell(lastCell, null);
            TrySetConveyorSafe(current, this);
            lastCell = current;
            BeltSimulationService.Instance?.RegisterConveyor(this);
        }
    }

    void OnDestroy()
    {
        for (int i = 0; i < arrowClones.Count; i++)
        {
            if (arrowClones[i] != null)
                Destroy(arrowClones[i].gameObject);
        }
        arrowClones.Clear();
        arrowCloneSpriteRenderers.Clear();
        if (Application.isPlaying && !isGhost)
        {
            SetConveyorAtCell(lastCell, null);
            BeltSimulationService.Instance?.UnregisterConveyor(this);
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        ResolveArrowVisual();
        ResolveArrowSpriteRenderer();
        CacheArrowBaseLocalPosition();
        RefreshArrowVisualState(false);

        if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
        var gs = GetGridService();
        if (gs == null) return;
        var cell = gs.WorldToCell(transform.position);
        var world = gs.CellToWorld(cell, transform.position.z);
        if ((world - transform.position).sqrMagnitude > 1e-6f)
            transform.position = world;
    }
#endif

    void RegisterWithGridService()
    {
        var gs = GetGridService();
        if (gs == null) return;

        var currentCell = gs.WorldToCell(transform.position);

        // Only register if not a ghost
        if (!isGhost)
        {
            TrySetConveyorSafe(currentCell, this);
            lastCell = currentCell;
        }
    }

    void TrySetConveyorSafe(Vector2Int cell, Conveyor conveyor)
    {
        // check for an existing conveyor in the cell
        var gs = GetGridService();
        if (gs == null) return;
        var existing = gs.GetConveyor(cell);
        if (existing != null && existing != this)
        {
            // Check if existing is a ghost and this is not
            if (existing.isGhost && !this.isGhost)
            {
                // Replace ghost with real conveyor
                Destroy(existing.gameObject);
                gs.SetConveyor(cell, conveyor);
                return;
            }
            
            // Check if this is a ghost and existing is real
            if (this.isGhost && !existing.isGhost)
            {
                // Don't replace real conveyor with ghost, destroy this ghost instead
                Debug.Log($"Ghost conveyor trying to replace real conveyor at {cell}, destroying ghost.");
                Destroy(gameObject);
                return;
            }
            
            // if placing opposite direction (head-on), keep the earlier one and destroy the new one to avoid a crashy cycle
            var dvExisting = existing.DirVec();
            var dvNew = this.DirVec();
            if (dvExisting == -dvNew)
            {
                Debug.LogWarning($"Conveyor collision head-on at {cell}, destroying the new one to avoid cycle.");
                Destroy(gameObject);
                return;
            }
            // otherwise, replace existing with this (simple policy) or keep existing; here we keep existing
            Debug.LogWarning($"Conveyor already present at {cell}, keeping existing.");
            return;
        }
        gs.SetConveyor(cell, conveyor);
    }

    void SetConveyorAtCell(Vector2Int cell, Conveyor conveyor)
    {
        var gs = GetGridService();
        if (gs == null) return;
        gs.SetConveyor(cell, conveyor);
    }

    Vector2Int GetCellForPosition(Vector3 pos)
    {
        var gs = GetGridService();
        if (gs == null) return default;
        return gs.WorldToCell(pos);
    }

    GridService GetGridService()
    {
        if (grid != null) return grid;
        grid = GridService.Instance;
        if (grid == null)
            grid = FindAnyObjectByType<GridService>();
        return grid;
    }

    void ResolveArrowVisual()
    {
        if (arrowVisual != null) return;

        foreach (var child in GetComponentsInChildren<Transform>(true))
        {
            if (child == transform) continue;
            if (!child.name.Equals("Arrow", System.StringComparison.OrdinalIgnoreCase)) continue;
            arrowVisual = child;
            break;
        }
    }

    void ResolveArrowSpriteRenderer()
    {
        if (arrowSpriteRenderer != null) return;
        if (arrowVisual == null) return;
        arrowSpriteRenderer = arrowVisual.GetComponentInChildren<SpriteRenderer>(true);
    }

    void CacheArrowBaseLocalPosition()
    {
        if (arrowVisual == null) return;
        if (arrowBaseCached && !Application.isPlaying) arrowBaseLocalPosition = arrowVisual.localPosition;
        else if (!arrowBaseCached) arrowBaseLocalPosition = arrowVisual.localPosition;
        arrowBaseCached = true;
    }

    void RefreshArrowVisualState(bool animate)
    {
        if (arrowVisual == null) return;
        if (spriteRenderer != null && arrowSpriteRenderer != null)
        {
            arrowSpriteRenderer.sortingLayerID = spriteRenderer.sortingLayerID;
            arrowSpriteRenderer.sortingOrder = spriteRenderer.sortingOrder + arrowSortingOrderOffset;
        }
        if (spriteRenderer != null)
        {
            for (int i = 0; i < arrowCloneSpriteRenderers.Count; i++)
            {
                var cloneRenderer = arrowCloneSpriteRenderers[i];
                if (cloneRenderer == null) continue;
                cloneRenderer.sortingLayerID = spriteRenderer.sortingLayerID;
                cloneRenderer.sortingOrder = spriteRenderer.sortingOrder + arrowSortingOrderOffset;
            }
        }

        bool showArrow = !isCurve;
        if (arrowVisual.gameObject.activeSelf != showArrow)
            arrowVisual.gameObject.SetActive(showArrow);
        for (int i = 0; i < arrowClones.Count; i++)
        {
            var clone = arrowClones[i];
            if (clone != null && clone.gameObject.activeSelf != showArrow)
                clone.gameObject.SetActive(showArrow);
        }

        if (!showArrow)
        {
            if (arrowBaseCached)
                arrowVisual.localPosition = arrowBaseLocalPosition;
            ResetArrowClonePositions();
            return;
        }

        if (!arrowBaseCached)
            arrowBaseLocalPosition = arrowVisual.localPosition;

        bool shouldAnimate = animate && !isGhost;
        if (!shouldAnimate)
        {
            arrowVisual.localPosition = arrowBaseLocalPosition;
            ResetArrowClonePositions();
            return;
        }

        Vector3 startOffset = GetArrowLeadStartLocalOffset();
        Vector3 repeatOffset = GetArrowRepeatLocalOffset();
        if (repeatOffset.sqrMagnitude <= 0.000001f)
        {
            arrowVisual.localPosition = arrowBaseLocalPosition;
            ResetArrowClonePositions();
            return;
        }

        EnsureArrowClones();
        ApplyArrowClipping();

        float cycleSeconds = GetArrowCycleSeconds();
        float phaseTime = GetArrowPhaseTime();
        float progress = Mathf.Repeat(phaseTime / cycleSeconds, 1f);
        Vector3 offset = startOffset + repeatOffset * progress;
        arrowVisual.localPosition = arrowBaseLocalPosition + offset;

        for (int i = 0; i < arrowClones.Count; i++)
        {
            var clone = arrowClones[i];
            if (clone == null) continue;
            clone.localPosition = arrowBaseLocalPosition + startOffset + repeatOffset * (progress - (i + 1));
        }
    }

    float GetArrowCycleSeconds()
    {
        float secondsPerCell = arrowCycleSeconds > 0.01f ? arrowCycleSeconds : 0.45f;
        if (arrowUseBeltVisualTiming)
        {
            var beltService = BeltSimulationService.Instance;
            if (beltService != null)
                secondsPerCell = beltService.GetVisualSecondsPerCell(secondsPerCell);
        }

        float cellSize = GetCellHalfExtentWorld() * 2f;
        float repeatDistance = GetArrowRepeatWorldDistance();
        if (cellSize <= 0.0001f || repeatDistance <= 0.0001f)
            return Mathf.Max(1f / 120f, secondsPerCell);

        float repeatRatio = repeatDistance / cellSize;
        return Mathf.Max(1f / 120f, secondsPerCell * repeatRatio);
    }

    float GetArrowPhaseTime()
    {
        var beltService = BeltSimulationService.Instance;
        if (beltService != null)
            return beltService.VisualClock;
        return Time.time;
    }

    Vector3 GetArrowLeadStartLocalOffset()
    {
        var parent = arrowVisual != null && arrowVisual.parent != null ? arrowVisual.parent : transform;
        float startWorld = GetArrowLeadStartWorldDistanceFromCenter();
        if (Mathf.Abs(startWorld) <= 0.0001f) return Vector3.zero;

        return parent.InverseTransformVector(transform.right.normalized * startWorld);
    }

    Vector3 GetArrowRepeatLocalOffset()
    {
        var parent = arrowVisual != null && arrowVisual.parent != null ? arrowVisual.parent : transform;
        float repeatWorld = GetArrowRepeatWorldDistance();
        if (repeatWorld <= 0f) return Vector3.zero;

        return parent.InverseTransformVector(transform.right.normalized * repeatWorld);
    }

    float GetArrowLeadStartWorldDistanceFromCenter()
    {
        float halfVisible = GetArrowHalfVisibleWorldDistance();
        float halfArrow = GetArrowHalfExtentWorldAlongConveyor();
        if (halfVisible <= 0f || halfArrow <= 0f)
            return 0f;

        return halfVisible - halfArrow;
    }

    void EnsureArrowClones()
    {
        if (!Application.isPlaying) return;
        if (arrowVisual == null) return;
        if (arrowSpriteRenderer == null) return;

        int requiredCloneCount = GetRequiredArrowCloneCount();
        while (arrowClones.Count < requiredCloneCount)
        {
            var cloneGO = Instantiate(arrowVisual.gameObject, arrowVisual.parent);
            cloneGO.name = $"ArrowClone_{arrowClones.Count + 1}";

            var cloneTransform = cloneGO.transform;
            var cloneRenderer = cloneTransform.GetComponentInChildren<SpriteRenderer>(true);
            if (cloneRenderer != null)
            {
                cloneRenderer.sortingLayerID = arrowSpriteRenderer.sortingLayerID;
                cloneRenderer.sortingOrder = arrowSpriteRenderer.sortingOrder;
            }

            arrowClones.Add(cloneTransform);
            arrowCloneSpriteRenderers.Add(cloneRenderer);
        }

        while (arrowClones.Count > requiredCloneCount)
        {
            int lastIndex = arrowClones.Count - 1;
            if (arrowClones[lastIndex] != null)
                Destroy(arrowClones[lastIndex].gameObject);
            arrowClones.RemoveAt(lastIndex);
            arrowCloneSpriteRenderers.RemoveAt(lastIndex);
        }
    }

    void ResetArrowClonePositions()
    {
        for (int i = 0; i < arrowClones.Count; i++)
        {
            var clone = arrowClones[i];
            if (clone != null)
                clone.localPosition = arrowBaseLocalPosition;
        }
    }

    int GetRequiredArrowCloneCount()
    {
        float repeatWorld = GetArrowRepeatWorldDistance();
        if (repeatWorld <= 0.0001f)
            return 0;

        float visibleSpanWorld = GetArrowVisibleSpanWorldDistance();
        return Mathf.Max(1, Mathf.CeilToInt(visibleSpanWorld / repeatWorld));
    }

    float GetArrowRepeatWorldDistance()
    {
        return GetArrowHalfVisibleWorldDistance() * 2f;
    }

    float GetArrowVisibleSpanWorldDistance()
    {
        return GetArrowRepeatWorldDistance() + GetArrowHalfExtentWorldAlongConveyor() * 2f;
    }

    float GetArrowHalfVisibleWorldDistance()
    {
        float halfCell = GetCellHalfExtentWorld();
        if (halfCell <= 0.0001f)
            return 0f;

        float maxPadding = Mathf.Max(0f, halfCell - 0.0001f);
        float padding = Mathf.Clamp(arrowCellPadding, 0f, maxPadding);
        return halfCell - padding;
    }

    float GetCellHalfExtentWorld()
    {
        float cellSize = 1f;
        var gs = GetGridService();
        if (gs != null && gs.CellSize > 0.0001f)
            cellSize = gs.CellSize;
        return cellSize * 0.5f;
    }

    void ApplyArrowClipping()
    {
        if (!Application.isPlaying) return;
        if (spriteRenderer == null) return;

        var clipMaterial = GetArrowClipMaterial();
        if (clipMaterial == null) return;

        if (arrowSpriteRenderer != null && arrowSpriteRenderer.sharedMaterial != clipMaterial)
            arrowSpriteRenderer.sharedMaterial = clipMaterial;
        for (int i = 0; i < arrowCloneSpriteRenderers.Count; i++)
        {
            var cloneRenderer = arrowCloneSpriteRenderers[i];
            if (cloneRenderer != null && cloneRenderer.sharedMaterial != clipMaterial)
                cloneRenderer.sharedMaterial = clipMaterial;
        }

        var bounds = spriteRenderer.bounds;
        var clipRect = new Vector4(bounds.min.x, bounds.min.y, bounds.max.x, bounds.max.y);
        ApplyArrowClipRect(arrowSpriteRenderer, clipRect);
        for (int i = 0; i < arrowCloneSpriteRenderers.Count; i++)
            ApplyArrowClipRect(arrowCloneSpriteRenderers[i], clipRect);
    }

    void ApplyArrowClipRect(SpriteRenderer sr, Vector4 clipRect)
    {
        if (sr == null) return;
        if (arrowPropertyBlock == null)
            arrowPropertyBlock = new MaterialPropertyBlock();

        sr.GetPropertyBlock(arrowPropertyBlock);
        arrowPropertyBlock.SetVector(ClipRectId, clipRect);
        sr.SetPropertyBlock(arrowPropertyBlock);
    }

    Material GetArrowClipMaterial()
    {
        if (arrowClipMaterial != null) return arrowClipMaterial;
        if (arrowClipShader == null)
            arrowClipShader = Shader.Find("Custom/ConveyorArrowClip");
        if (arrowClipShader == null)
            return null;

        arrowClipMaterial = new Material(arrowClipShader)
        {
            name = "ConveyorArrowClip (Runtime)"
        };
        return arrowClipMaterial;
    }

    float GetArrowHalfExtentWorldAlongConveyor()
    {
        if (arrowSpriteRenderer == null) return 0f;

        var extents = arrowSpriteRenderer.bounds.extents;
        Vector3 dir = transform.right.normalized;
        return Mathf.Abs(dir.x) * extents.x
             + Mathf.Abs(dir.y) * extents.y
             + Mathf.Abs(dir.z) * extents.z;
    }
}
