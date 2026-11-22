using System.Diagnostics;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>Records how much of a run's input came from outside the repository.</summary>
internal static class UnverifiedInputTelemetry
{
    public const string CommentsTotalTag = "pipeline.comments.total";
    public const string CommentsUnverifiedTag = "pipeline.comments.unverified";
    public const string AuthorVerifiedTag = "pipeline.issue.author_verified";

    public static void RecordIssueContext(
        Activity? activity, ILogger logger, string runId, IssueDetail issue, IReadOnlyList<IssueComment> comments)
    {
        var unverified = comments.Count(c => c.IsVerified == false);
        activity?.SetTag(CommentsTotalTag, comments.Count);
        activity?.SetTag(CommentsUnverifiedTag, unverified);
        activity?.SetTag(AuthorVerifiedTag, issue.IsAuthorVerified);
        logger.Information(
            "Pipeline {RunId} issue context: {UnverifiedComments} unverified of {TotalComments} comments, author verified: {AuthorVerified}",
            runId, unverified, comments.Count, issue.IsAuthorVerified);
    }
}
