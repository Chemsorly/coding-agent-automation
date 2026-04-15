namespace KiroWebUI.Pipeline.Models;

public sealed class IssueComment
{
    public required string Id { get; init; }
    public required string Body { get; init; }
    public required string Author { get; init; }
    public required DateTime CreatedAt { get; init; }
}
