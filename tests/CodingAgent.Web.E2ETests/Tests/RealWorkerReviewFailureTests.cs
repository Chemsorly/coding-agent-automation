using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Reviewer crashes in a PR review run on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process
/// against the E2E API host, with the fake providers injected as in <see cref="RealWorkerReviewSmokeTests"/>.
///
/// <para>
/// A reviewer crashes when it exits non-zero without writing its findings file; <c>EnqueueCodeGen(exitCode: 4)</c> is
/// that shape. When every reviewer crashed, nothing was reviewed: the run must end Failed with <c>agent:error</c> on the
/// pull request and post no review, because a posted "No issues found" reads as a clean review. When at least one
/// reviewer succeeded, the run behaves as before.
/// </para>
///
/// <para>
/// The scripts are consumed in call order, and the fake agent provider runs the reviewers one after another, so the
/// first script belongs to <c>alpha</c> and the second to <c>beta</c>. A call without a script exits 0, which counts as a
/// clean reviewer.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerReviewFailureTests : HeadlessE2ETestBase
{
    private const string FirstReviewer = "alpha";
    private const string SecondReviewer = "beta";

    public RealWorkerReviewFailureTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// Both reviewers exit 4 without a findings file. The run fails with <c>agent:error</c> on the pull request, posts no
    /// review, never sets <c>agent:done</c>, and makes no review summary call after the two reviewers.
    /// </summary>
    [Fact]
    public async Task WhenEveryReviewerCrashes_ReviewRunShouldFailWithErrorLabelAndPostNoReview()
    {
        const int prNumber = 6901;
        await SeedReviewAsync(prNumber);

        Fixture.AgentProvider
            .EnqueueCodeGen(exitCode: 4)
            .EnqueueCodeGen(exitCode: 4);

        var workItemId = await RunReviewWorkerToCompletionAsync(prNumber);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        await WaitForHistoryAsync(r => r.IssueIdentifier == prNumber.ToString() && r.FinalStep == PipelineStep.Failed);

        entity.ErrorMessage.Should().Be("All review agents failed; no review was posted.");
        entity.FailureReason.Should().Be(FailureReason.AgentError);

        (await WaitForTerminalPrLabelAsync(prNumber)).Should().Equal(AgentLabels.Error);

        Fixture.RepositoryProvider.PostedReviews.ToArray().Should().BeEmpty("nothing was reviewed, so nothing is posted");
        Fixture.RepositoryProvider.PrLabelChanges.ToArray().Should().NotContain(("Add", prNumber, AgentLabels.Done));

        // The two reviewers only: no acceptance criteria check and no review summary ran after them.
        var calls = Fixture.AgentProvider.Calls;
        calls.Should().HaveCount(2);
        calls[0].Request.Prompt.Should().Contain(AgentWorkspacePaths.GetReviewFindingsFilePath(FirstReviewer));
        calls[1].Request.Prompt.Should().Contain(AgentWorkspacePaths.GetReviewFindingsFilePath(SecondReviewer));
    }

    /// <summary>
    /// The first reviewer exits 4 without a findings file and the second writes a WARNING. Only an all-crash run fails:
    /// this one still completes, sets <c>agent:done</c> and posts the review with the second reviewer's finding.
    /// </summary>
    [Fact]
    public async Task WhenOneReviewerCrashes_ReviewRunShouldStillCompleteAndPostReview()
    {
        const int prNumber = 6902;
        await SeedReviewAsync(prNumber);

        // The call after the reviewers is the acceptance criteria check, which is on by default; it takes the code
        // generation script and writes no report. The review summary then finds an empty queue and is skipped.
        Fixture.AgentProvider
            .EnqueueCodeGen(exitCode: 4)
            .Enqueue(new AgentReviewFindingsScript
            {
                AgentName = SecondReviewer,
                Findings = "[WARNING] src/Foo.cs:1 — naming\n"
            })
            .EnqueueCodeGen();

        var workItemId = await RunReviewWorkerToCompletionAsync(prNumber);

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        (await WaitForTerminalPrLabelAsync(prNumber)).Should().Equal(AgentLabels.Done);

        // The provider does not offer inline comments, so the review is a body-only submission that carries the finding.
        var posted = Fixture.RepositoryProvider.PostedReviews.ToArray();
        posted.Should().ContainSingle();
        posted[0].Body.Should().Contain("naming");
    }

    /// <summary>
    /// Seeds a pull request and its issue side with <c>agent:next</c>, the template its worker needs to be assigned, and
    /// one reviewer configuration with two agents that matches every template.
    /// </summary>
    private async Task SeedReviewAsync(int prNumber)
    {
        var identifier = prNumber.ToString();

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = identifier,
            Title = "Review PR",
            Description = "",
            Labels = [AgentLabels.Next],
            BranchName = "feature/auto-42-review",
            TargetBranch = "main",
            Url = $"https://github.com/e2e-org/e2e-repo/pull/{prNumber}",
            IsDraft = false
        });

        // A pull request is also an issue under the same number on GitHub; the harness models them as two fakes.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = identifier,
            Title = "Review PR",
            Description = "",
            Labels = [AgentLabels.Next]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = $"review-failure-template-{prNumber}",
            Name = "Review Failure Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // The store has no reviewers after a reset. Empty match labels match every template.
        await Fixture.ConfigStore.SaveReviewerConfigAsync(new ReviewerConfiguration
        {
            DisplayName = "E2E Reviewers",
            Agents =
            [
                new ReviewAgent { Name = FirstReviewer, Prompt = "Review." },
                new ReviewAgent { Name = SecondReviewer, Prompt = "Review." }
            ]
        }, CancellationToken.None);
    }

    /// <summary>Claims the review work item for the real worker and runs it until its host stops.</summary>
    private async Task<Guid> RunReviewWorkerToCompletionAsync(int prNumber)
    {
        var agentId = $"real-review-failure-{Guid.NewGuid():N}";
        var workItemId = await Fixture.ClaimReviewWorkItemAsync(prNumber, agentId);

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
        return workItemId;
    }

    // The API and hub threads swap labels by replacing list elements while the label wait polls. ToArray copies without
    // enumerating, so the poll cannot fail with "Collection was modified" (a LINQ read of the live list can).
    private IReadOnlyList<string> AgentLabelsOnPr(int prNumber) =>
        Fixture.RepositoryProvider.PullRequests.ToArray().Single(pr => pr.Number == prNumber)
            .Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Waits until the label swap has landed: a terminal label is on the pull request and neither <c>agent:next</c> nor
    /// <c>agent:in-progress</c> is left (a swap adds the new label before it removes the old). It does not wait for a
    /// particular label, so a wrong label fails the caller's assertion with the labels found, not with a timeout. The
    /// label lags the work item's status on both outcomes: the API's HTTP handler writes the Failed label, and the hub
    /// writes the Done label after the agent's status POST.
    /// </summary>
    private async Task<IReadOnlyList<string>> WaitForTerminalPrLabelAsync(int prNumber)
    {
        await WaitUntilAsync(() =>
        {
            var labels = AgentLabelsOnPr(prNumber);
            return labels.Any(l => AgentLabels.TerminalLabels.Contains(l))
                   && !labels.Contains(AgentLabels.Next)
                   && !labels.Contains(AgentLabels.InProgress);
        });
        return AgentLabelsOnPr(prNumber);
    }
}
