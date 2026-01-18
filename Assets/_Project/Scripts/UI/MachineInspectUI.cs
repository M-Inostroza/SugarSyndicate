using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using DG.Tweening;

public class MachineInspectUI : MonoBehaviour
{
    [Header("Zoom")]
    [SerializeField] float zoomSize = 2.8f;
    [SerializeField, Min(0.01f)] float zoomDuration = 0.35f;
    [SerializeField] AnimationCurve zoomEaseCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("UI References")]
    [SerializeField] GameObject uiRoot;
    [SerializeField] CanvasGroup backdropGroup;
    [SerializeField] Button backdropButton;
    [SerializeField] CanvasGroup panelGroup;
    [SerializeField] RectTransform panelRect;
    [SerializeField] Button closeButton;
    [SerializeField] Button buyDroneButton;
    [SerializeField, Min(0)] int buyDroneCost = 50;
    [SerializeField] Button buyCrawlerButton;
    [SerializeField, Min(0)] int buyCrawlerCost = 50;
    [SerializeField] TMP_Text titleText;
    [SerializeField] TMP_Text maintenanceText;
    [SerializeField] Slider maintenanceSlider;
    [SerializeField] TMP_Text processText;
    [SerializeField] bool persistAcrossScenes = false;

    [Header("Input")]
    [SerializeField] bool blockClicksOverUI = true;

    [Header("UI Animation")]
    [SerializeField] bool animateUI = true;
    [SerializeField, Min(0.01f)] float uiOpenDuration = 0.2f;
    [SerializeField, Min(0.01f)] float uiCloseDuration = 0.15f;
    [SerializeField] AnimationCurve uiOpenEaseCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [SerializeField] AnimationCurve uiCloseEaseCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [SerializeField] Vector2 panelClosedOffset = new Vector2(0f, -20f);
    [SerializeField, Range(0.5f, 1.5f)] float panelClosedScale = 0.92f;
    [SerializeField, Range(0.5f, 1.5f)] float panelOpenScale = 1f;

    Camera cam;
    Vector3 prevCamPos;
    float prevCamSize;
    float prevTimeScale = 1f;
    bool isOpen;
    bool isClosing;
    MonoBehaviour currentMachine;
    bool hasMaintenance;
    float lastMaintenance = -1f;

    Vector2 panelBasePos;
    bool panelBaseCached;

    Tweener moveTween;
    Tweener zoomTween;
    Sequence uiTween;
    int closeWaitCount;

    void Awake()
    {
        if (persistAcrossScenes) DontDestroyOnLoad(gameObject);
        CacheUiReferences();
        WireUi(true);
        CachePanelBasePos();
        SetUiImmediate(false);
    }

    void OnEnable()
    {
        WireUi(true);
    }

    void OnDisable()
    {
        WireUi(false);
    }

    void OnDestroy()
    {
        if (isOpen)
            Time.timeScale = prevTimeScale;
        WireUi(false);
    }

    void Update()
    {
        if (isOpen)
        {
            if (currentMachine == null)
            {
                FinishClose();
                return;
            }

            if (!isClosing)
            {
                UpdateMaintenance();
                if (Input.GetKeyDown(KeyCode.Escape))
                    Close();
            }
            return;
        }

        if (GameManager.Instance != null && GameManager.Instance.State != GameState.Play) return;
        if (blockClicksOverUI && EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (!Input.GetMouseButtonDown(0)) return;

        var machine = TryPickMachineAt(Input.mousePosition);
        if (machine != null)
            Open(machine);
    }

    void Open(MonoBehaviour machine)
    {
        if (machine == null || isOpen) return;
        currentMachine = machine;
        BuildMachineInfo(machine, out var title, out var processSummary, out hasMaintenance);

        if (titleText != null) titleText.text = title;
        if (processText != null) processText.text = processSummary;
        lastMaintenance = -1f;

        SetUiActive(true);
        CachePanelBasePos();

        prevTimeScale = Time.timeScale;
        Time.timeScale = 0f;

        cam = Camera.main;
        if (cam != null)
        {
            prevCamPos = cam.transform.position;
            prevCamSize = cam.orthographic ? cam.orthographicSize : 0f;
            var target = new Vector3(machine.transform.position.x, machine.transform.position.y, prevCamPos.z);
            moveTween?.Kill();
            zoomTween?.Kill();
            moveTween = cam.transform.DOMove(target, zoomDuration).SetEase(SafeCurve(zoomEaseCurve)).SetUpdate(true);
            if (cam.orthographic)
                zoomTween = DOTween.To(() => cam.orthographicSize, s => cam.orthographicSize = s, zoomSize, zoomDuration)
                    .SetEase(SafeCurve(zoomEaseCurve))
                    .SetUpdate(true);
        }

        PlayUiOpen();

        isOpen = true;
        isClosing = false;
        UpdateMaintenance();
        UpdateDroneHqUi();
    }

    void Close()
    {
        if (!isOpen || isClosing) return;
        isClosing = true;
        closeWaitCount = 0;

        PlayUiClose();
        if (uiTween != null)
        {
            closeWaitCount++;
            uiTween.OnComplete(NotifyCloseStepComplete);
        }

        moveTween?.Kill();
        zoomTween?.Kill();

        cam = Camera.main != null ? Camera.main : cam;
        if (cam != null)
        {
            var duration = Mathf.Max(0.01f, zoomDuration);
            moveTween = cam.transform.DOMove(prevCamPos, duration).SetEase(SafeCurve(zoomEaseCurve)).SetUpdate(true);
            if (cam.orthographic)
                zoomTween = DOTween.To(() => cam.orthographicSize, s => cam.orthographicSize = s, prevCamSize, duration)
                    .SetEase(SafeCurve(zoomEaseCurve))
                    .SetUpdate(true);

            var waiter = moveTween ?? zoomTween;
            if (waiter != null)
            {
                closeWaitCount++;
                waiter.OnComplete(NotifyCloseStepComplete);
            }
        }

        if (closeWaitCount == 0)
            FinishClose();
    }

    void NotifyCloseStepComplete()
    {
        if (!isClosing) return;
        closeWaitCount = Mathf.Max(0, closeWaitCount - 1);
        if (closeWaitCount == 0)
            FinishClose();
    }

    void FinishClose()
    {
        Time.timeScale = prevTimeScale;
        isOpen = false;
        isClosing = false;
        currentMachine = null;
        hasMaintenance = false;
        lastMaintenance = -1f;

        SetUiImmediate(false);
        SetUiActive(false);
        UpdateDroneHqUi();
    }

    void UpdateMaintenance()
    {
        if (maintenanceSlider == null || maintenanceText == null) return;
        if (!hasMaintenance)
        {
            maintenanceSlider.value = 1f;
            maintenanceText.text = "Maintenance: N/A";
            return;
        }

        if (!TryGetMaintenance(currentMachine, out var value)) return;
        value = Mathf.Clamp01(value);
        if (Mathf.Abs(value - lastMaintenance) < 0.001f) return;
        lastMaintenance = value;
        maintenanceSlider.value = value;
        maintenanceText.text = $"Maintenance: {Mathf.RoundToInt(value * 100f)}%";
    }

    MonoBehaviour TryPickMachineAt(Vector3 screenPos)
    {
        var camMain = Camera.main;
        if (camMain == null) return null;
        var world = camMain.ScreenToWorldPoint(screenPos);
        world.z = 0f;

        var hit = Physics2D.OverlapPoint(world);
        if (hit != null)
        {
            var fromCollider = FindMachineFromTransform(hit.transform);
            if (fromCollider != null) return fromCollider;
        }

        var grid = GridService.Instance;
        if (grid == null) return null;
        var cell = grid.WorldToCell(world);
        if (MachineRegistry.TryGet(cell, out var machine) && machine is MonoBehaviour mb)
            return mb;
        var mines = FindObjectsByType<SugarMine>(FindObjectsSortMode.None);
        foreach (var mine in mines)
        {
            if (mine == null) continue;
            if (grid.WorldToCell(mine.transform.position) == cell)
                return mine;
        }
        var hqs = FindObjectsByType<DroneHQ>(FindObjectsSortMode.None);
        foreach (var hq in hqs)
        {
            if (hq == null) continue;
            if (grid.WorldToCell(hq.transform.position) == cell)
                return hq;
        }

        return null;
    }

    MonoBehaviour FindMachineFromTransform(Transform t)
    {
        if (t == null) return null;
        var mbs = t.GetComponentsInParent<MonoBehaviour>(true);
        for (int i = 0; i < mbs.Length; i++)
        {
            var mb = mbs[i];
            if (mb is IMachine) return mb;
            if (mb is SugarMine) return mb;
            if (mb is DroneHQ) return mb;
        }
        return null;
    }

    void BuildMachineInfo(MonoBehaviour machine, out string title, out string process, out bool hasMaintenanceInfo)
    {
        title = "Machine";
        process = "Processing: N/A";
        hasMaintenanceInfo = false;

        if (machine is PressMachine press)
        {
            title = "Press";
            process = press.GetProcessSummary();
            hasMaintenanceInfo = true;
            return;
        }
        if (machine is ColorizerMachine colorizer)
        {
            title = "Colorizer";
            process = colorizer.GetProcessSummary();
            hasMaintenanceInfo = true;
            return;
        }
        if (machine is SugarMine mine)
        {
            title = "Mine";
            process = mine.GetProcessSummary();
            hasMaintenanceInfo = true;
            return;
        }
        if (machine is DroneHQ)
        {
            title = "Drone HQ";
            process = BuildDroneHqSummary();
            hasMaintenanceInfo = false;
            return;
        }
        if (machine is StorageContainerMachine storage)
        {
            title = "Storage";
            return;
            process = $"Stores items ({storage.StoredItemCount}/{storage.Capacity})";
            return;
        }
        if (machine is SolarPanelMachine solar)
        {
            title = "Solar Panel";
            process = solar.GetProcessSummary();
            hasMaintenanceInfo = false;
            return;
        }
        if (machine is Truck truck)
        {
            title = "Truck";
            process = $"Ships items ({truck.StoredItemCount}/{truck.Capacity})";
            return;
        }
        if (machine is WaterPump)
        {
            title = "Water Pump";
            process = "No item processing";
            return;
        }

        if (machine != null)
            title = machine.GetType().Name;
    }

    bool TryGetMaintenance(MonoBehaviour machine, out float value)
    {
        value = 1f;
        if (machine is PressMachine press)
        {
            value = press.Maintenance01;
            return true;
        }
        if (machine is ColorizerMachine colorizer)
        {
            value = colorizer.Maintenance01;
            return true;
        }
        if (machine is SugarMine mine)
        {
            value = mine.Maintenance01;
            return true;
        }
        return false;
    }

    string BuildDroneHqSummary()
    {
        var service = DroneTaskService.Instance;
        if (service == null)
            return "Drones: 0/0\nSpeed: 0";

        int totalDrones = service.TotalDrones;
        int maxDrones = service.MaxDrones;
        int totalCrawlers = service.TotalCrawlers;
        int maxCrawlers = service.MaxCrawlers;
        float speed = service.DroneSpeed;
        float crawlerSpeed = service.CrawlerSpeed;
        string maxD = maxDrones > 0 ? $"/{maxDrones}" : string.Empty;
        string maxC = maxCrawlers > 0 ? $"/{maxCrawlers}" : string.Empty;
        return $"Drones: {totalDrones}{maxD}\nCrawlers: {totalCrawlers}{maxC}\nDrone Speed: {speed:0.##}\nCrawler Speed: {crawlerSpeed:0.##}";
    }

    void UpdateDroneHqUi()
    {
        bool isHq = currentMachine is DroneHQ;
        if (!isHq)
        {
            if (buyDroneButton != null) buyDroneButton.gameObject.SetActive(false);
            if (buyCrawlerButton != null) buyCrawlerButton.gameObject.SetActive(false);
            return;
        }

        var service = DroneTaskService.Instance;
        if (buyDroneButton != null)
        {
            buyDroneButton.gameObject.SetActive(true);
            buyDroneButton.interactable = service != null && service.CanAddDrone;
        }
        if (buyCrawlerButton != null)
        {
            buyCrawlerButton.gameObject.SetActive(true);
            buyCrawlerButton.interactable = service != null && service.CanAddCrawler;
        }
    }

    public void TryBuyDrone()
    {
        var service = DroneTaskService.Instance;
        if (service == null) return;
        if (service.TryBuyDrone(buyDroneCost))
        {
            if (processText != null) processText.text = BuildDroneHqSummary();
            UpdateDroneHqUi();
        }
    }

    public void TryBuyCrawler()
    {
        var service = DroneTaskService.Instance;
        if (service == null) return;
        if (service.TryBuyCrawler(buyCrawlerCost))
        {
            if (processText != null) processText.text = BuildDroneHqSummary();
            UpdateDroneHqUi();
        }
    }

    void CacheUiReferences()
    {
        if (uiRoot == null) uiRoot = gameObject;

        if (panelRect == null && panelGroup != null)
            panelRect = panelGroup.GetComponent<RectTransform>();
        if (panelGroup == null && panelRect != null)
            panelGroup = panelRect.GetComponent<CanvasGroup>();
        if (backdropGroup == null && backdropButton != null)
            backdropGroup = backdropButton.GetComponent<CanvasGroup>();
    }

    void CachePanelBasePos()
    {
        if (panelRect == null) return;
        panelBasePos = panelRect.anchoredPosition;
        panelBaseCached = true;
    }

    void WireUi(bool add)
    {
        if (backdropButton != null)
        {
            if (add)
            {
                backdropButton.onClick.RemoveListener(Close);
                backdropButton.onClick.AddListener(Close);
            }
            else
            {
                backdropButton.onClick.RemoveListener(Close);
            }
        }
        if (closeButton != null)
        {
            if (add)
            {
                closeButton.onClick.RemoveListener(Close);
                closeButton.onClick.AddListener(Close);
            }
            else
            {
                closeButton.onClick.RemoveListener(Close);
            }
        }
        if (buyDroneButton != null)
        {
            if (add)
            {
                buyDroneButton.onClick.RemoveListener(TryBuyDrone);
                buyDroneButton.onClick.AddListener(TryBuyDrone);
            }
            else
            {
                buyDroneButton.onClick.RemoveListener(TryBuyDrone);
            }
        }
        if (buyCrawlerButton != null)
        {
            if (add)
            {
                buyCrawlerButton.onClick.RemoveListener(TryBuyCrawler);
                buyCrawlerButton.onClick.AddListener(TryBuyCrawler);
            }
            else
            {
                buyCrawlerButton.onClick.RemoveListener(TryBuyCrawler);
            }
        }
    }

    void SetUiActive(bool visible)
    {
        if (uiRoot == null) return;
        if (uiRoot == gameObject) return;
        uiRoot.SetActive(visible);
    }

    void SetUiImmediate(bool visible)
    {
        if (!panelBaseCached) CachePanelBasePos();
        if (backdropGroup != null)
        {
            backdropGroup.alpha = visible ? 1f : 0f;
            backdropGroup.blocksRaycasts = visible;
            backdropGroup.interactable = visible;
        }
        if (panelGroup != null)
        {
            panelGroup.alpha = visible ? 1f : 0f;
            panelGroup.blocksRaycasts = visible;
            panelGroup.interactable = visible;
        }
        if (panelRect != null)
        {
            panelRect.anchoredPosition = panelBasePos;
            panelRect.localScale = Vector3.one * panelOpenScale;
        }
    }

    void PlayUiOpen()
    {
        if (!panelBaseCached) CachePanelBasePos();

        uiTween?.Kill();
        uiTween = null;

        if (backdropGroup != null)
        {
            backdropGroup.alpha = animateUI ? 0f : 1f;
            backdropGroup.blocksRaycasts = true;
            backdropGroup.interactable = true;
        }
        if (panelGroup != null)
        {
            panelGroup.alpha = animateUI ? 0f : 1f;
            panelGroup.blocksRaycasts = true;
            panelGroup.interactable = true;
        }
        if (panelRect != null)
        {
            panelRect.localScale = Vector3.one * (animateUI ? panelClosedScale : panelOpenScale);
            panelRect.anchoredPosition = panelBasePos + (animateUI ? panelClosedOffset : Vector2.zero);
        }

        if (!animateUI) return;

        uiTween = DOTween.Sequence().SetUpdate(true);
        if (backdropGroup != null)
            uiTween.Join(backdropGroup.DOFade(1f, uiOpenDuration).SetEase(SafeCurve(uiOpenEaseCurve)));
        if (panelGroup != null)
            uiTween.Join(panelGroup.DOFade(1f, uiOpenDuration).SetEase(SafeCurve(uiOpenEaseCurve)));
        if (panelRect != null)
        {
            uiTween.Join(panelRect.DOScale(panelOpenScale, uiOpenDuration).SetEase(SafeCurve(uiOpenEaseCurve)));
            uiTween.Join(panelRect.DOAnchorPos(panelBasePos, uiOpenDuration).SetEase(SafeCurve(uiOpenEaseCurve)));
        }
    }

    void PlayUiClose()
    {
        uiTween?.Kill();
        uiTween = null;

        if (!animateUI)
        {
            if (backdropGroup != null) backdropGroup.alpha = 0f;
            if (panelGroup != null) panelGroup.alpha = 0f;
            return;
        }

        uiTween = DOTween.Sequence().SetUpdate(true);
        if (backdropGroup != null)
            uiTween.Join(backdropGroup.DOFade(0f, uiCloseDuration).SetEase(SafeCurve(uiCloseEaseCurve)));
        if (panelGroup != null)
            uiTween.Join(panelGroup.DOFade(0f, uiCloseDuration).SetEase(SafeCurve(uiCloseEaseCurve)));
        if (panelRect != null)
        {
            uiTween.Join(panelRect.DOScale(panelClosedScale, uiCloseDuration).SetEase(SafeCurve(uiCloseEaseCurve)));
            uiTween.Join(panelRect.DOAnchorPos(panelBasePos + panelClosedOffset, uiCloseDuration).SetEase(SafeCurve(uiCloseEaseCurve)));
        }
    }

    static AnimationCurve SafeCurve(AnimationCurve curve)
    {
        if (curve != null && curve.length > 0) return curve;
        return AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    }
}
