namespace KiroWebUI.Pipeline.Models;

public sealed class IssueSummary
{
    public required string Identifier { get; init; }  // e.g., "123"
    public required string Title { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
}
