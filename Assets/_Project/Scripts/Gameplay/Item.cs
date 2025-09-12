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
}
