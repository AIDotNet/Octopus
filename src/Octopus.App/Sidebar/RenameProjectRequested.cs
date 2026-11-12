namespace Octopus.App.Sidebar;

/// <summary>Sent (through the messenger) when the project rail asks the project panel to start renaming a project.</summary>
public sealed record RenameProjectRequested(string ProjectId);
