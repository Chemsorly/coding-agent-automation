using CodingAgentWebUI.E2ETests.Fakes;
using k8s.Models;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Base class for K8s chat E2E tests.
/// Provides helpers for dispatching chat pods, connecting fake agents, and asserting labels.
/// </summary>
public abstract class K8sChatE2ETestBase : IAsyncLifetime
{
    protected K8sChatE2EFixture Fixture { get; }

    protected K8sChatE2ETestBase(K8sChatE2EFixture fixture)
    {
        Fixture = fixture;
    }

    public Task InitializeAsync()
    {
        Fixture.Factory.ResetAll();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Chat helpers ──────────────────────────────────────────────────────

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

        // Connect the fake agent with chat labels (this satisfies the dispatcher's poll loop)
        var fakeAgent = new FakeAgentClient(agentId, agentSelector.Split(',', StringSplitOptions.TrimEntries));
        await fakeAgent.ConnectAsChatAgentAsync(Fixture.ServerAddress, Fixture.ApiKey, dispatchId);

        // Now wait for dispatch to complete
        var returnedAgentId = await dispatchTask.WaitAsync(TimeSpan.FromSeconds(30));

        return (returnedAgentId, fakeAgent);
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
    /// No-op — PVC claiming is stateless; there is nothing to wait for.
    /// Kept for source compatibility; callers can be updated to remove the call.
    /// </summary>
    protected static Task WaitForPvcReleasedAsync(
        string pvcName,
        TimeSpan? timeout = null)
    {
        return Task.CompletedTask;
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

    /// <summary>Polls a condition until true or timeout.</summary>
    protected static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException("Condition not met within timeout");
    }
}
