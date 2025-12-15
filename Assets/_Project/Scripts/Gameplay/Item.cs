
using System;
using UnityEngine;

/// <summary>
/// Simple runtime item representation. Stores an identifier and an optional
/// reference to a visual <see cref="Transform"/> so the simulation can move
/// the view along the belt grid.
/// </summary>
public class Item
{
    public int id;
    public Transform view;
    public string type;

    // Helper for simple type checks. Empty expected type acts as a wildcard.
    public bool IsType(string expectedType)
    {
        if (string.IsNullOrWhiteSpace(expectedType)) return true;
        return string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase);
    }

    // Helper for matching against multiple accepted types (case-insensitive). Empty list acts as wildcard.
    public bool IsAnyType(params string[] expectedTypes)
    {
        if (expectedTypes == null || expectedTypes.Length == 0) return true;
        foreach (var raw in expectedTypes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (string.Equals(type, raw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
