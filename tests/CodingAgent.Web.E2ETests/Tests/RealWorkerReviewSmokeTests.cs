using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Smoke test that runs a PR review through the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>
/// in-process, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// The repository fake opts into native inline review comments
/// (<see cref="InMemoryRepositoryProvider.SupportsInlineReviewComments"/>) and keeps every submission with its
/// comments, so the test can assert what <c>PostReviewFindingsStep</c> posted to the pull request.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerReviewSmokeTests : HeadlessE2ETestBase
{
    private const int PrNumber = 5201;

    public RealWorkerReviewSmokeTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// One reviewer reports a WARNING with a file and line. The review step dismisses the previous automated review,
    /// anchors one inline comment to the head commit and requests changes, and the work item succeeds.
    /// </summary>
    [Fact]
    public async Task WhenReviewerReportsFindingWithLocation_ShouldSubmitInlineCommentAndRequestChanges()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        Fixture.RepositoryProvider.SupportsInlineReviewComments = true;

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = PrNumber,
            Identifier = PrNumber.ToString(),
            Title = "Review PR",
            Description = "",
            Labels = [],
            BranchName = "feature/auto-42-review",
            TargetBranch = "main",
            Url = $"https://github.com/e2e-org/e2e-repo/pull/{PrNumber}",
            IsDraft = false
        });

        // A pull request is also an issue under the same number on GitHub; the harness models them as two fakes.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = PrNumber.ToString(),
            Title = "Review PR",
            Description = "",
            Labels = []
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "review-template",
            Name = "Review Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // The store has no reviewers after a reset. Empty match labels match every template.
        await Fixture.ConfigStore.SaveReviewerConfigAsync(new ReviewerConfiguration
        {
            DisplayName = "E2E Reviewers",
            Agents = [new ReviewAgent { Name = "Security", Prompt = "Review." }]
        }, CancellationToken.None);

        // The scripts are consumed in call order: the reviewer first. The next call is the acceptance criteria check,
        // which is on by default and runs after the reviewers while they run one after another; it takes the code
        // generation script and writes no report. The review summary then finds an empty queue and is skipped.
        Fixture.AgentProvider
            .Enqueue(new AgentReviewFindingsScript
            {
                AgentName = "Security",
                Findings = "[WARNING] src/Foo.cs:12 — Null check missing\n"
            })
            .EnqueueCodeGen();

        var agentId = $"real-review-{Guid.NewGuid():N}";
        var workItemId = await Fixture.ClaimReviewWorkItemAsync(PrNumber, agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        var submissions = Fixture.RepositoryProvider.ReviewSubmissions;
        submissions.Should().ContainSingle("the review step submits once through the inline-capable overload");
        var (prNumber, submission) = submissions[0];
        prNumber.Should().Be(PrNumber);
        submission.Type.Should().Be(PullRequestReviewType.RequestChanges, "a WARNING finding requests changes");
        submission.Comments.Should().ContainSingle();
        submission.Comments[0].Path.Should().Be("src/Foo.cs");
        submission.Comments[0].Line.Should().Be(12);
        submission.Comments[0].Body.Should().Contain("Null check missing");
        submission.Body.Should().Contain(CommentMarkers.PrReview);
        submission.CommitId.Should().Be("abc123def456");

        Fixture.RepositoryProvider.DismissedReviews.Should().ContainSingle();
        var (dismissedPr, marker, _) = Fixture.RepositoryProvider.DismissedReviews[0];
        dismissedPr.Should().Be(PrNumber);
        marker.Should().Be(CommentMarkers.PrReview);
    }
}
