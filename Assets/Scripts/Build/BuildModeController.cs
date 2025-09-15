using System;
using UnityEngine;
using UnityEngine.EventSystems;

public enum BuildableType { None, Conveyor }

public class BuildModeController : MonoBehaviour
{
    [Header("Placer refs")]
    [SerializeField] ConveyorPlacer conveyorPlacer;

    public static bool IsDragging { get; private set; } = false;

    public event Action onExitBuildMode;

    BuildableType current = BuildableType.None;

    void Update()
    {
        if (current == BuildableType.None) return;

        bool pointerOverUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        // cancel build with Esc or right mouse
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1))
        {
            CancelBuildMode();
            return;
        }

        // Rotate preview
        if (Input.GetKeyDown(KeyCode.R))
        {
            switch (current)
            {
                case BuildableType.Conveyor:
                    conveyorPlacer?.RotatePreview();
                    break;
            }
        }

        // Update preview position
        switch (current)
        {
            case BuildableType.Conveyor:
                conveyorPlacer?.UpdatePreviewPosition();
                break;
        }

        // Handle pointer down/up for drag-aware placement
        if (Input.GetMouseButtonDown(0))
        {
            if (!pointerOverUI)
            {
                IsDragging = true; // announce drag start
                // start potential drag - placer will record start cell
                conveyorPlacer?.OnPointerDown();
            }
        }

        if (Input.GetMouseButtonUp(0))
        {
            bool keepPlacing = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool placed = false;

            if (IsDragging)
            {
                switch (current)
                {
                    case BuildableType.Conveyor:
                        placed = conveyorPlacer != null && conveyorPlacer.OnPointerUp();
                        break;
                }
            }

            IsDragging = false; // announce drag end

            if (placed)
            {
                if (!keepPlacing) CancelBuildMode();
                else conveyorPlacer?.RefreshPreviewAfterPlace();
            }
        }
    }

    public void StartBuildMode(BuildableType type)
    {
        if (current != BuildableType.None) CancelBuildMode();
        current = type;

        // Enter global Build state so systems can pause (e.g., belt sim)
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);
        var enterState = GameManager.Instance != null ? GameManager.Instance.State.ToString() : "<no GameManager>";

        switch (current)
        {
            case BuildableType.Conveyor:
                conveyorPlacer?.BeginPreview();
                break;
        }
    }

    // UI-friendly overloads so Button.onClick can call without enum arguments
    public void StartConveyorBuildMode()
    {
        StartBuildMode(BuildableType.Conveyor);
    }

    public void StartBuildModeInt(int type)
    {
        StartBuildMode((BuildableType)type);
    }

    public void CancelBuildMode()
    {
        IsDragging = false; // ensure cleared
        switch (current)
        {
            case BuildableType.Conveyor:
                conveyorPlacer?.EndPreview();
                break;
        }
        current = BuildableType.None;
        onExitBuildMode?.Invoke();

        // Return to Play state when leaving build mode
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Play);
        var exitState = GameManager.Instance != null ? GameManager.Instance.State.ToString() : "<no GameManager>";
    }

    // UI helper to start conveyor build mode and immediately enable delete mode
    public void StartDeleteConveyorMode()
    {
        StartBuildMode(BuildableType.Conveyor);
        // Enter global Delete state so systems can pause appropriately
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Delete);
        conveyorPlacer?.StartDeleteMode();
    }
}
