namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Represents a single file change for PR description rendering.
/// </summary>
public sealed record FileChangeSummary(string Status, string Path, int LinesAdded = 0, int LinesDeleted = 0);
