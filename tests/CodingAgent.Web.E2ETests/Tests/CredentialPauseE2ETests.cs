using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Spec 056 task 11.1: an agent reports over the hub that its credential's quota is used up. The API ends the run
/// (Failed, <c>ProviderUnavailable</c>), pauses the credential and queues the same work as a new item, which waits for
/// the credential; a resume lets the dispatcher start it. The <c>claude,dotnet</c> template has a credential of its own
/// (Claude subscription), so the pause holds no other test's work.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class CredentialPauseE2ETests : HeadlessE2ETestBase
{
    private const string IssueId = "5601";
    private const string AgentId = "pause-e2e-agent";

    public CredentialPauseE2ETests(E2EFixture fixture) : base(fixture) { }

    private CredentialPauseStore Pauses => Fixture.ApiServices.GetRequiredService<CredentialPauseStore>();

    private async Task SeedAsync()
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = IssueId,
            Title = "Quota runs out mid-run",
            Description = "## Requirements\nDo the thing\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = ["agent:next"],
        });
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-pause-e2e",
            Name = "Pause Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, CancellationToken.None);
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-pause-e2e",
            DisplayName = "Pause Claude profile",
            JobTemplate = E2ERouting.Key("claude", "dotnet"),
            Settings = new Dictionary<string, string> { [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription },
        });
    }

    private async Task<WorkItemEntity?> RequeuedAsync(Guid endedId)
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        return await db.WorkItems.AsNoTracking()
            .Where(w => w.IssueIdentifier == IssueId && w.Id != endedId)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync();
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, Func<T, bool> done, TimeSpan timeout, string what)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        T? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await read();
            if (last is not null && done(last))
                return last;
            await Task.Delay(200);
        }
        throw new TimeoutException($"{what} did not happen within {timeout.TotalSeconds}s (last: {last})");
    }

    [Fact]
    public async Task AQuotaFailureOverTheHub_PausesTheCredential_RequeuesTheWork_AndAResumeStartsIt()
    {
        await SeedAsync();
        await using var agent = new FakeAgentClient(AgentId, "claude", "dotnet");
        await agent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        var result = await DispatchIssueAsync(IssueId);
        Assert.True(result.Success, $"Dispatch failed: {result.ErrorMessage}");
        var endedId = Guid.Parse(result.WorkItemId!);
        var assignment = await agent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: the agent's quota runs out; it reports a Failed completion carrying the provider failure (hub path).
        await agent.AcceptAndCompleteJobWithPayloadAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FailureReason = "Claude usage limit reached",
            CompletedAt = DateTimeOffset.UtcNow,
            AnalysisConcerns = [],
            AnalysisBlockingIssues = [],
            BlacklistedFilesDetected = [],
            CodeReviewAgentsRun = [],
            ProviderFailure = AgentProviderFailure.Create(
                AgentProviderFailure.QuotaExhausted, DateTimeOffset.UtcNow.AddHours(5), "Claude AI usage limit reached"),
        });

        // Assert: the run ended as a requeue, not as an agent error.
        var ended = await WaitForWorkItemStatusAsync(endedId, WorkItemStatus.Failed, TimeSpan.FromSeconds(15));
        Assert.Equal(FailureReason.ProviderUnavailable, ended.FailureReason);
        Assert.StartsWith("Requeued: Claude subscription paused", ended.ErrorMessage);

        // Assert: the credential is paused automatically.
        var pause = await Pauses.LoadAsync(AgentCredentials.ClaudeSubscription, CancellationToken.None);
        Assert.NotNull(pause);
        Assert.Equal(CredentialPauseSources.Auto, pause!.Source);
        Assert.Equal(CredentialPauseReasons.QuotaExhausted, pause.Reason);

        // Assert: the same work is queued again and waits for the credential, although the agent is idle.
        var requeued = await EventuallyAsync(() => RequeuedAsync(endedId), w => w.HoldReason is not null,
            TimeSpan.FromSeconds(10), "The requeued item's hold");
        Assert.Equal(WorkItemStatus.Pending, requeued.Status);
        Assert.Equal(HoldReasons.CredentialPaused, requeued.HoldReason);
        Assert.StartsWith("Claude subscription paused — quota used up", requeued.HoldText);
        await Fixture.JobController.DispatchOnceAsync();
        await Fixture.JobController.DispatchOnceAsync();
        Assert.Equal(WorkItemStatus.Pending, (await RequeuedAsync(endedId))!.Status);

        // Act: an operator resumes the credential.
        await Pauses.ResumeAsync(AgentCredentials.ClaudeSubscription, "e2e", CancellationToken.None);

        // Assert: the dispatcher starts the requeued work on the idle agent.
        var started = await EventuallyAsync(() => RequeuedAsync(endedId), w => w.Status != WorkItemStatus.Pending,
            TimeSpan.FromSeconds(15), "The requeued item's dispatch");
        Assert.Equal(AgentId, started.AssignedAgentId);
        Assert.Null(started.HoldReason);
    }
}
