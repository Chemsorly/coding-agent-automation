using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Base class for E2E tests that assert on state rather than on pages — WorkItem rows, hub
/// traffic, run history, and the Jobs the app would have created in a cluster. No Playwright, so
/// these run anywhere; <see cref="E2ETestBase"/> is the browser-driving sibling and shares the
/// same <see cref="E2EFixture"/>.
///
/// This merges what used to be three base classes — <c>DbModeE2ETestBase</c>,
/// <c>K8sModeE2ETestBase</c> and <c>K8sChatE2ETestBase</c> — one per deployment mode. Spec 041
/// left a single mode, so the split described nothing; the helpers were disjoint and are simply
/// collected here.
/// </summary>
public abstract class HeadlessE2ETestBase : IAsyncLifetime
{
    protected E2EFixture Fixture { get; }

    /// <summary>
    /// Where <c>FakeAgentClient</c> connects. This is the Pipeline API, not the Blazor app:
    /// Spec 044 removed <c>MapHub&lt;AgentHub&gt;</c> from the monolith, so connecting there
    /// fails negotiate with 405.
    /// </summary>
    protected string BaseUrl => Fixture.AgentHubUrl;

    /// <summary>Alias for <see cref="BaseUrl"/>; both point at the Pipeline API hub here.</summary>
    protected string AgentHubUrl => Fixture.AgentHubUrl;

    protected HeadlessE2ETestBase(E2EFixture fixture)
    {
        Fixture = fixture;
    }

    public Task InitializeAsync()
    {
        // Reset all state between tests
        Fixture.Factory.ResetAll();

        // Guard: verify DI replacement worked
        var factory = Fixture.Factory.Services.GetRequiredService<IProviderFactory>();
        if (factory is not FakeProviderFactory)
            throw new InvalidOperationException(
                $"DI replacement failed: IProviderFactory resolved as {factory.GetType().Name} instead of FakeProviderFactory");

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Dispatch Helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Dispatches an issue through the full pipeline:
    /// IDispatchOrchestrationService.PrepareDistributionRequestAsync → IWorkDistributor.DistributeAsync
    /// Returns the DistributionResult (contains WorkItem ID).
    /// </summary>
    protected async Task<DistributionResult> DispatchIssueAsync(
        string issueIdentifier,
        string? templateId = null,
        string? projectId = null,
        CancellationToken ct = default)
    {
        var orchService = Fixture.Factory.Services.GetRequiredService<IDispatchOrchestrationService>();
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();

        projectId ??= WellKnownIds.DefaultProjectId;
        var project = await Fixture.ConfigStore.GetProjectByIdAsync(projectId, ct)
            ?? throw new InvalidOperationException($"Project '{projectId}' not found in ConfigStore");

        // templateId used to be accepted and then ignored, so every dispatch ran against the
        // default providers no matter which template a test seeded. Tests that seed a template
        // specifically to select a different repository provider (token vending, brain sync) could
        // not work at all. Resolve from the template when one is named, and fall back to the
        // defaults otherwise so existing callers are unaffected.
        var issueProviderId = "issue-e2e";
        var repoProviderId = "repo-e2e";
        string? brainProviderId = null;

        if (!string.IsNullOrEmpty(templateId))
        {
            var templates = await Fixture.ConfigStore.LoadTemplatesForProjectAsync(projectId, ct);
            var template = templates.FirstOrDefault(t => t.Id == templateId)
                ?? throw new InvalidOperationException(
                    $"Template '{templateId}' not found in project '{projectId}'");

            issueProviderId = template.IssueProviderId;
            repoProviderId = template.RepoProviderId;
            brainProviderId = template.BrainProviderId;
        }

        // PrepareDistributionRequestAsync performs full orchestration:
        // issue fetch, label swap, profile/QG resolution, run creation, provider config preparation
        var request = await orchService.PrepareDistributionRequestAsync(
            new ImplementationDispatchOrchestrationRequest
            {
                IssueIdentifier = issueIdentifier,
                IssueProviderId = issueProviderId,
                RepoProviderId = repoProviderId,
                BrainProviderId = brainProviderId,
                PipelineProviderId = null,
                InitiatedBy = "e2e-test",
                Project = project
            },
            ct);

        if (request is null)
            return new DistributionResult(
                Success: false,
                WorkItemId: null,
                ErrorMessage: $"Orchestration failed for issue '{issueIdentifier}' " +
                    "(issue not found, no matching profile, or dedup guard rejected).");

        var distResult = await distributor.DistributeAsync(request, ct);
        return distResult;
    }

    /// <summary>
    /// Distributes work through <c>IWorkDistributor</c> directly, skipping orchestration.
    /// Use when the test is about the WorkItem row and not about issue resolution.
    /// </summary>
    protected async Task<DistributionResult> DistributeDirectlyAsync(
        string issueIdentifier,
        string agentSelector = "kiro,dotnet")
    {
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            AgentSelector = agentSelector,
            TimeoutSeconds = 3600,
            TaskType = WorkItemTaskType.Implementation,
            ProjectId = WellKnownIds.DefaultProjectId,
            InitiatedBy = "e2e-test"
        };
        return await distributor.DistributeAsync(request, CancellationToken.None);
    }

