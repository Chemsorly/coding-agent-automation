using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Cross-mode parity tests: verifies that core behavioral invariants hold across
/// BOTH Legacy (in-memory) and SignalR (DB) operational modes.
/// Each test method runs the same logical scenario against the DB-mode infrastructure
/// and proves the same observable outcome as the Legacy-mode happy path tests.
/// This catches "works in one mode, broken in the other" bugs.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "CrossModeParity")]
public sealed class CrossModeParityTests : DbModeE2ETestBase, IClassFixture<DbModeE2EFixture>
{
    public CrossModeParityTests(DbModeE2EFixture fixture) : base(fixture) { }

    private async Task SeedStandardTestDataAsync(string issueId, string title = "Parity test issue")
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nParity test\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = new[] { "enhancement", "agent:next" }
        });

        var templates = await Fixture.ConfigStore.LoadAllTemplatesAsync(CancellationToken.None);
        if (!templates.Any(t => t.Id == "template-parity"))
        {
            await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
            {
                Id = "template-parity",
                Name = "Parity Template",
                IssueProviderId = "issue-e2e",
                RepoProviderId = "repo-e2e",
                Enabled = true
            }, CancellationToken.None);
        }

        var profiles = await Fixture.ConfigStore.LoadAgentProfilesAsync(CancellationToken.None);
        if (!profiles.Any(p => p.Id == "profile-parity"))
        {
            await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
            {
                Id = "profile-parity",
                DisplayName = "Parity Profile",
                MatchLabels = new[] { "parity" },
                AgentProviderConfigId = "agent-e2e",
                Enabled = true
            }, CancellationToken.None);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parity A1: Dispatch → Agent completes → Run in history
    // Same outcome as Legacy HappyPathTests, but via DB mode
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parity_DispatchToCompletion_MatchesLegacyBehavior()
    {
        // Arrange (same seeding as Legacy happy path)
        await SeedStandardTestDataAsync("P100", "Cross-mode happy path");
        await using var agent = new FakeAgentClient("parity-agent-1", "parity");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // Act: dispatch
        var result = await DispatchIssueAsync("P100");
        Assert.True(result.Success);

        // Agent receives and completes
        var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("P100", job.IssueIdentifier);
        Assert.Equal("Cross-mode happy path", job.IssueDetail.Title);
        await agent.AcceptAndCompleteJobAsync(job.JobId);

        // Assert: same outcome as Legacy mode — run appears in history with Completed step
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "P100" && r.FinalStep == PipelineStep.Completed,
            TimeSpan.FromSeconds(15));
        Assert.NotNull(history);
        Assert.NotNull(history.PullRequestUrl);

        // Assert: DB-specific — WorkItem reached terminal state
        var workItemId = Guid.Parse(result.WorkItemId!);
        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        Assert.NotNull(item.CompletedAt);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parity A2: Dedup invariant — same issue dispatched twice → rejected
    // Must hold in BOTH modes
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parity_DedupInvariant_SameIssueRejected()
    {
        await SeedStandardTestDataAsync("P101");
        await using var agent = new FakeAgentClient("parity-dedup-agent", "parity");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        // First dispatch succeeds
        var r1 = await DispatchIssueAsync("P101");
        Assert.True(r1.Success);

        // Second dispatch while first is active → rejected
        var r2 = await DispatchIssueAsync("P101");
        Assert.False(r2.Success,
            "Dedup invariant: second dispatch for same active issue must fail in ALL modes");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parity A3: Cancellation semantics — same in all modes
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parity_Cancellation_AgentReportsCancelled_HistoryRecords()
    {
        await SeedStandardTestDataAsync("P102");
        await using var agent = new FakeAgentClient("parity-cancel-agent", "parity");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("P102");
        Assert.True(result.Success);

        var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Agent reports cancellation
        await agent.AcceptAndCompleteJobWithPayloadAsync(job.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow,
            FinalLabel = "agent:cancelled",
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        // Assert: history shows Cancelled (same invariant as Legacy mode)
        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "P102" && r.FinalStep == PipelineStep.Cancelled,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(history);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parity A4: Failure semantics — FailureReason propagated in all modes
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parity_FailureReason_PropagatedToHistory()
    {
        await SeedStandardTestDataAsync("P103");
        await using var agent = new FakeAgentClient("parity-fail-agent", "parity");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("P103");
        Assert.True(result.Success);

        var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await agent.AcceptAndCompleteJobWithPayloadAsync(job.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FailureReason = "Build failed: CS0001 syntax error",
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 1,
            FilesChangedCount = 2,
            LinesAdded = 10,
            LinesRemoved = 5,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>()
        });

        var history = await WaitForHistoryAsync(
            r => r.IssueIdentifier == "P103" && r.FinalStep == PipelineStep.Failed,
            TimeSpan.FromSeconds(10));
        Assert.NotNull(history);
        Assert.Contains("CS0001", history.FailureReason ?? "");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parity A5: JobAssignmentMessage completeness — all fields populated
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parity_JobAssignment_ContainsAllRequiredFields()
    {
        await SeedStandardTestDataAsync("P104", "Field completeness check");
        await using var agent = new FakeAgentClient("parity-fields-agent", "parity");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("P104");
        Assert.True(result.Success);

        var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // These fields must be populated in ALL modes (invariant)
        Assert.Equal("P104", job.IssueIdentifier);
        Assert.NotNull(job.IssueDetail);
        Assert.Equal("Field completeness check", job.IssueDetail.Title);
        Assert.NotNull(job.ParsedIssue);
        Assert.NotNull(job.PipelineConfiguration);
        Assert.NotEmpty(job.ProviderConfigs);
        Assert.NotNull(job.RepoProviderConfigId);
        Assert.NotNull(job.AgentProviderConfigId);
        Assert.NotNull(job.ResolvedProfileId);
        Assert.NotNull(job.InitiatedBy);
        Assert.NotNull(job.JobId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parity A6: Agent state transitions — Idle → Busy → Idle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parity_AgentStateTransitions_IdleBusyIdle()
    {
        await SeedStandardTestDataAsync("P105");
        await using var agent = new FakeAgentClient("parity-state-agent", "parity");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var registry = Fixture.Factory.Services.GetRequiredService<Orchestration.Registry.AgentRegistryService>();

        // Initially Idle
        var entry = registry.GetByAgentId("parity-state-agent");
        Assert.NotNull(entry);
        Assert.Equal(AgentStatus.Idle, entry.Status);

        // Dispatch → agent becomes Busy
        var result = await DispatchIssueAsync("P105");
        Assert.True(result.Success);
        var job = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await agent.AcceptJobAsync(job.JobId);

        await WaitUntilAsync(
            () => registry.GetByAgentId("parity-state-agent")?.Status == AgentStatus.Busy,
            TimeSpan.FromSeconds(5));

        // Complete → returns to Idle
        await agent.AcceptAndCompleteJobAsync(job.JobId);

        await WaitUntilAsync(
            () => registry.GetByAgentId("parity-state-agent")?.Status == AgentStatus.Idle,
            TimeSpan.FromSeconds(10));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Parity K8s: KubernetesWorkDistributor inserts Pending (vs SignalR Dispatched)
    // Validates mode-specific differences are intentional
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Parity_SignalRMode_WorkItemStartsAsDispatched_WhenAgentAvailable()
    {
        // In SignalR mode, when an agent is available, WorkItem starts as Dispatched
        // (unlike K8s mode where it always starts as Pending)
        await SeedStandardTestDataAsync("P106");
        await using var agent = new FakeAgentClient("parity-signalr-agent", "parity");
        await agent.ConnectAsync(BaseUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync("P106");
        Assert.True(result.Success);
        Assert.False(result.Queued, "SignalR mode with idle agent → immediate dispatch, not queued");

        // WorkItem should be Dispatched (not Pending) since agent was available
        var workItemId = Guid.Parse(result.WorkItemId!);
        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Dispatched, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemStatus.Dispatched, item.Status);
        Assert.NotNull(item.DispatchedAt);
    }

    [Fact]
    public async Task Parity_SignalRMode_WorkItemQueuedAsPending_WhenNoAgent()
    {
        // In SignalR mode, when no agent is available, WorkItem is queued as Pending
        // (same as K8s mode behavior)
        await SeedStandardTestDataAsync("P107");
        // DO NOT connect agent

        var result = await DispatchIssueAsync("P107");
        Assert.True(result.Success);
        Assert.True(result.Queued, "SignalR mode without agent → queued as Pending");

        // WorkItem should be Pending
        var workItemId = Guid.Parse(result.WorkItemId!);
        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Pending, TimeSpan.FromSeconds(5));
        Assert.Equal(WorkItemStatus.Pending, item.Status);
        Assert.Null(item.DispatchedAt);
    }
}
