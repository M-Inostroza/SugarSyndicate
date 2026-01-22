using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

// Simple sink that consumes any item. Counts SugarBlock deliveries.
public class Truck : MonoBehaviour, IMachine, IMachineStorageWithCapacity, IPowerConsumer, IGhostState
{
    [Header("Services")]
    [SerializeField] GridService grid;
    [SerializeField] PowerService powerService;

    [Header("State")]
    [SerializeField] bool activeOnStart = false;

    [Header("Docking")]
    [SerializeField] Sprite truckSprite;
    [SerializeField] Vector3 truckDockOffset = Vector3.zero;
    [SerializeField, Min(0.1f)] float approachSpeed = 6f;
    [SerializeField, Min(0f)] float offscreenViewportPadding = 0.1f;
    [SerializeField] Camera approachCamera;
    [SerializeField] bool logDocking;

    [Header("Capacity")]
    [SerializeField, Min(0)] int capacity = 30;
    [SerializeField] int currentLoad;

    [Header("Power")]
    [SerializeField, Min(0f)] float powerUsageWatts = 0f;

    [Header("Compass")]
    [SerializeField] bool showCompass = true;
    [SerializeField, Tooltip("Arrow sprite should face up in the texture.")] Sprite compassSprite;
    [SerializeField] Color compassTint = new Color(1f, 1f, 1f, 0.85f);
    [SerializeField, Min(8f)] float compassSize = 48f;
    [SerializeField, Min(0f)] float compassEdgePadding = 24f;
    [SerializeField] bool showWhenOnScreen = false;
    [SerializeField] Vector3 compassWorldOffset = Vector3.zero;
    [SerializeField] Camera compassCamera;
    [SerializeField] int compassSortingOrder = 5500;

    public int StoredItemCount => Mathf.Max(0, currentLoad);
    public int Capacity => Mathf.Max(0, capacity);

    public static event Action<string> OnItemDelivered;
    public static event Action<int> OnSugarBlockDelivered;
    public static event Action<bool> OnTrucksCalledChanged;
    public static event Action<Truck> OnTruckDocked;

    public Vector2Int Cell => cell;
    public Vector2Int InputVec => Vector2Int.zero;
    public bool IsDocked => isDocked;
    public bool IsGhost => false;

    Vector2Int cell;
    bool registered;
    int sugarBlocksDelivered;
    bool isActive;
    bool isDocked;
    bool isArriving;
    bool dockedNotified;
    const string SugarBlockType = "SugarBlock";

    static bool trucksCalled;
    public static bool TrucksCalled => trucksCalled;

    static Canvas compassCanvas;
    static RectTransform compassCanvasRect;
    static Plane[] compassFrustumPlanes = new Plane[6];

