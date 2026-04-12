namespace KiroWebUI.Pipeline.Models;

public sealed class ParsedIssue
{
    public required string RequirementsSection { get; init; }
    public required IReadOnlyList<string> AcceptanceCriteria { get; init; }
}
