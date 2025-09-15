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

    [Header("Input")]
    [SerializeField] MouseButton panButton = MouseButton.Left;
    [SerializeField] bool requirePlayState = true; // if true, only pans when GameManager.State == Play

    [Header("Behavior")]
    [Tooltip("Multiplier for drag amount. 1 means exact 1:1 world drag distance.")]
    [SerializeField, Min(0.01f)] float dragSensitivity = 1f;

    Vector3 lastMouseWorld;
    bool dragging;

    void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
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
            lastMouseWorld = GetMouseWorldOnPlane(targetCamera);
        }
        else if (dragging && Input.GetMouseButton(btn))
        {
            var cur = GetMouseWorldOnPlane(targetCamera);
            var delta = cur - lastMouseWorld; // how much the world moved under the cursor
            if (delta.sqrMagnitude > 0f)
            {
                // move camera opposite to mouse delta
                var pos = targetCamera.transform.position;
                pos -= delta * dragSensitivity;
                targetCamera.transform.position = pos;
                lastMouseWorld = cur;
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
        world.z = cam.transform.position.z; // keep camera's z for relative deltas
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
            return string.Equals(name, "Play", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }
}