    /// <summary>
    /// Inserts a Pending WorkItem straight into the database, bypassing both orchestration and
    /// the distributor. Use when the test is about what the dispatch loop does with a row.
    /// </summary>
    protected async Task<Guid> InsertPendingWorkItemAsync(
        string issueIdentifier,
        string agentSelector = "kiro,dotnet",
        int timeoutSeconds = 3600,
        string? projectId = null)
    {
        var workItemId = Guid.NewGuid();
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Pending,
            Payload = "{}",
            AgentSelector = agentSelector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = timeoutSeconds,
            ProjectId = projectId ?? WellKnownIds.DefaultProjectId
        });
        await db.SaveChangesAsync();
        return workItemId;
    }

    // ── Chat Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a chat pod for <paramref name="agentSelector"/> and connects a
    /// <see cref="FakeAgentClient"/> as a chat agent. Returns <c>(agentId, fakeAgent)</c>.
    /// The caller is responsible for disposing the <see cref="FakeAgentClient"/>.
    /// </summary>
    protected async Task<(string agentId, FakeAgentClient fakeAgent)> DispatchChatPodAndConnectAsync(
        string agentSelector,
        string? model = null,
        string? effort = null,
        string? overrideAgentId = null)
    {
        var agentId = overrideAgentId ?? $"fake-chat-agent-{Guid.NewGuid().ToString("N")[..6]}";

        // Start dispatch — this will poll for agent connection
        var dispatchTask = Fixture.Factory.ChatDispatcher.DispatchChatPodAsync(
            agentSelector, model, effort, CancellationToken.None);

        // Wait for the job to be created (brief poll), then connect the fake agent
        await WaitForChatJobCreatedAsync(agentSelector, timeout: TimeSpan.FromSeconds(10));

        // Find the dispatch ID from the created job so we can connect with matching labels
        var encodedSelector = agentSelector.Replace(',', '_').Replace(" ", "");
        var job = Fixture.K8sClient.GetChatJobBySelector(encodedSelector)
            ?? throw new InvalidOperationException(
                $"Chat job not found for selector '{encodedSelector}' after waiting");

        var labels = job.Metadata?.Labels;
        string? dispatchId = null;
        if (labels is not null)
            labels.TryGetValue("caa/chat-session-id", out dispatchId);
        if (dispatchId is null)
            throw new InvalidOperationException("Chat job is missing caa/chat-session-id label");

        // Connect the fake agent with chat labels (this satisfies the dispatcher's poll loop).
        // The hub is on the API host since Spec 044, not on the Blazor app.
        var fakeAgent = new FakeAgentClient(agentId, agentSelector.Split(',', StringSplitOptions.TrimEntries));
        await fakeAgent.ConnectAsChatAgentAsync(AgentHubUrl, Fixture.ApiKey, dispatchId);

        // Now wait for dispatch to complete
        var returnedAgentId = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(30));

        return (returnedAgentId, fakeAgent);
    }

    /// <summary>
    /// Asserts that <paramref name="job"/> has all required K8s chat labels and
    /// container env vars.
    /// </summary>
    protected static void AssertChatJobLabels(V1Job job, string? dispatchId = null)
    {
        var labels = job.Metadata?.Labels
            ?? throw new Xunit.Sdk.XunitException("Job has no metadata labels");

        Assert.True(labels.ContainsKey("caa/chat-session-id"),
            "Job missing label: caa/chat-session-id");
        Assert.True(labels.ContainsKey("caa/chat-selector"),
            "Job missing label: caa/chat-selector");

        if (dispatchId is not null)
            Assert.Equal(dispatchId, labels["caa/chat-session-id"]);

        // Assert env vars on the first container
        var container = job.Spec?.Template?.Spec?.Containers?.FirstOrDefault()
            ?? throw new Xunit.Sdk.XunitException("Job has no containers");

        var envNames = container.Env?.Select(e => e.Name).ToHashSet() ?? new HashSet<string>();
        Assert.Contains("AGENT_CHAT_MODE", envNames);
        Assert.Equal("true", container.Env!.First(e => e.Name == "AGENT_CHAT_MODE").Value);
        Assert.Contains("AGENT_CHAT_SESSION_ID", envNames);
    }

    /// <summary>
    /// No-op — PVC claiming is stateless; there is nothing to wait for.
    /// Kept for source compatibility; callers can be updated to remove the call.
    /// </summary>
    protected static Task WaitForPvcReleasedAsync(
        string pvcName,
        TimeSpan? timeout = null)
    {
        return Task.CompletedTask;
    }

    // ── Wait Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Polls the WorkItems table until a WorkItem matching the predicate appears, or times out.
    /// </summary>
    protected async Task<WorkItemEntity> WaitForWorkItemAsync(
        Func<WorkItemEntity, bool> predicate,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var match = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => predicate(w));
            if (match is not null) return match;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"No matching WorkItem found within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls until a specific WorkItem reaches the expected status, or times out.
    /// </summary>
    protected async Task<WorkItemEntity> WaitForWorkItemStatusAsync(
        Guid workItemId,
        WorkItemStatus expectedStatus,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            if (item?.Status == expectedStatus) return item;
            await Task.Delay(interval);
        }

        // Final check with diagnostic info
        await using var finalDb = Fixture.DbContextFactory.CreateDbContext();
        var finalItem = await finalDb.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        throw new TimeoutException(
            $"WorkItem {workItemId} did not reach status {expectedStatus} within " +
            $"{(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s. " +
            $"Current status: {finalItem?.Status.ToString() ?? "NOT FOUND"}");
    }

    /// <summary>Polls until the fake K8s client has at least the expected number of created jobs.</summary>
    protected async Task WaitForK8sJobCreatedAsync(int expectedCount = 1, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (Fixture.K8sClient.CreatedJobs.Count >= expectedCount)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException(
            $"Expected {expectedCount} K8s Job(s), got {Fixture.K8sClient.CreatedJobs.Count} within " +
            $"{(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls <see cref="FakeKubernetesJobClient.ChatJobs"/> until a job with a
    /// <c>caa/chat-selector</c> label matching <paramref name="agentSelector"/> appears.
    /// </summary>
    protected async Task WaitForChatJobCreatedAsync(
        string agentSelector,
        TimeSpan? timeout = null)
    {
        var encodedSelector = agentSelector.Replace(',', '_').Replace(" ", "");
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline)
        {
            var job = Fixture.K8sClient.GetChatJobBySelector(encodedSelector);
            if (job is not null) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            $"Chat job for selector '{agentSelector}' (encoded: '{encodedSelector}') " +
            $"not created within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s. " +
            $"ChatJobs: [{string.Join(", ", Fixture.K8sClient.ChatJobs.Keys)}]");
    }

    /// <summary>
    /// Polls the history service until a run matching the predicate appears, or times out.
    /// </summary>
    protected async Task<PipelineRunSummary> WaitForHistoryAsync(
        Func<PipelineRunSummary, bool> predicate,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            var runs = (await Fixture.HistoryService.GetRunHistoryAsync());
            var match = runs.FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"No matching run appeared in history within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls a condition until it returns true, or times out.
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Condition not met within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }

    /// <summary>
    /// Polls an async condition until it returns true, or times out.
    /// </summary>
    protected static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(interval);
        }

        throw new TimeoutException(
            $"Condition not met within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds}s");
    }
}
