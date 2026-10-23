using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="PipelineRunExtensions.IsConsolidation"/>.
/// </summary>
public class PipelineRunIsConsolidationTests
{
    private static PipelineRun CreateRun(string issueProviderConfigId, PipelineRunType runType = PipelineRunType.Implementation) =>
        PipelineRun.CreateForRunType(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = "rp-1",
            RunType = runType
        });

    [Fact]
    public void WhenIssueProviderConfigIdIsConsolidationSentinel_IsConsolidationReturnsTrue()
    {
        var run = CreateRun(ConsolidationConstants.ProviderConfigId);

        run.IsConsolidation().Should().BeTrue();
    }

    [Fact]
    public void WhenIssueProviderConfigIdIsRegularProviderId_IsConsolidationReturnsFalse()
    {
        var run = CreateRun("github-123");

        run.IsConsolidation().Should().BeFalse();
    }

    [Fact]
    public void WhenRunTypeIsConsolidationButIssueProviderConfigIdIsNotSentinel_IsConsolidationReturnsFalse()
    {
        // RunType is NOT the authoritative signal for consolidation detection at runtime.
        // Only IssueProviderConfigId == ConsolidationConstants.ProviderConfigId is authoritative.
        // A run may reach this state if created outside the normal factory path; routing it
        // through IsConsolidation() must produce false so it does not get handled as a
        // consolidation run (e.g. routed to ConsolidationJobCompletionStrategy).
        var run = CreateRun("real-provider", PipelineRunType.Consolidation);

        run.IsConsolidation().Should().BeFalse();
    }
}
