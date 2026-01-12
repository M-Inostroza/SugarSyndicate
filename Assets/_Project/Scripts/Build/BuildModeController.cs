using System;
using UnityEngine;
using UnityEngine.EventSystems;
using System.Reflection;

public enum BuildableType { None, Conveyor }

public class BuildModeController : MonoBehaviour
{
    [Header("Placer refs")]
    [SerializeField] ConveyorPlacer conveyorPlacer;

    public static bool IsDragging { get; private set; } = false;
    public static bool HasActiveTool { get; private set; } = false;
    public static void SetToolActive(bool active) => HasActiveTool = active;

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
                if (!keepPlacing)
                {
                    // If we are already in Build mode, just end the current preview but keep the game paused in Build.
                    bool inBuildState = GameManager.Instance != null && GameManager.Instance.State == GameState.Build;
                    if (inBuildState) EndCurrentPreview();
                    else CancelBuildMode();
                }
                else
                {
                    conveyorPlacer?.RefreshPreviewAfterPlace();
                }
            }
        }
    }

    // End only the current preview without touching the global GameManager state
    void EndCurrentPreview()
    {
        switch (current)
        {
            case BuildableType.Conveyor:
                conveyorPlacer?.EndPreview();
                break;
        }
        current = BuildableType.None;
        HasActiveTool = false;
    }

    public void StartBuildMode(BuildableType type)
    {
        // Ensure only one building tool is active at a time by stopping other builders
        TryStopMachineBuilder();
        TryStopJunctionBuilder();

        // Enter Build state so belt sim pauses item movement while placing
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Build);

        // If a preview is already active, just end that preview and start the new one
        // without changing GameManager state. Global state is controlled elsewhere.
        if (current != BuildableType.None) EndCurrentPreview();
        current = type;
        HasActiveTool = current != BuildableType.None;

        switch (current)
        {
            case BuildableType.Conveyor:
                conveyorPlacer?.BeginPreview();
                break;
        }
        if (current == BuildableType.Conveyor) BuildSelectionNotifier.Notify("Conveyor");
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
        HasActiveTool = false;
        onExitBuildMode?.Invoke();
        BuildSelectionNotifier.Notify(null);

        // Return to Play state when leaving build mode
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Play);
        var exitState = GameManager.Instance != null ? GameManager.Instance.State.ToString() : "<no GameManager>";
    }

    // Clear any active build tool without leaving Build mode.
    public void ClearActiveTool()
    {
        IsDragging = false;
        TryStopMachineBuilder();
        TryStopJunctionBuilder();

        if (current != BuildableType.None)
            EndCurrentPreview();

        current = BuildableType.None;
        HasActiveTool = false;
        BuildSelectionNotifier.Notify(null);

        if (GameManager.Instance != null && GameManager.Instance.State == GameState.Delete)
            GameManager.Instance.SetState(GameState.Build);
    }

    // When switching to belt building, stop any active MachineBuilder session
    void TryStopMachineBuilder()
    {
        try
        {
            var mb = FindAnyObjectByType<MachineBuilder>();
            if (mb != null) mb.StopBuilding();
        }
        catch { }
    }

    void TryStopJunctionBuilder()
    {
        try
        {
            var jb = FindAnyObjectByType<JunctionBuilder>();
            if (jb != null)
            {
                var mi = typeof(JunctionBuilder).GetMethod("StopBuilding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mi != null) mi.Invoke(jb, null);
            }
        }
        catch { }
    }

    // UI helper to start conveyor build mode and immediately enable delete mode
    public void StartDeleteConveyorMode()
    {
        // Stop any other builders
        TryStopMachineBuilder();
        TryStopJunctionBuilder();

        // Reset any active preview
        if (current != BuildableType.None) EndCurrentPreview();

        current = BuildableType.Conveyor;
        HasActiveTool = true;

        // Enter global Delete state so systems can pause appropriately
        if (GameManager.Instance != null) GameManager.Instance.SetState(GameState.Delete);

        // Start delete visuals/logic on placer
        conveyorPlacer?.BeginPreview(); // ensures placer is ready for input
        conveyorPlacer?.StartDeleteMode();
    }

    // NEW: Stop delete mode explicitly (UI button). Returns to Play state.
    public void StopDeleteMode()
    {
        if (GameManager.Instance != null && GameManager.Instance.State == GameState.Delete)
        {
            // Reuse existing cancel logic to clean up previews etc.
            CancelBuildMode();
        }
    }

    // Helper to know if delete mode is active
    public bool IsInDeleteMode()
    {
        return GameManager.Instance != null && GameManager.Instance.State == GameState.Delete;
    }
}
