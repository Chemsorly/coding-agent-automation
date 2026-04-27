namespace CodingAgentWebUI.Pipeline.Models;

public sealed class IssueSummary
{
    public required string Identifier { get; init; }  // e.g., "123"
    public required string Title { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>Issue creation date, used for FIFO ordering in the pipeline loop.</summary>
    public DateTime? CreatedAt { get; init; }
}
