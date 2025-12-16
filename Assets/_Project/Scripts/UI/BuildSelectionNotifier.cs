using System;

public static class BuildSelectionNotifier
{
    public static event Action<string> OnSelectionChanged;

    public static void Notify(string selectionName)
    {
        try { OnSelectionChanged?.Invoke(selectionName); } catch { }
    }
}
