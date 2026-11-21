using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.CodeReview.Models;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Runs a PR review through the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> in-process, with the fake
/// providers injected as in <see cref="RealAgentWorkerSmokeTests"/>, so <c>ReviewCodeStep</c> and
/// <c>PostReviewFindingsStep</c> run end to end. <see cref="RealWorkerReviewSmokeTests"/> proves that one finding
/// becomes one inline comment; these tests cover what the step does around it: the diff-hunk filter, the cost line,
/// the body-only fallbacks, the dismissal of the stale review, the structured-output retry and the skip comment.
///
/// <para>
/// The reviewers run one after another, because <see cref="ScriptedAgentProvider.SupportsParallelExecution"/> is
/// false, so the scripts are consumed in the order the reviewers are configured. After the reviewers come the
/// acceptance criteria check (on by default), then the review summary, and only then the follow-up prompt that
/// <c>PostReviewFindingsStep</c> sends for a finding without a file and line. The acceptance criteria check and the
/// summary run on the empty queue where no script is needed; where a later call is scripted, each of them must be
/// given a script, or it takes the next one.
/// </para>
///
/// <para>
/// Each test uses its own pull request number. The PR carries <c>agent:next</c> and the claim moves it to
/// <c>agent:in-progress</c> and, when the run completes, to <c>agent:done</c>.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerPrReviewTests : HeadlessE2ETestBase
{
    private const string AlphaFinding = "[CRITICAL] src/Foo.cs:11 — null dereference";
    private const string BetaFinding = "[WARNING] src/Foo.cs:99 — outside the diff";
    private const string FollowUpPromptStart = "Your previous review output did not include file:line references";
    private const string SkipReason = "No enabled reviewer configuration matches this repository's labels. Review skipped.";

    private static readonly string[] AgentNextLabel = ["agent:next"];

    // Line 10 is context, line 11 is the added line and line 12 is context: only line 11 can carry a comment.
    // Context lines start with a space, so the lines are joined instead of written as one indented literal.
    private static readonly string FooDiff = string.Join("\n",
        "diff --git a/src/Foo.cs b/src/Foo.cs",
        "--- a/src/Foo.cs",
        "+++ b/src/Foo.cs",
        "@@ -10,2 +10,3 @@",
        " context",
        "+added",
        " context",
        "");

    public RealWorkerPrReviewTests(E2EFixture fixture) : base(fixture) { }

    // ── Tests ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two reviewers report a finding each, at lines 11 and 99 of <c>src/Foo.cs</c>, and the first one writes the diff
    /// of the pull request. Only line 11 lies in the diff, so one inline comment is kept. The CRITICAL and WARNING
    /// findings request changes, and the body names both reviewers and carries the cost of both calls.
    /// </summary>
    [Fact]
    public async Task WhenTwoReviewersReportFindings_ShouldPostInlineCommentsOnDiffLinesAndCostLine()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const int prNumber = 6701;

        // The cost counts only when the result reports token usage as well.
        var usage = new TokenUsage { InputTokens = 1000, OutputTokens = 200 };
        Fixture.AgentProvider
            .Enqueue(new WriteFindingsScript("alpha", AlphaFinding, FooDiff) { Usage = usage, Cost = 0.10m })
            .Enqueue(new WriteFindingsScript("beta", BetaFinding) { Usage = usage, Cost = 0.10m });

        var agentId = NewAgentId();
        var workItemId = await SeedAndClaimAsync(prNumber, agentId, "alpha", "beta");

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        await AssertPrLabelIsDoneAsync(prNumber);

        var review = Fixture.RepositoryProvider.PostedReviews.Should().ContainSingle().Subject;
        review.Type.Should().Be(PullRequestReviewType.RequestChanges);
        review.Body.Should().Contain(CommentMarkers.PrReview);
        review.Body.Should().Contain("**Review Agents**: alpha, beta");
        review.Body.Should().Contain("| [CRITICAL] | 1 |");
        review.Body.Should().Contain("| [WARNING] | 1 |");
        review.Body.Should().Contain($"*Cost: {CostFormatter.FormatCost(0.20m)}*");

        var comments = KeptComments();
        comments.Should().ContainSingle("line 99 is outside the diff hunk and is dropped");
        comments[0].Path.Should().Be("src/Foo.cs");
        comments[0].Line.Should().Be(11);
        comments[0].Body.Should().Contain("null dereference");
    }

    /// <summary>
    /// With no diff file the comments are kept, so the submission is an inline one. It throws, and the step posts
    /// the review again without comments, still asking for changes.
    /// </summary>
    [Fact]
    public async Task WhenInlineSubmissionThrows_ShouldStillPostBodyOnlyReview()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const int prNumber = 6702;

        EnqueueAlphaAndBeta();
        Fixture.RepositoryProvider.InlineSubmissionError = new InvalidOperationException("scripted inline failure");

        var agentId = NewAgentId();
        var workItemId = await SeedAndClaimAsync(prNumber, agentId, "alpha", "beta");

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        await AssertPrLabelIsDoneAsync(prNumber);

        // The fake records an inline submission only when it succeeds, so none means the throw happened.
        Fixture.RepositoryProvider.ReviewSubmissions.Should().BeEmpty("the inline submission threw and was not recorded");

        var review = Fixture.RepositoryProvider.PostedReviews.Should().ContainSingle().Subject;
        review.Type.Should().Be(PullRequestReviewType.RequestChanges);
        review.Body.Should().Contain("null dereference");
    }

    /// <summary>
    /// Inline comments are switched off, so the review is posted without comments. GitHub refuses a request for
    /// changes on the author's own pull request; the step posts the review again as a plain comment.
    /// </summary>
    [Fact]
    public async Task WhenReviewingOwnPullRequest_ShouldDowngradeToCommentReview()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const int prNumber = 6703;

        // The worker reads the configuration once, when it fetches its assignment, so the change comes first.
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { CodeReview = c.CodeReview with { InlineComments = c.CodeReview.InlineComments with { Enabled = false } } },
            CancellationToken.None);

        EnqueueAlphaAndBeta();
        Fixture.RepositoryProvider.BodyOnlyRequestChangesError =
            new InvalidOperationException("Can not request changes on your own pull request");

        var agentId = NewAgentId();
        var workItemId = await SeedAndClaimAsync(prNumber, agentId, "alpha", "beta");

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        // The body table and the review type both come from the run's critical and warning counts, so the table
        // shows that the first attempt was a RequestChanges one, which the fake refused before recording it.
        var review = Fixture.RepositoryProvider.PostedReviews.Should().ContainSingle().Subject;
        review.Type.Should().Be(PullRequestReviewType.Comment);
        review.Body.Should().Contain("| [CRITICAL] | 1 |");
        review.Body.Should().Contain("| [WARNING] | 1 |");
    }

    /// <summary>
    /// A provider with native inline comments gets the previous automated review dismissed before the new one is
    /// posted, instead of the collapse of the old comment that other providers get.
    /// </summary>
    [Fact]
    public async Task WhenInlineSupported_ShouldDismissStaleReviewAndPostNewOne()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const int prNumber = 6704;

        EnqueueAlphaAndBeta();

        var agentId = NewAgentId();
        var workItemId = await SeedAndClaimAsync(prNumber, agentId, "alpha", "beta");

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        var dismissed = Fixture.RepositoryProvider.DismissedReviews.Should().ContainSingle().Subject;
        dismissed.PrNumber.Should().Be(prNumber);
        dismissed.Marker.Should().Be(CommentMarkers.PrReview);
        dismissed.Reason.Should().Be("Superseded by a newer automated review.");

        Fixture.RepositoryProvider.PostedReviews.Should().ContainSingle();
    }

    /// <summary>
    /// No reviewer configuration exists, so no agent runs. The review step records why, and the pull request gets a
    /// plain comment with that reason instead of a review.
    /// </summary>
    [Fact]
    public async Task WhenNoReviewerMatches_ShouldPostSkipCommentWithoutRunningAgents()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const int prNumber = 6705;

        var agentId = NewAgentId();
        var workItemId = await SeedAndClaimAsync(prNumber, agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        await AssertPrLabelIsDoneAsync(prNumber);

        Fixture.AgentProvider.Calls.Should().BeEmpty("without a reviewer the review step returns before any agent call");

        var review = Fixture.RepositoryProvider.PostedReviews.Should().ContainSingle().Subject;
        review.Type.Should().Be(PullRequestReviewType.Comment);
        review.Body.Should().Contain(CommentMarkers.PrReview);
        review.Body.Should().Contain(SkipReason);
    }

    /// <summary>
    /// The only reviewer reports a WARNING without a file and line. The step sends it one follow-up prompt asking for
    /// the structured format, and the answer becomes the inline comment.
    ///
    /// <para>
    /// The scripts are consumed in call order: the reviewer, the acceptance criteria check, the review summary and
    /// then the follow-up. The two calls in the middle write nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenFindingsHaveNoLocation_ShouldRetryForStructuredOutputAndPostInlineComment()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const int prNumber = 6706;

        Fixture.AgentProvider
            .Enqueue(new WriteFindingsScript("alpha", "[WARNING] something is off somewhere"))
            .EnqueueCodeGen() // the acceptance criteria check
            .EnqueueCodeGen() // the review summary
            .Enqueue(new CodeGenScript { OutputLines = ["[WARNING] src/Bar.cs:3 — something is off"] }); // the follow-up

        var agentId = NewAgentId();
        var workItemId = await SeedAndClaimAsync(prNumber, agentId, "alpha");

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        Fixture.AgentProvider.Calls.Count(c => c.Request.Prompt.Contains(FollowUpPromptStart)).Should().Be(1);

        var comments = KeptComments();
        comments.Should().ContainSingle();
        comments[0].Path.Should().Be("src/Bar.cs");
        comments[0].Line.Should().Be(3);

        Fixture.RepositoryProvider.PostedReviews.Should().ContainSingle();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static string NewAgentId() => $"real-review-{Guid.NewGuid():N}";

    /// <summary>
    /// Alpha reports a CRITICAL and beta a WARNING finding, both in <c>src/Foo.cs</c>. No diff is written, so the
    /// step keeps both comments. The acceptance criteria check and the summary that follow run on the empty queue.
    /// </summary>
    private void EnqueueAlphaAndBeta() =>
        Fixture.AgentProvider
            .Enqueue(new WriteFindingsScript("alpha", AlphaFinding))
            .Enqueue(new WriteFindingsScript("beta", BetaFinding));

    /// <summary>
    /// Seeds pull request <paramref name="prNumber"/> with <c>agent:next</c> on both sides, as the real provider has
    /// it (the harness models the pull request and its issue as two fakes), a template, and one reviewer
    /// configuration with <paramref name="reviewerNames"/> in that order, then claims the review for
    /// <paramref name="agentId"/>. Empty match labels match every template; no names seeds no reviewer. Returns the
    /// work item GUID.
    /// </summary>
    private async Task<Guid> SeedAndClaimAsync(int prNumber, string agentId, params string[] reviewerNames)
    {
        Fixture.RepositoryProvider.SupportsInlineReviewComments = true;

        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = prNumber.ToString(),
            Title = "Review PR",
            Description = "",
            Labels = AgentNextLabel,
            BranchName = "feature/auto-42-review",
            TargetBranch = "main",
            Url = $"https://github.com/e2e-org/e2e-repo/pull/{prNumber}",
            IsDraft = false
        });

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = prNumber.ToString(),
            Title = "Review PR",
            Description = "",
            Labels = AgentNextLabel
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "review-template",
            Name = "Review Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        if (reviewerNames.Length > 0)
        {
            await Fixture.ConfigStore.SaveReviewerConfigAsync(new ReviewerConfiguration
            {
                DisplayName = "E2E Reviewers",
                Agents = reviewerNames.Select(name => new ReviewAgent { Name = name, Prompt = "Review." }).ToList()
            }, CancellationToken.None);
        }

        return await Fixture.ClaimReviewWorkItemAsync(prNumber, agentId);
    }

    /// <summary>Runs the real worker for <paramref name="workItemId"/> until it stops.</summary>
    private async Task RunWorkerAsync(string agentId, Guid workItemId)
    {
        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// The <c>agent:</c> labels of pull request <paramref name="prNumber"/>. The list is copied first: the hub thread
    /// replaces its entries while the state is polled, and an enumeration that overlaps a write can throw.
    /// </summary>
    private List<string> PrAgentLabels(int prNumber) =>
        Fixture.RepositoryProvider.PullRequests.ToArray().Single(p => p.Number == prNumber).Labels
            .Where(l => l.StartsWith("agent:", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// The pull request ends with <c>agent:done</c> alone: the run's own swaps removed <c>agent:next</c> and
    /// <c>agent:in-progress</c>. The state is polled, as the swap is the last thing the hub does for the run.
    /// </summary>
    private async Task AssertPrLabelIsDoneAsync(int prNumber)
    {
        await WaitUntilAsync(
            () => PrAgentLabels(prNumber).SequenceEqual([AgentLabels.Done]),
            TimeSpan.FromSeconds(10));

        PrAgentLabels(prNumber).Should().Equal(AgentLabels.Done);
    }

    /// <summary>The inline comments of the one submission that went through the inline-capable overload.</summary>
    private List<ReviewComment> KeptComments()
    {
        var submission = Fixture.RepositoryProvider.ReviewSubmissions.Should().ContainSingle().Subject;
        return submission.Submission.Comments.ToList();
    }

    /// <summary>
    /// Writes the findings file of one review agent, as the review prompt asks, and the diff of the pull request when
    /// <paramref name="fullDiff"/> is given.
    /// </summary>
    private sealed class WriteFindingsScript(string agentName, string findings, string? fullDiff = null) : AgentScript
    {
        public override void Execute(string workspacePath)
        {
            var path = Path.Combine(workspacePath, AgentWorkspacePaths.GetReviewFindingsFilePath(agentName));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, findings);
            if (fullDiff is not null)
                File.WriteAllText(Path.Combine(workspacePath, AgentWorkspacePaths.FullDiffFilePath), fullDiff);
        }
    }
}
