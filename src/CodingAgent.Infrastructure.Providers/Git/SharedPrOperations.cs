using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Infrastructure.Git;

/// <summary>
/// Static helpers shared by <c>GitHubRepositoryProvider</c> and <c>GitLabRepositoryProvider</c>
/// for PR/MR pagination, comment projection, bot/author detection, and dismiss-loop orchestration.
/// Centralises provider-agnostic logic so that Sonar CPD sees a single definition rather than
/// two identical copies.
/// </summary>
internal static class SharedPrOperations
{
    // ── Pagination ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates page and pageSize arguments for <c>ListOpenPullRequestsAsync</c>.
    /// Throws <see cref="ArgumentOutOfRangeException"/> on invalid values.
    /// </summary>
    internal static void ValidatePaginationArgs(int page, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 100);
    }

    /// <summary>
    /// Builds a <see cref="PagedResult{T}"/> from an overfetched list (pageSize+1 items requested).
    /// Trims to <paramref name="pageSize"/> items and sets <c>HasMore</c> accordingly.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="overfetchedItems">
    /// Items fetched using the overfetch-by-one pattern (up to pageSize+1 items).
    /// The list is trimmed to <paramref name="pageSize"/> before being returned.
    /// </param>
    /// <param name="page">The requested 1-based page number.</param>
    /// <param name="pageSize">The requested page size.</param>
    internal static PagedResult<T> BuildPagedResult<T>(List<T> overfetchedItems, int page, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(overfetchedItems);
        var hasMore = overfetchedItems.Count > pageSize;
        var items = overfetchedItems.Take(pageSize).ToList();
        return new PagedResult<T>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    /// <summary>
    /// Builds a <see cref="PagedResult{T}"/> from a pre-mapped items list and an externally
    /// computed <paramref name="hasMore"/> flag. Use this overload when the provider maps items
    /// separately from the overfetch detection (e.g. GitHub's multi-step fetch strategy where
    /// closed PRs may be filtered out after computing HasMore).
    /// </summary>
    internal static PagedResult<T> BuildPagedResult<T>(List<T> items, int page, int pageSize, bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PagedResult<T>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        };
    }

    // ── Bot / Author Detection ────────────────────────────────────────────────

    /// <summary>
    /// Determines whether a comment author username belongs to a bot account.
    /// Uses the canonical <c>[bot]</c>-suffix convention shared by GitHub and GitLab.
    /// This is strictly an <c>EndsWith("[bot]")</c> check — names that merely contain
    /// "bot" (e.g. "robotnik") are NOT flagged.
    /// </summary>
    // TODO [WARNING]: This helper uses EndsWith("[bot]") only. The original GitHub implementation
    // also checked AccountType.Bot (c.User?.Type == AccountType.Bot), covering GitHub App accounts
    // whose login name does not follow the [bot]-suffix convention (e.g. "copilot-for-prs",
    // "my-deploy-bot"). Those accounts now return IsBot = false. If AccountType.Bot detection is
    // needed for GitHub, the GitHub provider should OR in that condition before calling this helper,
    // or this helper should gain an optional isSystemBot parameter.
    // Similarly, the original GitLab implementation used Contains("bot", OrdinalIgnoreCase), which
    // also flagged usernames like "gitlab-bot" or "ci-bot" that do not carry the [bot] suffix.
    // Those accounts are now treated as non-bot by this shared helper.
    // TODO [WARNING]: This method has no explicit null guard on `author`. In practice all call sites
    // pass `... ?? string.Empty` so null never reaches here, but for defensive consistency and to
    // match the ArgumentNullException.ThrowIfNull guards used by other helpers in this class, either
    // add ArgumentNullException.ThrowIfNull(author) or annotate the parameter as non-nullable and
    // document the precondition explicitly.
    internal static bool IsBotAuthor(string author)
        => author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether a comment author is the same person as the PR author
    /// (case-insensitive username comparison).
    /// </summary>
    internal static bool IsCommentAuthor(string author, string prAuthor)
        => string.Equals(author, prAuthor, StringComparison.OrdinalIgnoreCase);

    // ── Comment Tails ─────────────────────────────────────────────────────────

    /// <summary>
    /// Finalizes a list of <see cref="PrConversationComment"/> items for
    /// <c>ListPullRequestCommentsAsync</c>: sorts by <c>CreatedAt</c> ascending.
    /// Does NOT apply a <c>Take</c> cap — <c>ListPullRequestCommentsAsync</c> returns
    /// all comments.
    /// </summary>
    internal static IReadOnlyList<PrConversationComment> FinalizeConversationComments(
        List<PrConversationComment> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.OrderBy(c => c.CreatedAt).ToList().AsReadOnly();
    }

    /// <summary>
    /// Finalizes a sequence of <see cref="PullRequestReviewComment"/> items for
    /// <c>GetAgentPullRequestsAsync</c>:
    /// <list type="number">
    ///   <item>Filters out pipeline-generated comments (checked via <see cref="CommentMarkers.IsPipelineGeneratedComment"/>).</item>
    ///   <item>Sorts by <c>CreatedAt</c> ascending.</item>
    ///   <item>Caps at 50 items.</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<PullRequestReviewComment> FinalizeReviewComments(
        IEnumerable<PullRequestReviewComment> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source
            .Where(c => !CommentMarkers.IsPipelineGeneratedComment(c.Body))
            .OrderBy(c => c.CreatedAt)
            .Take(50)
            .ToList();
    }

    // ── Dismiss Loop ──────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the dismiss/resolve loop that is identical in structure across the GitHub and GitLab
    /// providers. For each item in <paramref name="matchingItems"/>, invokes
    /// <paramref name="dismissItem"/>; if it throws a non-cancellation exception, logs a warning
    /// and continues with the remaining items.
    /// </summary>
    /// <typeparam name="TItem">Type of review/discussion item to dismiss.</typeparam>
    /// <param name="matchingItems">Pre-filtered list of items to dismiss (empty → no-op).</param>
    /// <param name="dismissItem">
    /// SDK-specific action to perform for each item. Receives the item and the
    /// <see cref="CancellationToken"/> so that cancellation propagates correctly even when
    /// the helper is exercised in isolation by tests.
    /// </param>
    /// <param name="getItemId">Extracts a human-readable ID string for log messages.</param>
    /// <param name="entityLabel">Label used in log messages (e.g. "review", "discussion thread").</param>
    /// <param name="prNumber">PR/MR number used in log messages.</param>
    /// <param name="ct">Cancellation token passed to <paramref name="dismissItem"/>.</param>
    internal static async Task RunDismissLoopAsync<TItem>(
        IReadOnlyList<TItem> matchingItems,
        Func<TItem, CancellationToken, Task> dismissItem,
        Func<TItem, string> getItemId,
        string entityLabel,
        int prNumber,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(matchingItems);
        ArgumentNullException.ThrowIfNull(dismissItem);
        ArgumentNullException.ThrowIfNull(getItemId);
        ArgumentNullException.ThrowIfNull(entityLabel);

        foreach (var item in matchingItems)
        {
            try
            {
                await dismissItem(item, ct);
            }
            // TODO [WARNING]: The continue-on-error branch (catch block) and the OperationCanceledException
            // propagation guarantee are not covered by any direct unit test. The GitLab integration test
            // DismissPreviousReviewAsync_IndividualResolveFailure_LogsAndContinues only exercises the happy
            // path (no failure injected). Add a unit test in SharedPrOperationsTests that:
            //   (a) passes a dismissItem lambda that throws a non-OperationCanceledException on the first
            //       item and verifies the second item is still processed (continue-on-error), and
            //   (b) passes a dismissItem lambda that throws OperationCanceledException and verifies it
            //       propagates out of RunDismissLoopAsync without being swallowed.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(
                    ex,
                    "Failed to dismiss {EntityLabel} {ItemId} on PR/MR #{PrNumber}. Continuing with remaining items.",
                    entityLabel, getItemId(item), prNumber);
            }
        }
    }
}
