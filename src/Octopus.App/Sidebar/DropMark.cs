namespace Octopus.App.Sidebar;

/// <summary>What a square or a folder on the rail shows while something dragged is over it.</summary>
public enum DropMark
{
    None,
    /// <summary>Dropping puts the dragged square into this group (or makes a group with this square).</summary>
    Into,
    /// <summary>A line above: dropping puts the dragged thing right before this one.</summary>
    Before,
    /// <summary>A line below: dropping puts the dragged thing right after this one.</summary>
    After,
}
