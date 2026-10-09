using System.Net.Http.Json;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests that pin the "exactly one correct agent label" contract for every terminal
/// pipeline outcome, verified in production reporting order (HTTP POST first, then SignalR hub).
///
/// <para>
/// Each test dispatches one run, completes it via
/// <see cref="FakeAgentClient.CompleteLikeProductionAsync"/> (the same two-channel sequence used
/// by real agent pods), and asserts that:
/// <list type="bullet">
///   <item>Exactly one <c>agent:*</c> terminal label is present after completion (net set from
///   the audit log, plus a confirmatory check on the materialized <see cref="IssueDetail.Labels"/>
///   or <c>PullRequestSummary.Labels</c>).</item>
///   <item>That label is the expected one for the given payload.</item>
/// </list>
/// </para>
///
/// <para>
/// The canonical label-gap bug (#3009) went undetected for weeks because most tests completed
/// runs over the hub alone, not via the production HTTP+hub sequence. This class pins each
/// outcome end-to-end to prevent regressions.
/// </para>
///
/// <para>
/// <b>Label channels summary:</b>
/// <list type="bullet">
///   <item>Succeeded, WontDo — label applied by the <b>hub</b> path
///   (<c>HandleJobCompletedAsync → SwapLabelAndPostCommentAsync</c>). Mandatory timing anchor:
///   <c>WaitForHistoryAsync</c>.</item>
///   <item>Failed (any variant) — label applied by the <b>HTTP</b> path
///   (<c>WorkItemStatusTransitionService → FailRunWithLabelAsync</c>).</item>
///   <item>Cancelled — label applied by <c>CancelRunAsync</c> inside the HTTP path's
///   <c>TransitionAsync</c>; hub path also applies it via <c>SwapLabelAndPostCommentAsync</c> when
///   it arrives second.</item>
/// </list>
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "TerminalLabelOutcomes")]
[Collection(E2ECollection.Name)]
public sealed class TerminalLabelOutcomeTests : HeadlessE2ETestBase
{
    // ── Terminal label set ────────────────────────────────────────────────

    /// <summary>All <c>agent:*</c> labels that may only appear once at the end of a run.</summary>
    private static readonly string[] TerminalLabels =
    [
        AgentLabels.Done,
        AgentLabels.Error,
        AgentLabels.NeedsRefinement,
        AgentLabels.WontDo,
        AgentLabels.Cancelled
    ];

    // ── Test selector ─────────────────────────────────────────────────────

    /// <summary>
    /// Agent profile match label used by this test class.  Must correspond to a
    /// <c>labels:</c> entry in <see cref="E2ETestDefaults.InstallJobTemplates"/>.
    /// </summary>
    private const string AgentSelector = "db-e2e";

    public TerminalLabelOutcomeTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared seed helper ────────────────────────────────────────────────

    /// <summary>
    /// Seeds the issue, job template, and agent profile required for an implementation
    /// dispatch.  Mirrors the pattern established in
    /// <c>CompleteLikeProductionTests.SeedTestDataAsync</c>.
    /// </summary>
    private async Task SeedImplementationIssueAsync(
        string issueId,
        string issueTitle = "Terminal label outcome test")
    {
        // TODO [WARNING]: Each call adds a new PipelineJobTemplate and AgentProfile keyed by issueId
        // to the shared fixture's config store. These accumulate across theory rows in the same
        // [Collection] because there is no cleanup after each row. While dispatch uses MatchLabels
        // (not "exactly N profiles exist"), this unbounded growth will complicate debugging if a
        // future test relies on profile-count invariants. Verify that InMemoryConfigurationStore.Reset()
        // (called by HeadlessE2ETestBase.InitializeAsync) clears all templates and profiles seeded
        // mid-test, or add explicit cleanup here. (Reported by DotNetSpecialist, Correctness review.)
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = issueTitle,
            Description = "## Requirements\nVerify terminal label.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = ["enhancement", "agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = $"template-terminal-{issueId}",
            Name = "Terminal Label E2E Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = $"profile-terminal-{issueId}",
            DisplayName = "Terminal Label E2E Agent Profile",
            MatchLabels = [AgentSelector],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    // ── Assertion helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Primary assertion: replays <c>LabelChanges</c> in order to compute the net terminal-label
    /// set for <paramref name="issueId"/>, then asserts exactly one terminal label remains and
    /// it equals <paramref name="expectedLabel"/>.
    ///
    /// Immune to seed artefacts — seed labels that are removed before completion do not appear
    /// in the net set.
    /// </summary>
    private void AssertExactlyOneTerminalLabelAdded(string issueId, string expectedLabel)
    {
        // TODO [WARNING]: This assertion counts net adds only (add minus remove). It passes when
        // the correct label is present and not subsequently removed, but it would also pass for a
        // double-swap (add→remove→add, net count = 1) if a wrong label happened to be added and
        // then fully removed. A production bug that adds the terminal label twice and removes it
        // once (leaving net count = 1) would be invisible here. The secondary
        // AssertMaterializedTerminalLabel partially guards this, but neither method detects a
        // spurious intermediate removal. Consider also asserting that the correct label was added
        // exactly once in the raw LabelChanges sequence if this gap becomes a concern.
        // (Reported by TestQualityReviewer.)
        var net = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, label, added) in Fixture.IssueProvider.LabelChanges)
        {
            if (id != issueId || !TerminalLabels.Contains(label)) continue;
            if (added) net.Add(label); else net.Remove(label);
        }

