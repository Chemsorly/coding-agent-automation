using System.Text.Json;
using CodingAgent.Contracts;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Redacts brain review detail (lines, checks, flaky tests, commit) from run summaries returned to
/// project-scoped users. Global readonly users and above see the full outcome (spec 054, 8A.2, DD13, req 12.5).
/// </summary>
internal static class BrainReviewVisibility
{
    /// <summary>
    /// Returns the run unchanged when there is no outcome or the caller has global read access.
    /// Otherwise returns a copy with the detail fields cleared but the aggregate counts preserved.
    /// Never mutates the input (T19).
    /// </summary>
    public static PipelineRunSummary Apply(PipelineRunSummary run, IUserAccess access)
    {
        if (run.BrainConsolidationOutcome is null || access.Grant.GlobalRole >= AccessRole.ReadOnly)
            return run;

        // Clone via JSON round-trip to avoid mutating the in-memory store (T19).
        var clone = JsonSerializer.Deserialize<PipelineRunSummary>(
            JsonSerializer.SerializeToUtf8Bytes(run, PipelineJsonOptions.Default),
            PipelineJsonOptions.Default)!;

        var o = clone.BrainConsolidationOutcome!;
        clone.BrainConsolidationOutcome = o with
        {
            Lines = [],
            LinesTruncated = false,
            Checks = [],
            FlakyTests = [],
            PushedCommitSha = null,
            PushedCommitUrl = null,
        };
        return clone;
    }

    /// <summary>
    /// Applies <see cref="Apply(PipelineRunSummary, IUserAccess)"/> to every item in the page.
    /// Returns the same <paramref name="page"/> instance when no item needs redacting.
    /// </summary>
    public static PagedResult<PipelineRunSummary> Apply(PagedResult<PipelineRunSummary> page, IUserAccess access)
    {
        if (access.Grant.GlobalRole >= AccessRole.ReadOnly)
            return page;

        var redacted = page.Items.Select(r => Apply(r, access)).ToList();
        return new PagedResult<PipelineRunSummary>
        {
            Items = redacted,
            Page = page.Page,
            PageSize = page.PageSize,
            HasMore = page.HasMore,
        };
    }
}
