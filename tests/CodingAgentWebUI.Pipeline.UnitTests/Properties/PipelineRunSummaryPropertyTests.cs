using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for PipelineRunSummary mapping.
/// Feature: 009-pr-rework-pipeline
/// </summary>
public class PipelineRunSummaryPropertyTests
{
    /// <summary>
    /// Feature: 009-pr-rework-pipeline, Property 5: IsRework reflects LinkedPullRequest
    /// 
    /// For any PipelineRun instance, ToSummary().IsRework equals (LinkedPullRequest != null).
    /// 
    /// **Validates: Requirements REQ-11.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunArbitraries) })]
    public void ToSummary_IsRework_ReflectsLinkedPullRequest(PipelineRun run)
    {
        var summary = run.ToSummary();

        summary.IsRework.Should().Be(run.LinkedPullRequest != null);
    }

    // --- Custom Arbitraries for PipelineRun and LinkedPullRequest ---

    public class PipelineRunArbitraries
    {
        private static readonly string[] RunIds = { "run-1", "run-2", "run-3", "run-abc", "run-xyz" };
        private static readonly string[] IssueIds = { "42", "100", "7", "999", "1" };
        private static readonly string[] IssueTitles = { "Fix bug", "Add feature", "Refactor code", "Update docs", "Improve perf" };
        private static readonly string[] ConfigIds = { "config-1", "config-2", "config-3" };
        private static readonly string[] BranchNames = { "feature/auto-42-fix", "feature/auto-100-add", "feature/auto-7-ref" };
        private static readonly string[] PrUrls = { "https://github.com/org/repo/pull/1", "https://github.com/org/repo/pull/42", "https://github.com/org/repo/pull/100" };

        public static Arbitrary<PipelineRun> PipelineRunArb()
        {
            var linkedPrGen =
                from number in Gen.Choose(1, 10000)
                from branchName in Gen.Elements(BranchNames)
                from url in Gen.Elements(PrUrls)
                from isDraft in Gen.Elements(true, false)
                select new LinkedPullRequest
                {
                    Number = number,
                    BranchName = branchName,
                    Url = url,
                    IsDraft = isDraft
                };

            var runGen =
                from runId in Gen.Elements(RunIds)
                from issueId in Gen.Elements(IssueIds)
                from issueTitle in Gen.Elements(IssueTitles)
                from issueProviderConfigId in Gen.Elements(ConfigIds)
                from repoProviderConfigId in Gen.Elements(ConfigIds)
                from step in Gen.Elements(
                    PipelineStep.Created,
                    PipelineStep.CloningRepository,
                    PipelineStep.GeneratingCode,
                    PipelineStep.Completed,
                    PipelineStep.Failed)
                from hasLinkedPr in Gen.Elements(true, false)
                from linkedPr in hasLinkedPr
                    ? linkedPrGen.Select(pr => (LinkedPullRequest?)pr)
                    : Gen.Constant((LinkedPullRequest?)null)
                select new PipelineRun
                {
                    RunId = runId,
                    IssueIdentifier = issueId,
                    IssueTitle = issueTitle,
                    IssueProviderConfigId = issueProviderConfigId,
                    RepoProviderConfigId = repoProviderConfigId,
                    CurrentStep = step,
                    StartedAt = DateTime.UtcNow,
                    LinkedPullRequest = linkedPr
                };

            return runGen.ToArbitrary();
        }
    }
}
