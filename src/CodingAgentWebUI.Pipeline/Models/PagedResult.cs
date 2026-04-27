namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents a paginated result set with forward-navigation support.
/// </summary>
/// <typeparam name="T">The type of items in the result set.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Items on the current page.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>The 1-based page number.</summary>
    public required int Page { get; init; }

    /// <summary>The requested page size.</summary>
    public required int PageSize { get; init; }

    /// <summary>Whether more items exist beyond this page.</summary>
    public required bool HasMore { get; init; }
}
