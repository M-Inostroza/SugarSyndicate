using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Simple camera panning with mouse drag while in Play state.
/// - Click and drag to move the camera.
/// - Ignores drags when pointer is over UI.
/// - Only active when GameManager.State == Play if requirePlayState is true.
/// Attach this to any GameObject in the scene (commonly the Camera) and assign the target camera.
/// </summary>
public class CameraDragPan : MonoBehaviour
{
    public enum MouseButton { Left = 0, Right = 1, Middle = 2 }

    public static event System.Action CameraDragged;

    [Header("Target")]
    [SerializeField] Camera targetCamera; // defaults to Camera.main
    [SerializeField] GridService gridService;

    [Header("Input")]
    [SerializeField] MouseButton panButton = MouseButton.Left;
    [SerializeField] bool requirePlayState = true; // if true, only pans when GameManager.State == Play

    [Header("Behavior")]
    [Tooltip("Multiplier for drag amount. 1 means exact 1:1 world drag distance.")]
    [SerializeField, Min(0.01f)] float dragSensitivity = 1f;
    [Tooltip("World-space distance required during a drag before we notify listeners.")]
    [SerializeField, Min(0.01f)] float notifyDistance = 0.1f;

    [Header("Bounds")]
    [SerializeField] bool constrainToGrid = true;
    [SerializeField, Min(0f)] float edgePadding = 0.1f;

    [Header("Smoothing")]
    [Tooltip("Smooth camera towards drag target to avoid jitter.")]
    [SerializeField] bool smooth = true;
    [SerializeField, Range(0.01f, 0.3f)] float smoothTime = 0.08f;

    // Drag anchors
    Vector3 dragOriginWorld;  // world position under cursor when drag started (on z=0 plane)
    Vector3 camOriginPos;     // camera position when drag started

    // State
    bool dragging;
    bool notifiedDrag;
    Vector3 smoothVelocity;   // for SmoothDamp

    void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (gridService == null) gridService = GridService.Instance;
    }

    void Update()
    {
        if (targetCamera == null) return;

        // Respect game state if requested
        if (requirePlayState && !IsPlayState())
        {
            dragging = false;
            return;
        }

        // Do not pan when pointer is over UI
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
        {
            dragging = false;
            return;
        }

        int btn = (int)panButton;

        if (Input.GetMouseButtonDown(btn))
        {
            dragging = true;
            notifiedDrag = false;
            dragOriginWorld = GetMouseWorldOnPlane(targetCamera);
            camOriginPos = targetCamera.transform.position;
            smoothVelocity = Vector3.zero; // reset smoothing for a crisp start
        }
        else if (dragging && Input.GetMouseButton(btn))
        {
            var curWorld = GetMouseWorldOnPlane(targetCamera);
            var delta = curWorld - dragOriginWorld; // anchored world delta since drag start
            var desired = new Vector3(
                camOriginPos.x - delta.x * dragSensitivity,
                camOriginPos.y - delta.y * dragSensitivity,
                camOriginPos.z);

            if (!notifiedDrag && delta.sqrMagnitude >= notifyDistance * notifyDistance)
            {
                notifiedDrag = true;
                CameraDragged?.Invoke();
            }

            if (smooth)
            {
                var pos = Vector3.SmoothDamp(targetCamera.transform.position, desired, ref smoothVelocity, smoothTime);
                ClampToGrid(ref pos);
                targetCamera.transform.position = pos;
            }
            else
            {
                ClampToGrid(ref desired);
                targetCamera.transform.position = desired;
            }
        }
        else if (Input.GetMouseButtonUp(btn))
        {
            dragging = false;
        }
    }

    static Vector3 GetMouseWorldOnPlane(Camera cam)
    {
        // Assumes the game world is on Z = 0 plane (2D). For perspective/orthographic cams this works
        // by converting using the distance from camera to z=0.
        var mp = Input.mousePosition;
        float planeZ = 0f;
        float camZ = cam.transform.position.z;
        mp.z = planeZ - camZ; // distance along camera forward to hit z=0 plane
        var world = cam.ScreenToWorldPoint(mp);
        world.z = 0f; // return a point on the plane
        return world;
    }

    bool IsPlayState()
    {
        var gm = GameManager.Instance;
        if (gm == null) return true;

        if (gm.State == GameState.Play)
            return true;

        if (gm.State == GameState.Build || gm.State == GameState.Delete)
            return !HasActiveBuildTool();

        return true;
    }

    bool HasActiveBuildTool()
    {
        return BuildModeController.HasActiveTool;
    }

    void ClampToGrid(ref Vector3 pos)
    {
        if (!constrainToGrid) return;
        if (targetCamera == null) return;
        if (gridService == null) gridService = GridService.Instance;
        if (gridService == null) return;
        if (!targetCamera.orthographic) return; // only clamp orthographic cameras

        var origin = (Vector2)gridService.Origin;
        var size = gridService.GridSize;
        float cs = gridService.CellSize;
        float minX = origin.x;
        float minY = origin.y;
        float maxX = origin.x + size.x * cs;
        float maxY = origin.y + size.y * cs;

        float halfH = targetCamera.orthographicSize;
        float halfW = halfH * targetCamera.aspect;
        float pad = edgePadding;

        float clampMinX = minX + halfW + pad;
        float clampMaxX = maxX - halfW - pad;
        float clampMinY = minY + halfH + pad;
        float clampMaxY = maxY - halfH - pad;

        // If camera view is larger than grid, center it and bail
        if (clampMinX > clampMaxX) { pos.x = (minX + maxX) * 0.5f; }
        else pos.x = Mathf.Clamp(pos.x, clampMinX, clampMaxX);
        if (clampMinY > clampMaxY) { pos.y = (minY + maxY) * 0.5f; }
        else pos.y = Mathf.Clamp(pos.y, clampMinY, clampMaxY);
    }
}
