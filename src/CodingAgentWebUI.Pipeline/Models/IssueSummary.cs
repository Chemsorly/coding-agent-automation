namespace CodingAgentWebUI.Pipeline.Models;

public sealed class IssueSummary : IHasCreatedAt
{
    public required string Identifier { get; init; }  // e.g., "123"
    public required string Title { get; init; }
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>
    /// Hex colour values (without leading '#') for each label, keyed by label name.
    /// Only populated by providers that expose colour data (e.g. GitHub). Null for providers that don't.
    /// </summary>
    public IReadOnlyDictionary<string, string>? LabelColors { get; init; }

    /// <summary>Issue creation date, used for FIFO ordering in the pipeline loop.</summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>Issue body text, used for dependency reference parsing. Null when not populated.</summary>
    public string? Description { get; init; }

    /// <summary>Web URL of the issue on the provider (GitHub HtmlUrl / GitLab WebUrl), or null if unknown.</summary>
    public string? Url { get; init; }
}
