using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// The hand-off points the API side has for a review's author flag, in one test: the request that was dispatched,
/// the allow-listed payload stored with the work item, the JSON round trip through <c>WorkItems.Payload</c>, and the
/// assignment built at claim time. Every hop copies the flag by hand, and a hop that is lost reads false without an
/// error. The flag is false by default, so the <c>true</c> row is the one that detects a lost hop.
/// </summary>
public sealed class ReviewAuthorFlagFlowTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnverifiedAndVerifiedPr_ReachTheAssignment(bool value)
    {
        var dispatched = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("5"),
            IssueProviderConfigId = "repo-prov-1",
            RepoProviderConfigId = "repo-prov-1",
            InitiatedBy = InitiatedByConstants.LoopReview,
            TaskType = WorkItemTaskType.Review,
            RunType = PipelineRunType.Review,
            AgentSelector = "dotnet",
            AgentProviderConfigId = "profile-1",
            TimeoutSeconds = 3600,
            LinkedPullRequest = new LinkedPullRequest
            {
                Url = "https://example.com/pr/5",
                BranchName = "feature/pages",
                IsDraft = false,
                Number = 5
            },
            ReviewPrTargetBranch = "main",
            ReviewPrDescription = "Pages the API",
            ReviewPrAuthor = "alice",
            ReviewPrAuthorVerified = value
        };

        var minimal = WorkItemEntityBuilder.BuildMinimalPayload(dispatched);
        var stored = JsonSerializer.Serialize(minimal, PipelineJsonOptions.Default);
        WorkItemPayload.TryDeserialize(stored, out var claimed).Should().BeTrue();
        // The claim hands the stored request to the factory after enrichment; the agent profile is set by the route there.
        var assignment = JobAssignmentMessageFactory.BuildJobAssignmentMessage(
            Guid.NewGuid(), claimed! with { AgentProviderConfigId = "profile-1" });

        assignment.ReviewPrAuthorVerified.Should().Be(value);
        assignment.ReviewPrAuthor.Should().Be("alice");
    }
}
