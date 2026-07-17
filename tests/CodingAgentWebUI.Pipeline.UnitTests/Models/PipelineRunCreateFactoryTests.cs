using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class PipelineRunCreateFactoryTests
{
    [Fact]
    public void Create_SetsRequiredProperties()
    {
        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "org/repo#1",
            issueTitle: "Fix bug",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        run.RunId.Should().Be("r1");
        run.IssueIdentifier.Should().Be("org/repo#1");
        run.IssueTitle.Should().Be("Fix bug");
        run.IssueProviderConfigId.Should().Be("ip-1");
        run.RepoProviderConfigId.Should().Be("rp-1");
    }

    [Fact]
    public void Create_SetsInvariantDefaults()
    {
        var before = DateTimeOffset.UtcNow;
        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "org/repo#1",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");
        var after = DateTimeOffset.UtcNow;

        run.CurrentStep.Should().Be(PipelineStep.Created);
        run.StartedAtOffset.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        run.LastStepChangeAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        run.InitiatedBy.Should().Be("manual");
        run.RunType.Should().Be(PipelineRunType.Implementation);
    }

    [Fact]
    public void Create_StartedAtTimestampConsistency()
    {
        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");

#pragma warning disable CS0618
        run.StartedAt.Should().Be(run.StartedAtOffset.UtcDateTime);
#pragma warning restore CS0618
    }

    [Fact]
    // TODO: Add assertion that LastStepChangeAt != startedAt (remains independently set to UtcNow) to guard against regression if factory changes to `LastStepChangeAt = now`.
    public void Create_WithExplicitStartedAt_UsesProvidedTimestamp()
    {
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            startedAt: timestamp);

        run.StartedAtOffset.Should().Be(timestamp);
#pragma warning disable CS0618
        run.StartedAt.Should().Be(timestamp.UtcDateTime);
#pragma warning restore CS0618
    }

    [Fact]
    public void ResetStartedAt_UpdatesBothProperties()
    {
        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");

        var dispatchTime = new DateTimeOffset(2026, 7, 17, 14, 58, 38, TimeSpan.Zero);
        run.ResetStartedAt(dispatchTime);

        run.StartedAtOffset.Should().Be(dispatchTime);
#pragma warning disable CS0618
        run.StartedAt.Should().Be(dispatchTime.UtcDateTime);
#pragma warning restore CS0618
    }

    [Fact]
    public void ResetStartedAt_DurationReflectsActualWorkTime()
    {
        // Simulate: run created (enqueued) at 06:45, dispatched at 14:58, completed at 16:29
        var enqueueTime = new DateTimeOffset(2026, 7, 17, 6, 45, 0, TimeSpan.Zero);
        var dispatchTime = new DateTimeOffset(2026, 7, 17, 14, 58, 0, TimeSpan.Zero);
        var completeTime = new DateTimeOffset(2026, 7, 17, 16, 29, 0, TimeSpan.Zero);

        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            startedAt: enqueueTime);

        // After dispatch, reset StartedAt to actual dispatch time
        run.ResetStartedAt(dispatchTime);
        run.MarkCompleted(completeTime);

        // Duration should be ~91 minutes (actual work), not ~584 minutes (queue-inclusive)
        var duration = run.CompletedAtOffset!.Value - run.StartedAtOffset;
        duration.TotalMinutes.Should().BeApproximately(91, 1);
    }

    [Fact]
    public void Create_PassesThroughInitOnlyProperties()
    {
        var contexts = new List<LinkedIssueContext>
        {
            new() { Identifier = "#2", Title = "Related", Description = "desc" }
        };

        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            runType: PipelineRunType.Review,
            initiatedBy: "loop",
            agentId: "agent-1",
            agentProviderConfigId: "ap-1",
            brainProviderConfigId: "bp-1",
            reviewPrBranchName: "feature/x",
            reviewPrTargetBranch: "main",
            reviewPrUrl: "https://github.com/org/repo/pull/1",
            reviewPrDescription: "PR desc",
            reviewPrAuthor: "user1",
            linkedIssueContexts: contexts,
            decompositionSource: "project-level");

        run.RunType.Should().Be(PipelineRunType.Review);
        run.InitiatedBy.Should().Be("loop");
        run.AgentId.Should().Be("agent-1");
        run.AgentProviderConfigId.Should().Be("ap-1");
        run.BrainProviderConfigId.Should().Be("bp-1");
        run.ReviewPrBranchName.Should().Be("feature/x");
        run.ReviewPrTargetBranch.Should().Be("main");
        run.ReviewPrUrl.Should().Be("https://github.com/org/repo/pull/1");
        run.ReviewPrDescription.Should().Be("PR desc");
        run.ReviewPrAuthor.Should().Be("user1");
        run.LinkedIssueContexts.Should().BeSameAs(contexts);
        run.DecompositionSource.Should().Be("project-level");
    }

    [Fact]
    public void Create_NullableInitProperties_DefaultToNull()
    {
        var run = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");

        run.AgentId.Should().BeNull();
        run.AgentProviderConfigId.Should().BeNull();
        run.BrainProviderConfigId.Should().BeNull();
        run.ReviewPrBranchName.Should().BeNull();
        run.ReviewPrTargetBranch.Should().BeNull();
        run.ReviewPrUrl.Should().BeNull();
        run.ReviewPrDescription.Should().BeNull();
        run.ReviewPrAuthor.Should().BeNull();
        run.LinkedIssueContexts.Should().BeNull();
        run.DecompositionSource.Should().BeNull();
    }
}