        Assert.True(net.Count == 1,
            $"Expected exactly 1 terminal label on issue {issueId}, got [{string.Join(", ", net)}]. " +
            $"Full LabelChanges: [{string.Join(", ", Fixture.IssueProvider.LabelChanges.Select(lc => $"{(lc.Added ? "+" : "-")}{lc.Label}"))}]");
        Assert.Equal(expectedLabel, net.Single());
    }

    /// <summary>
    /// Secondary assertion: checks the materialized <c>IssueDetail.Labels</c> (which
    /// <see cref="Fakes.InMemoryIssueProvider"/> keeps in sync via element replacement on every
    /// add/remove) to confirm the net state matches the audit log.
    /// </summary>
    private void AssertMaterializedTerminalLabel(string issueId, string expectedLabel)
    {
        var issue = Fixture.IssueProvider.Issues.Single(i => i.Identifier == issueId);
        var terminal = issue.Labels.Intersect(TerminalLabels).ToList();
        Assert.True(terminal.Count == 1,
            $"Expected exactly 1 terminal label in materialized Issues[].Labels for {issueId}, " +
            $"got [{string.Join(", ", terminal)}]. All labels: [{string.Join(", ", issue.Labels)}]");
        Assert.Equal(expectedLabel, terminal[0]);
    }

    /// <summary>
    /// Primary assertion for PR-review runs: replays <c>PrLabelChanges</c> in order to compute
    /// the net terminal-label set for PR <paramref name="prNumber"/>, then asserts exactly one
    /// terminal label remains and it equals <paramref name="expectedLabel"/>.
    ///
    /// Uses the same net-replay approach as <see cref="AssertExactlyOneTerminalLabelAdded"/> to
    /// handle remove-then-add sequences correctly.
    /// </summary>
    private void AssertExactlyOneTerminalPrLabelAdded(int prNumber, string expectedLabel)
    {
        var net = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (action, number, label) in Fixture.RepositoryProvider.PrLabelChanges)
        {
            if (number != prNumber || !TerminalLabels.Contains(label)) continue;
            if (action == "Add") net.Add(label); else net.Remove(label);
        }

        Assert.True(net.Count == 1,
            $"Expected exactly 1 terminal label on PR {prNumber}, got [{string.Join(", ", net)}]. " +
            $"Full PrLabelChanges: [{string.Join(", ", Fixture.RepositoryProvider.PrLabelChanges.Select(c => $"{c.Action}:{c.Label}"))}]");
        Assert.Equal(expectedLabel, net.Single());

        // Secondary: materialized state
        var pr = Fixture.RepositoryProvider.PullRequests.Single(p => p.Number == prNumber);
        var terminal = pr.Labels.Intersect(TerminalLabels).ToList();
        Assert.True(terminal.Count == 1,
            $"Expected exactly 1 terminal label in materialized PullRequests.Labels for PR {prNumber}, " +
            $"got [{string.Join(", ", terminal)}]. All labels: [{string.Join(", ", pr.Labels)}]");
        Assert.Equal(expectedLabel, terminal[0]);
    }

    // ── MemberData providers ──────────────────────────────────────────────

    /// <summary>
    /// The six table rows from the issue, as <c>[Theory, MemberData]</c> inputs.
    /// Each entry: (issueId, issueTitle, payload, expectedLabel).
    /// </summary>
    public static TheoryData<string, string, JobCompletionPayload, string> ImplementationRows()
    {
        return new TheoryData<string, string, JobCompletionPayload, string>
        {
            // R1 — Completed, PR created → agent:done
            {
                "3089-r1-done",
                "R1: Completed with PR → agent:done",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/1",
                    RetryCount = 0,
                    FilesChangedCount = 3,
                    LinesAdded = 50,
                    LinesRemoved = 10,
                    BrainUpdatesPushed = false,
                    AnalysisRecommendation = AnalysisGateResult.Ready,
                    AnalysisConcerns = [],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.Done
            },
            // R2 — Completed with FinalLabel=agent:wont-do → agent:wont-do (hub-path only)
            {
                "3089-r2-wontdo",
                "R2: Completed WontDo → agent:wont-do",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Completed,
                    FinalLabel = AgentLabels.WontDo,
                    CompletedAt = DateTimeOffset.UtcNow,
                    PullRequestUrl = null,
                    RetryCount = 0,
                    FilesChangedCount = 0,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                    BrainUpdatesPushed = false,
                    AnalysisRecommendation = AnalysisGateResult.WontDo,
                    AnalysisConcerns = ["Issue is out of scope"],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.WontDo
            },
            // R3 — Failed, NotReady, FinalLabel=agent:needs-refinement → agent:needs-refinement
            {
                "3089-r3-nr",
                "R3: Failed NotReady → agent:needs-refinement",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Failed,
                    FinalLabel = AgentLabels.NeedsRefinement,
                    FailureReason = "Analysis gate: issue lacks acceptance criteria",
                    FailureCategory = FailureReason.AgentError,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RetryCount = 0,
                    FilesChangedCount = 0,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                    BrainUpdatesPushed = false,
                    AnalysisRecommendation = AnalysisGateResult.NotReady,
                    AnalysisConcerns = ["Acceptance criteria missing"],
                    AnalysisBlockingIssues = ["No clear success condition"],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.NeedsRefinement
            },
            // R4 — Failed, quality gates exhausted, IsDraftPr=true → agent:error
            // NOTE: R4 and R5 both expect agent:error but represent distinct code paths.
            // R4 is the draft-PR path (quality gates exhausted, IsDraftPr=true, FinalLabel=null —
            // the error label is resolved by ResolveFailedFinalLabel from IsDraftPr).
            // R5 is any other agent error (IsDraftPr=false, FinalLabel=null — resolved by default).
            // Keep them as separate rows to pin that IsDraftPr=true does not accidentally route
            // to a label other than agent:error (e.g. a misrouted agent:needs-refinement).
            // TODO [WARNING]: No row covers IsDraftPr=true with a non-null FinalLabel to verify
            // that IsDraftPr does not override an explicit FinalLabel. If the routing logic
            // changes, this gap could hide a regression. (Reported by TestQualityReviewer.)
            {
                "3089-r4-draft-error",
                "R4: Failed DraftPr exhausted → agent:error",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Failed,
                    FinalLabel = null,
                    FailureReason = "Quality gates exhausted after max retries",
                    FailureCategory = FailureReason.QualityGateExhausted,
                    IsDraftPr = true,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RetryCount = 3,
                    FilesChangedCount = 5,
                    LinesAdded = 100,
                    LinesRemoved = 20,
                    BrainUpdatesPushed = false,
                    AnalysisRecommendation = AnalysisGateResult.Ready,
                    AnalysisConcerns = [],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.Error
            },
            // R5 — Failed, other error, no FinalLabel → agent:error
            {
                "3089-r5-error",
                "R5: Failed other error → agent:error",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Failed,
                    FinalLabel = null,
                    FailureReason = "Unexpected error during code generation",
                    FailureCategory = FailureReason.AgentError,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RetryCount = 0,
                    FilesChangedCount = 0,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                    BrainUpdatesPushed = false,
                    AnalysisConcerns = [],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.Error
            },
            // R6 — Cancelled → agent:cancelled
            {
                "3089-r6-cancelled",
                "R6: Cancelled → agent:cancelled",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RetryCount = 0,
                    FilesChangedCount = 0,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                    BrainUpdatesPushed = false,
                    AnalysisConcerns = [],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.Cancelled
            }
        };
    }

    /// <summary>
    /// PR review rows: Completed, Failed, Cancelled.
    /// Each entry: (issueId, prNumber, prTitle, payload, expectedLabel).
    /// </summary>
    public static TheoryData<string, int, string, JobCompletionPayload, string> ReviewRows()
    {
        return new TheoryData<string, int, string, JobCompletionPayload, string>
        {
            // Rev1 — Review Completed → agent:done on the PR
            {
                "99",
                99,
                "Rev1: PR review completed → agent:done",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Completed,
                    CompletedAt = DateTimeOffset.UtcNow,
                    PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/99",
                    RetryCount = 0,
                    FilesChangedCount = 0,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                    BrainUpdatesPushed = false,
                    AnalysisConcerns = [],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.Done
            },
            // Rev2 — Review Failed → agent:error on the PR
            {
                "88",
                88,
                "Rev2: PR review failed → agent:error",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Failed,
                    FailureReason = "Code review agent crashed",
                    FailureCategory = FailureReason.AgentError,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RetryCount = 0,
                    FilesChangedCount = 0,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                    BrainUpdatesPushed = false,
                    AnalysisConcerns = [],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.Error
            },
            // Rev3 — Review Cancelled → agent:cancelled on the PR
            {
                "77",
                77,
                "Rev3: PR review cancelled → agent:cancelled",
                new JobCompletionPayload
                {
                    FinalStep = PipelineStep.Cancelled,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RetryCount = 0,
                    FilesChangedCount = 0,
                    LinesAdded = 0,
                    LinesRemoved = 0,
                    BrainUpdatesPushed = false,
                    AnalysisConcerns = [],
                    AnalysisBlockingIssues = [],
                    BlacklistedFilesDetected = [],
                    CodeReviewAgentsRun = []
                },
                AgentLabels.Cancelled
            }
        };
    }

    // ── Implementation run table (main six rows) ──────────────────────────

    /// <summary>
    /// Verifies each of the six issue-table rows from the issue:
    /// Completed-Done, Completed-WontDo, Failed-NR, Failed-Error (draft), Failed-Error, Cancelled.
    ///
    /// <para>
    /// <b>Timing:</b> <c>WaitForHistoryAsync</c> is used as the universal anchor — it is
    /// mandatory for hub-path labels (Done, WontDo) and redundant-but-safe for HTTP-path labels
    /// (Error, NeedsRefinement, Cancelled).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ImplementationRows))]
    public async Task ImplementationRun_TerminalLabel_ExactlyOne_CorrectLabel(
        string issueId,
        string issueTitle,
        JobCompletionPayload payload,
        string expectedLabel)
    {
        // Arrange
        await SeedImplementationIssueAsync(issueId, issueTitle);

        await using var agent = new FakeAgentClient($"agent-{issueId}", AgentSelector);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var dispatchResult = await DispatchIssueAsync(issueId);
        Assert.True(dispatchResult.Success,
            $"Dispatch failed for {issueId}: {dispatchResult.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(issueId, assignment.IssueIdentifier);

        // AcceptJobAsync must precede CompleteLikeProductionAsync so AuthorizeAgentForWorkItemAsync
        // can verify AssignedAgentId matches the posting agent.
        await agent.AcceptJobAsync(assignment.JobId);

        // Act — complete via HTTP-then-hub (production ordering)
        await agent.CompleteLikeProductionAsync(assignment.JobId, payload);

        // WaitForHistoryAsync is the mandatory timing anchor for hub-path labels (Done, WontDo);
        // it is safe for all rows because history is written before SwapLabelAsync.
        await WaitForHistoryAsync(
            r => r.IssueIdentifier == issueId && r.FinalStep == payload.FinalStep,
            TimeSpan.FromSeconds(15));

        // Assert — primary: audit log replay
        AssertExactlyOneTerminalLabelAdded(issueId, expectedLabel);

        // Assert — secondary: materialized state
        AssertMaterializedTerminalLabel(issueId, expectedLabel);
    }

    // ── PR review runs ────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that review-run outcomes (Completed, Failed, Cancelled) each produce exactly one
    /// terminal label on the PR (via <c>InMemoryRepositoryProvider.PrLabelChanges</c>) rather than
    /// on the issue.
    ///
    /// <para>
    /// PR review dispatch uses <see cref="IDispatchOrchestrationService.PrepareReviewDistributionRequestAsync"/>.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ReviewRows))]
    public async Task ReviewRun_TerminalPrLabel_ExactlyOne_CorrectLabel(
        string prIdentifier,
        int prNumber,
        string prTitle,
        JobCompletionPayload payload,
        string expectedLabel)
    {
        // Arrange — seed the PR in the repository provider and also as an issue
        // (dispatch preparation calls GetIssueAsync for the PR identifier on GitHub where
        // every PR is also an issue; the fake requires explicit seeding on both sides).
        // TODO [WARNING]: PR is seeded with Labels = ["agent:next"]. The secondary assertion in
        // AssertExactlyOneTerminalPrLabelAdded checks that materialized pr.Labels contains exactly
        // one terminal label. If the production label-swap path for PRs does not issue a
        // RemovePrLabel call for "agent:next" before (or as part of) adding the terminal label,
        // pr.Labels after completion would contain both "agent:next" and the terminal label. The
        // net audit-log assertion (PrLabelChanges replay) would still pass, but the materialized
        // secondary assertion would fail. Verify that InMemoryRepositoryProvider.SwapPrLabelAsync
        // (or the production equivalent) removes "agent:next" as part of the swap, or seed the PR
        // without any pre-existing agent:* label to avoid the ambiguity. (Reported by Correctness review.)
        Fixture.RepositoryProvider.PullRequests.Add(new PullRequestSummary
        {
            Number = prNumber,
            Identifier = prIdentifier,
            Title = prTitle,
            Description = $"PR review E2E test — {prTitle}",
            Labels = ["agent:next"],
            BranchName = $"feature/test-{prNumber}",
            TargetBranch = "main",
            Url = $"https://github.com/e2e-org/e2e-repo/pull/{prNumber}",
            IsDraft = false
        });

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = prIdentifier,
            Title = prTitle,
            Description = $"PR review E2E test — {prTitle}",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = $"template-review-{prNumber}",
            Name = "Review E2E Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = $"profile-review-{prNumber}",
            DisplayName = "Review E2E Agent Profile",
            MatchLabels = [AgentSelector],
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        await using var agent = new FakeAgentClient($"review-agent-{prNumber}", AgentSelector);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        // Dispatch as a review run
        var orchService = Fixture.Factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(WellKnownIds.DefaultProjectId, CancellationToken.None)
            ?? throw new InvalidOperationException("Default project not found");

        var pr = Fixture.RepositoryProvider.PullRequests.Single(p => p.Number == prNumber);
        var reviewRequest = new ReviewDispatchRequest
        {
            PrIdentifier = pr.Identifier,
            PrNumber = pr.Number,
            PrBranchName = pr.BranchName,
            PrTitle = pr.Title,
            PrDescription = pr.Description,
            PrAuthor = null,
            PrUrl = pr.Url,
            PrTargetBranch = pr.TargetBranch,
            IssueProviderId = new ProviderConfigId("issue-e2e"),
            RepoProviderId = new ProviderConfigId("repo-e2e"),
            BrainProviderId = null,
            InitiatedBy = "e2e-test"
        };

        var distributionRequest = await orchService.PrepareReviewDistributionRequestAsync(reviewRequest, project);
        Assert.NotNull(distributionRequest);

        var distResult = await distributor.DistributeAsync(distributionRequest, CancellationToken.None);
        Assert.True(distResult.Success, $"Review dispatch failed for PR {prNumber}: {distResult.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(prIdentifier, assignment.IssueIdentifier);
        Assert.Equal(PipelineRunType.Review, assignment.RunType);

        await agent.AcceptJobAsync(assignment.JobId);

        // Act — complete via HTTP-then-hub (production ordering)
        await agent.CompleteLikeProductionAsync(assignment.JobId, payload);

        await WaitForHistoryAsync(
            r => r.IssueIdentifier == prIdentifier && r.FinalStep == payload.FinalStep,
            TimeSpan.FromSeconds(15));

        // Assert — PR label (not issue label) must contain exactly one terminal label
        AssertExactlyOneTerminalPrLabelAdded(prNumber, expectedLabel);
    }

    // ── HubThenHttp replay ────────────────────────────────────────────────

    /// <summary>
    /// HH1: Hub fires first for a Succeeded run, then HTTP arrives.
    ///
    /// <para>
    /// The hub applies <c>agent:done</c>. The subsequent HTTP POST finds the WorkItem already in
    /// <c>Succeeded</c> state; <c>TransitionAsync</c>'s pre-read guard returns
    /// <c>AlreadyAtTarget</c> for <c>Succeeded</c> and exits without calling any lifecycle method.
    /// Final state must be identical to the <c>HttpThenHub</c> result: exactly <c>agent:done</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HubThenHttp_Succeeded_LabelIsDone_SameAsHttpThenHub()
    {
        const string issueId = "3089-hh1-done";
        await SeedImplementationIssueAsync(issueId, "HH1: HubThenHttp Succeeded → agent:done");

        await using var agent = new FakeAgentClient($"agent-{issueId}", AgentSelector);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var dispatchResult = await DispatchIssueAsync(issueId);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await agent.AcceptJobAsync(assignment.JobId);

        // Act — hub first, then HTTP
        await agent.CompleteLikeProductionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            PullRequestUrl = "https://github.com/e2e-org/e2e-repo/pull/2",
            RetryCount = 0,
            FilesChangedCount = 2,
            LinesAdded = 30,
            LinesRemoved = 5,
            BrainUpdatesPushed = false,
            AnalysisConcerns = [],
            AnalysisBlockingIssues = [],
            BlacklistedFilesDetected = [],
            CodeReviewAgentsRun = []
        }, order: CompletionOrder.HubThenHttp);

        await WaitForHistoryAsync(
            r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Completed,
            TimeSpan.FromSeconds(15));

        AssertExactlyOneTerminalLabelAdded(issueId, AgentLabels.Done);
        AssertMaterializedTerminalLabel(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// HH2: Hub fires first for a needs-refinement run, then HTTP arrives.
    ///
    /// <para>
    /// When the hub fires first with <c>FinalStep=Failed, FinalLabel=agent:needs-refinement</c>:
    /// the hub path reads <c>FinalLabel</c> from the payload, sees it is in <c>AgentLabels.All</c>,
    /// and applies it directly via <c>SwapLabelAsync</c>. The run is still alive at this point
    /// (<c>skipLabelSwap=false</c>), so <c>agent:needs-refinement</c> is applied.
    /// The HTTP POST then arrives; <c>TransitionDetailedAsync(Failed→Failed)</c> is rejected
    /// and <c>FailRunWithLabelAsync</c> does NOT run.
    /// </para>
    ///
    /// <para>
    /// Net result: <b>both <c>HttpThenHub</c> and <c>HubThenHttp</c> produce
    /// <c>agent:needs-refinement</c></b> because the hub path reads <c>FinalLabel</c> from the
    /// SignalR payload directly. The production two-channel sequence is therefore symmetric for
    /// this outcome.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HubThenHttp_NeedsRefinement_LabelIsNeedsRefinement_SameAsHttpThenHub()
    {
        const string issueId = "3089-hh2-nr";
        await SeedImplementationIssueAsync(issueId, "HH2: HubThenHttp NeedsRefinement → agent:needs-refinement");

        await using var agent = new FakeAgentClient($"agent-{issueId}", AgentSelector);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var dispatchResult = await DispatchIssueAsync(issueId);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await agent.AcceptJobAsync(assignment.JobId);

        // Act — hub fires first (reports Failed with FinalLabel=NeedsRefinement), then HTTP arrives.
        // The hub applies agent:needs-refinement (FinalLabel is in AgentLabels.All, run is still
        // alive so skipLabelSwap=false). The HTTP POST then finds Failed→Failed rejected and exits.
        await agent.CompleteLikeProductionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FinalLabel = AgentLabels.NeedsRefinement,
            FailureReason = "Analysis gate: issue needs more detail",
            FailureCategory = FailureReason.AgentError,
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisRecommendation = AnalysisGateResult.NotReady,
            AnalysisConcerns = ["Acceptance criteria missing"],
            AnalysisBlockingIssues = [],
            BlacklistedFilesDetected = [],
            CodeReviewAgentsRun = []
        }, order: CompletionOrder.HubThenHttp);

        await WaitForHistoryAsync(
            r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Failed,
            TimeSpan.FromSeconds(15));

        // Both HubThenHttp and HttpThenHub produce agent:needs-refinement:
        // - HttpThenHub: HTTP applies NeedsRefinement via FailRunWithLabelAsync; hub sees
        //   skipLabelSwap=true and does not overwrite.
        // - HubThenHttp: hub applies NeedsRefinement from FinalLabel (run still alive);
        //   HTTP arrives and finds Failed→Failed rejected, FailRunWithLabelAsync never runs.
        AssertExactlyOneTerminalLabelAdded(issueId, AgentLabels.NeedsRefinement);
        AssertMaterializedTerminalLabel(issueId, AgentLabels.NeedsRefinement);
    }

    // ── Timeout-race scenario ─────────────────────────────────────────────

    /// <summary>
    /// Timeout race: JobController (operator key) posts HTTP <c>Failed</c> for the work item
    /// while the agent simultaneously reports <c>Cancelled</c> over the hub.
    ///
    /// <para>
    /// <b>Operator HTTP path:</b> <c>AuthorizeAgentForWorkItemAsync</c> passes operator-
    /// authenticated callers (those without <c>auth_kind == "agent"</c>) through without agent-
    /// binding validation. <see cref="E2EFixture.CreateApiClient()"/> returns such a client
    /// (Bearer = master <c>AGENT_API_KEY</c>).
    /// </para>
    ///
    /// <para>
    /// The race winner is non-deterministic, so the assertion verifies the invariant rather than
    /// a specific label: exactly one terminal <c>agent:*</c> label must be present after both
    /// tasks complete, and it must match the <c>WorkItem.Status</c> persisted in the database.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TimeoutRace_OperatorFailed_AgentCancelled_ExactlyOneTerminalLabel()
    {
        const string issueId = "3089-race";
        await SeedImplementationIssueAsync(issueId, "Race: operator Failed vs agent Cancelled");

        await using var agent = new FakeAgentClient($"agent-{issueId}", AgentSelector);
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var dispatchResult = await DispatchIssueAsync(issueId);
        Assert.True(dispatchResult.Success, $"Dispatch failed: {dispatchResult.ErrorMessage}");

        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(issueId, assignment.IssueIdentifier);

        var workItemId = Guid.Parse(assignment.JobId);
        await agent.AcceptJobAsync(assignment.JobId);

        // Operator client — authenticated with the master AGENT_API_KEY, which bypasses agent
        // binding in AuthorizeAgentForWorkItemAsync.
        using var operatorClient = Fixture.CreateApiClient();

        var failedUpdate = new WorkItemStatusUpdate
        {
            Status = "Failed",
            AgentId = agent.AgentId,
            ErrorMessage = "Timeout: job controller deadline exceeded",
            FailureReason = FailureReason.Timeout.ToString()
        };

        var cancelledPayload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            FilesChangedCount = 0,
            LinesAdded = 0,
            LinesRemoved = 0,
            BrainUpdatesPushed = false,
            AnalysisConcerns = [],
            AnalysisBlockingIssues = [],
            BlacklistedFilesDetected = [],
            CodeReviewAgentsRun = []
        };

        // Act — interleave the two paths; the race winner is non-deterministic
        // TODO [WARNING]: The agent side uses agent.ReportCompletionAsync (hub-only), not
        // CompleteLikeProductionAsync. In production the agent always sends an HTTP POST first
        // then fires the hub. This test therefore models "operator-HTTP vs agent-hub-only" rather
        // than the actual production race of "operator-HTTP vs agent-HTTP+hub". The actual
        // two-channel race is untested and could have a label gap not caught here.
        // (Reported by Correctness review and TestQualityReviewer.)
        var httpTask = Task.Run(() =>
            operatorClient.PostAsJsonAsync(
                $"/api/work-items/{workItemId}/status",
                failedUpdate,
                PipelineJsonOptions.Default));

        // TODO [WARNING]: httpTask is Task<HttpResponseMessage>; PostAsJsonAsync does not throw on
        // non-2xx responses. Task.WhenAll completes without inspecting httpTask.Result.IsSuccessStatusCode.
        // If the HTTP POST is rejected (409/404/403 because the work item already reached a terminal
        // state), the test silently treats it as a success. Capture and log (or assert) the HTTP
        // status after Task.WhenAll to make the race's observable outcome explicit.
        // (Reported by DotNetSpecialist.)
        var hubTask = Task.Run(() =>
            agent.ReportCompletionAsync(assignment.JobId, cancelledPayload));

        await Task.WhenAll(httpTask, hubTask);

        // Wait for the race to settle in history. Require a terminal FinalStep (Failed or
        // Cancelled) so that a non-terminal history entry written during dispatch or acceptance
        // cannot satisfy the predicate and cause assertions to run before the winning path's
        // terminal cleanup — including the label swap — has completed.
        // Fix for [CRITICAL] TestQualityReviewer:819 / Correctness:697 / DotNetSpecialist:730.
        // TODO [WARNING]: The agent side uses agent.ReportCompletionAsync (hub-only), not
        // CompleteLikeProductionAsync. In production the agent always sends an HTTP POST first
        // then fires the hub. This test therefore models "operator-HTTP vs agent-hub-only" rather
        // than the actual production race of "operator-HTTP vs agent-HTTP+hub". The actual
        // two-channel race is untested and could have a label gap not caught here.
        // (Reported by Correctness review and TestQualityReviewer.)
        await WaitForHistoryAsync(
            r => r.IssueIdentifier == issueId &&
                 (r.FinalStep == PipelineStep.Failed || r.FinalStep == PipelineStep.Cancelled),
            TimeSpan.FromSeconds(15));

        // Assert invariant: exactly one terminal label, regardless of race winner
        // TODO [WARNING]: This assertion can observe a false pass when the wrong terminal step
        // wins the race and the label matches by coincidence (e.g. Failed wins, label is
        // agent:error, test passes — but only because the HTTP POST was never processed, not
        // because both paths actually raced). Without asserting httpTask.Result.IsSuccessStatusCode,
        // the test cannot distinguish a genuine two-path race from a single-path completion.
        // (Reported by TestQualityReviewer.)
        var net = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, label, added) in Fixture.IssueProvider.LabelChanges)
        {
            if (id != issueId || !TerminalLabels.Contains(label)) continue;
            if (added) net.Add(label); else net.Remove(label);
        }

        Assert.True(net.Count == 1,
            $"Expected exactly 1 terminal label after race, got [{string.Join(", ", net)}]. " +
            $"Full LabelChanges: [{string.Join(", ", Fixture.IssueProvider.LabelChanges.Select(lc => $"{(lc.Added ? "+" : "-")}{lc.Label}"))}]");

        var actualLabel = net.Single();

        // TODO [WARNING]: The label net-replay logic above (HashSet add/remove over LabelChanges)
        // is duplicated inline rather than calling the shared AssertExactlyOneTerminalLabelAdded
        // helper. If the helper is updated (e.g. to add the raw-sequence count check in its own
        // TODO), this copy will silently diverge. Consider refactoring to use the helper.
        // (Reported by TestQualityReviewer.)

        // Poll until the WorkItem row reaches a terminal status before reading it for the
        // expected-label derivation. Without polling, WorkItem.Status may still be Running or
        // Dispatched at assertion time (the service's SaveChangesAsync for WorkItems fires after
        // the history write), causing the switch to throw InvalidOperationException with a
        // misleading error rather than a meaningful assertion failure.
        // Fix for [CRITICAL] TestQualityReviewer:836 / DotNetSpecialist:745.
        // TODO [WARNING]: httpTask is Task<HttpResponseMessage>; PostAsJsonAsync does not throw on
        // non-2xx responses. Task.WhenAll completes without inspecting httpTask.Result.IsSuccessStatusCode.
        // If the HTTP POST is rejected (409/404/403 because the work item already reached a terminal
        // state), the test silently treats it as a success. Capture and log (or assert) the HTTP
        // status after Task.WhenAll to make the race's observable outcome explicit.
        // (Reported by DotNetSpecialist.)
        // TODO [WARNING]: WaitForWorkItemByIdUntilTerminalAsync queries by ID only and checks
        // status in-memory, which avoids the EF Core untranslatable-predicate issue. The
        // 15-second timeout is independent and starts fresh after WaitForHistoryAsync completes;
        // a shorter timeout (e.g. 5 seconds) would give a more informative diagnostic on
        // pathological WorkItem write delays. (Reported by TestQualityReviewer.)
        // Use WaitForWorkItemStatusAsync-style polling (query by ID only, check status in-memory)
        // to avoid EF Core's inability to translate a Func<> delegate predicate to SQL.
        // The race winner is non-deterministic (Failed or Cancelled), so we check both statuses
        // after retrieval rather than passing a compound predicate to FirstOrDefaultAsync.
        var workItem = await WaitForWorkItemByIdUntilTerminalAsync(workItemId, timeout: TimeSpan.FromSeconds(15));

        var expectedFromStatus = workItem.Status switch
        {
            WorkItemStatus.Failed => AgentLabels.Error,
            WorkItemStatus.Cancelled => AgentLabels.Cancelled,
            _ => throw new InvalidOperationException(
                $"Unexpected WorkItem status after race: {workItem.Status}")
        };

        Assert.Equal(expectedFromStatus, actualLabel);

        // Also verify the materialized state agrees
        AssertMaterializedTerminalLabel(issueId, expectedFromStatus);
    }
}