    Renderer dockRenderer;
    Transform truckVisual;
    SpriteRenderer truckSpriteRenderer;
    Image compassImage;
    RectTransform compassRect;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        isDocked = activeOnStart;
        isActive = activeOnStart;
        dockRenderer = GetDockRenderer();
    }

    void OnEnable()
    {
        UndergroundVisibilityRegistry.RegisterOverlay(this);
        OnTrucksCalledChanged += HandleTrucksCalledChanged;
        EnsureCompassIndicator();
    }

    void OnDisable()
    {
        UndergroundVisibilityRegistry.UnregisterOverlay(this);
        OnTrucksCalledChanged -= HandleTrucksCalledChanged;
        SetCompassVisible(false);
    }

    void Start()
    {
        if (grid == null) grid = GridService.Instance;
        if (grid == null) return;

        EnsureStorageDisplay();

        TryRegisterAsMachineAndSnap();
        MachineRegistry.Register(this);
        registered = true;

        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        powerService?.RegisterConsumer(this);

        EnsureTruckVisual();
        EnsureDockRenderer();
        if (isDocked)
        {
            CompleteDocking(false);
        }
        else if (trucksCalled)
        {
            BeginArrival();
        }
        else
        {
            SetTruckVisualVisible(false);
        }
    }

    void OnDestroy()
    {
        try
        {
            DestroyCompassIndicator();
            if (powerService == null) powerService = PowerService.Instance;
            powerService?.UnregisterConsumer(this);
            if (!registered) return;
            MachineRegistry.Unregister(this);
            if (grid == null) grid = GridService.Instance;
            if (grid != null)
            {
                grid.ClearCell(cell);
                var c = grid.GetCell(cell);
                if (c != null) c.hasMachine = false;
            }
        }
        catch { }
    }

    void LateUpdate()
    {
        UpdateCompassIndicator();
    }

    void Update()
    {
        if (!isArriving) return;
        UpdateArrival();
    }

    public void Activate() => isActive = true;
    public void Deactivate() => isActive = false;

    void HandleTrucksCalledChanged(bool called)
    {
        if (called) BeginArrival();
        else ResetDocking();
    }

    public static void SetTrucksCalled(bool called)
    {
        if (trucksCalled == called) return;
        trucksCalled = called;
        try { OnTrucksCalledChanged?.Invoke(trucksCalled); } catch { }
    }

    void ResetDocking()
    {
        isArriving = false;
        dockedNotified = false;
        EnsureTruckVisual();
        EnsureDockRenderer();

        if (activeOnStart)
        {
            CompleteDocking(false);
        }
        else
        {
            isDocked = false;
            Deactivate();
            SetTruckVisualVisible(false);
        }
    }

    void BeginArrival()
    {
        if (isDocked)
        {
            Activate();
            return;
        }

        EnsureTruckVisual();
        EnsureDockRenderer();
        dockedNotified = false;

        if (!HasTruckSprite())
        {
            if (logDocking)
                Debug.Log($"[Truck] '{name}' missing sprite; docking immediately.");
            CompleteDocking(true);
            return;
        }

        isArriving = true;
        Deactivate();

        if (truckVisual != null)
        {
            truckVisual.position = GetArrivalStartPosition();
            if (logDocking)
                Debug.Log($"[Truck] '{name}' arrival start at {truckVisual.position} -> dock {GetDockedWorldPosition()}");
        }
        SetTruckVisualVisible(true);
    }

    void UpdateArrival()
    {
        if (!HasTruckSprite() || truckVisual == null)
        {
            if (logDocking)
                Debug.Log($"[Truck] '{name}' arrival aborted; missing sprite or visual.");
            CompleteDocking(true);
            return;
        }

        if (approachSpeed <= 0f)
        {
            if (logDocking)
                Debug.Log($"[Truck] '{name}' arrival speed <= 0; docking immediately.");
            CompleteDocking(true);
            return;
        }

        Vector3 target = GetDockedWorldPosition();
        float step = approachSpeed * Time.deltaTime;
        truckVisual.position = Vector3.MoveTowards(truckVisual.position, target, step);
        if ((truckVisual.position - target).sqrMagnitude <= 0.0001f)
            CompleteDocking(true);
    }

    void CompleteDocking(bool notify)
    {
        isArriving = false;
        isDocked = true;
        Activate();

        if (truckVisual != null)
            truckVisual.position = GetDockedWorldPosition();
        SetTruckVisualVisible(HasTruckSprite());

        if (notify && !dockedNotified)
        {
            dockedNotified = true;
            if (logDocking)
                Debug.Log($"[Truck] '{name}' docked at {GetDockedWorldPosition()}");
            try { OnTruckDocked?.Invoke(this); } catch { }
        }
    }

    void EnsureTruckVisual()
    {
        if (truckVisual == null)
        {
            var existing = transform.Find("TruckVisual");
            if (existing != null) truckVisual = existing;
        }

        if (truckVisual != null && truckSpriteRenderer == null)
            truckSpriteRenderer = truckVisual.GetComponent<SpriteRenderer>();

        if (truckVisual == null && truckSprite != null)
        {
            var go = new GameObject("TruckVisual");
            go.transform.SetParent(transform, false);
            truckVisual = go.transform;
            truckSpriteRenderer = go.AddComponent<SpriteRenderer>();
        }

        if (truckVisual != null && truckSpriteRenderer == null && truckSprite != null)
            truckSpriteRenderer = truckVisual.gameObject.AddComponent<SpriteRenderer>();

        if (truckSpriteRenderer != null)
        {
            if (truckSprite != null)
                truckSpriteRenderer.sprite = truckSprite;
            SyncTruckVisualSorting();
        }
    }

    void EnsureDockRenderer()
    {
        if (dockRenderer == null)
            dockRenderer = GetDockRenderer();
    }

    Renderer GetDockRenderer()
    {
        var renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0) return null;
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;
            if (truckVisual != null && r.transform.IsChildOf(truckVisual)) continue;
            return r;
        }
        return null;
    }

    void SyncTruckVisualSorting()
    {
        if (truckSpriteRenderer == null) return;
        if (dockRenderer == null) return;

        if (dockRenderer is SpriteRenderer dockSprite)
        {
            truckSpriteRenderer.sortingLayerID = dockSprite.sortingLayerID;
            truckSpriteRenderer.sortingOrder = dockSprite.sortingOrder + 1;
            return;
        }

        var group = dockRenderer.GetComponentInParent<SortingGroup>();
        if (group != null)
        {
            truckSpriteRenderer.sortingLayerID = group.sortingLayerID;
            truckSpriteRenderer.sortingOrder = group.sortingOrder + 1;
        }
    }

    bool HasTruckSprite()
    {
        return truckSpriteRenderer != null && truckSpriteRenderer.sprite != null;
    }

    void SetTruckVisualVisible(bool visible)
    {
        if (truckSpriteRenderer != null)
            truckSpriteRenderer.enabled = visible;
    }

    Vector3 GetDockedWorldPosition()
    {
        return transform.position + truckDockOffset;
    }

    Vector3 GetArrivalStartPosition()
    {
        var dockPos = GetDockedWorldPosition();
        var cam = approachCamera != null ? approachCamera : Camera.main;
        if (cam == null)
        {
            if (logDocking)
                Debug.Log($"[Truck] '{name}' no approach camera; using fallback spawn.");
            return dockPos + Vector3.right * 10f;
        }

        var viewport = cam.WorldToViewportPoint(dockPos);
        float z = viewport.z;
        if (z <= 0f)
        {
            z = Vector3.Dot(dockPos - cam.transform.position, cam.transform.forward);
            if (z <= 0.01f) z = cam.nearClipPlane + 1f;
        }

        viewport.x = 1f + Mathf.Max(0f, offscreenViewportPadding);
        var start = cam.ViewportToWorldPoint(new Vector3(viewport.x, viewport.y, z));
        return start;
    }

    public bool CanAcceptFrom(Vector2Int approachFromVec)
    {
        return isActive;
    }

    public bool TryStartProcess(Item item)
    {
        if (!isActive) return false;
        if (!HasPower()) return false;
        if (item == null) return false;

        if (currentLoad >= capacity)
        {
            Debug.Log($"[Truck] Rejecting item; capacity full ({currentLoad}/{capacity}) at {cell}");
            return false;
        }

        var type = string.IsNullOrWhiteSpace(item.type) ? "Unknown" : item.type.Trim();
        Debug.Log($"[Truck] Accepted item '{type}' at {cell}");

        currentLoad++;
        LevelStats.RecordShipped(type);
        OnItemDelivered?.Invoke(type);

        if (IsSugarBlock(type))
        {
            sugarBlocksDelivered++;
            OnSugarBlockDelivered?.Invoke(sugarBlocksDelivered);
        }
        return true; // consume any item
    }

    void EnsureStorageDisplay()
    {
        if (GetComponent<MachineStorageDisplay>() != null) return;
        gameObject.AddComponent<MachineStorageDisplay>();
    }


    void TryRegisterAsMachineAndSnap()
    {
        try
        {
            if (grid == null) grid = GridService.Instance;
            if (grid == null) { Debug.LogWarning("[Truck] GridService not found"); return; }

            cell = grid.WorldToCell(transform.position);
            grid.SetMachineCell(cell);
            var world = grid.CellToWorld(cell, transform.position.z);
            transform.position = world;
        }
        catch { }
    }

    bool IsSugarBlock(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        return string.Equals(type.Trim(), SugarBlockType, StringComparison.OrdinalIgnoreCase);
    }

    public float GetConsumptionWatts()
    {
        return Mathf.Max(0f, powerUsageWatts);
    }

    bool HasPower()
    {
        if (powerUsageWatts <= 0f) return true;
        if (!PowerConsumerUtil.IsMachinePowered(this)) return false;
        if (powerService == null) powerService = PowerService.Instance ?? PowerService.EnsureInstance();
        return powerService != null && powerService.HasPowerFor(this, powerUsageWatts);
    }

    void EnsureCompassIndicator()
    {
        if (!showCompass || compassSprite == null) return;
        EnsureCompassCanvas(compassSortingOrder);
        if (compassCanvas == null || compassImage != null) return;

        var go = new GameObject("TruckCompass");
        go.transform.SetParent(compassCanvas.transform, false);
        compassImage = go.AddComponent<Image>();
        compassImage.sprite = compassSprite;
        compassImage.color = compassTint;
        compassImage.raycastTarget = false;
        compassRect = compassImage.rectTransform;
        compassRect.sizeDelta = new Vector2(compassSize, compassSize);
        compassRect.pivot = new Vector2(0.5f, 0.5f);
        compassImage.enabled = false;
    }

    void DestroyCompassIndicator()
    {
        if (compassImage != null) Destroy(compassImage.gameObject);
        compassImage = null;
        compassRect = null;
    }

    void SetCompassVisible(bool visible)
    {
        if (compassImage != null) compassImage.enabled = visible;
    }

    void UpdateCompassIndicator()
    {
        if (!showCompass || compassSprite == null)
        {
            SetCompassVisible(false);
            return;
        }

        if (compassImage == null) EnsureCompassIndicator();
        if (compassImage == null || compassRect == null) return;

        var cam = compassCamera != null ? compassCamera : Camera.main;
        if (cam == null)
        {
            SetCompassVisible(false);
            return;
        }

        var worldPos = transform.position + compassWorldOffset;
        bool onScreen = IsVisibleInCamera(cam);
        if (!onScreen)
        {
            var viewport = cam.WorldToViewportPoint(worldPos);
            onScreen = viewport.z > 0f &&
                viewport.x >= 0f && viewport.x <= 1f &&
                viewport.y >= 0f && viewport.y <= 1f;
        }

        if (onScreen && !showWhenOnScreen)
        {
            SetCompassVisible(false);
            return;
        }

        SetCompassVisible(true);
        if (compassImage.color != compassTint) compassImage.color = compassTint;

        var screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var screenPos = cam.WorldToScreenPoint(worldPos);
        var dir = new Vector2(screenPos.x, screenPos.y) - screenCenter;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector2.up;
        if (screenPos.z < 0f) dir = -dir;
        dir.Normalize();

        float pad = compassEdgePadding + compassSize * 0.5f;
        float boundX = Mathf.Max(0f, screenCenter.x - pad);
        float boundY = Mathf.Max(0f, screenCenter.y - pad);
        float absX = Mathf.Abs(dir.x);
        float absY = Mathf.Abs(dir.y);
        float tX = absX > 0.0001f ? boundX / absX : float.PositiveInfinity;
        float tY = absY > 0.0001f ? boundY / absY : float.PositiveInfinity;
        float t = Mathf.Min(tX, tY);
        var edgePos = screenCenter + dir * t;

        if (compassCanvasRect != null)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(compassCanvasRect, edgePos, null, out var localPos);
            compassRect.anchoredPosition = localPos;
        }
        else
        {
            compassRect.position = edgePos;
        }

        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        compassRect.rotation = Quaternion.Euler(0f, 0f, angle - 90f);
        compassRect.sizeDelta = new Vector2(compassSize, compassSize);
    }

    bool IsVisibleInCamera(Camera cam)
    {
        if (cam == null) return false;
        if (dockRenderer == null) dockRenderer = GetDockRenderer();
        if (dockRenderer == null || !dockRenderer.enabled) return false;
        GeometryUtility.CalculateFrustumPlanes(cam, compassFrustumPlanes);
        return GeometryUtility.TestPlanesAABB(compassFrustumPlanes, dockRenderer.bounds);
    }

    static void EnsureCompassCanvas(int sortingOrder)
    {
        if (compassCanvas != null)
        {
            if (sortingOrder > compassCanvas.sortingOrder)
                compassCanvas.sortingOrder = sortingOrder;
            return;
        }

        var go = new GameObject("TruckCompassCanvas");
        DontDestroyOnLoad(go);
        compassCanvas = go.AddComponent<Canvas>();
        compassCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        compassCanvas.sortingOrder = sortingOrder;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        go.AddComponent<GraphicRaycaster>();
        compassCanvasRect = compassCanvas.GetComponent<RectTransform>();
    }
}
