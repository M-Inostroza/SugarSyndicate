using UnityEngine;
using UnityEngine.EventSystems;
using System;
using System.Linq;
using System.Reflection;

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

    [Header("Target")]
    [SerializeField] Camera targetCamera; // defaults to Camera.main
    [SerializeField] GridService gridService;

    [Header("Input")]
    [SerializeField] MouseButton panButton = MouseButton.Left;
    [SerializeField] bool requirePlayState = true; // if true, only pans when GameManager.State == Play

    [Header("Behavior")]
    [Tooltip("Multiplier for drag amount. 1 means exact 1:1 world drag distance.")]
    [SerializeField, Min(0.01f)] float dragSensitivity = 1f;

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
        try
        {
            // Find type named GameManager
            var gmType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t.Name == "GameManager");
            if (gmType == null) return true; // if no GM, allow pan

            // Try static Instance property
            object instance = null;
            var piInstance = gmType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (piInstance != null)
            {
                try { instance = piInstance.GetValue(null, null); } catch { instance = null; }
            }
            if (instance == null)
            {
                // Fallback: find component in scene
                var go = GameObject.Find("GameManager");
                if (go != null) instance = go.GetComponent(gmType);
                if (instance == null)
                {
                    var all = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                    foreach (var mb in all) { if (mb != null && mb.GetType() == gmType) { instance = mb; break; } }
                }
            }
            if (instance == null) return true;

            // Read State property/field and compare its ToString() to "Play"
            object stateObj = null;
            var piState = gmType.GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (piState != null)
            {
                try { stateObj = piState.GetValue(instance, null); } catch { stateObj = null; }
            }
            if (stateObj == null)
            {
                var fiState = gmType.GetField("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fiState != null) { try { stateObj = fiState.GetValue(instance); } catch { stateObj = null; } }
            }
            if (stateObj == null) return true;

            var name = stateObj.ToString();
            if (string.Equals(name, "Play", StringComparison.OrdinalIgnoreCase)) return true;

            // Allow panning in Build/Delete when no build tool is active
            if (string.Equals(name, "Build", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Delete", StringComparison.OrdinalIgnoreCase))
            {
                return !HasActiveBuildTool();
            }

            // other states: default allow
            return true;
        }
        catch { return true; }
    }

    bool HasActiveBuildTool()
    {
        try
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t.Name == "BuildModeController");
            if (type == null) return false;

            // static property HasActiveTool
            var pi = type.GetProperty("HasActiveTool", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi != null && pi.PropertyType == typeof(bool))
            {
                try { return (bool)pi.GetValue(null, null); } catch { }
            }

            var fi = type.GetField("HasActiveTool", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi != null && fi.FieldType == typeof(bool))
            {
                try { return (bool)fi.GetValue(null); } catch { }
            }
        }
        catch { }
        return false;
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
