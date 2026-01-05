using System.Collections;
using UnityEngine;

public class ScanModeController : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] GridService grid;
    [SerializeField] SugarZoneOverlay sugarOverlay;
    [SerializeField] bool autoCreateOverlay = true;

    [Header("Input")]
    [SerializeField] bool enableInput = true;
    [SerializeField] KeyCode scanKey = KeyCode.Tab;

    [Header("Timing")]
    [SerializeField, Min(0f)] float scanDuration = 3f;
    [SerializeField, Min(0f)] float cooldown = 5f;
    [SerializeField] bool autoScanOnStart = false;
    [SerializeField] bool hideOverlayOnStart = true;
    [SerializeField] bool debugLogScanPress = false;

    bool isScanning;
    float nextReadyTime;
    Coroutine scanRoutine;

    void Awake()
    {
        if (grid == null) grid = GridService.Instance ?? FindAnyObjectByType<GridService>();
        if (sugarOverlay == null && grid != null)
            sugarOverlay = grid.GetComponent<SugarZoneOverlay>();
        if (sugarOverlay == null && autoCreateOverlay && grid != null)
            sugarOverlay = SugarZoneOverlay.FindOrCreate(grid);
    }

    void Start()
    {
        if (hideOverlayOnStart && sugarOverlay != null)
        {
            sugarOverlay.Hide();
            StartCoroutine(HideNextFrame());
        }
        if (autoScanOnStart) TryScan();
    }

    void Update()
    {
        if (!enableInput) return;
        if (Input.GetKeyDown(scanKey))
        {
            if (debugLogScanPress) Debug.Log($"[ScanMode] Scan key pressed ({scanKey}).");
            TryScan();
        }
    }

    // UI Button hook
    public void OnScanButtonPressed()
    {
        if (debugLogScanPress) Debug.Log("[ScanMode] Scan button pressed.");
        TryScan();
    }

    public void TryScan()
    {
        if (isScanning) return;
        if (Time.time < nextReadyTime) return;
        if (sugarOverlay == null) return;
        if (debugLogScanPress) Debug.Log("[ScanMode] Scan triggered.");
        if (scanRoutine != null) StopCoroutine(scanRoutine);
        scanRoutine = StartCoroutine(ScanRoutine());
    }

    public void CancelScan()
    {
        if (scanRoutine != null) StopCoroutine(scanRoutine);
        scanRoutine = null;
        isScanning = false;
        if (sugarOverlay != null) sugarOverlay.Hide();
    }

    IEnumerator ScanRoutine()
    {
        isScanning = true;
        sugarOverlay.Show();
        yield return new WaitForSeconds(scanDuration);
        sugarOverlay.Hide();
        isScanning = false;
        nextReadyTime = Time.time + cooldown;
        scanRoutine = null;
    }

    IEnumerator HideNextFrame()
    {
        yield return null;
        if (hideOverlayOnStart && sugarOverlay != null) sugarOverlay.Hide();
    }
}
