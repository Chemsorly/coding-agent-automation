namespace KiroWebUI.Pipeline.Models;

public sealed class IssueDetail
{
    public required string Identifier { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }
    public required IReadOnlyList<string> AcceptanceCriteria { get; init; }
}
